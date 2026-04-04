using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    public class FaltskyttePatrolGenerator
    {
        /// <summary>
        /// Generates patrols from competition registrations.
        /// Groups by weapon group, fills patrols up to patrolSize, assigns start times.
        /// Multi-class shooters are separated by at least multiClassGapMinutes.
        /// </summary>
        public FaltskyttePatrolGenerationResult Generate(
            List<CompetitionRegistration> registrations,
            int patrolSize,
            int patrolIntervalMinutes,
            DateTime? firstStartTime,
            string weaponGrouping = "Separate",
            int multiClassGapMinutes = 0)
        {
            if (!registrations.Any())
                return new FaltskyttePatrolGenerationResult { Patrols = new(), Message = "Inga anmälningar." };

            // Each registration is a separate patrol entry (one per member per weapon class)
            // Group by weapon group based on grouping strategy
            IEnumerable<IGrouping<string, CompetitionRegistration>> groups;
            if (weaponGrouping == "MixAll")
            {
                groups = registrations
                    .GroupBy(r => "Alla")
                    .ToList();
            }
            else if (weaponGrouping == "CombineAR")
            {
                groups = registrations
                    .GroupBy(r =>
                    {
                        var wg = GetWeaponGroup(r.MemberClass);
                        return (wg == "A" || wg == "R") ? "A+R" : wg;
                    })
                    .OrderBy(g => GetGroupSortOrder(g.Key))
                    .ToList();
            }
            else
            {
                groups = registrations
                    .GroupBy(r => GetWeaponGroup(r.MemberClass))
                    .OrderBy(g => GetGroupSortOrder(g.Key))
                    .ToList();
            }

            // Identify multi-class shooters (same MemberId in multiple registrations)
            var multiClassMembers = registrations
                .GroupBy(r => r.MemberId)
                .Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.Select(r => r.MemberClass).ToList());

            // Minimum patrol gap for multi-class shooters
            int minPatrolGap = multiClassGapMinutes > 0 && patrolIntervalMinutes > 0
                ? (int)Math.Ceiling((double)multiClassGapMinutes / patrolIntervalMinutes)
                : 0;

            var patrols = new List<FaltskytteGeneratedPatrol>();
            int patrolNumber = 1;
            var currentTime = firstStartTime ?? DateTime.Today.AddHours(9);

            foreach (var group in groups)
            {
                var allMembers = group.OrderBy(r => r.MemberName).ToList();
                var weaponGroup = group.Key;

                // Check if this group actually has multi-class shooters
                var groupMultiClass = allMembers.Where(r => multiClassMembers.ContainsKey(r.MemberId)).ToList();
                var groupMultiByMember = groupMultiClass
                    .GroupBy(r => r.MemberId)
                    .Where(g => g.Count() > 1) // Only members with 2+ registrations IN THIS GROUP
                    .ToDictionary(g => g.Key, g => g.OrderBy(r => GetGroupSortOrder(GetWeaponGroup(r.MemberClass))).ToList());

                if (minPatrolGap > 0 && groupMultiByMember.Any())
                {
                    // Separation-aware patrol filling:
                    var singleClass = allMembers.Where(r => !groupMultiByMember.ContainsKey(r.MemberId)).ToList();
                    var multiClass = allMembers.Where(r => groupMultiByMember.ContainsKey(r.MemberId)).ToList();

                    var multiByMember = groupMultiByMember;

                    // Split into rounds: round 0 = each member's first weapon, round 1 = second weapon, etc.
                    int maxRounds = multiByMember.Values.Max(v => v.Count);
                    var rounds = new List<List<CompetitionRegistration>>();
                    for (int round = 0; round < maxRounds; round++)
                    {
                        var batch = new List<CompetitionRegistration>();
                        foreach (var kvp in multiByMember)
                        {
                            if (round < kvp.Value.Count)
                                batch.Add(kvp.Value[round]);
                        }
                        rounds.Add(batch.OrderBy(r => r.MemberName).ToList());
                    }

                    var patrolSlots = new List<List<CompetitionRegistration>>();

                    // Helper: ensure slot exists and find next with space at or after 'from'
                    int FindSlot(int from)
                    {
                        while (from < patrolSlots.Count && patrolSlots[from].Count >= patrolSize)
                            from++;
                        if (from >= patrolSlots.Count)
                            patrolSlots.Add(new List<CompetitionRegistration>());
                        return from;
                    }

                    // Place round 0 (first weapon) interleaved with single-class shooters
                    var firstRound = rounds.Count > 0 ? rounds[0] : new List<CompetitionRegistration>();
                    var combined = new List<CompetitionRegistration>();
                    combined.AddRange(singleClass);
                    combined.AddRange(firstRound);
                    combined = combined.OrderBy(r => r.MemberName).ToList();

                    foreach (var reg in combined)
                    {
                        var slot = FindSlot(0);
                        patrolSlots[slot].Add(reg);
                    }

                    // Track where each member's first weapon landed
                    var memberFirstSlot = new Dictionary<int, int>();
                    for (int s = 0; s < patrolSlots.Count; s++)
                    {
                        foreach (var reg in patrolSlots[s])
                        {
                            if (multiByMember.ContainsKey(reg.MemberId) && !memberFirstSlot.ContainsKey(reg.MemberId))
                                memberFirstSlot[reg.MemberId] = s;
                        }
                    }

                    // Place subsequent rounds as batches, respecting gap from each member's previous placement
                    var memberLastSlot = new Dictionary<int, int>(memberFirstSlot);
                    for (int round = 1; round < rounds.Count; round++)
                    {
                        var batch = rounds[round];
                        // Find the earliest slot any member in this batch can go
                        int batchEarliest = 0;
                        foreach (var reg in batch)
                        {
                            if (memberLastSlot.TryGetValue(reg.MemberId, out var last))
                                batchEarliest = Math.Max(batchEarliest, last + minPatrolGap);
                        }

                        // Place the entire batch together, filling patrols sequentially from batchEarliest
                        foreach (var reg in batch)
                        {
                            var slot = FindSlot(batchEarliest);
                            patrolSlots[slot].Add(reg);
                            memberLastSlot[reg.MemberId] = slot;
                        }
                    }

                    // Convert non-empty slots to patrols
                    foreach (var slot in patrolSlots)
                    {
                        if (slot.Count == 0) continue;
                        patrols.Add(new FaltskytteGeneratedPatrol
                        {
                            PatrolNumber = patrolNumber,
                            StartTime = currentTime,
                            WeaponGroup = weaponGroup,
                            Members = slot.Select((r, idx) => new FaltskytteGeneratedPatrolMember
                            {
                                MemberId = r.MemberId,
                                Position = idx + 1,
                                Name = r.MemberName ?? "Okänd",
                                Club = r.MemberClub ?? "",
                                ShootingClass = r.MemberClass
                            }).ToList()
                        });
                        patrolNumber++;
                        currentTime = currentTime.AddMinutes(patrolIntervalMinutes);
                    }
                }
                else
                {
                    // Simple sequential filling (no multi-class separation needed)
                    for (int i = 0; i < allMembers.Count; i += patrolSize)
                    {
                        var patrolMembers = allMembers.Skip(i).Take(patrolSize).ToList();
                        patrols.Add(new FaltskytteGeneratedPatrol
                        {
                            PatrolNumber = patrolNumber,
                            StartTime = currentTime,
                            WeaponGroup = weaponGroup,
                            Members = patrolMembers.Select((r, idx) => new FaltskytteGeneratedPatrolMember
                            {
                                MemberId = r.MemberId,
                                Position = idx + 1,
                                Name = r.MemberName ?? "Okänd",
                                Club = r.MemberClub ?? "",
                                ShootingClass = r.MemberClass
                            }).ToList()
                        });
                        patrolNumber++;
                        currentTime = currentTime.AddMinutes(patrolIntervalMinutes);
                    }
                }
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
            var sc = ShootingClasses.GetByName(shootingClass)
                ?? ShootingClasses.GetById(shootingClass);
            if (sc != null) return sc.Weapon.ToString();

            // Fallback: first letter
            if (!string.IsNullOrEmpty(shootingClass))
                return shootingClass.Substring(0, 1);
            return "?";
        }

        private static int GetGroupSortOrder(string weaponGroup) => weaponGroup switch
        {
            "C" => 1,
            "B" => 2,
            "A" => 3,
            "R" => 4,
            "M" => 5,
            _ => 99
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
