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
    /// <summary>En skytt i den nuvarande startlistan, så att arrangören kan låsa de som ska ligga kvar.</summary>
    public sealed record CurrentPlacement(int PatrolMemberId, int MemberId, string Name, string ShootingClass, int PatrolNumber, DateTime? StartTime, bool Locked);

    public sealed record PatrolPreview(PatrolPlan Plan, string Fingerprint, string? BlockedReason, int ExistingPatrols, int ExistingShooters, int Moved, int Unchanged, int Added, int Removed,
        List<CurrentPlacement>? Current = null, int KeptFitting = 0, List<PatrolMove>? Moves = null, List<PatrolGroupSpec>? Groups = null);

    /// <summary>En skytts förändring i förhandsvisningen: <c>Kind</c> = moved | added | removed, från och till (patrull och tid) och skälet i klartext.</summary>
    public sealed record PatrolMove(string Kind, int MemberId, string Name, string ShootingClass, int? FromPatrol, DateTime? FromTime, int? ToPatrol, DateTime? ToTime, string Why);

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
            StaffOwnerResolver owners, ILogger<PatrolPlanService> logger, HpskSite.Services.Hosting.DatabaseLock dbLock)
        {
            _scopes = scopes; _registrations = registrations; _competitions = competitions; _planning = planning; _owners = owners; _logger = logger; _lock = dbLock;
        }

        private readonly HpskSite.Services.Hosting.DatabaseLock _lock;

        // ------------------------------------------------------------ underlaget

        public sealed class PatrolBasis
        {
            public List<CompetitionRegistration> Regs { get; init; } = new();
            public List<FaltskyttePatrol> Patrols { get; init; } = new();
            public List<FaltskyttePatrolMember> Members { get; init; } = new();
            public PatrolConstraints Constraints { get; init; } = new();
            public HashSet<int> Reserves { get; init; } = new();
            public List<string> Fingerprint { get; init; } = new();
            public bool Published { get; init; }
            /// <summary>Vilka vapengrupper som är publicerade ("C,B"); tom = hela listan.</summary>
            public string? PublishedGroups { get; init; }
            /// <summary>Tävlingens stationskonfiguration (kopian i tävlingen): stationer och skjuttider per grupp för stationsbilden och krockarna.</summary>
            public string? StationConfigJson { get; init; }
            public bool PatrolPublished(FaltskyttePatrol p) => HpskSite.CompetitionTypes.Faltskytte.Services.FaltskyttePatrolWrites.IsPatrolPublished(Published, PublishedGroups, p.WeaponGroup);
            /// <summary>Skyttar som redan har resultat i tävlingen. De låses på sin patrull och tid: ett nytt förslag får aldrig röra befintligt resultat.</summary>
            public HashSet<int> ResultMembers { get; init; } = new();
            public bool Started { get; init; }
            public DateTime? Date { get; init; }
        }

        private async Task<(PatrolBasis? Basis, string? Error)> LoadAsync(StaffOwner owner, PatrolPlanSettings settings, FaltPlanOptions? options = null)
        {
            if (!owner.HasCompetition) return (null, "Startlistan hör till en tävling.");
            var comp = _competitions.GetSaved(owner.Comp);
            if (comp == null) return (null, "Tävlingen hittades inte.");
            if (!FunctionaryRoles.FaltFamily.Contains(comp.CompetitionType ?? "", StringComparer.OrdinalIgnoreCase))
                return (null, "Startlistan byggs runt funktionärerna i fält (och magnumfält) än så länge.");

            // Före alla scope: repositoryt har egen läsning.
            var regs = (await _registrations.GetCompetitionRegistrations(owner.Comp)).Where(r => r.IsActive && r.MemberId > 0).ToList();

            var family = _planning.Family(owner.PlanningId).Select(f => f.Id).ToList();
            var idList = family.Count > 0 ? string.Join(",", family) : "0";
            var shoot = _owners.ShootTime(owner.PlanningId);
            var planningRow = _owners.Planning(owner.PlanningId);
            var overrides = _planning.ShooterModeOverrides(owner.PlanningId);

            HashSet<int> resultMembers; List<FaltskyttePatrol> patrols; List<FaltskyttePatrolMember> members; List<StaffAssignment> assigns; List<StaffPass> passes; List<int> signedUp;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                resultMembers = db.Fetch<int>("SELECT DISTINCT MemberId FROM FaltskytteResultEntry WHERE CompetitionId = @0", owner.Comp).ToHashSet();
                patrols = db.Fetch<FaltskyttePatrol>("SELECT * FROM FaltskyttePatrol WHERE CompetitionId = @0 ORDER BY PatrolNumber", owner.Comp);
                members = db.Fetch<FaltskyttePatrolMember>("SELECT m.* FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE p.CompetitionId = @0", owner.Comp);
                assigns = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE (PlanningId IN ({idList}) OR CompetitionId = @0) AND MemberId > 0", owner.Comp);
                passes = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({idList})");
                signedUp = db.Fetch<int>($"SELECT DISTINCT MemberId FROM StaffHelpSignup WHERE PlanningId IN ({idList}) AND MemberId > 0");
            }
            var passById = passes.ToDictionary(p => p.Id);

            // Uppdragen: varje icke avböjt uppdrag med känt intervall är upptagen tid.
            var jobs = new Dictionary<int, List<StaffInterval>>();
            foreach (var a in assigns)
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

            var fp = new List<string> { $"set:{settings.PatrolSize}/{settings.IntervalMinutes}/{settings.FirstStart:s}/{settings.WeaponGrouping}/{settings.MultiClassGapMinutes}/{settings.KeepFitting}/{OptionsKey(options)}",
                $"shoot:{shoot.BeforeMinutes}/{shoot.DurationMinutes}/{shoot.AfterMinutes}" };
            fp.AddRange(regs.OrderBy(r => r.MemberId).ThenBy(r => r.MemberClass, StringComparer.Ordinal).Select(r => $"r:{r.MemberId}|{r.MemberClass}|{HpskSite.Models.StartPreference.Normalize(r.StartPreference)}|{r.JoinMemberId}"));   // önskemål och kompis hör till underlaget: ändras de efter förhandsvisningen ska Tillämpa säga ifrån
            fp.AddRange(patrols.OrderBy(p => p.Id).Select(p => $"p:{p.Id}|{p.PatrolNumber}|{p.StartTime:s}|{p.WeaponGroup}|{p.DepartedAt:s}"));
            fp.AddRange(members.OrderBy(m => m.Id).Select(m => $"m:{m.Id}|{m.PatrolId}|{m.MemberId}|{m.ShootingClass}|{m.Position}|{m.Locked}|{m.Status}"));
            fp.AddRange(jobs.OrderBy(k => k.Key).SelectMany(k => k.Value.Select(v => $"j:{k.Key}|{v.Start:s}|{v.End:s}|{v.AllDay}")));
            fp.Add("res:" + string.Join(",", reserves.OrderBy(x => x)));
            fp.Add("resm:" + string.Join(",", resultMembers.OrderBy(x => x)));
            fp.Add("stn:" + Hash(new[] { comp.StationConfig ?? "" }));
            fp.Add("early:" + string.Join(",", early.OrderBy(x => x)));

            return (new PatrolBasis
            {
                Regs = regs, Patrols = patrols, Members = members, Reserves = reserves, Fingerprint = fp, ResultMembers = resultMembers,
                Constraints = new PatrolConstraints { Jobs = jobs, Shoot = shoot, Early = early },
                Published = comp.FaltskyttePatrolsPublished, PublishedGroups = comp.FaltskyttePublishedGroups, StationConfigJson = comp.StationConfig,
                Started = patrols.Any(p => p.DepartedAt != null),
                Date = comp.CompetitionDate?.Date,
            }, null);
        }

        /// <summary>
        /// Planen, och hur många som behölls för att deras nuvarande start redan passar. Låsta ligger alltid kvar; med <see cref="PatrolPlanSettings.KeepFitting"/> gör
        /// de som har en start utan krock mot eget uppdrag det också — annars skulle en ombyggnad flytta nästan alla för att några få krockar.
        /// Reserver och de som ska gå tidigt (funktionärspatrull) flyttas aldrig av det här skälet.
        /// </summary>
        /// <summary>Flyttar, tar bort eller ändrar förslaget något i en publicerad grupp (en skytt vars patrull eller tid inte längre stämmer)?</summary>
        private static bool TouchesPublished(PatrolBasis b, PatrolPlan plan)
        {
            if (!b.Published) return false;
            string K(int id, string cls) => id + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(cls);
            var after = new Dictionary<string, DateTime?>();
            foreach (var p in plan.Patrols) foreach (var m in p.Members) after.TryAdd(K(m.MemberId, m.ShootingClass), p.StartTime);
            var membersByPatrol = b.Members.GroupBy(m => m.PatrolId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var p0 in b.Patrols.Where(b.PatrolPublished))
                foreach (var m in membersByPatrol.GetValueOrDefault(p0.Id) ?? new())
                    if (!after.TryGetValue(K(m.MemberId, m.ShootingClass), out var t) || t != p0.StartTime) return true;
            return false;
        }

        private static string OptionsKey(FaltPlanOptions? o) => o == null ? "gammal" :
            $"motor/{o.MinRest}/{o.WishRest}/{o.CloseMinutes}/{o.Priority}/{o.Order}/{o.Champion}/{o.GlideEvery}/{o.GlideMinutes}/{o.RoundExtraSeconds}/{o.UseExistingGroups}/{o.Iterations}/{o.PauseBlockMin}/{o.PauseExtMin}/{o.Day2}/{o.Open2Min}/{o.Close2Min}/{o.DayBreak}/{o.ArTogether}/{o.PreCount}/{o.PreStartMin}/{o.RoundMinutes}";

        private static (PatrolPlan Plan, int KeptFitting) PlanOf(PatrolBasis b, PatrolPlanSettings settings, FaltPlanOptions? options = null)
        {
            var patrolById = b.Patrols.ToDictionary(p => p.Id);
            // Platserna är fasta (9ccd668): den som stannar i sin patrull behåller sin plats. DNS-skyttar ligger kvar i sin patrull utan plats och flyttas aldrig.
            static bool Dns(FaltskyttePatrolMember m) => string.Equals(m.Status, "DNS", StringComparison.OrdinalIgnoreCase);
            LockedPlacement Place(FaltskyttePatrolMember m) =>
                new(m.MemberId, m.MemberName, m.ClubName, m.ShootingClass, patrolById[m.PatrolId].StartTime!.Value, patrolById[m.PatrolId].WeaponGroup, Dns(m) ? 0 : m.Position, Dns(m));
            var placed = b.Members.Where(m => patrolById.TryGetValue(m.PatrolId, out var p) && p.StartTime != null).ToList();
            var locked = placed.Where(m => m.Locked || Dns(m) || b.ResultMembers.Contains(m.MemberId) || (options != null && b.PatrolPublished(patrolById[m.PatrolId]))).Select(Place).ToList();   // publicerade grupper ligger fast (nya motorn)
            var fitting = settings.KeepFitting
                ? placed.Where(m => !m.Locked && !Dns(m) && !b.ResultMembers.Contains(m.MemberId) && !(options != null && b.PatrolPublished(patrolById[m.PatrolId])) && !b.Reserves.Contains(m.MemberId) && !b.Constraints.IsEarly(m.MemberId)
                        && !b.Constraints.Blocks(m.MemberId, patrolById[m.PatrolId].StartTime!.Value)).Select(Place).ToList()
                : new List<LockedPlacement>();
            if (options != null)
            {
                // Nya motorn avgör själv vilka som passar (vila, uppdrag, tak): hela dagens lista skickas in och bara de manuella låsen (och de med resultat) är fasta.
                options.StationConfigJson ??= b.StationConfigJson;
                var plan = FaltPatrolAdapter.PlanKeep(b.Regs, locked, placed.Select(Place).ToList(), b.Constraints, b.Reserves, settings, GroupsOf(b, settings), options,
                    (b.Date ?? settings.FirstStart?.Date ?? DateTime.Today).Date, out _, out var kept);
                return (plan, kept);
            }
            return (FaltskyttePatrolPlanner.Plan(b.Regs, locked.Concat(fitting).ToList(), b.Constraints, b.Reserves, settings, GroupsOf(b, settings)), fitting.Count);
        }

        /// <summary>
        /// Grupperna i dagens startlista, med var och ens egen första start, avstånd och patrullstorlek — så som de skapades (en körning per grupp, med egen första start).
        /// Tiderna per patrull är uppskattningar; det som gäller är gruppens första start. Ingen startlista än = inga grupper (då används inställningarna).
        /// </summary>
        private static List<PatrolGroupSpec> GroupsOf(PatrolBasis b, PatrolPlanSettings settings)
        {
            var result = new List<PatrolGroupSpec>();
            var membersByPatrol = b.Members.GroupBy(m => m.PatrolId).ToDictionary(g => g.Key, g => g.Count());
            foreach (var g in b.Patrols.Where(p => p.StartTime != null).GroupBy(p => p.WeaponGroup ?? ""))
            {
                var times = g.Select(p => p.StartTime!.Value).Distinct().OrderBy(t => t).ToList();
                var gaps = times.Zip(times.Skip(1), (a, c) => (int)(c - a).TotalMinutes).Where(x => x is >= 1 and <= 120).ToList();
                var interval = gaps.Count > 0 ? gaps.GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key : settings.IntervalMinutes;
                var size = Math.Clamp(g.Max(p => membersByPatrol.GetValueOrDefault(p.Id)), 1, 12);
                var codes = g.Key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(c => !string.Equals(c, "Alla", StringComparison.OrdinalIgnoreCase) && !string.Equals(c, "MixAll", StringComparison.OrdinalIgnoreCase)).ToList();
                result.Add(new PatrolGroupSpec(g.Key, codes, times[0], interval, size, g.Count()));
            }
            return result;
        }

        private static string Hash(IEnumerable<string> parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))[..24];

        /// <summary>
        /// Inställningarna för en körning. Det som inte anges tas ur dagens startlista när det finns en — största patrullen, vanligaste avståndet mellan
        /// starterna och första starten — så att en ombyggnad fortsätter på den takt som redan gäller. Finns ingen lista: 6 per patrull, 15 minuter, 09:00 tävlingsdagen.
        /// </summary>
        public PatrolPlanSettings Normalize(int competitionId, int patrolSize, int intervalMinutes, DateTime? firstStart, string? grouping, int gap, bool keepFitting = true)
        {
            var date = _competitions.GetSaved(competitionId)?.CompetitionDate?.Date ?? DateTime.Today;
            int? inferSize = null, inferInterval = null; DateTime? inferFirst = null;
            if (patrolSize <= 0 || intervalMinutes <= 0 || firstStart == null)
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                var rows = s.Database.Fetch<PatrolSizeRow>("SELECT p.StartTime, (SELECT COUNT(*) FROM FaltskyttePatrolMember m WHERE m.PatrolId = p.Id) AS Members FROM FaltskyttePatrol p WHERE p.CompetitionId = @0 AND p.StartTime IS NOT NULL", competitionId);
                if (rows.Count > 0)
                {
                    inferSize = Math.Clamp(rows.Max(r => r.Members), 1, 12);
                    inferFirst = rows.Min(r => r.StartTime);
                    var gaps = rows.Select(r => r.StartTime).Distinct().OrderBy(t => t).Zip(rows.Select(r => r.StartTime).Distinct().OrderBy(t => t).Skip(1), (a, c) => (int)(c - a).TotalMinutes)
                        .Where(g => g is >= 1 and <= 120).ToList();
                    if (gaps.Count > 0) inferInterval = gaps.GroupBy(g => g).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                }
            }
            return new PatrolPlanSettings
            {
                PatrolSize = patrolSize is >= 1 and <= 12 ? patrolSize : inferSize ?? 6,
                IntervalMinutes = intervalMinutes is >= 1 and <= 120 ? intervalMinutes : inferInterval ?? 15,
                FirstStart = firstStart ?? inferFirst ?? date.AddHours(9),
                WeaponGrouping = grouping is "MixAll" or "CombineAR" ? grouping : "Separate",
                MultiClassGapMinutes = gap is > 0 and <= 600 ? gap : 0,
                KeepFitting = keepFitting,
            };
        }

        private sealed class PatrolSizeRow
        {
            public DateTime StartTime { get; set; }
            public int Members { get; set; }
        }

        // ------------------------------------------------------------ förhandsvisning

        public async Task<PatrolPreview> PreviewAsync(StaffOwner owner, PatrolPlanSettings settings, FaltPlanOptions? options = null)
        {
            var (b, err) = await LoadAsync(owner, settings, options);
            if (b == null) return new PatrolPreview(new PatrolPlan(), "", err, 0, 0, 0, 0, 0, 0);
            return PreviewOf(b, settings, options);
        }

        /// <summary>
        /// Förhandsvisningen ur ett färdigt underlag. Den ENDA vägen från underlag till förslag: en riktig tävling (<c>LoadAsync</c>) och ett övningsscenario
        /// (<see cref="ScenarioBuilder"/>) går båda hit, så att övningen beter sig exakt som skarp data. Ren funktion, ingen databas.
        /// </summary>
        public static PatrolPreview PreviewOf(PatrolBasis b, PatrolPlanSettings settings, FaltPlanOptions? options)
        {
            var (plan, keptFitting) = PlanOf(b, settings, options);

            // Vad ändras? Jämför per (skytt, klass) före och efter.
            var patrolById = b.Patrols.ToDictionary(p => p.Id);
            string K(int id, string cls) => id + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(cls);
            var beforeRows = b.Members.Where(m => patrolById.ContainsKey(m.PatrolId)).GroupBy(m => K(m.MemberId, m.ShootingClass))
                .ToDictionary(g => g.Key, g => (M: g.First(), P: patrolById[g.First().PatrolId]));
            var afterRows = plan.Patrols.SelectMany(p => p.Members.Select(m => (Key: K(m.MemberId, m.ShootingClass), M: m, P: p))).GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => (g.First().M, g.First().P));
            var before = beforeRows.ToDictionary(kv => kv.Key, kv => kv.Value.P.StartTime);
            var after = afterRows.ToDictionary(kv => kv.Key, kv => kv.Value.P.StartTime);
            var unchanged = after.Count(kv => before.TryGetValue(kv.Key, out var t) && t == kv.Value);
            var moved = after.Count(kv => before.TryGetValue(kv.Key, out var t) && t != kv.Value);
            var added = after.Count(kv => !before.ContainsKey(kv.Key));
            var removed = before.Count(kv => !after.ContainsKey(kv.Key));

            // Vilka, från vad till vad, och varför — så att arrangören ser exakt vem som berörs innan något skrivs.
            string Why(int memberId, DateTime? oldStart)
            {
                if (oldStart != null && b.Constraints.Jobs.TryGetValue(memberId, out var js))
                {
                    var win = b.Constraints.Shoot.Window(oldStart.Value, null);
                    foreach (var j in js)
                        if (j.Overlaps(win))
                            return j.AllDay ? "har ett uppdrag hela dagen" : $"har ett uppdrag {j.Start:HH\\:mm}–{j.End:HH\\:mm} som krockar med starten {oldStart:HH\\:mm}";
                }
                if (b.Reserves.Contains(memberId)) return "är reserv och får ingen fast start";
                if (b.Constraints.IsEarly(memberId)) return "ska gå tidigt (funktionärspatrull eller skjuter först)";
                return settings.KeepFitting ? "flyttas för att få plats" : "startlistan byggs om från noll";
            }
            var moves = new List<PatrolMove>();
            foreach (var kv in afterRows.OrderBy(k => k.Value.P.StartTime).ThenBy(k => k.Value.M.Name, StringComparer.Ordinal))
            {
                if (beforeRows.TryGetValue(kv.Key, out var old))
                {
                    if (old.P.StartTime != kv.Value.P.StartTime)
                        moves.Add(new PatrolMove("moved", kv.Value.M.MemberId, kv.Value.M.Name, kv.Value.M.ShootingClass, old.P.PatrolNumber, old.P.StartTime, kv.Value.P.PatrolNumber, kv.Value.P.StartTime, Why(kv.Value.M.MemberId, old.P.StartTime)));
                }
                else moves.Add(new PatrolMove("added", kv.Value.M.MemberId, kv.Value.M.Name, kv.Value.M.ShootingClass, null, null, kv.Value.P.PatrolNumber, kv.Value.P.StartTime, "ny anmälan"));
            }
            foreach (var kv in beforeRows.Where(k => !afterRows.ContainsKey(k.Key)).OrderBy(k => k.Value.P.StartTime))
                moves.Add(new PatrolMove("removed", kv.Value.M.MemberId, kv.Value.M.MemberName, kv.Value.M.ShootingClass, kv.Value.P.PatrolNumber, kv.Value.P.StartTime, null, null,
                    b.Reserves.Contains(kv.Value.M.MemberId) ? "är reserv och får ingen fast start" : "är inte anmäld längre"));

            string? blocked = null;
            if (options == null ? b.Published : TouchesPublished(b, plan)) blocked = "Startlistan är publicerad. Avpublicera den först, eller tvinga fram ombyggnaden.";
            else if (b.Started) blocked = "Någon patrull har redan gått ut. Startlistan bör inte byggas om nu.";
            var current = b.Members.Where(m => patrolById.ContainsKey(m.PatrolId))
                .Select(m => new CurrentPlacement(m.Id, m.MemberId, m.MemberName, m.ShootingClass, patrolById[m.PatrolId].PatrolNumber, patrolById[m.PatrolId].StartTime, m.Locked))
                .OrderBy(c => c.PatrolNumber).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            return new PatrolPreview(plan, Hash(b.Fingerprint), blocked, b.Patrols.Count, b.Members.Count, moved, unchanged, added, removed, current, keptFitting, moves, GroupsOf(b, settings));
        }

        // ------------------------------------------------------------ tillämpa

        public async Task<PatrolApplyOutcome> ApplyAsync(StaffOwner owner, PatrolPlanSettings settings, string? fingerprint, bool force, int byMemberId, FaltPlanOptions? options = null)
        {
            // Samma lås som förslagen (per tävling): ingen annan skriver bemanning medan startlistan byggs om, och tvärtom.
            await using var lease = await _lock.TryAcquireAsync(StaffProposalService.LockName(owner), TimeSpan.FromSeconds(10));
            if (lease == null) return new PatrolApplyOutcome(false, StaffProposalService.BusyMessage, 0, 0, 0);
            // Samma lås som alla andra som ändrar patruller (efteranmälan, byt plats, walk-in ...): ingen skriver mellan läsning och skrivning.
            await using var patrolLease = await _lock.TryAcquireAsync(HpskSite.CompetitionTypes.Faltskytte.Services.FaltskyttePatrolWrites.LockName(owner.Comp), TimeSpan.FromSeconds(10));
            if (patrolLease == null) return new PatrolApplyOutcome(false, HpskSite.CompetitionTypes.Faltskytte.Services.FaltskyttePatrolWrites.BusyMessage, 0, 0, 0);
            var (b, err) = await LoadAsync(owner, settings, options);
            if (b == null) return new PatrolApplyOutcome(false, err, 0, 0, 0);
            if (!string.Equals(Hash(b.Fingerprint), fingerprint, StringComparison.Ordinal))
                return new PatrolApplyOutcome(false, "Anmälningarna, startlistan eller uppdragen har ändrats sedan förhandsvisningen. Gör en ny — inget ändrades.", 0, 0, 0);
            var (plan, _) = PlanOf(b, settings, options);
            // Publicerad lista: med den gamla vägen gäller hela listan; med nya motorn bara om förslaget rör en publicerad grupp (publicerade grupper ligger fast, övriga byggs).
            var publishedHit = options == null ? b.Published : TouchesPublished(b, plan);
            if ((publishedHit || b.Started) && !force)
                return new PatrolApplyOutcome(false, publishedHit ? "Startlistan är publicerad. Inget ändrades." : "En patrull har redan gått ut. Inget ändrades.", 0, 0, 0);

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
                // Kontrollerna görs HÄR, inne i transaktionen med UPDLOCK/HOLDLOCK, så att ingen hinner skriva mellan kontroll och skrivning.
                var nowPatrols = db.Fetch<FaltskyttePatrol>("SELECT * FROM FaltskyttePatrol WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0 ORDER BY PatrolNumber", owner.Comp);
                var nowMembers = db.Fetch<FaltskyttePatrolMember>("SELECT m.* FROM FaltskyttePatrolMember m WITH (UPDLOCK, HOLDLOCK) JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE p.CompetitionId = @0", owner.Comp);
                if (StateHash(nowPatrols, nowMembers) != StateHash(b.Patrols, b.Members))
                    return new PatrolApplyOutcome(false, "Startlistan ändrades medan förslaget räknades. Gör en ny förhandsvisning — inget ändrades.", 0, 0, 0);
                var resultRows = db.ExecuteScalar<int>("SELECT COUNT(*) FROM FaltskytteResultEntry WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0", owner.Comp);
                // Inga spärrar är absoluta: resultat och publicerad lista kan alltid tvingas fram, men bara uttryckligen, och det loggas.
                if (resultRows > 0 && !force)
                    return new PatrolApplyOutcome(false, $"Det finns redan resultat ({resultRows} inmatade rader). Ett nytt förslag kan flytta skyttar som redan har skjutit. Inget ändrades.", 0, 0, 0);
                if (force && (b.Published || b.Started || resultRows > 0))
                    _logger.LogWarning("Startlistan byggs om med force i tävling {Comp}: publicerad {Pub}, startad {Started}, resultatrader {Rows}, av medlem {By}", owner.Comp, b.Published, b.Started, resultRows, byMemberId);

                backupId = Convert.ToInt32(db.ExecuteScalar<int>(
                    "INSERT INTO FaltskyttePatrolBackup (CompetitionId, CreatedUtc, CreatedBy, Json) OUTPUT INSERTED.Id VALUES (@0, @1, @2, @3)",
                    owner.Comp, DateTime.UtcNow, byMemberId, JsonConvert.SerializeObject(backup)));
                WritePlan(db, owner.Comp, plan, nowPatrols, nowMembers, lockedKeys, settings.PatrolSize, keepNumbers: b.Published || b.ResultMembers.Count > 0 || resultRows > 0);
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
            using var lease = _lock.TryAcquire(StaffProposalService.LockName(owner), TimeSpan.FromSeconds(10));
            if (lease == null) return (false, StaffProposalService.BusyMessage);
            using var patrolLease = _lock.TryAcquire(HpskSite.CompetitionTypes.Faltskytte.Services.FaltskyttePatrolWrites.LockName(owner.Comp), TimeSpan.FromSeconds(10));
            if (patrolLease == null) return (false, HpskSite.CompetitionTypes.Faltskytte.Services.FaltskyttePatrolWrites.BusyMessage);
            using var scope = _scopes.CreateScope();
            var db = scope.Database;
            var row = db.FirstOrDefault<PatrolBackupRow>("SELECT * FROM FaltskyttePatrolBackup WHERE Id = @0 AND CompetitionId = @1", backupId, owner.Comp);
            if (row == null) return (false, "Säkerhetskopian finns inte.");
            if (row.RestoredUtc != null) return (false, "Den är redan återställd.");
            var doc = JsonConvert.DeserializeObject<PatrolBackupDoc>(row.Json);
            if (doc == null) return (false, "Säkerhetskopian går inte att läsa.");

            var nowPatrols = db.Fetch<FaltskyttePatrol>("SELECT * FROM FaltskyttePatrol WHERE CompetitionId = @0 ORDER BY PatrolNumber", owner.Comp);
            var nowMembers = db.Fetch<FaltskyttePatrolMember>("SELECT m.* FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE p.CompetitionId = @0 ORDER BY m.Position", owner.Comp);
            var nowHash = AfterHash(nowPatrols.OrderBy(p => p.PatrolNumber).Select(p => (p.PatrolNumber, p.StartTime, p.WeaponGroup,
                nowMembers.Where(m => m.PatrolId == p.Id).OrderBy(m => m.Position).Select(m => (m.MemberId, m.ShootingClass)).ToList())));
            if (nowHash != doc.AfterHash && !force)
                return (false, "Startlistan har ändrats sedan ombyggnaden. Att återställa nu tar bort de ändringarna.");

            var resultRows = db.ExecuteScalar<int>("SELECT COUNT(*) FROM FaltskytteResultEntry WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0", owner.Comp);
            var published = _competitions.GetSaved(owner.Comp)?.FaltskyttePatrolsPublished == true;
            if ((resultRows > 0 || published || nowPatrols.Any(p => p.DepartedAt != null)) && !force)
                return (false, resultRows > 0
                    ? $"Det finns resultat ({resultRows} rader). Att återställa nu kan flytta skyttar som redan har skjutit. Inget ändrades."
                    : "Startlistan är publicerad eller en patrull har gått ut. Inget ändrades.");
            if (force) _logger.LogWarning("Startlistan återställs med force i tävling {Comp}: publicerad {Pub}, resultatrader {Rows}", owner.Comp, published, resultRows);

            // Återställer med SAMMA id: patruller och medlemmar som finns kvar uppdateras, borttagna läggs tillbaka med sitt gamla id, nya (efter ombyggnaden) tas bort.
            db.Execute("UPDATE FaltskyttePatrol SET PatrolNumber = PatrolNumber + 100000 WHERE CompetitionId = @0", owner.Comp);   // parkerar numren (unik nyckel)
            var backupPatrolIds = doc.Patrols.Select(bp => bp.Patrol.Id).ToHashSet();
            var backupMemberIds = doc.Patrols.SelectMany(bp => bp.Members.Select(m => m.Id)).ToHashSet();
            foreach (var m in nowMembers.Where(m => !backupMemberIds.Contains(m.Id))) db.Execute("DELETE FROM FaltskyttePatrolMember WHERE Id = @0", m.Id);
            var nowPatrolIds = nowPatrols.Select(p => p.Id).ToHashSet();
            foreach (var bp in doc.Patrols)
            {
                var p = bp.Patrol;
                if (nowPatrolIds.Contains(p.Id))
                    db.Execute("UPDATE FaltskyttePatrol SET PatrolNumber = @1, StartTime = @2, WeaponGroup = @3, Label = @4, Held = @5, CurrentStation = @6, DepartedAt = @7, Capacity = @8, StartTimeChangedUtc = @9 WHERE Id = @0",
                        p.Id, p.PatrolNumber, p.StartTime, p.WeaponGroup, p.Label, p.Held, p.CurrentStation, p.DepartedAt, p.Capacity, p.StartTimeChangedUtc);
                else
                    db.Execute("SET IDENTITY_INSERT FaltskyttePatrol ON; INSERT INTO FaltskyttePatrol (Id, CompetitionId, PatrolNumber, StartTime, WeaponGroup, CurrentStation, Label, DepartedAt, Held, Capacity, StartTimeChangedUtc) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10); SET IDENTITY_INSERT FaltskyttePatrol OFF;",
                        p.Id, owner.Comp, p.PatrolNumber, p.StartTime, p.WeaponGroup, p.CurrentStation, p.Label, p.DepartedAt, p.Held, p.Capacity, p.StartTimeChangedUtc);
            }
            var nowMemberIds = nowMembers.Select(m => m.Id).ToHashSet();
            foreach (var bp in doc.Patrols)
                foreach (var m in bp.Members)
                {
                    if (nowMemberIds.Contains(m.Id))
                        db.Execute("UPDATE FaltskyttePatrolMember SET PatrolId = @1, MemberId = @2, Position = @3, ShootingClass = @4, MemberName = @5, ClubName = @6, Status = @7, Locked = @8 WHERE Id = @0",
                            m.Id, bp.Patrol.Id, m.MemberId, m.Position, m.ShootingClass, m.MemberName, m.ClubName, m.Status, m.Locked);
                    else
                        db.Execute("SET IDENTITY_INSERT FaltskyttePatrolMember ON; INSERT INTO FaltskyttePatrolMember (Id, PatrolId, MemberId, Position, ShootingClass, MemberName, ClubName, Status, Locked) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8); SET IDENTITY_INSERT FaltskyttePatrolMember OFF;",
                            m.Id, bp.Patrol.Id, m.MemberId, m.Position, m.ShootingClass, m.MemberName, m.ClubName, m.Status, m.Locked);
                }
            foreach (var p in nowPatrols.Where(p => !backupPatrolIds.Contains(p.Id))) db.Execute("DELETE FROM FaltskyttePatrol WHERE Id = @0", p.Id);
            db.Execute("UPDATE FaltskyttePatrolBackup SET RestoredUtc = @1 WHERE Id = @0", backupId, DateTime.UtcNow);
            scope.Complete();
            return (true, null);
        }

        /// <summary>Senaste ombyggnaden som går att ångra (inte redan återställd), eller 0.</summary>
        public int LatestUndoable(StaffOwner owner)
        {
            if (!owner.HasCompetition) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int?>("SELECT TOP 1 Id FROM FaltskyttePatrolBackup WHERE CompetitionId = @0 AND RestoredUtc IS NULL ORDER BY Id DESC", owner.Comp) ?? 0;
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

        // ------------------------------------------------------------ skrivning

        private static string StateHash(IEnumerable<FaltskyttePatrol> ps, IEnumerable<FaltskyttePatrolMember> ms) =>
            Hash(ps.OrderBy(p => p.Id).Select(p => $"p:{p.Id}|{p.PatrolNumber}|{p.StartTime:s}|{p.WeaponGroup}|{p.DepartedAt:s}|{p.Held}|{p.Label}")
                .Concat(ms.OrderBy(m => m.Id).Select(m => $"m:{m.Id}|{m.PatrolId}|{m.MemberId}|{m.ShootingClass}|{m.Position}|{m.Locked}|{m.Status}")));

        /// <summary>
        /// Skriver planen med UPDATE där det går: en patrull som finns kvar behåller sitt id (och därmed Label, Held, CurrentStation, DepartedAt, kalender-UID och
        /// påminnelser), en skytt som finns kvar behåller sitt medlemsid. Nya patruller och skyttar läggs till; bara det som inte längre finns i planen tas bort.
        /// En patrull matchas mot den gamla med flest gemensamma skyttar (annars samma nummer och tid).
        /// </summary>
        private static void WritePlan(IDatabase db, int comp, PatrolPlan plan, List<FaltskyttePatrol> oldPatrols, List<FaltskyttePatrolMember> oldMembers, HashSet<string> lockedKeys, int capacity, bool keepNumbers = false)
        {
            static string Key(int memberId, string cls) => memberId + "|" + HpskSite.Models.ShootingClasses.ToCanonicalName(cls);
            var oldKeysByPatrol = oldMembers.GroupBy(m => m.PatrolId).ToDictionary(g => g.Key, g => g.Select(m => Key(m.MemberId, m.ShootingClass)).ToHashSet());
            var free = oldPatrols.ToList();
            var matched = new FaltskyttePatrol?[plan.Patrols.Count];
            for (var i = 0; i < plan.Patrols.Count; i++)
            {
                var pp = plan.Patrols[i];
                var keys = pp.Members.Select(m => Key(m.MemberId, m.ShootingClass)).ToHashSet();
                var best = free.Select(o => new { o, Overlap = oldKeysByPatrol.TryGetValue(o.Id, out var s) ? s.Count(keys.Contains) : 0 })
                    .Where(x => x.Overlap > 0 || (x.o.PatrolNumber == pp.PatrolNumber && x.o.StartTime == pp.StartTime))
                    .OrderByDescending(x => x.Overlap).ThenBy(x => x.o.StartTime == pp.StartTime ? 0 : 1).ThenBy(x => x.o.PatrolNumber == pp.PatrolNumber ? 0 : 1)
                    .FirstOrDefault();
                if (best != null) { matched[i] = best.o; free.Remove(best.o); }
            }
            // Beslut 1 (Stefan 2026-10-30): efter publicering/resultat BEHÅLLS numren; nya patruller får nästa lediga. Annars gäller planens nummer (tidsordning).
            var finalNumbers = plan.Patrols.Select(pp => pp.PatrolNumber).ToArray();
            if (keepNumbers)
            {
                var used = new HashSet<int>(); var next = Math.Max(oldPatrols.Count == 0 ? 0 : oldPatrols.Max(o => o.PatrolNumber), plan.Patrols.Count == 0 ? 0 : plan.Patrols.Max(pp => pp.PatrolNumber)) + 1;
                for (var i = 0; i < plan.Patrols.Count; i++) if (matched[i] is { } mo && used.Add(mo.PatrolNumber)) finalNumbers[i] = mo.PatrolNumber;
                for (var i = 0; i < plan.Patrols.Count; i++) if (!(matched[i] is { } m2 && finalNumbers[i] == m2.PatrolNumber)) { while (used.Contains(next)) next++; finalNumbers[i] = next; used.Add(next); }
            }
            // Numren parkeras först (unik nyckel på tävling + nummer), sedan sätts de slutliga.
            db.Execute("UPDATE FaltskyttePatrol SET PatrolNumber = PatrolNumber + 100000 WHERE CompetitionId = @0", comp);
            var patrolIds = new int[plan.Patrols.Count];
            for (var i = 0; i < plan.Patrols.Count; i++)
            {
                var pp = plan.Patrols[i];
                if (matched[i] is { } o)
                {
                    db.Execute("UPDATE FaltskyttePatrol SET PatrolNumber = @1, StartTime = @2, WeaponGroup = @3, Capacity = @4, StartTimeChangedUtc = CASE WHEN StartTime IS NULL AND @2 IS NULL OR StartTime = @2 THEN StartTimeChangedUtc ELSE @5 END WHERE Id = @0", o.Id, finalNumbers[i], pp.StartTime, pp.WeaponGroup, capacity, DateTime.UtcNow);
                    patrolIds[i] = o.Id;
                }
                else
                {
                    var row = new FaltskyttePatrol { CompetitionId = comp, PatrolNumber = finalNumbers[i], StartTime = pp.StartTime, WeaponGroup = pp.WeaponGroup, Capacity = capacity };
                    db.Insert(row);
                    patrolIds[i] = row.Id;
                }
            }
            // Skyttar: de som finns kvar flyttas (samma id), nya läggs till, de som inte finns i planen tas bort.
            var planKeys = plan.Patrols.SelectMany(p => p.Members.Select(m => Key(m.MemberId, m.ShootingClass))).ToHashSet();
            var oldByKey = new Dictionary<string, FaltskyttePatrolMember>();
            foreach (var m in oldMembers)
            {
                var k = Key(m.MemberId, m.ShootingClass);
                if (planKeys.Contains(k) && !oldByKey.ContainsKey(k)) oldByKey[k] = m;
                else db.Execute("DELETE FROM FaltskyttePatrolMember WHERE Id = @0", m.Id);
            }
            for (var i = 0; i < plan.Patrols.Count; i++)
                foreach (var m in plan.Patrols[i].Members)
                {
                    var k = Key(m.MemberId, m.ShootingClass);
                    var locked = lockedKeys.Contains(k);
                    var status = m.IsDns ? "DNS" : null;
                    if (oldByKey.TryGetValue(k, out var om))
                        db.Execute("UPDATE FaltskyttePatrolMember SET PatrolId = @1, Position = @2, ShootingClass = @3, MemberName = @4, ClubName = @5, Status = @6, Locked = @7 WHERE Id = @0",
                            om.Id, patrolIds[i], m.Position, m.ShootingClass, m.Name, m.Club, status, locked);
                    else
                        db.Insert(new FaltskyttePatrolMember { PatrolId = patrolIds[i], MemberId = m.MemberId, Position = m.Position, ShootingClass = m.ShootingClass, MemberName = m.Name, ClubName = m.Club, Status = status, Locked = locked });
                }
            foreach (var o in free) db.Execute("DELETE FROM FaltskyttePatrol WHERE Id = @0", o.Id);
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
            public DateTime CreatedUtc { get; set; }
            public int CreatedBy { get; set; }
            public string Json { get; set; } = "";
            public DateTime? RestoredUtc { get; set; }
        }
    }
}
