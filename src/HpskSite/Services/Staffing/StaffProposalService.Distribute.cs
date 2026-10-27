using System.Globalization;
using HpskSite.Models.Staffing;

namespace HpskSite.Services.Staffing
{
    /// <summary>Fördela: vilken roll, vilka personer (ur den inklistrade listan) och hur långa block dagen delas i.</summary>
    public sealed record DistributionSpec(string RoleKey, IReadOnlyList<int> MemberIds, int BlockMinutes);

    public partial class StaffProposalService
    {
        /// <summary>
        /// <b>Fördela dem åt mig.</b> Samma lösare, samma fingeravtryck, samma tillämpa och ångra som "Fyll med förslag" — men poolen är de NAMNGIVNA personerna (de behöver inte
        /// ha anmält sig som funktionärer) och luckorna är <b>block</b>: dagen delas (<see cref="SlotSlicer"/>) så att en person kan ta ett block före eller efter sin egen start.
        /// Hade luckan varit hela dagen skulle den som skjuter 11:30 inte kunna placeras alls. Har dagen pass används passens tider (och passens behov) som förut.
        /// Behovet gäller per block: en station som behöver 1 markör får en per block. Det som redan står i blocket räknas av. Ingenting lagras; allt räknas om vid varje anrop.
        /// </summary>
        /// <summary>Blocklängderna som prövas när användaren inte valt någon (BlockMinutes 0), längst först.</summary>
        /// <summary>
        /// Persontest 8: tre timmars block gav "skift med hål" (röda tider mitt på dagen, sex personer på två stationer). Längst av allt är HELA dagen (<see cref="SlotSlicer.MaxBlockMinutes"/>):
        /// en person på en station hela dagen. Den prövas först och används så fort den fyller alla platser; blocken är en reserv för dem som själva skjuter mitt på dagen.
        /// </summary>
        public static readonly int[] AutoBlockMinutes = { SlotSlicer.MaxBlockMinutes, 180, 120, 90 };

        /// <summary>Fördelningen fyller varje plats — då är det längsta blocket som klarar det rätt val, även om fler namn hade fått plats med kortare block (resten behövs helt enkelt inte).</summary>
        public static bool IsComplete(int stillMissing) => stillMissing == 0;

        /// <summary>Det bättre av två fördelningar: fler personer placerade först, sedan fler tider fyllda. Lika = det första (längre block) behålls.</summary>
        public static bool IsBetterDistribution(int personsA, int placedA, int personsB, int placedB) => personsA > personsB || (personsA == personsB && placedA > placedB);

        /// <summary>
        /// Persontest 6b: standardblocken på 3 timmar lämnade 3 av 12 utan plats och förslaget sa inte att kortare block hjälper. Är <c>BlockMinutes</c> 0 prövas <see cref="AutoBlockMinutes"/>
        /// och den som placerar flest personer (sedan flest tider) väljs, och stannar så fort alla namngivna är placerade. Ett valt värde används som det är. Svaret bär blocklängden som valdes,
        /// så att "Lägg in" bygger om exakt samma förslag.
        /// </summary>
        public (Proposal Proposal, int BlockMinutes) BuildDistributionBest(StaffOwner owner, DistributionSpec spec, CancellationToken ct = default)
        {
            if (spec.BlockMinutes > 0)
            {
                var one = Math.Clamp(spec.BlockMinutes, SlotSlicer.MinBlockMinutes, SlotSlicer.MaxBlockMinutes);
                return (BuildDistribution(owner, spec with { BlockMinutes = one }, ct), one);
            }
            (Proposal P, int Block, int Persons)? best = null;
            foreach (var b in AutoBlockMinutes)
            {
                ct.ThrowIfCancellationRequested();
                var p = BuildDistribution(owner, spec with { BlockMinutes = b }, ct);
                if (p.Problems.Count > 0 || p.Slots.Count == 0) return (p, b);   // felet/avsaknaden ser likadan ut för alla blocklängder
                if (IsComplete(p.Result.StillMissing)) return (p, b);              // inga hål: längre block är alltid bättre än kortare
                var persons = p.Result.Proposed.Select(r => r.MemberId).Distinct().Count();
                if (best == null || IsBetterDistribution(persons, p.Result.Placed, best.Value.Persons, best.Value.P.Result.Placed)) best = (p, b, persons);
                if (persons >= p.People.Count) break;   // alla namngivna har en plats — längre block är bättre, prova inte kortare
            }
            return (best!.Value.P, best.Value.Block);
        }

