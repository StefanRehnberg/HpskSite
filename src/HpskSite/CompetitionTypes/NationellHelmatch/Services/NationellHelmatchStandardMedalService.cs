using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;

namespace HpskSite.CompetitionTypes.NationellHelmatch.Services
{
    /// <summary>
    /// Standard medal calculation for Nationell Helmatch competitions.
    /// Uses ONLY percentage-based placement medals (top 1/9 Silver, top 1/3 Bronze).
    /// No fixed-score thresholds are applied (same pattern as Duell).
    /// Minimum series count: 12 (Nationell Helmatch always has 12 series).
    /// </summary>
    public class NationellHelmatchStandardMedalService
    {
        /// <summary>
        /// Calculate standard medals for Nationell Helmatch shooters.
        /// Placement-only: top 1/9 Silver, top 1/3 Bronze per weapon group.
        /// </summary>
        public void CalculateStandardMedals(List<PrecisionShooterResult> shooters, StandardMedalConfig config)
        {
            if (shooters == null || !shooters.Any() || config.SeriesCount < 12)
                return;

            // Group shooters by weapon group (and classification for Group C in SM/Landsdel)
            var groups = GroupByWeaponGroup(shooters, config.ShouldSplitGroupC);

            foreach (var group in groups)
            {
                ApplyPercentageMedals(group.Value);
            }
        }

        private Dictionary<string, List<PrecisionShooterResult>> GroupByWeaponGroup(
            List<PrecisionShooterResult> shooters, bool shouldSplitGroupC)
        {
            var groups = new Dictionary<string, List<PrecisionShooterResult>>();

            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);

                string groupKey;
                if (shouldSplitGroupC && weaponGroup == "C")
                {
                    var classification = ExtractClassification(shooter.ShootingClass);
                    groupKey = classification != null ? $"C-{classification}" : "C-Öppen";
                }
                else
                {
                    groupKey = weaponGroup;
                }

                if (!groups.ContainsKey(groupKey))
                    groups[groupKey] = new List<PrecisionShooterResult>();

                groups[groupKey].Add(shooter);
            }

            return groups;
        }

        /// <summary>
        /// Apply percentage-based medals only (no fixed-score thresholds).
        /// </summary>
        private void ApplyPercentageMedals(List<PrecisionShooterResult> groupShooters)
        {
            if (!groupShooters.Any())
                return;

            var sortedShooters = groupShooters
                .OrderByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.TotalXCount)
                .ToList();

            int shooterCount = sortedShooters.Count;
            int silverQuota = shooterCount / 9;
            int bronzeQuota = shooterCount / 3;

            // Award Silver to top 1/9
            for (int i = 0; i < silverQuota && i < sortedShooters.Count; i++)
            {
                if (sortedShooters[i].StandardMedal != "B")
                    sortedShooters[i].StandardMedal = "S";
            }

            // Award Bronze to top 1/3
            for (int i = 0; i < bronzeQuota && i < sortedShooters.Count; i++)
            {
                if (string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    sortedShooters[i].StandardMedal = "B";
            }

            // Handle ties at Bronze boundary
            if (bronzeQuota > 0 && bronzeQuota < sortedShooters.Count)
            {
                var lastBronze = sortedShooters[bronzeQuota - 1];
                for (int i = bronzeQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastBronze.TotalScore &&
                        sortedShooters[i].TotalXCount == lastBronze.TotalXCount &&
                        string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    {
                        sortedShooters[i].StandardMedal = "B";
                    }
                    else break;
                }
            }

            // Handle ties at Silver boundary
            if (silverQuota > 0 && silverQuota < sortedShooters.Count)
            {
                var lastSilver = sortedShooters[silverQuota - 1];
                for (int i = silverQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastSilver.TotalScore &&
                        sortedShooters[i].TotalXCount == lastSilver.TotalXCount)
                    {
                        sortedShooters[i].StandardMedal = "S";
                    }
                    else break;
                }
            }
        }

        private static string ExtractWeaponGroup(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass))
                return "C";
            var firstChar = shootingClass.Trim().ToUpper()[0];
            if (firstChar == 'A' || firstChar == 'B' || firstChar == 'C')
                return firstChar.ToString();
            return "C";
        }

        private static string? ExtractClassification(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass))
                return null;
            var upper = shootingClass.ToUpper().Trim();
            if (upper.Contains("DAM")) return "Dam";
            if (upper.Contains("JUN")) return "Jun";
            if (upper.Contains("VET Y") || upper.Contains("VETY")) return "Vet Y";
            if (upper.Contains("VET Ä") || upper.Contains("VETÄ")) return "Vet Ä";
            return null;
        }
    }
}
