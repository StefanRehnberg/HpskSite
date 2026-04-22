using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    public class FaltskyttePatrolGenerator
    {
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
            Dictionary<int, List<DateTime>>? existingMemberStartTimes = null)
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

            var patrols = new List<FaltskytteGeneratedPatrol>();
            int patrolNumber = 1;
            var currentTime = startTime;

            foreach (var group in groups)
            {
                var allMembers = group.OrderBy(r => r.MemberName).ToList();
                var weaponGroup = group.Key;

                // Split: unconstrained first (fill normally), then constrained (need gap check)
                var unconstrained = allMembers.Where(r => !NeedsGap(r.MemberId)).ToList();
                var constrained = allMembers.Where(r => NeedsGap(r.MemberId)).ToList();

                // Sort constrained by weapon priority so first weapon class goes early
                constrained = constrained
                    .OrderBy(r => GetGroupSortOrder(GetWeaponGroup(r.MemberClass)))
                    .ThenBy(r => r.MemberName)
                    .ToList();

                var patrolSlots = new List<List<CompetitionRegistration>>();

                int FindNextSlot(int from)
                {
                    while (from < patrolSlots.Count && patrolSlots[from].Count >= patrolSize)
                        from++;
                    while (patrolSlots.Count <= from)
                        patrolSlots.Add(new List<CompetitionRegistration>());
                    return from;
                }

                // Place unconstrained shooters first
                foreach (var reg in unconstrained)
                {
                    var slot = FindNextSlot(0);
                    patrolSlots[slot].Add(reg);
                }

                // Place constrained shooters respecting time gap
                foreach (var reg in constrained)
                {
                    var myTimes = memberTimes.TryGetValue(reg.MemberId, out var t) ? t : null;

                    int bestSlot = -1;
                    for (int s = 0; ; s++)
                    {
                        while (patrolSlots.Count <= s)
                            patrolSlots.Add(new List<CompetitionRegistration>());
                        if (patrolSlots[s].Count >= patrolSize) continue;

                        var slotTime = currentTime.AddMinutes(s * patrolIntervalMinutes);
                        bool ok = true;
                        if (myTimes != null)
                        {
                            foreach (var et in myTimes)
                            {
                                if (Math.Abs((slotTime - et).TotalMinutes) < gapMinutes)
                                { ok = false; break; }
                            }
                        }
                        if (ok) { bestSlot = s; break; }
                    }

                    patrolSlots[bestSlot].Add(reg);

                    // Record this placement so subsequent registrations for the same member see it
                    var placedTime = currentTime.AddMinutes(bestSlot * patrolIntervalMinutes);
                    if (!memberTimes.ContainsKey(reg.MemberId))
                        memberTimes[reg.MemberId] = new List<DateTime>();
                    memberTimes[reg.MemberId].Add(placedTime);
                }

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
                TotalPatrols = patrols.Count,
                TotalShooters = registrations.Count,
                Message = $"{patrols.Count} patruller skapade med {registrations.Count} starter."
            };
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

    public class FaltskyttePatrolGenerationResult
    {
        public List<FaltskytteGeneratedPatrol> Patrols { get; set; } = new();
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
