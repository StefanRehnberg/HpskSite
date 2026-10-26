using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>En skytt vars patrull och tid ligger fast (lås). Bygger om-körningen runt den, aldrig över den.</summary>
    public sealed record LockedPlacement(int MemberId, string Name, string Club, string ShootingClass, DateTime StartTime, string? WeaponGroup);

    public sealed class PatrolPlanSettings
    {
        public int PatrolSize { get; init; } = 6;
        public int IntervalMinutes { get; init; } = 15;
        public DateTime? FirstStart { get; init; }
        public string WeaponGrouping { get; init; } = "Separate";
        public int MultiClassGapMinutes { get; init; }
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
            PatrolConstraints constraints, ISet<int> reserves, PatrolPlanSettings settings)
        {
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

            var gen = new FaltskyttePatrolGenerator().Generate(free, settings.PatrolSize, settings.IntervalMinutes, settings.FirstStart,
                settings.WeaponGrouping, settings.MultiClassGapMinutes, existing, constraints);
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
    }
}
