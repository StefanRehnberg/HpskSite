using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>En start som visas vid en kandidat ("Klass B1 · patrull 12 · 10:50").</summary>
    public sealed record CandidateStart(string Title, string Where, DateTime Start, DateTime? End);

    /// <summary>
    /// En möjlig person för en plats: vem hen är, om hen anmält sig som funktionär, när hen själv skjuter, och om hen är ledig just den
    /// tiden. <see cref="Free"/> null = ingen tid angiven än. <see cref="Conflict"/> är det första hindret (hårt eller en varning).
    /// </summary>
    public sealed record StaffCandidate(int MemberId, string Name, string? Club, string? ShooterClass, IReadOnlyList<CandidateStart> Starts,
        bool IsShooter, bool Volunteer, bool RoleMatch, string? Comment, bool? Free, SlotConflict? Conflict, int AssignedToday,
        bool AlreadyHere, CandidateTier Tier, string Mode = ShooterMode.Coordinated, IReadOnlyList<string>? Placements = null);

    public sealed class CandidateQuery
    {
        public StaffOwner Owner { get; init; } = null!;
        public string? RoleKey { get; init; }
        /// <summary>Platsen: vilken dag och vilken tid. Null dag = ingen dag vald (då räknas ingen ledighet).</summary>
        public DateTime? Date { get; init; }
        public TimeSpan? From { get; init; }
        public TimeSpan? To { get; init; }
        /// <summary>Uppdragets plats (station) — så att "redan här" kan se samma roll och plats.</summary>
        public string? ScopeKey { get; init; }
        public string? Search { get; init; }
        /// <summary>Bara de här personerna (kontrollen i "lägg in flera"). Null = alla i poolen.</summary>
        public IReadOnlyCollection<int>? OnlyMembers { get; init; }
        /// <summary>Bara de som anmält sig som funktionärer den dagen.</summary>
        public bool OnlyVolunteers { get; init; }
        public int Take { get; init; } = 40;
    }

    public sealed record CandidateResult(List<StaffCandidate> Candidates, int Total, int Shooters, int Volunteers, int VolunteersWithoutAssignment);

    /// <summary>
    /// Planera dagen steg 1 (Expertgruppen, Stefans svar: "startlistan tillsammans med funktionärsanmälan till tävlingen ska vara källan"):
    /// <b>kandidatlistan</b>. Poolen är de som står i tävlingens anmälningar eller har anmält sig som funktionärer i planeringen. Varje kandidat bär
    /// sina starter (<see cref="StartWindowReader"/>), sin anmälan (dag, roll, tider) och om hen är ledig på den angivna tiden
    /// (<see cref="StaffingRules"/>). Rangordningen är <see cref="CandidateRules"/>. Ren läsning — ingenting skrivs.
    /// </summary>
    public class StaffCandidateService
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly StartWindowReader _starts;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly HpskSite.Services.ClubService _clubs;
        private readonly ILogger<StaffCandidateService> _logger;

        public StaffCandidateService(IScopeProvider scopes, PlanningService planning, StartWindowReader starts, StaffOwnerResolver owners,
            RoleCatalogService roles, HpskSite.Services.ClubService clubs, ILogger<StaffCandidateService> logger, StaffBusyReader busy)
        {
            _scopes = scopes; _planning = planning; _starts = starts; _owners = owners; _roles = roles; _clubs = clubs; _logger = logger; _busy = busy;
        }

        private readonly StaffBusyReader _busy;

        private sealed class RegRow { public int MemberId { get; set; } public string? MemberName { get; set; } public int ClubId { get; set; } public string? ClassKey { get; set; } }

        public CandidateResult Find(CandidateQuery q)
        {
            var owner = q.Owner;
            var ids = _planning.Family(owner.PlanningId).Select(f => f.Id).ToList();
            var idList = ids.Count > 0 ? string.Join(",", ids) : "0";
            var shoot = _owners.ShootTime(owner.PlanningId);

            List<RegRow> regs = new(); List<StaffHelpSignup> signups = new(); List<StaffAssignment> assigns = new(); List<StaffPass> passes = new();
            HashSet<int> slotIds = new();
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                if (owner.HasCompetition)
                    regs = db.Fetch<RegRow>(@"SELECT r.MemberId, r.MemberName, r.ClubId, c.ClassKey FROM CompetitionRegistration r
                        LEFT JOIN CompetitionRegistrationClass c ON c.RegistrationId = r.Id WHERE r.CompetitionId = @0 AND r.IsActive = 1 AND r.MemberId > 0", owner.Comp);
                if (ids.Count > 0)
                {
                    signups = db.Fetch<StaffHelpSignup>($"SELECT * FROM StaffHelpSignup WHERE PlanningId IN ({idList}) AND MemberId > 0");
                    assigns = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE PlanningId IN ({idList}) AND MemberId > 0");
                    passes = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({idList})");
                    if (q.Date != null)
                        slotIds = db.Fetch<int>($"SELECT Id FROM StaffHelpSlot WHERE PlanningId IN ({idList}) AND CAST(SlotDate AS date) = @0", q.Date.Value.Date).ToHashSet();
                }
            }
            var passById = passes.ToDictionary(p => p.Id);
            // Planera dagen: hur personen förhåller sig till startlistan — planeringens förval, med personens eget undantag över.
            var planningRow = _owners.Planning(owner.PlanningId);
            var overrides = _planning.ShooterModeOverrides(owner.PlanningId);

            // Starterna över ALLA tävlingar familjens planeringar bemannar (en funktionär kan skjuta i systertävlingen) — samma läsning som förslagen.
            var startsByMember = _busy.StartsByMember(owner);
            var regByMember = regs.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.ToList());
            var signupByMember = signups.GroupBy(x => x.MemberId).ToDictionary(g => g.Key, g => g.ToList());

            var slot = q.Date != null && q.From != null && q.To != null ? StaffInterval.From(q.Date.Value, q.From, q.To) : null;
            var discipline = _owners.Discipline(owner);
            string RoleLabel(string key) => _roles.NameFor(owner, discipline, key);

            var members = regByMember.Keys.Union(signupByMember.Keys).Where(m => q.OnlyMembers == null || q.OnlyMembers.Contains(m)).ToList();
            var result = new List<StaffCandidate>();
            foreach (var mid in members)
            {
                var name = regByMember.TryGetValue(mid, out var rr) && !string.IsNullOrWhiteSpace(rr[0].MemberName) ? rr[0].MemberName!
                    : signupByMember.TryGetValue(mid, out var ss) ? ss.OrderByDescending(x => x.ModifiedDate).First().MemberName : $"Medlem {mid}";
                if (!string.IsNullOrWhiteSpace(q.Search) && name.IndexOf(q.Search.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;

                // Anmälan: dag, roll och tider.
                VolunteerFit fit = new(false, false, Array.Empty<(TimeSpan, TimeSpan)>());
                string? comment = null;
                if (signupByMember.TryGetValue(mid, out var sg))
                {
                    comment = sg.Select(x => x.Comment).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    var choices = sg.SelectMany(x => ParseChoices(x.SlotsJson)).ToList();
                    fit = q.Date != null ? VolunteerRules.Fit(choices, q.Date.Value, q.RoleKey, slotIds)
                        : new VolunteerFit(choices.Count > 0, false, Array.Empty<(TimeSpan, TimeSpan)>());
                }
                if (q.OnlyVolunteers && !fit.Volunteers) continue;

                var myStarts = (startsByMember.GetValueOrDefault(mid) ?? new List<StartWindow>())
                    .Where(w => q.Date == null || w.Start.Date == q.Date.Value.Date)
                    .Select(w => new CandidateStart(w.Title, w.Where, w.Start, w.End)).ToList();

                // Mina uppdrag den dagen (inte avböjda) och om hen redan står här.
                var mineToday = new List<(StaffAssignment A, DateTime? Date, StaffInterval? Iv)>();
                foreach (var a in assigns.Where(a => a.MemberId == mid && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)))
                {
                    var (d, iv) = FamilyWeekService.WhenOf(a, a.PassId is > 0 && passById.TryGetValue(a.PassId.Value, out var p) ? p : null);
                    if (q.Date != null && d?.Date != q.Date.Value.Date) continue;
                    mineToday.Add((a, d, iv));
                }
                var alreadyHere = q.RoleKey != null && mineToday.Any(m => string.Equals(m.A.RoleKey, q.RoleKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.A.ScopeKey ?? "", q.ScopeKey ?? "", StringComparison.OrdinalIgnoreCase));

                // Var personen redan är placerad den dagen ("Stationsmarkör Station 2 09:00–12:00"), så att listan visar vilka som är tagna och var.
                var placements = mineToday.OrderBy(m => m.Iv?.Start ?? DateTime.MinValue).Select(m =>
                    $"{RoleLabel(m.A.RoleKey)}{(string.IsNullOrEmpty(m.A.ScopeKey) ? "" : $" {m.A.ScopeType} {m.A.ScopeKey}".TrimEnd())} {(m.Iv is { AllDay: false } v ? $"{v.Start:HH\\:mm}–{v.End:HH\\:mm}" : "hela dagen")}").ToList();

                // Ledig? Egna starter (med förberedelse och vila), andra uppdrag och tiderna personen anmält sig till.
                bool? free = null; SlotConflict? conflict = null;
                var mode = ShooterModeRules.Effective(planningRow?.ShooterMode, overrides.GetValueOrDefault(mid));
                if (slot is { } sl)
                {
                    var busy = new List<BusyItem>();
                    // Alla lägen räknar en start som finns: en reserv som tagit en lucka står i startlistan, och den starten gäller.
                    foreach (var w in startsByMember.GetValueOrDefault(mid) ?? new List<StartWindow>())
                    {
                        var win = shoot.Window(w.Start, w.End);
                        // Fönstret säger varför en uppdragstid krockar: förberedelsen före och vilan efter räknas in, inte bara starttiden.
                        busy.Add(new BusyItem(BusyKind.Start, win,
                            $"egen start {w.Start:HH\\:mm} ({w.Title.ToLowerInvariant()}, {w.Where.Split(" · ")[0].ToLowerInvariant()}) — upptagen {win.Start:HH\\:mm}–{win.End:HH\\:mm}"));
                    }
                    foreach (var m in mineToday.Where(m => m.Iv is { } && !(alreadyHere && string.Equals(m.A.RoleKey, q.RoleKey, StringComparison.OrdinalIgnoreCase) && string.Equals(m.A.ScopeKey ?? "", q.ScopeKey ?? "", StringComparison.OrdinalIgnoreCase))))
                    {
                        var iv = m.Iv!.Value;
                        busy.Add(new BusyItem(BusyKind.Assignment, iv, $"{RoleLabel(m.A.RoleKey)} {(iv.AllDay ? "hela dagen" : $"{iv.Start:HH\\:mm}–{iv.End:HH\\:mm}")}"));
                    }
                    if (fit.Volunteers) busy.AddRange(StaffingRules.OutsideAvailability(q.Date!.Value, fit.Windows));
                    conflict = StaffingRules.FirstConflict(sl, null, busy);
                    free = conflict is not { IsHard: true };
                }

                var classes = regByMember.TryGetValue(mid, out var rl) ? string.Join(", ", rl.Select(x => x.ClassKey).Where(c => !string.IsNullOrEmpty(c)).Distinct().Select(c => HpskSite.Models.ShootingClasses.DisplayName(c))) : null;
                var clubId = rr?.FirstOrDefault(x => x.ClubId > 0)?.ClubId ?? 0;
                string? club = null;
                if (clubId > 0) { try { club = _clubs.GetClubNameById(clubId); } catch { /* utan förening */ } }
                var tier = CandidateRules.Tier(fit.Volunteers, fit.RoleMatch, free);
                result.Add(new StaffCandidate(mid, name, club, string.IsNullOrWhiteSpace(classes) ? null : classes, myStarts, regByMember.ContainsKey(mid), fit.Volunteers,
                    fit.RoleMatch, comment, free, conflict, mineToday.Count, alreadyHere, tier, mode, placements));
            }

            var ranked = CandidateRules.Rank(result, c => c.Tier, c => c.Name, c => c.MemberId);
            var assignedMembers = assigns.Where(a => !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)).Select(a => a.MemberId ?? 0).ToHashSet();
            var volunteersAll = signupByMember.Keys.ToList();
            return new CandidateResult(ranked.Take(Math.Max(1, q.Take)).ToList(), ranked.Count, regByMember.Count, volunteersAll.Count,
                volunteersAll.Count(m => !assignedMembers.Contains(m)));
        }

        private static List<HelpSlotChoice> ParseChoices(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonConvert.DeserializeObject<List<HelpSlotChoice>>(json) ?? new(); }
            catch { return new(); }
        }
    }
}
