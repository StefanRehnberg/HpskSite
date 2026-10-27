using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// En lucka som ska fyllas: <see cref="Missing"/> personer saknas för roll, station, dag och tid. <see cref="Key"/> är luckans stabila identitet
    /// (roll|station|dag|tid) — samma lucka ger samma nyckel vid varje körning.
    /// </summary>
    public sealed record SolverSlot(string Key, string RoleKey, string RoleName, string? ScopeType, string? ScopeKey, string? ScopeLabel, DateTime Date,
        StaffInterval Interval, string? Place, int Missing);

    /// <summary>
    /// En person som kan komma i fråga. <see cref="Busy"/> är det personen redan är upptagen med den dagen: egna starter (med förberedelse och vila),
    /// befintliga uppdrag och tiderna utanför det hen anmält sig till. <see cref="RoleMatch"/>: per roll, har hen anmält sig just till den.
    /// </summary>
    public sealed record SolverPerson(int MemberId, string Name, bool Volunteer, IReadOnlySet<string> Roles, IReadOnlyList<BusyItem> Busy, int AssignedAlready);

    public sealed record SolverOptions
    {
        /// <summary>Roller som aldrig föreslås — en människa utser dem (skjutledare och stationschef är säkerhetsroller, SHB D.8.1).</summary>
        public IReadOnlySet<string> NeverProposeRoles { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skjutledare", "stationschef", "tavlingsledning" };
        /// <summary>Föreslå bara de som anmält sig som funktionärer (Stefans svar: startlistan tillsammans med funktionärsanmälan är källan).</summary>
        public bool OnlyVolunteers { get; init; } = true;
        public TimeSpan? TravelMargin { get; init; }
        /// <summary>Förbättringsfasen (flytta förslagets egna uppdrag så att fler luckor fylls). Av bara i test, för att visa att den gör nytta.</summary>
        public bool Improve { get; init; } = true;
    }

    public sealed record ProposedRow(string SlotKey, int MemberId, string Name, string RoleKey, string? ScopeType, string? ScopeKey, DateTime Date, StaffInterval Interval,
        string Why, string? Warning);

    public sealed record UnfilledSlot(string SlotKey, string RoleName, string? ScopeLabel, DateTime Date, StaffInterval Interval, int Missing, string Reason, int CandidatesLeft);

    public sealed record SolverResult(IReadOnlyList<ProposedRow> Proposed, IReadOnlyList<UnfilledSlot> Unfilled)
    {
        public int Placed => Proposed.Count;
        public int StillMissing => Unfilled.Sum(u => u.Missing);
    }

    /// <summary>
    /// Planera dagen steg 3: <b>luckfyllaren</b> — en RENT, deterministisk funktion (ingen databas, ingen klocka, inget slumptal). Fyller luckor i
    /// bemanningen med personer som anmält sig som funktionärer, så att ingen står där hen själv skjuter eller redan har ett annat uppdrag.
    /// <list type="bullet">
    /// <item><b>Hårt:</b> ingen överlappning med personens upptagna tid (<see cref="StaffingRules.FirstConflict"/> — samma regel som veckan och
    /// kandidatlistan) och aldrig två förslag åt samma person som överlappar. Ett hårt hinder fäller kandidaten, alltid.</item>
    /// <item><b>Aldrig föreslagna:</b> roller i <see cref="SolverOptions.NeverProposeRoles"/> — de hamnar bland de olösta med skäl.</item>
    /// <item><b>Ordning:</b> de svåraste luckorna först (färst kandidater), sedan tidigast. Bland kandidaterna: anmäld just till rollen, utan trång
    /// förflyttning, färst uppdrag hittills (jämn fördelning), sedan namn och id.</item>
    /// </list>
    /// Befintliga uppdrag finns bara som upptagen tid — förslaget rör dem aldrig.
    /// </summary>
    public static class StaffingSolver
    {
        private static readonly StringComparer Sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true);

        public static SolverResult Solve(IReadOnlyList<SolverSlot> slots, IReadOnlyList<SolverPerson> people, SolverOptions? options = null, CancellationToken ct = default)
        {
            options ??= new SolverOptions();
            var proposed = new List<ProposedRow>();
            var unfilled = new List<UnfilledSlot>();
            var count = people.ToDictionary(p => p.MemberId, p => p.AssignedAlready);
            // Luckor som ännu inte är behandlade (för flexibilitetsmåttet): ursprungligt antal som saknas, 0 när luckan är klar.
            var remaining = slots.ToDictionary(s => s.Key, s => s.Missing);
            var slotByKey = slots.ToDictionary(s => s.Key);
            var leftBySlot = new Dictionary<string, int>();
            var reasonBySlot = new Dictionary<string, (string Reason, int Count)>();

            bool Eligible(SolverPerson p, SolverSlot s) => (!options.OnlyVolunteers || p.Volunteer);
            // Personens upptagna tid: det hen redan har, plus det förslaget hittills lagt på hen.
            IEnumerable<BusyItem> BusyOf(SolverPerson p) => p.Busy.Concat(proposed.Where(r => r.MemberId == p.MemberId)
                .Select(r => new BusyItem(BusyKind.Assignment, r.Interval, "förslaget", slotByKey[r.SlotKey].Place)));
            SlotConflict? Conflict(SolverPerson p, SolverSlot s) => StaffingRules.FirstConflict(s.Interval, s.Place, BusyOf(p), options.TravelMargin);
            string Why(SolverPerson p, SolverSlot s) => (p.Roles.Contains(s.RoleKey) ? $"anmäld som {s.RoleName.ToLowerInvariant()}" : "anmäld, var det behövs")
                + (count[p.MemberId] == 0 ? ", inte placerad än" : $", {count[p.MemberId]} placeringar hittills");
            ProposedRow Row(SolverPerson p, SolverSlot s, SlotConflict? c) => new(s.Key, p.MemberId, p.Name, s.RoleKey, s.ScopeType, s.ScopeKey, s.Date, s.Interval, Why(p, s),
                c == null ? null : $"{c.Label} ({(c.GapMinutes is int g ? g + " min emellan" : "trångt")})");

            // Hur många som alls kan stå på luckan (utan att räkna med förslag som ännu inte gjorts) — den svåraste först.
            var order = slots.Where(s => s.Missing > 0).Select(s => new
            {
                Slot = s,
                Never = options.NeverProposeRoles.Contains(s.RoleKey),
                Count = people.Count(p => Eligible(p, s) && Conflict(p, s) is not { IsHard: true }),
            }).OrderBy(x => x.Count).ThenBy(x => x.Slot.Date).ThenBy(x => x.Slot.Interval.Start).ThenBy(x => x.Slot.Key, StringComparer.Ordinal).ToList();

            foreach (var x in order)
            {
                ct.ThrowIfCancellationRequested();
                var s = x.Slot;
                if (x.Never)
                {
                    unfilled.Add(new UnfilledSlot(s.Key, s.RoleName, s.ScopeLabel, s.Date, s.Interval, s.Missing,
                        $"{s.RoleName} utses av en människa — förslaget lägger inte in någon på en säkerhetsroll", x.Count));
                    continue;
                }
                var left = s.Missing;
                remaining[s.Key] = left;
                while (left > 0)
                {
                    var taken = proposed.Where(r => r.SlotKey == s.Key).Select(r => r.MemberId).ToHashSet();
                    // Täckning före jämn fördelning: den som passar på FÄRRE andra luckor används först, så att de flexibla finns kvar till de svåra
                    // luckorna. (Fördelningen mellan lika flexibla avgörs sedan av antalet uppdrag hittills.)
                    var open = order.Where(o => !o.Never && o.Slot.Missing > 0 && remaining[o.Slot.Key] > 0 && o.Slot.Key != s.Key).Select(o => o.Slot).ToList();
                    var ranked = people.Where(p => Eligible(p, s) && !taken.Contains(p.MemberId))
                        .Select(p => (P: p, C: Conflict(p, s)))
                        .Where(t => t.C is not { IsHard: true })
                        .Select(t => (t.P, t.C, Flex: open.Count(o => Conflict(t.P, o) is not { IsHard: true })))
                        .OrderBy(t => t.Flex)
                        .ThenBy(t => t.P.Roles.Contains(s.RoleKey) ? 0 : 1)
                        .ThenBy(t => t.C == null ? 0 : 1)
                        .ThenBy(t => count[t.P.MemberId])
                        .ThenBy(t => t.P.Name, Sv).ThenBy(t => t.P.MemberId).ToList();
                    if (ranked.Count == 0) break;
                    var p = ranked[0].P; var c = ranked[0].C;
                    proposed.Add(Row(p, s, c));
                    count[p.MemberId]++;
                    left--;
                    remaining[s.Key] = left;
                }
                remaining[s.Key] = 0;
                if (left > 0)
                {
                    leftBySlot[s.Key] = left;
                    reasonBySlot[s.Key] = (x.Count == 0
                        ? (people.Any(p => Eligible(p, s)) ? "Alla som anmält sig är upptagna den tiden" : "Ingen har anmält sig som funktionär den dagen")
                        : $"Bara {x.Count} av de anmälda {(x.Count == 1 ? "är ledig" : "är lediga")} den tiden", x.Count);
                }
            }

            // Förbättring: en lucka som ännu saknar folk kan få en person som bara blockeras av ETT av förslagets egna uppdrag — om det uppdraget kan
            // flyttas till någon annan som är ledig. Varje flytt fyller en lucka till utan att tömma en annan; upprepas tills inget mer går (högst 50 varv).
            for (var pass = 0; options.Improve && pass < 50 && leftBySlot.Count > 0; pass++)
            {
                var improved = false;
                foreach (var key in leftBySlot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var s = slotByKey[key];
                    while (leftBySlot.GetValueOrDefault(key) > 0 && TryAugment(s)) { leftBySlot[key]--; improved = true; }
                    if (leftBySlot[key] == 0) leftBySlot.Remove(key);
                }
                if (!improved) break;
            }
            foreach (var kv in leftBySlot)
            {
                var s = slotByKey[kv.Key];
                unfilled.Add(new UnfilledSlot(s.Key, s.RoleName, s.ScopeLabel, s.Date, s.Interval, kv.Value, reasonBySlot[kv.Key].Reason, reasonBySlot[kv.Key].Count));
            }

            bool TryAugment(SolverSlot s)
            {
                var onSlot = proposed.Where(r => r.SlotKey == s.Key).Select(r => r.MemberId).ToHashSet();
                foreach (var p in people.Where(p => Eligible(p, s) && !onSlot.Contains(p.MemberId)).OrderBy(p => p.Name, Sv).ThenBy(p => p.MemberId))
                {
                    // Personens EGNA hinder (starter, andra uppdrag, anmälda tider) går inte att flytta på.
                    if (StaffingRules.FirstConflict(s.Interval, s.Place, p.Busy, options.TravelMargin) is { IsHard: true }) continue;
                    var blockers = proposed.Where(r => r.MemberId == p.MemberId && r.Interval.Overlaps(s.Interval)).ToList();
                    if (blockers.Count != 1) continue;
                    var r = blockers[0];
                    var rs = slotByKey[r.SlotKey];
                    var onR = proposed.Where(x => x.SlotKey == r.SlotKey).Select(x => x.MemberId).ToHashSet();
                    foreach (var q in people.Where(q => q.MemberId != p.MemberId && Eligible(q, rs) && !onR.Contains(q.MemberId)).OrderBy(q => q.Name, Sv).ThenBy(q => q.MemberId))
                    {
                        if (Conflict(q, rs) is { IsHard: true }) continue;
                        // q tar över raden; p tar den nya luckan (kontrollera att p inte nu krockar med sina andra förslag).
                        proposed.Remove(r);
                        count[p.MemberId]--;
                        if (Conflict(p, s) is { IsHard: true }) { proposed.Add(r); count[p.MemberId]++; continue; }
                        proposed.Add(Row(q, rs, Conflict(q, rs)));
                        count[q.MemberId]++;
                        proposed.Add(Row(p, s, Conflict(p, s)));
                        count[p.MemberId]++;
                        return true;
                    }
                }
                return false;
            }
            return new SolverResult(
                proposed.OrderBy(r => r.Date).ThenBy(r => r.Interval.Start).ThenBy(r => r.SlotKey, StringComparer.Ordinal).ThenBy(r => r.Name, Sv).ToList(),
                unfilled.OrderBy(u => u.Date).ThenBy(u => u.Interval.Start).ThenBy(u => u.SlotKey, StringComparer.Ordinal).ToList());
        }

        /// <summary>
        /// Den OBEROENDE kontrollen: går igenom ett färdigt förslag och räknar brotten mot de hårda villkoren — utan att använda lösarens egen logik.
        /// Ett förslag med brott är ett fel i lösaren, aldrig något som visas för användaren.
        /// </summary>
        public static List<string> Validate(IReadOnlyList<SolverSlot> slots, IReadOnlyList<SolverPerson> people, SolverResult result, SolverOptions? options = null)
        {
            options ??= new SolverOptions();
            var problems = new List<string>();
            var byKey = slots.ToDictionary(s => s.Key);
            var person = people.ToDictionary(p => p.MemberId);
            foreach (var r in result.Proposed)
            {
                if (!byKey.TryGetValue(r.SlotKey, out var s)) { problems.Add($"okänd lucka {r.SlotKey}"); continue; }
                if (!person.TryGetValue(r.MemberId, out var p)) { problems.Add($"okänd person {r.MemberId}"); continue; }
                if (options.NeverProposeRoles.Contains(s.RoleKey)) problems.Add($"{p.Name} föreslogs till säkerhetsrollen {s.RoleKey}");
                if (options.OnlyVolunteers && !p.Volunteer) problems.Add($"{p.Name} har inte anmält sig som funktionär");
                foreach (var b in p.Busy)
                    if (b.Interval.Overlaps(s.Interval)) problems.Add($"{p.Name} är upptagen ({b.Label}) när hen föreslås {s.RoleName} {s.Key}");
            }
            foreach (var g in result.Proposed.GroupBy(r => r.MemberId))
            {
                var list = g.ToList();
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                        if (list[i].Interval.Overlaps(list[j].Interval)) problems.Add($"{list[i].Name} föreslogs på två platser samtidigt");
            }
            foreach (var g in result.Proposed.GroupBy(r => r.SlotKey))
            {
                if (g.Count() != g.Select(r => r.MemberId).Distinct().Count()) problems.Add($"samma person två gånger på {g.Key}");
                if (byKey.TryGetValue(g.Key, out var s) && g.Count() > s.Missing) problems.Add($"fler föreslagna än som saknas på {g.Key}");
            }
            // Summakontrollen: varje lucka är antingen fylld eller redovisad som olöst — ingen försvinner tyst.
            foreach (var s in slots.Where(s => s.Missing > 0))
            {
                var filled = result.Proposed.Count(r => r.SlotKey == s.Key);
                var open = result.Unfilled.Where(u => u.SlotKey == s.Key).Sum(u => u.Missing);
                if (filled + open != s.Missing) problems.Add($"luckan {s.Key}: {filled} föreslagna + {open} olösta ≠ {s.Missing} som saknas");
            }
            return problems;
        }
    }
}
