using System.Security.Cryptography;
using System.Text;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Models.Staffing;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.Services.Competitions;
using HpskSite.Services.Planning;
using Newtonsoft.Json;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    public sealed record PatrolPreview(PatrolPlan Plan, string Fingerprint, string? BlockedReason, int ExistingPatrols, int ExistingShooters, int Moved, int Unchanged, int Added, int Removed);

    public sealed record PatrolApplyOutcome(bool Ok, string? Message, int BackupId, int Patrols, int Shooters);

    /// <summary>
    /// Planera dagen steg 3b: <b>Bygg startlistan runt funktionärerna</b> (fält). Läser anmälningarna, uppdragen (från bemanningen), lägena och låsen,
    /// låter <see cref="FaltskyttePatrolPlanner"/> räkna ut en ny startlista och — först när arrangören tillämpar — skriver den.
    /// <list type="bullet">
    /// <item><b>Förhandsvisning lagras inte</b>: den räknas om vid varje anrop, med ett fingeravtryck av underlaget (anmälningar, nuvarande patruller med lås,
    /// uppdrag, lägen, inställningar). Har något ändrats sedan avvisas tillämpningen.</item>
    /// <item><b>Vägrar</b> när startlistan är publicerad eller en patrull redan gått ut — om inte arrangören uttryckligen tvingar fram det.</item>
    /// <item><b>Backup före varje skrivning</b> (<c>FaltskyttePatrolBackup</c>, hela listan som JSON) och allt skrivs i EN transaktion. <see cref="Undo"/> återställer
    /// den — men bara om ingen ändrat listan sedan.</item>
    /// <item>Registreringarna läses <i>före</i> transaktionen (egen anslutning i någon annans scope = lås).</item>
    /// </list>
    /// </summary>
    public class PatrolPlanService
    {
        private readonly IScopeProvider _scopes;
        private readonly UmbracoStartListRepository _registrations;
        private readonly ICompetitionStore _competitions;
        private readonly PlanningService _planning;
        private readonly StaffOwnerResolver _owners;
        private readonly ILogger<PatrolPlanService> _logger;

        public PatrolPlanService(IScopeProvider scopes, UmbracoStartListRepository registrations, ICompetitionStore competitions, PlanningService planning,
            StaffOwnerResolver owners, ILogger<PatrolPlanService> logger)
        {
            _scopes = scopes; _registrations = registrations; _competitions = competitions; _planning = planning; _owners = owners; _logger = logger;
        }

        // ------------------------------------------------------------ underlaget

        private sealed class Basis
        {
            public List<CompetitionRegistration> Regs { get; init; } = new();
            public List<FaltskyttePatrol> Patrols { get; init; } = new();
            public List<FaltskyttePatrolMember> Members { get; init; } = new();
            public PatrolConstraints Constraints { get; init; } = new();
            public HashSet<int> Reserves { get; init; } = new();
            public List<string> Fingerprint { get; init; } = new();
            public bool Published { get; init; }
            public bool Started { get; init; }
            public DateTime? Date { get; init; }
        }

        private async Task<(Basis? Basis, string? Error)> LoadAsync(StaffOwner owner, PatrolPlanSettings settings)
        {
            if (!owner.HasCompetition) return (null, "Startlistan hör till en tävling.");
            var comp = _competitions.GetSaved(owner.Comp);
            if (comp == null) return (null, "Tävlingen hittades inte.");
            if (!FunctionaryRoles.FaltFamily.Contains(comp.CompetitionType ?? "", StringComparer.OrdinalIgnoreCase))
                return (null, "Startlistan byggs runt funktionärerna i fält (och magnumfält) än så länge.");

            // Före alla scope: repositoryt har egen läsning.
            var regs = (await _registrations.GetCompetitionRegistrations(owner.Comp)).Where(r => r.IsActive && r.MemberId > 0).ToList();

            var family = owner.HasPlanning ? _planning.Family(owner.PlanningId).Select(f => f.Id).ToList() : new List<int>();
            var idList = family.Count > 0 ? string.Join(",", family) : "0";
            var shoot = owner.HasPlanning ? _owners.ShootTime(owner.PlanningId) : ShootTimeRules.Default(_owners.Discipline(owner));
            var planningRow = owner.HasPlanning ? _owners.Planning(owner.PlanningId) : null;
            var overrides = owner.HasPlanning ? _planning.ShooterModeOverrides(owner.PlanningId) : new Dictionary<int, string>();

            List<FaltskyttePatrol> patrols; List<FaltskyttePatrolMember> members; List<StaffAssignment> assigns; List<StaffPass> passes; List<int> signedUp;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                patrols = db.Fetch<FaltskyttePatrol>("SELECT * FROM FaltskyttePatrol WHERE CompetitionId = @0 ORDER BY PatrolNumber", owner.Comp);
                members = db.Fetch<FaltskyttePatrolMember>("SELECT m.* FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE p.CompetitionId = @0", owner.Comp);
                assigns = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE (PlanningId IN ({idList}) OR CompetitionId = @0) AND MemberId > 0", owner.Comp);
                passes = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({idList})");
                signedUp = db.Fetch<int>($"SELECT DISTINCT MemberId FROM StaffHelpSignup WHERE PlanningId IN ({idList}) AND MemberId > 0");
            }
            var passById = passes.ToDictionary(p => p.Id);

            // Uppdragen: varje icke avböjt uppdrag med känt intervall är upptagen tid.
            var jobs = new Dictionary<int, List<StaffInterval>>();
            foreach (var a in assigns.Where(a => !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)))
            {
                var (_, iv) = FamilyWeekService.WhenOf(a, a.PassId is > 0 && passById.TryGetValue(a.PassId.Value, out var p) ? p : null);
                if (iv is not { } v) continue;
                if (!jobs.TryGetValue(a.MemberId!.Value, out var l)) jobs[a.MemberId.Value] = l = new List<StaffInterval>();
                l.Add(v);
            }

            // Lägen gäller bara funktionärer (de som har ett uppdrag eller anmält sig): reserven får ingen fast start; patrull och "skjuter först" går tidigt.
            var staff = jobs.Keys.Concat(signedUp).ToHashSet();
            var reserves = new HashSet<int>(); var early = new HashSet<int>();
            foreach (var mid in regs.Select(r => r.MemberId).Distinct().Where(staff.Contains))
            {
                var mode = ShooterModeRules.Effective(planningRow?.ShooterMode, overrides.GetValueOrDefault(mid));
                if (!ShooterModeRules.GetsFixedStart(mode)) reserves.Add(mid);
                else if (mode is ShooterMode.Patrol or ShooterMode.ShootFirst) early.Add(mid);
            }

            var fp = new List<string> { $"set:{settings.PatrolSize}/{settings.IntervalMinutes}/{settings.FirstStart:s}/{settings.WeaponGrouping}/{settings.MultiClassGapMinutes}",
                $"shoot:{shoot.BeforeMinutes}/{shoot.DurationMinutes}/{shoot.AfterMinutes}" };
            fp.AddRange(regs.OrderBy(r => r.MemberId).ThenBy(r => r.MemberClass, StringComparer.Ordinal).Select(r => $"r:{r.MemberId}|{r.MemberClass}"));
            fp.AddRange(patrols.OrderBy(p => p.Id).Select(p => $"p:{p.Id}|{p.PatrolNumber}|{p.StartTime:s}|{p.WeaponGroup}|{p.DepartedAt:s}"));
            fp.AddRange(members.OrderBy(m => m.Id).Select(m => $"m:{m.Id}|{m.PatrolId}|{m.MemberId}|{m.ShootingClass}|{m.Position}|{m.Locked}|{m.Status}"));
            fp.AddRange(jobs.OrderBy(k => k.Key).SelectMany(k => k.Value.Select(v => $"j:{k.Key}|{v.Start:s}|{v.End:s}|{v.AllDay}")));
            fp.Add("res:" + string.Join(",", reserves.OrderBy(x => x)));
            fp.Add("early:" + string.Join(",", early.OrderBy(x => x)));

            return (new Basis
            {
                Regs = regs, Patrols = patrols, Members = members, Reserves = reserves, Fingerprint = fp,
                Constraints = new PatrolConstraints { Jobs = jobs, Shoot = shoot, Early = early },
                Published = comp.FaltskyttePatrolsPublished,
                Started = patrols.Any(p => p.DepartedAt != null),
                Date = comp.CompetitionDate?.Date,
            }, null);
        }

        private static PatrolPlan PlanOf(Basis b, PatrolPlanSettings settings)
        {
            var patrolById = b.Patrols.ToDictionary(p => p.Id);
            var locked = b.Members.Where(m => m.Locked && patrolById.TryGetValue(m.PatrolId, out var p) && p.StartTime != null)
                .Select(m => new LockedPlacement(m.MemberId, m.MemberName, m.ClubName, m.ShootingClass, patrolById[m.PatrolId].StartTime!.Value, patrolById[m.PatrolId].WeaponGroup))
                .ToList();
            return FaltskyttePatrolPlanner.Plan(b.Regs, locked, b.Constraints, b.Reserves, settings);
        }

        private static string Hash(IEnumerable<string> parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))[..24];

        /// <summary>Standardinställningar för en tävling: första start 09:00 tävlingsdagen om inget annat sägs.</summary>
        public PatrolPlanSettings Normalize(int competitionId, int patrolSize, int intervalMinutes, DateTime? firstStart, string? grouping, int gap)
        {
            var date = _competitions.GetSaved(competitionId)?.CompetitionDate?.Date ?? DateTime.Today;
            return new PatrolPlanSettings
            {
                PatrolSize = patrolSize is >= 1 and <= 12 ? patrolSize : 6,
                IntervalMinutes = intervalMinutes is >= 1 and <= 120 ? intervalMinutes : 15,
                FirstStart = firstStart ?? date.AddHours(9),
                WeaponGrouping = grouping is "MixAll" or "CombineAR" ? grouping : "Separate",
                MultiClassGapMinutes = gap is > 0 and <= 600 ? gap : 0,
            };
        }

        // ------------------------------------------------------------ förhandsvisning

        public async Task<PatrolPreview> PreviewAsync(StaffOwner owner, PatrolPlanSettings settings)
        {
            var (b, err) = await LoadAsync(owner, settings);
            if (b == null) return new PatrolPreview(new PatrolPlan(), "", err, 0, 0, 0, 0, 0, 0);
            var plan = PlanOf(b, settings);

            // Vad ändras? Jämför per (skytt, klass) före och efter.
            var patrolById = b.Patrols.ToDictionary(p => p.Id);
            string K(int id, string cls) => id + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(cls);
            var before = b.Members.Where(m => patrolById.ContainsKey(m.PatrolId)).GroupBy(m => K(m.MemberId, m.ShootingClass))
                .ToDictionary(g => g.Key, g => patrolById[g.First().PatrolId].StartTime);
            var after = plan.Patrols.SelectMany(p => p.Members.Select(m => (Key: K(m.MemberId, m.ShootingClass), p.StartTime))).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().StartTime);
            var unchanged = after.Count(kv => before.TryGetValue(kv.Key, out var t) && t == kv.Value);
            var moved = after.Count(kv => before.TryGetValue(kv.Key, out var t) && t != kv.Value);
            var added = after.Count(kv => !before.ContainsKey(kv.Key));
            var removed = before.Count(kv => !after.ContainsKey(kv.Key));

            string? blocked = null;
            if (b.Published) blocked = "Startlistan är publicerad. Avpublicera den först, eller tvinga fram ombyggnaden.";
            else if (b.Started) blocked = "Någon patrull har redan gått ut. Startlistan bör inte byggas om nu.";
            return new PatrolPreview(plan, Hash(b.Fingerprint), blocked, b.Patrols.Count, b.Members.Count, moved, unchanged, added, removed);
        }

        // ------------------------------------------------------------ tillämpa

        public async Task<PatrolApplyOutcome> ApplyAsync(StaffOwner owner, PatrolPlanSettings settings, string? fingerprint, bool force, int byMemberId)
        {
            var (b, err) = await LoadAsync(owner, settings);
            if (b == null) return new PatrolApplyOutcome(false, err, 0, 0, 0);
            if (!string.Equals(Hash(b.Fingerprint), fingerprint, StringComparison.Ordinal))
                return new PatrolApplyOutcome(false, "Anmälningarna, startlistan eller uppdragen har ändrats sedan förhandsvisningen. Gör en ny — inget ändrades.", 0, 0, 0);
            if ((b.Published || b.Started) && !force)
                return new PatrolApplyOutcome(false, b.Published ? "Startlistan är publicerad. Inget ändrades." : "En patrull har redan gått ut. Inget ändrades.", 0, 0, 0);

            var plan = PlanOf(b, settings);
            if (plan.Patrols.Count == 0) return new PatrolApplyOutcome(false, "Det finns inga skyttar att bygga en startlista av.", 0, 0, 0);
            var lockedKeys = b.Members.Where(m => m.Locked).Select(m => m.MemberId + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(m.ShootingClass)).ToHashSet();

            var backup = new PatrolBackupDoc
            {
                Patrols = b.Patrols.Select(p => new PatrolBackupPatrol { Patrol = p, Members = b.Members.Where(m => m.PatrolId == p.Id).ToList() }).ToList(),
                AfterHash = AfterHash(plan.Patrols.Select(p => (p.PatrolNumber, p.StartTime, p.WeaponGroup, p.Members.Select(m => (m.MemberId, m.ShootingClass)).ToList()))),
            };

            int backupId;
            using (var scope = _scopes.CreateScope())   // EN transaktion: faller något återgår allt
            {
                var db = scope.Database;
                backupId = Convert.ToInt32(db.ExecuteScalar<int>(
                    "INSERT INTO FaltskyttePatrolBackup (CompetitionId, CreatedAt, CreatedBy, Json) OUTPUT INSERTED.Id VALUES (@0, @1, @2, @3)",
                    owner.Comp, DateTime.UtcNow, byMemberId, JsonConvert.SerializeObject(backup)));
                db.Execute("DELETE FROM FaltskyttePatrolMember WHERE PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @0)", owner.Comp);
                db.Execute("DELETE FROM FaltskyttePatrol WHERE CompetitionId = @0", owner.Comp);
                foreach (var p in plan.Patrols)
                {
                    var row = new FaltskyttePatrol { CompetitionId = owner.Comp, PatrolNumber = p.PatrolNumber, StartTime = p.StartTime, WeaponGroup = p.WeaponGroup };
                    db.Insert(row);
                    foreach (var m in p.Members)
                        db.Insert(new FaltskyttePatrolMember
                        {
                            PatrolId = row.Id, MemberId = m.MemberId, Position = m.Position, ShootingClass = m.ShootingClass, MemberName = m.Name, ClubName = m.Club,
                            Locked = lockedKeys.Contains(m.MemberId + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(m.ShootingClass)),
                        });
                }
                scope.Complete();
            }
            _logger.LogInformation("Startlistan byggdes om runt funktionärerna i tävling {Comp}: {Patrols} patruller, backup {Backup}, av medlem {By}", owner.Comp, plan.Patrols.Count, backupId, byMemberId);
            return new PatrolApplyOutcome(true, null, backupId, plan.Patrols.Count, plan.Patrols.Sum(p => p.Members.Count));
        }

        // ------------------------------------------------------------ ångra

        /// <summary>
        /// Återställer startlistan som den var före senaste ombyggnaden. Bara om ingen ändrat listan sedan dess (annars förloras den ändringen):
        /// då vägras det om inte <paramref name="force"/> är satt.
        /// </summary>
        public (bool Ok, string? Message) Undo(StaffOwner owner, int backupId, bool force)
        {
            if (!owner.HasCompetition) return (false, "Ingen tävling.");
            using var scope = _scopes.CreateScope();
            var db = scope.Database;
            var row = db.FirstOrDefault<PatrolBackupRow>("SELECT * FROM FaltskyttePatrolBackup WHERE Id = @0 AND CompetitionId = @1", backupId, owner.Comp);
            if (row == null) return (false, "Säkerhetskopian finns inte.");
            if (row.RestoredAt != null) return (false, "Den är redan återställd.");
            var doc = JsonConvert.DeserializeObject<PatrolBackupDoc>(row.Json);
            if (doc == null) return (false, "Säkerhetskopian går inte att läsa.");

            var nowPatrols = db.Fetch<FaltskyttePatrol>("SELECT * FROM FaltskyttePatrol WHERE CompetitionId = @0 ORDER BY PatrolNumber", owner.Comp);
            var nowMembers = db.Fetch<FaltskyttePatrolMember>("SELECT m.* FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE p.CompetitionId = @0 ORDER BY m.Position", owner.Comp);
            var nowHash = AfterHash(nowPatrols.OrderBy(p => p.PatrolNumber).Select(p => (p.PatrolNumber, p.StartTime, p.WeaponGroup,
                nowMembers.Where(m => m.PatrolId == p.Id).OrderBy(m => m.Position).Select(m => (m.MemberId, m.ShootingClass)).ToList())));
            if (nowHash != doc.AfterHash && !force)
                return (false, "Startlistan har ändrats sedan ombyggnaden. Att återställa nu tar bort de ändringarna.");

            db.Execute("DELETE FROM FaltskyttePatrolMember WHERE PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @0)", owner.Comp);
            db.Execute("DELETE FROM FaltskyttePatrol WHERE CompetitionId = @0", owner.Comp);
            foreach (var bp in doc.Patrols)
            {
                var p = bp.Patrol; p.Id = 0; p.CompetitionId = owner.Comp;
                db.Insert(p);
                foreach (var m in bp.Members) { m.Id = 0; m.PatrolId = p.Id; db.Insert(m); }
            }
            db.Execute("UPDATE FaltskyttePatrolBackup SET RestoredAt = @1 WHERE Id = @0", backupId, DateTime.UtcNow);
            scope.Complete();
            return (true, null);
        }

        /// <summary>Senaste ombyggnaden som går att ångra (inte redan återställd), eller 0.</summary>
        public int LatestUndoable(StaffOwner owner)
        {
            if (!owner.HasCompetition) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int?>("SELECT TOP 1 Id FROM FaltskyttePatrolBackup WHERE CompetitionId = @0 AND RestoredAt IS NULL ORDER BY Id DESC", owner.Comp) ?? 0;
        }

        // ------------------------------------------------------------ lås

        /// <summary>Låser eller låser upp en skytts plats. Bara i tävlingen ägaren hör till. Antal rader som ändrades (0 = fanns inte).</summary>
        public int SetLocked(StaffOwner owner, int patrolMemberId, bool locked)
        {
            if (!owner.HasCompetition) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Execute(
                "UPDATE m SET Locked = @2 FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE m.Id = @0 AND p.CompetitionId = @1",
                patrolMemberId, owner.Comp, locked);
        }

        // ------------------------------------------------------------ hjälp

        private static string AfterHash(IEnumerable<(int Number, DateTime? Start, string? Group, List<(int MemberId, string Class)> Members)> patrols) =>
            Hash(patrols.OrderBy(p => p.Number).Select(p => $"{p.Number}|{p.Start:s}|{p.Group}|" + string.Join(",", p.Members.OrderBy(m => m.MemberId).ThenBy(m => m.Class, StringComparer.Ordinal).Select(m => $"{m.MemberId}:{m.Class}"))));

        private sealed class PatrolBackupDoc
        {
            public List<PatrolBackupPatrol> Patrols { get; set; } = new();
            public string AfterHash { get; set; } = "";
        }

        private sealed class PatrolBackupPatrol
        {
            public FaltskyttePatrol Patrol { get; set; } = new();
            public List<FaltskyttePatrolMember> Members { get; set; } = new();
        }

        [TableName("FaltskyttePatrolBackup")]
        [PrimaryKey("Id", AutoIncrement = true)]
        private sealed class PatrolBackupRow
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public DateTime CreatedAt { get; set; }
            public int CreatedBy { get; set; }
            public string Json { get; set; } = "";
            public DateTime? RestoredAt { get; set; }
        }
    }
}