        public Proposal BuildDistribution(StaffOwner owner, DistributionSpec spec, CancellationToken ct = default)
        {
            var discipline = _owners.Discipline(owner);
            var grid = _grid.BuildGrid(owner, discipline, 0);
            var passes = _pass.GetPasses(owner).ToDictionary(p => p.Id);
            var program = DayProgram.Default;
            var cols = grid.Columns.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();
            var block = Math.Clamp(spec.BlockMinutes <= 0 ? SlotSlicer.DefaultBlockMinutes : spec.BlockMinutes, SlotSlicer.MinBlockMinutes, SlotSlicer.MaxBlockMinutes);
            var ids = spec.MemberIds.Where(i => i > 0).Distinct().ToList();

            (TimeSpan Start, TimeSpan End)? PassWindow(StaffPassView p) =>
                StaffTimeCoverage.ParseRange(string.IsNullOrEmpty(p.StartTime) || string.IsNullOrEmpty(p.EndTime) ? null : p.StartTime + "-" + p.EndTime);

            // ---- luckorna: block per station och dag ----
            var slots = new List<SolverSlot>();
            var passOf = new Dictionary<string, int?>();
            var timed = new HashSet<string>();
            foreach (var row in grid.Rows.Where(r => string.Equals(r.RoleKey, spec.RoleKey, StringComparison.OrdinalIgnoreCase)))
                foreach (var c in cols)
                {
                    var date = DateTime.ParseExact(c.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var pc = row.PassCover.TryGetValue(c.Key, out var l) ? l : new List<GridPassCover>();
                    if (pc.Any(p => p.Needed > 0))
                    {
                        // Rollen har sitt behov per pass: fyll passens brist, som "Fyll med förslag" gör.
                        foreach (var p in pc.Where(p => p.Needed > p.Filled))
                        {
                            if (!passes.TryGetValue(p.PassId, out var pv) || PassWindow(pv) is not { } win || StaffInterval.From(date, win.Start, win.End) is not { } iv) continue;
                            var key = $"{row.PlanningId}|{row.RoleKey}|{row.ScopeKey}|{c.Key}|p{p.PassId}";
                            slots.Add(new SolverSlot(key, row.RoleKey, row.RoleName, row.ScopeType, row.ScopeKey, row.ScopeLabel, date, iv, null, p.Needed - p.Filled));
                            passOf[key] = p.PassId;
                        }
                        continue;
                    }
                    var need = row.NeedFor(c.Key);
                    if (need <= 0) continue;
                    var existing = (row.Cells.TryGetValue(c.Key, out var cell) ? cell : new List<GridEntry>())
                        .Where(e => !string.Equals(e.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.PassId is int pid && passes.TryGetValue(pid, out var pp) && PassWindow(pp) is { } w ? w : StaffTimeCoverage.ParseRange(e.TimeLabel))
                        .ToList();
                    foreach (var b in SlotSlicer.Blocks(date, program, block))
                    {
                        var covered = StaffTimeCoverage.Peak(existing.Select(s => (s == null ? ((TimeSpan, TimeSpan)?)null : s.Value)), (b.Start.TimeOfDay, b.End.TimeOfDay == TimeSpan.Zero ? TimeSpan.FromDays(1) : b.End.TimeOfDay));
                        var missing = need - covered;
                        if (missing <= 0) continue;
                        var key = $"{row.PlanningId}|{row.RoleKey}|{row.ScopeKey}|{c.Key}|b{b.Start:HHmm}";
                        slots.Add(new SolverSlot(key, row.RoleKey, row.RoleName, row.ScopeType, row.ScopeKey, row.ScopeLabel, date, b, null, missing));
                        passOf[key] = null; timed.Add(key);
                    }
                }

            // ---- personerna: de namngivna, med sina starter (över familjen) och uppdrag ----
            var names = ids.Count == 0 ? new Dictionary<int, string>()
                : _candidates.Find(new CandidateQuery { Owner = owner, OnlyMembers = ids, Take = Math.Max(10, ids.Count) }).Candidates.ToDictionary(x => x.MemberId, x => x.Name);
            var idList = owner.HasPlanning ? _planning.Family(owner.PlanningId).Select(f => f.Id).ToList() : new List<int>();
            var famList = idList.Count > 0 ? string.Join(",", idList) : "0";
            List<StaffAssignment> assigns; List<StaffPass> allPasses;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                assigns = s.Database.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE PlanningId IN ({famList}) AND MemberId > 0");
                allPasses = s.Database.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({famList})");
            }
            var passById = allPasses.ToDictionary(p => p.Id);
            var shoot = owner.HasPlanning ? _owners.ShootTime(owner.PlanningId) : ShootTimeRules.Default(discipline);
            var startsBy = _busy.StartsByMember(owner);
            var people = new List<SolverPerson>();
            foreach (var mid in ids.Where(names.ContainsKey).OrderBy(i => i))
                people.Add(new SolverPerson(mid, names[mid], true, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { spec.RoleKey },
                    BusyOf(mid, startsBy, assigns, passById, shoot), assigns.Count(a => a.MemberId == mid && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))));

            var fp = new List<string> { $"dist:{spec.RoleKey}|{block}|{string.Join(",", ids.OrderBy(i => i))}", $"shoot:{shoot.BeforeMinutes}/{shoot.DurationMinutes}/{shoot.AfterMinutes}" };
            fp.AddRange(assigns.OrderBy(a => a.Id).Select(a => $"a:{a.Id}|{a.MemberId}|{a.Status}|{a.RoleKey}|{a.ScopeKey}|{a.StartsAt:s}|{a.EndsAt:s}|{a.PassId}|{a.DayDate:d}"));
            fp.AddRange(startsBy.Where(kv => ids.Contains(kv.Key)).SelectMany(kv => kv.Value.Select(w => $"w:{kv.Key}|{w.Start:s}|{w.End:s}")).OrderBy(x => x, StringComparer.Ordinal));
            fp.Add("need:" + string.Join(";", slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Missing}")));

            var res = StaffingSolver.Solve(slots, people, new SolverOptions { Improve = true }, ct);
            var problems = StaffingSolver.Validate(slots, people, res);
            return new Proposal(slots, people, res, Hash(fp), passOf, problems, timed);
        }
    }
}
