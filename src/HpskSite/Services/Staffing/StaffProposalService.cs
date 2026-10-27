using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <param name="TimedSlots">Luckor som är block (Fördela): raden skrivs med explicita tider, inte med pass eller heldag.</param>
    public sealed record Proposal(List<SolverSlot> Slots, List<SolverPerson> People, SolverResult Result, string Fingerprint, Dictionary<string, int?> PassOfSlot,
        List<string> Problems, HashSet<string>? TimedSlots = null);

    public sealed record ApplyOutcome(bool Ok, string? Message, Guid? BatchId, List<(int MemberId, string Name, int Id)> Added, List<(int MemberId, string Name, string Reason)> Skipped);

    /// <summary>
    /// Planera dagen steg 3: <b>Fyll med förslag</b>. Läser behoven (luckorna), de anmälda funktionärerna, deras starter och uppdrag, låter
    /// <see cref="StaffingSolver"/> räkna ut ett förslag, och — först när arrangören trycker Tillämpa — skriver det som Planerade rader i EN transaktion.
    /// <list type="bullet">
    /// <item><b>Förslaget lagras inte.</b> Det räknas om vid varje anrop; ingen funktionär kan se ett utkast, för det finns inget att se förrän det tillämpats.</item>
    /// <item><b>Fingeravtryck:</b> Tillämpa skickar med fingeravtrycket av underlaget (uppdrag, anmälningar, starter, behov, skjuttid). Har något ändrats
    /// sedan förslaget gjordes avvisas allt — hellre ett nytt förslag än en plan byggd på gamla fakta.</item>
    /// <item><b>Varje rad prövas på nytt</b> mot de aktuella hårda villkoren (samma regel som lösaren) — klienten kan inte lägga in någon annan.</item>
    /// <item><b>Ångra:</b> raderna delar ett <c>BatchId</c>; <see cref="Undo"/> tar bort dem som fortfarande är Planerade. Inget mejl skickas av tillämpningen.</item>
    /// </list>
    /// </summary>
    public partial class StaffProposalService
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly StartWindowReader _starts;
        private readonly StaffOwnerResolver _owners;
        private readonly StaffingGridService _grid;
        private readonly StaffPassService _pass;
        private readonly StaffingService _staffing;
        private readonly ILogger<StaffProposalService> _logger;
        private readonly HpskSite.Services.Hosting.DatabaseLock _lock;

        public StaffProposalService(IScopeProvider scopes, PlanningService planning, StartWindowReader starts, StaffOwnerResolver owners,
            StaffingGridService grid, StaffPassService pass, StaffingService staffing, ILogger<StaffProposalService> logger, HpskSite.Services.Hosting.DatabaseLock dbLock, StaffBusyReader busy, StaffCandidateService candidates)
        {
            _scopes = scopes; _planning = planning; _starts = starts; _owners = owners; _grid = grid; _pass = pass; _staffing = staffing; _logger = logger; _lock = dbLock; _busy = busy; _candidates = candidates;
        }

        private readonly StaffBusyReader _busy;
        private readonly StaffCandidateService _candidates;

        /// <summary>
        /// Ett lås för allt som skriver bemanning eller startlista på en tävling ("Planera dagen"): två redaktörer får inte tillämpa samtidigt — båda skulle passera
        /// fingeravtrycket och ge dubbletter, och en ombyggnad av startlistan raderar starter som förslaget räknat på. Resursnamnet delas av förslag och startlista.
        /// </summary>
        public static string LockName(StaffOwner owner) => owner.HasCompetition ? $"planera-dagen:c{owner.Comp}" : $"planera-dagen:p{owner.PlanningId}";
        public const string BusyMessage = "Någon annan ändrar bemanningen eller startlistan just nu. Försök igen om en stund — inget ändrades.";

        /// <summary>Högsta tid för en körning; sedan avbryts den och inget förslag visas.</summary>
        public static readonly TimeSpan MaxRun = TimeSpan.FromSeconds(20);

        public Proposal Build(StaffOwner owner, CancellationToken ct = default)
        {
            var discipline = _owners.Discipline(owner);
            var grid = _grid.BuildGrid(owner, discipline, 0);
            var passes = _pass.GetPasses(owner).ToDictionary(p => p.Id);
            var program = DayProgram.Default;
            var cols = grid.Columns.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();

            // ---- luckorna ----
            var slots = new List<SolverSlot>();
            var passOf = new Dictionary<string, int?>();
            foreach (var row in grid.Rows)
                foreach (var c in cols)
                {
                    var date = DateTime.ParseExact(c.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var pc = row.PassCover.TryGetValue(c.Key, out var l) ? l : new List<GridPassCover>();
                    if (pc.Any(p => p.Needed > 0))
                    {
                        foreach (var p in pc.Where(p => p.Needed > p.Filled))
                        {
                            if (!passes.TryGetValue(p.PassId, out var pv)) continue;
                            var win = StaffTimeCoverage.ParseRange(string.IsNullOrEmpty(pv.StartTime) || string.IsNullOrEmpty(pv.EndTime) ? null : pv.StartTime + "-" + pv.EndTime);
                            if (win == null) continue;
                            var iv = StaffInterval.From(date, win.Value.Start, win.Value.End);
                            if (iv == null) continue;
                            Add(row, date, iv.Value, p.Needed - p.Filled, p.PassId, $"{row.RoleKey}|{row.ScopeKey}|{c.Key}|p{p.PassId}");
                        }
                        continue;
                    }
                    var need = row.NeedFor(c.Key);
                    var filled = row.FilledByDay.TryGetValue(c.Key, out var f) ? f : 0;
                    if (need > filled)
                        Add(row, date, StaffInterval.From(date, null, null, program)!.Value, need - filled, null, $"{row.RoleKey}|{row.ScopeKey}|{c.Key}|day");
                }
            void Add(GridRow row, DateTime date, StaffInterval iv, int missing, int? passId, string key)
            {
                var k = $"{row.PlanningId}|{key}";
                slots.Add(new SolverSlot(k, row.RoleKey, row.RoleName, row.ScopeType, row.ScopeKey, row.ScopeLabel, date, iv, null, missing));
                passOf[k] = passId;
            }

            // ---- personerna ----
            var people = LoadPool(owner, slots, passes, program, out var fingerprintParts);
            var res = StaffingSolver.Solve(slots, people, null, ct);
            var problems = StaffingSolver.Validate(slots, people, res);
            fingerprintParts.Add("need:" + string.Join(";", slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Missing}")));
            return new Proposal(slots, people, res, Hash(fingerprintParts), passOf, problems);
        }

        private List<SolverPerson> LoadPool(StaffOwner owner, List<SolverSlot> slots, Dictionary<int, StaffPassView> passes, DayProgram program, out List<string> fp)
        {
            fp = new List<string>();
            var ids = _planning.Family(owner.PlanningId).Select(f => f.Id).ToList();
            var idList = ids.Count > 0 ? string.Join(",", ids) : "0";
            List<StaffHelpSignup> signups = new(); List<StaffAssignment> assigns = new(); List<StaffPass> allPasses = new();
            var slotIdsByDate = new Dictionary<DateTime, HashSet<int>>();
            var dates = slots.Select(s => s.Date.Date).Distinct().ToList();
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                signups = db.Fetch<StaffHelpSignup>($"SELECT * FROM StaffHelpSignup WHERE PlanningId IN ({idList}) AND MemberId > 0");
                assigns = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE PlanningId IN ({idList}) AND MemberId > 0");
                allPasses = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({idList})");
                foreach (var d in dates)
                    slotIdsByDate[d] = db.Fetch<int>($"SELECT Id FROM StaffHelpSlot WHERE PlanningId IN ({idList}) AND CAST(SlotDate AS date) = @0", d).ToHashSet();
            }
            var passById = allPasses.ToDictionary(p => p.Id);
            var shoot = _owners.ShootTime(owner.PlanningId);
            var startsBy = _busy.StartsByMember(owner);   // över alla tävlingar familjen bemannar

            fp.Add("shoot:" + $"{shoot.BeforeMinutes}/{shoot.DurationMinutes}/{shoot.AfterMinutes}");
            fp.AddRange(assigns.OrderBy(a => a.Id).Select(a => $"a:{a.Id}|{a.MemberId}|{a.Status}|{a.RoleKey}|{a.ScopeKey}|{a.StartsAt:s}|{a.EndsAt:s}|{a.PassId}|{a.DayDate:d}"));
            fp.AddRange(signups.OrderBy(x => x.Id).Select(x => $"s:{x.Id}|{x.MemberId}|{x.ModifiedDate.Ticks}"));
            fp.AddRange(startsBy.SelectMany(kv => kv.Value.Select(w => $"w:{kv.Key}|{w.Start:s}|{w.End:s}")).OrderBy(x => x, StringComparer.Ordinal));

            var result = new List<SolverPerson>();
            foreach (var g in signups.GroupBy(x => x.MemberId))
            {
                var mid = g.Key;
                var name = g.OrderByDescending(x => x.ModifiedDate).First().MemberName;
                var choices = g.SelectMany(x => ParseChoices(x.SlotsJson)).ToList();
                var roles = choices.Where(c => c.IsDayChoice && !string.IsNullOrWhiteSpace(c.RoleKey)).Select(c => c.RoleKey!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var busy = BusyOf(mid, startsBy, assigns, passById, shoot);   // egna starter + befintliga uppdrag, delat med Fördela
                // Varje dag i luckorna: har personen anmält sig den dagen, och till vilka tider? Annars är hela dagen upptagen.
                foreach (var d in dates)
                {
                    var fit = VolunteerRules.Fit(choices, d, null, slotIdsByDate[d]);
                    if (!fit.Volunteers) busy.Add(new BusyItem(BusyKind.Availability, new StaffInterval(d, d.AddDays(1), false), "har inte anmält sig den dagen"));
                    else busy.AddRange(StaffingRules.OutsideAvailability(d, fit.Windows));
                }
                var already = assigns.Count(a => a.MemberId == mid && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase));
                result.Add(new SolverPerson(mid, name, true, roles, busy, already));
            }
            return result.OrderBy(p => p.MemberId).ToList();
        }

        /// <summary>Det en person redan är upptagen med: egna starter (med förberedelse och vila) och befintliga uppdrag som inte är avböjda. EN regel för förslag och Fördela.</summary>
        private static List<BusyItem> BusyOf(int mid, Dictionary<int, List<StartWindow>> startsBy, List<StaffAssignment> assigns, Dictionary<int, StaffPass> passById, ShootTime shoot)
        {
            var busy = new List<BusyItem>();
            foreach (var w in startsBy.GetValueOrDefault(mid) ?? new List<StartWindow>())
                busy.Add(new BusyItem(BusyKind.Start, shoot.Window(w.Start, w.End), $"egen start {w.Start:HH\\:mm}"));
            foreach (var a in assigns.Where(a => a.MemberId == mid && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)))
            {
                var (_, iv) = FamilyWeekService.WhenOf(a, a.PassId is > 0 && passById.TryGetValue(a.PassId.Value, out var p) ? p : null);
                if (iv is { } v) busy.Add(new BusyItem(BusyKind.Assignment, v, "annat uppdrag"));
            }
            return busy;
        }

        /// <summary>Tillämpar (en del av) ett förslag. <paramref name="build"/> bygger om förslaget under låset — förvalet är "Fyll med förslag"; Fördela skickar sin egen.</summary>
        public ApplyOutcome Apply(StaffOwner owner, IReadOnlyList<(string SlotKey, int MemberId)> rows, string? fingerprint, int byMemberId, CancellationToken ct = default, Func<Proposal>? build = null)
        {
            var skipped = new List<(int, string, string)>();
            var added = new List<(int, string, int)>();
            if (rows.Count == 0) return new ApplyOutcome(false, "Inget att lägga in.", null, added, skipped);
            if (rows.Count > 500) return new ApplyOutcome(false, "Högst 500 rader åt gången.", null, added, skipped);
            using var lease = _lock.TryAcquire(LockName(owner), TimeSpan.FromSeconds(10));
            if (lease == null) return new ApplyOutcome(false, BusyMessage, null, added, skipped);
            var p = build != null ? build() : Build(owner, ct);   // bygg om UNDER låset: fingeravtrycket jämförs mot det som gäller när ingen annan skriver
            if (!string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal))
                return new ApplyOutcome(false, "Bemanningen eller anmälningarna har ändrats sedan förslaget gjordes. Gör ett nytt förslag — inget lades in.", null, added, skipped);
            if (p.Problems.Count > 0)
            {
                _logger.LogError("Staffing: förslaget bröt de hårda villkoren för planering {Planning}: {Problems}", owner.PlanningIdOrNull, string.Join(" | ", p.Problems));
                return new ApplyOutcome(false, "Förslaget kunde inte kontrolleras. Inget lades in.", null, added, skipped);
            }

            var slotByKey = p.Slots.ToDictionary(s => s.Key);
            var person = p.People.ToDictionary(x => x.MemberId);
            var never = new SolverOptions().NeverProposeRoles;
            var batch = Guid.NewGuid();
            var applied = new List<(int MemberId, SolverSlot Slot)>();
            using var scope = _scopes.CreateScope();   // EN transaktion: faller något avbryts allt
            foreach (var (key, mid) in rows.Distinct())
            {
                if (!slotByKey.TryGetValue(key, out var slot)) { skipped.Add((mid, MemberName(person, mid), "Luckan finns inte längre.")); continue; }
                if (!person.TryGetValue(mid, out var pr)) { skipped.Add((mid, MemberName(person, mid), "Personen har inte anmält sig som funktionär.")); continue; }
                if (never.Contains(slot.RoleKey)) { skipped.Add((mid, pr.Name, $"{slot.RoleName} utses av en människa.")); continue; }
                if (applied.Count(a => a.Slot.Key == key) >= slot.Missing) { skipped.Add((mid, pr.Name, "Luckan är redan full.")); continue; }
                var busy = pr.Busy.Concat(applied.Where(a => a.MemberId == mid).Select(a => new BusyItem(BusyKind.Assignment, a.Slot.Interval, "just inlagd", a.Slot.Place)));
                if (StaffingRules.FirstConflict(slot.Interval, slot.Place, busy) is { IsHard: true } c) { skipped.Add((mid, pr.Name, c.Label)); continue; }
                if (applied.Any(a => a.MemberId == mid && a.Slot.Key == key)) { skipped.Add((mid, pr.Name, "Står redan där.")); continue; }

                var passId = p.PassOfSlot.GetValueOrDefault(key);
                var req = new SaveStaffAssignmentRequest
                {
                    Id = 0, CompetitionId = 0, MemberId = mid, DisplayName = pr.Name, RoleKey = slot.RoleKey, ScopeType = slot.ScopeType, ScopeKey = slot.ScopeKey,
                    DayDate = slot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), PassId = passId, Status = StaffAssignmentStatus.Planned,
                };
                // Ett block (Fördela) skrivs med sina egna tider — inte som heldag eller pass.
                if (p.TimedSlots != null && p.TimedSlots.Contains(key))
                {
                    req.StartsAt = slot.Interval.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    req.EndsAt = slot.Interval.End.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
                var id = _staffing.Save(req, owner, byMemberId);
                if (id == 0) { skipped.Add((mid, pr.Name, "Kunde inte spara.")); continue; }
                scope.Database.Execute("UPDATE StaffAssignment SET BatchId = @0 WHERE Id = @1", batch, id);
                applied.Add((mid, slot));
                added.Add((mid, pr.Name, id));
            }
            scope.Complete();
            return new ApplyOutcome(true, null, added.Count > 0 ? batch : null, added, skipped);
        }

        /// <summary>
        /// Rader som ett tillämpat förslag lade in och som ingen rört sedan: fortfarande Planerade, och <c>ModifiedDate</c> ligger inom några sekunder från skapandet
        /// (BatchId sätts direkt efter, utan att röra ModifiedDate). En rad som någon ändrat — tid, roll, kontakt — är inte längre förslagets och tas inte bort.
        /// </summary>
        internal static bool UntouchedSinceApply(StaffAssignment a) =>
            a.Status == StaffAssignmentStatus.Planned && (a.ModifiedDate - a.CreatedDate).Duration() <= TimeSpan.FromSeconds(5);

        /// <summary>Ångrar en tillämpning: tar bort raderna med <paramref name="batchId"/> som fortfarande är Planerade och orörda. Antalet borttagna och kvarlämnade (svarat eller ändrat).</summary>
        public (int Removed, int Kept) Undo(StaffOwner owner, Guid batchId)
        {
            using var lease = _lock.TryAcquire(LockName(owner), TimeSpan.FromSeconds(10));
            if (lease == null) return (0, -1);
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var mine = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE BatchId = @0 AND " + StaffOwner.Where(1), batchId, owner.P, owner.C);
            var removable = mine.Where(UntouchedSinceApply).ToList();
            foreach (var a in removable) db.Execute("DELETE FROM StaffAssignment WHERE Id = @0", a.Id);
            return (removable.Count, mine.Count - removable.Count);
        }

        /// <summary>Senaste tillämpning som går att ångra (har orörda Planerade rader kvar) — stående, så att Ångra inte försvinner med en toast. Null = ingen.</summary>
        public (Guid BatchId, int Rows, DateTime At)? LatestUndoable(StaffOwner owner)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var rows = s.Database.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE BatchId IS NOT NULL AND " + StaffOwner.Where(0), owner.P, owner.C);
            var g = rows.Where(UntouchedSinceApply).GroupBy(a => a.BatchId!.Value).Select(x => (BatchId: x.Key, Rows: x.Count(), At: x.Max(a => a.CreatedDate))).OrderByDescending(x => x.At).FirstOrDefault();
            return g.Rows > 0 ? g : null;
        }

        private static string MemberName(Dictionary<int, SolverPerson> people, int id) => people.TryGetValue(id, out var p) ? p.Name : $"Medlem {id}";

        private static string Hash(IEnumerable<string> parts)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
            return Convert.ToHexString(bytes)[..24];
        }

        private static List<HelpSlotChoice> ParseChoices(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonConvert.DeserializeObject<List<HelpSlotChoice>>(json) ?? new(); }
            catch { return new(); }
        }
    }
}
