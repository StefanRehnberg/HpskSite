using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>En skytt vars patrull och tid ligger fast (lås). Bygger om-körningen runt den, aldrig över den.</summary>
    public sealed record LockedPlacement(int MemberId, string Name, string Club, string ShootingClass, DateTime StartTime, string? WeaponGroup);

    /// <summary>
    /// En grupp patruller som den ser ut i dagens startlista: etiketten ("B+C", "A+R"), vilka vapenklasser den rymmer (tom = alla), gruppens EGEN första start
    /// och avstånd mellan patrullerna. Fältets startlista skapas en grupp i taget med egen första start — patrullernas tider är uppskattningar
    /// (första start + avstånd × ordning), inte tider som hålls. Ombyggnaden håller sig därför inom gruppen.
    /// </summary>
    public sealed record PatrolGroupSpec(string Label, IReadOnlyCollection<string> WeaponCodes, DateTime FirstStart, int IntervalMinutes, int PatrolSize, int PatrolCount);

    public sealed class PatrolPlanSettings
    {
        public int PatrolSize { get; init; } = 6;
        public int IntervalMinutes { get; init; } = 15;
        public DateTime? FirstStart { get; init; }
        public string WeaponGrouping { get; init; } = "Separate";
        public int MultiClassGapMinutes { get; init; }
        /// <summary>Behåll de som redan har en start som passar (ingen krock med eget uppdrag) — bara de som krockar flyttas. Läses av tjänsten, inte av planeraren.</summary>
        public bool KeepFitting { get; init; } = true;
    }

    public sealed class PatrolPlan
    {
        public List<FaltskytteGeneratedPatrol> Patrols { get; init; } = new();
        public List<FaltskytteUnavoidable> Unavoidable { get; init; } = new();
        /// <summary>Anmälda som inte får någon fast start (reserver) — utelämnade, med namn.</summary>
        public List<string> ReservesLeftOut { get; init; } = new();
        /// <summary>Låsta som behölls på sin tid.</summary>
        public int LockedKept { get; init; }
        /// <summary>Låsta vars anmälan inte finns längre (släpptes) — de tas inte med.</summary>
        public List<string> LockedDropped { get; init; } = new();
        /// <summary>Låsta som inte fick plats i en patrull på sin tid, så en egen patrull lades vid samma tid.</summary>
        public List<string> LockedOverflow { get; init; } = new();
        /// <summary>Anmälda vars vapenklass inte finns i någon befintlig grupp — de placeras inte, och redovisas ("Namn (klass)").</summary>
        public List<string> Ungrouped { get; init; } = new();
    }

    /// <summary>
    /// Planera dagen steg 3b: den rena delen av "Bygg startlistan runt funktionärerna". Tar anmälningarna, de låsta skyttarna, uppdragen och
    /// reserverna och ger de patruller som ska skrivas — ingen databas, så att den går att bevisa med tester.
    /// <list type="bullet">
    /// <item>Reserver (läge "hoppar in om det blir en lucka") får ingen fast start och utelämnas.</item>
    /// <item>Låsta skyttar flätas tillbaka på sin starttid (samma tid, samma grupp om det finns plats), och deras tid räknas som upptagen vid mellanrumskontrollen.</item>
    /// <item>Övriga placeras av <see cref="FaltskyttePatrolGenerator"/> så att ingen start krockar med eget uppdrag; de som inte går att placera redovisas.</item>
    /// <item>Patrullerna numreras om i tidsordning, så att nummer och tid följer varandra.</item>
    /// </list>
    /// </summary>
    public static class FaltskyttePatrolPlanner
    {
        private static string Key(int memberId, string? cls) => memberId + "|" + ShootingClasses.ToCanonicalName(cls);

        public static PatrolPlan Plan(IReadOnlyList<CompetitionRegistration> registrations, IReadOnlyCollection<LockedPlacement> locked,
            PatrolConstraints constraints, ISet<int> reserves, PatrolPlanSettings settings, IReadOnlyList<PatrolGroupSpec>? groups = null)
        {
            if (groups is { Count: > 0 }) return PlanByGroups(registrations, locked, constraints, reserves, settings, groups);
            var lockedByKey = new Dictionary<string, LockedPlacement>();
            foreach (var l in locked) lockedByKey[Key(l.MemberId, l.ShootingClass)] = l;
            var regKeys = registrations.Select(r => Key(r.MemberId, r.MemberClass)).ToHashSet();

            var keptLocked = lockedByKey.Where(kv => regKeys.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            var dropped = lockedByKey.Where(kv => !regKeys.Contains(kv.Key)).Select(kv => kv.Value.Name).OrderBy(n => n).ToList();

            var reserveRegs = registrations.Where(r => reserves.Contains(r.MemberId) && !lockedByKey.ContainsKey(Key(r.MemberId, r.MemberClass))).ToList();
            var free = registrations.Where(r => !reserves.Contains(r.MemberId) && !lockedByKey.ContainsKey(Key(r.MemberId, r.MemberClass))).ToList();

            var existing = new Dictionary<int, List<DateTime>>();
            foreach (var l in keptLocked)
            {
                if (!existing.TryGetValue(l.MemberId, out var list)) existing[l.MemberId] = list = new List<DateTime>();
                list.Add(l.StartTime);
            }

            // De låsta tar plats i sina patruller: generatorn fyller bara det som är kvar, så att ingen patrull blir för stor.
            var taken = keptLocked.GroupBy(l => (FaltskyttePatrolGenerator.GroupKey(l.ShootingClass, settings.WeaponGrouping), l.StartTime)).ToDictionary(g => g.Key, g => g.Count());
            int occupied(string group, DateTime time) => taken.GetValueOrDefault((group, time));

            var gen = new FaltskyttePatrolGenerator().Generate(free, settings.PatrolSize, settings.IntervalMinutes, settings.FirstStart,
                settings.WeaponGrouping, settings.MultiClassGapMinutes, existing, constraints, occupied);
            var patrols = gen.Patrols;
            var overflow = new List<string>();

            foreach (var l in keptLocked.OrderBy(l => l.StartTime).ThenBy(l => l.Name, StringComparer.Ordinal))
            {
                var group = FaltskyttePatrolGenerator.GroupKey(l.ShootingClass, settings.WeaponGrouping);
                var target = patrols.FirstOrDefault(p => p.StartTime == l.StartTime && p.WeaponGroup == group && p.Members.Count < settings.PatrolSize);
                if (target == null)
                {
                    // Ingen patrull vid den tiden med plats: en egen patrull vid samma tid. Fanns det en full patrull där redovisas det.
                    if (patrols.Any(p => p.StartTime == l.StartTime && p.WeaponGroup == group)) overflow.Add(l.Name);
                    target = new FaltskytteGeneratedPatrol { StartTime = l.StartTime, WeaponGroup = group };
                    patrols.Add(target);
                }
                target.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = l.MemberId, Name = l.Name, Club = l.Club, ShootingClass = l.ShootingClass });
            }

            // Numrering i tidsordning (grupperna i generatorns ordning vid samma tid), och platserna 1..n i patrullen.
            var order = patrols.Select((p, i) => (p, i)).OrderBy(x => x.p.StartTime).ThenBy(x => x.i).Select(x => x.p).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                order[i].PatrolNumber = i + 1;
                for (var m = 0; m < order[i].Members.Count; m++) order[i].Members[m].Position = m + 1;
            }

            return new PatrolPlan
            {
                Patrols = order,
                Unavoidable = gen.Unavoidable,
                ReservesLeftOut = reserveRegs.Select(r => r.MemberName ?? $"Medlem {r.MemberId}").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                LockedKept = keptLocked.Count,
                LockedDropped = dropped,
                LockedOverflow = overflow,
            };
        }

        /// <summary>Rymmer gruppen klassen? Etiketten bär vapengruppens kod ("C", "A_Opt") — för magnumfält klassens id ("M1"). Tom lista = alla.</summary>
        private static bool Accepts(PatrolGroupSpec g, string? cls)
        {
            if (g.WeaponCodes.Count == 0) return true;
            var sc = ShootingClasses.GetById(cls ?? "") ?? ShootingClasses.GetByName(cls ?? "");
            return g.WeaponCodes.Contains(ShootingClasses.GetWeaponClassCode(cls), StringComparer.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(cls) && g.WeaponCodes.Contains(cls, StringComparer.OrdinalIgnoreCase))
                || (sc != null && g.WeaponCodes.Contains(sc.Id, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Ombyggnad inom de grupper som redan finns: varje grupp byggs med SIN första start, sitt avstånd och sin patrullstorlek; en skytt flyttas aldrig till en annan
        /// grupp. Anmälda utan grupp redovisas. Låsta (och de som redan har en passande start) ligger kvar i sin grupp på sin tid.
        /// </summary>
        private static PatrolPlan PlanByGroups(IReadOnlyList<CompetitionRegistration> registrations, IReadOnlyCollection<LockedPlacement> locked,
            PatrolConstraints constraints, ISet<int> reserves, PatrolPlanSettings settings, IReadOnlyList<PatrolGroupSpec> groupSpecs)
        {
            var groups = groupSpecs.OrderBy(g => g.FirstStart).ThenBy(g => g.Label, StringComparer.Ordinal).ToList();
            var lockedByKey = new Dictionary<string, LockedPlacement>();
            foreach (var l in locked) lockedByKey[Key(l.MemberId, l.ShootingClass)] = l;
            var regKeys = registrations.Select(r => Key(r.MemberId, r.MemberClass)).ToHashSet();
            var keptLocked = lockedByKey.Where(kv => regKeys.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            var dropped = lockedByKey.Where(kv => !regKeys.Contains(kv.Key)).Select(kv => kv.Value.Name).OrderBy(n => n).ToList();
            var reserveRegs = registrations.Where(r => reserves.Contains(r.MemberId) && !lockedByKey.ContainsKey(Key(r.MemberId, r.MemberClass))).ToList();

            PatrolGroupSpec? GroupOfLocked(LockedPlacement l) => groups.FirstOrDefault(g => g.Label == (l.WeaponGroup ?? "")) ?? groups.FirstOrDefault(g => Accepts(g, l.ShootingClass));
            PatrolGroupSpec? GroupOfReg(CompetitionRegistration r) => groups.FirstOrDefault(g => Accepts(g, r.MemberClass));

            var free = registrations.Where(r => !reserves.Contains(r.MemberId) && !lockedByKey.ContainsKey(Key(r.MemberId, r.MemberClass))).ToList();
            var ungrouped = free.Where(r => GroupOfReg(r) == null).Select(r => $"{r.MemberName ?? $"Medlem {r.MemberId}"} ({ShootingClasses.DisplayName(r.MemberClass)})").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

            // Tider som personer redan har (låsta, och det som placeras i tidigare grupper) — mellanrumskontrollen för den som skjuter i flera klasser.
            var memberTimes = new Dictionary<int, List<DateTime>>();
            void Note(int id, DateTime t) { if (!memberTimes.TryGetValue(id, out var l)) memberTimes[id] = l = new List<DateTime>(); l.Add(t); }
            foreach (var l in keptLocked) Note(l.MemberId, l.StartTime);

            var allPatrols = new List<(FaltskytteGeneratedPatrol P, int GroupIdx)>();
            var unavoidable = new List<FaltskytteUnavoidable>();
            var overflow = new List<string>();
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var lockedG = keptLocked.Where(l => GroupOfLocked(l) == g).OrderBy(l => l.StartTime).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
                var freeG = free.Where(r => GroupOfReg(r) == g).ToList();
                var taken = lockedG.GroupBy(l => l.StartTime).ToDictionary(x => x.Key, x => x.Count());
                var gen = new FaltskyttePatrolGenerator().Generate(freeG, g.PatrolSize, g.IntervalMinutes, g.FirstStart, "MixAll", settings.MultiClassGapMinutes,
                    memberTimes.ToDictionary(kv => kv.Key, kv => new List<DateTime>(kv.Value)), constraints, (_, t) => taken.GetValueOrDefault(t));
                var patrols = gen.Patrols;
                foreach (var p in patrols) p.WeaponGroup = g.Label;
                unavoidable.AddRange(gen.Unavoidable);
                foreach (var p in patrols) foreach (var m in p.Members) Note(m.MemberId, p.StartTime ?? g.FirstStart);

                foreach (var l in lockedG)
                {
                    var target = patrols.FirstOrDefault(p => p.StartTime == l.StartTime && p.Members.Count < g.PatrolSize);
                    if (target == null)
                    {
                        if (patrols.Any(p => p.StartTime == l.StartTime)) overflow.Add(l.Name);
                        target = new FaltskytteGeneratedPatrol { StartTime = l.StartTime, WeaponGroup = g.Label };
                        patrols.Add(target);
                    }
                    target.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = l.MemberId, Name = l.Name, Club = l.Club, ShootingClass = l.ShootingClass });
                }
                allPatrols.AddRange(patrols.Select(p => (p, gi)));
            }

            var order = allPatrols.OrderBy(x => x.P.StartTime).ThenBy(x => x.GroupIdx).Select(x => x.P).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                order[i].PatrolNumber = i + 1;
                for (var m = 0; m < order[i].Members.Count; m++) order[i].Members[m].Position = m + 1;
            }

            return new PatrolPlan
            {
                Patrols = order,
                Unavoidable = unavoidable,
                ReservesLeftOut = reserveRegs.Select(r => r.MemberName ?? $"Medlem {r.MemberId}").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                LockedKept = keptLocked.Count,
                LockedDropped = dropped,
                LockedOverflow = overflow,
                Ungrouped = ungrouped,
            };
        }
    }
}
