using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    public class FaltskyttePatrolGenerator
    {
        /// <summary>Så många platser (patruller) söks igenom för en skytt innan det räknas som omöjligt — hindrar en oändlig sökning.</summary>
        public const int MaxSlotsSearched = 600;

        /// <summary>Hur många timmar efter första starten en plats får ligga — en start dagen efter är aldrig ett svar på en krock.</summary>
        public const int MaxSearchHours = 14;

        /// <summary>
        /// Generates patrols from competition registrations.
        /// Respects multiClassGapMinutes between patrols for multi-class shooters,
        /// including existing patrols from previous generation runs.
        /// </summary>
        public FaltskyttePatrolGenerationResult Generate(
            List<CompetitionRegistration> registrations,
            int patrolSize,
            int patrolIntervalMinutes,
            DateTime? firstStartTime,
            string weaponGrouping = "Separate",
            int multiClassGapMinutes = 0,
            Dictionary<int, List<DateTime>>? existingMemberStartTimes = null,
            PatrolConstraints? constraints = null,
            Func<string, DateTime, int>? occupied = null)
        {
            if (patrolSize < 1) patrolSize = 6;
            if (patrolIntervalMinutes < 1) patrolIntervalMinutes = 15;

            if (!registrations.Any())
                return new FaltskyttePatrolGenerationResult { Patrols = new(), Message = "Inga anmälningar." };

            // Group by weapon group
            IEnumerable<IGrouping<string, CompetitionRegistration>> groups;
            if (weaponGrouping == "MixAll")
            {
                groups = registrations.GroupBy(r => "Alla").ToList();
            }
            else if (weaponGrouping == "CombineAR")
            {
                groups = registrations
                    .GroupBy(r => { var wg = GetWeaponGroup(r.MemberClass); return (wg == "A" || wg == "R") ? "A+R" : wg; })
                    .OrderBy(g => GetGroupSortOrder(g.Key)).ToList();
            }
            else
            {
                groups = registrations
                    .GroupBy(r => GetWeaponGroup(r.MemberClass))
                    .OrderBy(g => GetGroupSortOrder(g.Key)).ToList();
            }

            var startTime = firstStartTime ?? DateTime.Today.AddHours(9);
            var gapMinutes = multiClassGapMinutes > 0 ? multiClassGapMinutes : 0;

            // Build a mutable copy of known member times (existing + placed during this run)
            var memberTimes = new Dictionary<int, List<DateTime>>();
            if (existingMemberStartTimes != null)
            {
                foreach (var kvp in existingMemberStartTimes)
                    memberTimes[kvp.Key] = new List<DateTime>(kvp.Value);
            }

            // Identify members that appear more than once (across all registrations or have existing times)
            var memberRegCount = registrations.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Count());
            bool NeedsGap(int memberId) => gapMinutes > 0 && (memberRegCount.GetValueOrDefault(memberId, 0) > 1 || memberTimes.ContainsKey(memberId));
            // Planera dagen: den som har uppdrag (eller ska gå tidigt) placeras som de som behöver mellanrum — i tur och ordning, på första plats som passar.
            bool Constrained(int memberId) => NeedsGap(memberId) || (constraints?.IsConstrained(memberId) ?? false);
            var unavoidable = new List<FaltskytteUnavoidable>();

            var patrols = new List<FaltskytteGeneratedPatrol>();
            int patrolNumber = 1;
            var currentTime = startTime;

            foreach (var group in groups)
            {
                var allMembers = group.OrderBy(r => r.MemberName).ToList();
                var weaponGroup = group.Key;

                // Split: unconstrained first (fill normally), then constrained (need gap check)
                var unconstrained = allMembers.Where(r => !Constrained(r.MemberId)).ToList();
                var constrained = allMembers.Where(r => Constrained(r.MemberId)).ToList();

                // Sort constrained by weapon priority so first weapon class goes early. De som ska gå tidigt (funktionärspatrull, skjuter först)
                // placeras allra först i sin grupp, så de får de första platserna som passar.
                constrained = constrained
                    .OrderBy(r => constraints?.IsEarly(r.MemberId) == true ? 0 : 1)
                    .ThenBy(r => GetGroupSortOrder(GetWeaponGroup(r.MemberClass)))
                    .ThenBy(r => r.MemberName)
                    .ToList();

                var patrolSlots = new List<List<CompetitionRegistration>>();

                // Platser som redan är upptagna av fasta starter (låsta) räknas mot patrullens storlek.
                bool Full(int s) => (s < patrolSlots.Count ? patrolSlots[s].Count : 0)
                    + (occupied?.Invoke(weaponGroup, currentTime.AddMinutes(s * patrolIntervalMinutes)) ?? 0) >= patrolSize;

                int FindNextSlot(int from)
                {
                    while (Full(from))
                        from++;
                    while (patrolSlots.Count <= from)
                        patrolSlots.Add(new List<CompetitionRegistration>());
                    return from;
                }

                void PlaceConstrained(CompetitionRegistration reg)
                {
                    var myTimes = memberTimes.TryGetValue(reg.MemberId, out var t) ? t : null;

                    int bestSlot = -1;
                    for (int s = 0; s <= MaxSlotsSearched; s++)
                    {
                        var slotTime = currentTime.AddMinutes(s * patrolIntervalMinutes);
                        if (slotTime > startTime.AddHours(MaxSearchHours)) break;
                        while (patrolSlots.Count <= s)
                            patrolSlots.Add(new List<CompetitionRegistration>());
                        if (Full(s)) continue;

                        bool ok = true;
                        if (myTimes != null && gapMinutes > 0)
                        {
                            foreach (var et in myTimes)
                            {
                                if (Math.Abs((slotTime - et).TotalMinutes) < gapMinutes)
                                { ok = false; break; }
                            }
                        }
                        if (ok && constraints != null && constraints.Blocks(reg.MemberId, slotTime)) ok = false;
                        if (ok) { bestSlot = s; break; }
                    }
                    if (bestSlot < 0)
                    {
                        // Ingen plats går att få utan krock (uppdraget täcker hela dagen): första lediga plats, och det redovisas — aldrig tyst.
                        bestSlot = FindNextSlot(0);
                        unavoidable.Add(new FaltskytteUnavoidable { MemberId = reg.MemberId, Name = reg.MemberName ?? "Okänd", StartTime = currentTime.AddMinutes(bestSlot * patrolIntervalMinutes) });
                    }

                    patrolSlots[bestSlot].Add(reg);

                    // Record this placement so subsequent registrations for the same member see it
                    var placedTime = currentTime.AddMinutes(bestSlot * patrolIntervalMinutes);
                    if (!memberTimes.ContainsKey(reg.MemberId))
                        memberTimes[reg.MemberId] = new List<DateTime>();
                    memberTimes[reg.MemberId].Add(placedTime);
                }

                // De som ska gå tidigt får de första platserna (före alla andra), sedan fylls resten normalt, sist övriga med uppdrag eller mellanrum.
                foreach (var reg in constrained.Where(r => constraints?.IsEarly(r.MemberId) == true))
                    PlaceConstrained(reg);

                foreach (var reg in unconstrained)
                {
                    var slot = FindNextSlot(0);
                    patrolSlots[slot].Add(reg);
                }

                foreach (var reg in constrained.Where(r => constraints?.IsEarly(r.MemberId) != true))
                    PlaceConstrained(reg);

                // Convert non-empty slots to patrols
                for (int s = 0; s < patrolSlots.Count; s++)
                {
                    if (patrolSlots[s].Count == 0) continue;
                    var slotTime = currentTime.AddMinutes(s * patrolIntervalMinutes);
                    patrols.Add(new FaltskytteGeneratedPatrol
                    {
                        PatrolNumber = patrolNumber,
                        StartTime = slotTime,
                        WeaponGroup = weaponGroup,
                        Members = patrolSlots[s].Select((r, idx) => new FaltskytteGeneratedPatrolMember
                        {
                            MemberId = r.MemberId,
                            Position = idx + 1,
                            Name = r.MemberName ?? "Okänd",
                            Club = r.MemberClub ?? "",
                            ShootingClass = r.MemberClass
                        }).ToList()
                    });
                    patrolNumber++;
                }

                // Advance currentTime past the last slot
                if (patrolSlots.Count > 0)
                    currentTime = currentTime.AddMinutes(patrolSlots.Count * patrolIntervalMinutes);
            }

            return new FaltskyttePatrolGenerationResult
            {
                Patrols = patrols,
                Unavoidable = unavoidable,
                TotalPatrols = patrols.Count,
                TotalShooters = registrations.Count,
                Message = $"{patrols.Count} patruller skapade med {registrations.Count} starter."
            };
        }

        /// <summary>Vilken grupp en skytt hamnar i vid given gruppering — samma regel som <see cref="Generate"/> grupperar efter.</summary>
        public static string GroupKey(string shootingClass, string weaponGrouping)
        {
            if (weaponGrouping == "MixAll") return "Alla";
            var wg = GetWeaponGroup(shootingClass);
            if (weaponGrouping == "CombineAR") return (wg == "A" || wg == "R") ? "A+R" : wg;
            return wg;
        }

        private static string GetWeaponGroup(string shootingClass)
        {
            // No substring fallback — that would mis-categorize A_opt_X as plain A.
            var code = ShootingClasses.GetWeaponClassCode(shootingClass);
            return string.IsNullOrEmpty(code) ? "?" : code;
        }

        private static int GetGroupSortOrder(string weaponGroup) => weaponGroup switch
        {
            "C" => 1, "B" => 2, "A" => 3, "A_Opt" => 4, "R" => 5, "M" => 6, _ => 99
        };
    }

    // ── Generation result models ────────────────────────────────

    /// <summary>En skytt som inte gick att placera utan krock mot ett uppdrag (uppdraget täcker alla platser).</summary>
    public class FaltskytteUnavoidable
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// Planera dagen: det startlistan ska bygga runt. <see cref="Jobs"/> = personens uppdrag (upptagen tid); <see cref="Shoot"/> = hur länge en start tar
    /// (före, skjuttid, efter); <see cref="Early"/> = de som ska gå tidigt i sin grupp (funktionärspatrull, skjuter först).
    /// </summary>
    public sealed class PatrolConstraints
    {
        public IReadOnlyDictionary<int, List<HpskSite.Models.Staffing.StaffInterval>> Jobs { get; init; } = new Dictionary<int, List<HpskSite.Models.Staffing.StaffInterval>>();
        public HpskSite.Models.Staffing.ShootTime Shoot { get; init; } = HpskSite.Models.Staffing.ShootTimeRules.Default("Faltskytte");
        public ISet<int> Early { get; init; } = new HashSet<int>();

        public bool IsConstrained(int memberId) => Jobs.ContainsKey(memberId) || Early.Contains(memberId);
        public bool IsEarly(int memberId) => Early.Contains(memberId);

        /// <summary>Hindrar ett uppdrag att personen startar vid <paramref name="startTime"/>? Fönstret är förberedelse + skjuttid + vila.</summary>
        public bool Blocks(int memberId, DateTime startTime)
        {
            if (!Jobs.TryGetValue(memberId, out var jobs)) return false;
            var window = Shoot.Window(startTime, null);
            return jobs.Any(j => j.Overlaps(window));
        }
    }

    public class FaltskyttePatrolGenerationResult
    {
        public List<FaltskytteGeneratedPatrol> Patrols { get; set; } = new();
        /// <summary>Skyttar som inte gick att placera utan krock mot ett uppdrag. Tom = ingen krock kvar.</summary>
        public List<FaltskytteUnavoidable> Unavoidable { get; set; } = new();
        public int TotalPatrols { get; set; }
        public int TotalShooters { get; set; }
        public string Message { get; set; } = "";
    }

    public class FaltskytteGeneratedPatrol
    {
        public int PatrolNumber { get; set; }
        public DateTime? StartTime { get; set; }
        public string? WeaponGroup { get; set; }
        public List<FaltskytteGeneratedPatrolMember> Members { get; set; } = new();
    }

    public class FaltskytteGeneratedPatrolMember
    {
        public int MemberId { get; set; }
        public int Position { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
    }
}
