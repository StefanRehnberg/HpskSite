using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Calculates standard medals (Standardmedalj) for Fältskytte competitions.
    /// Rules: percentage-based only (no fixed-score thresholds).
    /// Silver = top 1/9, Bronze = top 1/3, with tie rule.
    /// Grouping: by weapon group (A, A Opt, B, C, R), ignoring skill class.
    /// SM/LM exception: C split by championship class (Öppen, Dam, Jun, Vet).
    /// Normal mode: ranked by total hits only. Poäng mode: ranked by total points.
    /// </summary>
    public class FaltskytteStandardMedalService
    {
        /// <summary>
        /// Calculate and assign standard medals to all shooters.
        /// </summary>
        /// <param name="shooters">All shooters with results</param>
        /// <param name="scoringMode">"Normal" or "Poang"</param>
        /// <param name="stationCount">Must be >= 6 for medals to apply</param>
        /// <param name="isChampionship">If true, split C by championship class (SM/LM)</param>
        public void CalculateStandardMedals(
            List<FaltskytteShooterResult> shooters,
            string scoringMode,
            int stationCount,
            bool isChampionship = false)
        {
            if (shooters == null || !shooters.Any() || stationCount < 6)
                return;

            bool isPoang = scoringMode.Equals("Poang", StringComparison.OrdinalIgnoreCase);

            // Group by weapon group (ignoring skill class)
            var groups = GroupByWeaponGroup(shooters, isChampionship);

            foreach (var group in groups)
            {
                CalculateMedalsForGroup(group.Value, isPoang);
            }
        }

        private Dictionary<string, List<FaltskytteShooterResult>> GroupByWeaponGroup(
            List<FaltskytteShooterResult> shooters, bool splitGroupC)
        {
            var groups = new Dictionary<string, List<FaltskytteShooterResult>>();

            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);
                string groupKey;

                if (splitGroupC && weaponGroup == "C")
                {
                    // SM/LM: split C by championship classification
                    var classification = ExtractClassification(shooter.ShootingClass);
                    groupKey = classification != null ? $"C-{classification}" : "C-Öppen";
                }
                else
                {
                    groupKey = weaponGroup;
                }

                if (!groups.ContainsKey(groupKey))
                    groups[groupKey] = new List<FaltskytteShooterResult>();
                groups[groupKey].Add(shooter);
            }

            return groups;
        }

        private void CalculateMedalsForGroup(List<FaltskytteShooterResult> groupShooters, bool isPoang)
        {
            if (!groupShooters.Any()) return;

            // Sort by the medal-relevant score:
            // Normal: total hits only (figures don't matter for standard medal)
            // Poäng: total points (hits + figures)
            List<FaltskytteShooterResult> sorted;
            if (isPoang)
            {
                sorted = groupShooters.OrderByDescending(s => s.TotalPoints).ToList();
            }
            else
            {
                sorted = groupShooters.OrderByDescending(s => s.TotalHits).ToList();
            }

            int count = sorted.Count;
            int silverQuota = count / 9;  // Top 1/9, rounded down
            int bronzeQuota = count / 3;  // Top 1/3, rounded down

            // Award Silver to top 1/9
            for (int i = 0; i < silverQuota && i < count; i++)
            {
                sorted[i].StandardMedal = "S";
            }

            // Tie rule for Silver: same score as last silver recipient
            if (silverQuota > 0 && silverQuota < count)
            {
                var lastSilverScore = GetMedalScore(sorted[silverQuota - 1], isPoang);
                for (int i = silverQuota; i < count; i++)
                {
                    if (GetMedalScore(sorted[i], isPoang) == lastSilverScore)
                        sorted[i].StandardMedal = "S";
                    else
                        break;
                }
            }

            // Award Bronze to top 1/3 (only if no medal yet — Silver is better)
            for (int i = 0; i < bronzeQuota && i < count; i++)
            {
                if (string.IsNullOrEmpty(sorted[i].StandardMedal))
                    sorted[i].StandardMedal = "B";
            }

            // Tie rule for Bronze: same score as last bronze recipient
            if (bronzeQuota > 0 && bronzeQuota < count)
            {
                var lastBronzeScore = GetMedalScore(sorted[bronzeQuota - 1], isPoang);
                for (int i = bronzeQuota; i < count; i++)
                {
                    if (GetMedalScore(sorted[i], isPoang) == lastBronzeScore &&
                        string.IsNullOrEmpty(sorted[i].StandardMedal))
                    {
                        sorted[i].StandardMedal = "B";
                    }
                    else if (GetMedalScore(sorted[i], isPoang) != lastBronzeScore)
                    {
                        break;
                    }
                }
            }
        }

        private static int GetMedalScore(FaltskytteShooterResult shooter, bool isPoang)
        {
            return isPoang ? shooter.TotalPoints : shooter.TotalHits;
        }

        private static string ExtractWeaponGroup(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass)) return "?";

            // "A Opt" / "A Optisk" is its own weapon group
            if (shootingClass.StartsWith("A Opt", StringComparison.OrdinalIgnoreCase))
                return "A Opt";

            // Use ShootingClasses lookup first
            var sc = ShootingClasses.GetByName(shootingClass) ?? ShootingClasses.GetById(shootingClass);
            if (sc != null) return sc.Weapon.ToString();

            // Fallback: first letter
            return shootingClass.Substring(0, 1);
        }

        private static string? ExtractClassification(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass)) return null;
            var lower = shootingClass.ToLower();
            if (lower.Contains("dam")) return "Dam";
            if (lower.Contains("jun")) return "Junior";
            if (lower.Contains("vet")) return "Veteran";
            return null; // Öppen (open class)
        }
    }
}
