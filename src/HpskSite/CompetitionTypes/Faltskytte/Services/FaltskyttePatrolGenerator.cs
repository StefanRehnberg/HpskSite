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
        /// </summary>
        public FaltskyttePatrolGenerationResult Generate(
            List<CompetitionRegistration> registrations,
            int patrolSize,
            int patrolIntervalMinutes,
            DateTime? firstStartTime)
        {
            if (!registrations.Any())
                return new FaltskyttePatrolGenerationResult { Patrols = new(), Message = "Inga anmälningar." };

            // Deduplicate: one entry per member (take first registration if multi-class)
            var uniqueRegistrations = registrations
                .GroupBy(r => r.MemberId)
                .Select(g => g.First())
                .ToList();

            // Group by weapon group
            var groups = uniqueRegistrations
                .GroupBy(r => GetWeaponGroup(r.MemberClass))
                .OrderBy(g => GetGroupSortOrder(g.Key))
                .ToList();

            var patrols = new List<FaltskytteGeneratedPatrol>();
            int patrolNumber = 1;
            var currentTime = firstStartTime ?? DateTime.Today.AddHours(9);

            foreach (var group in groups)
            {
                var members = group.OrderBy(r => r.MemberName).ToList();
                var weaponGroup = group.Key;

                // Fill patrols
                for (int i = 0; i < members.Count; i += patrolSize)
                {
                    var patrolMembers = members.Skip(i).Take(patrolSize).ToList();
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

            return new FaltskyttePatrolGenerationResult
            {
                Patrols = patrols,
                TotalPatrols = patrols.Count,
                TotalShooters = uniqueRegistrations.Count,
                Message = $"{patrols.Count} patruller skapade med {uniqueRegistrations.Count} skyttar."
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
