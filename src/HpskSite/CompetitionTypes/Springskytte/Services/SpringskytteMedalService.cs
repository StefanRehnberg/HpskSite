using HpskSite.CompetitionTypes.Springskytte.Models;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Standard medal calculation for Springskytte competitions.
    /// Percentage-based placement only: top 1/9 Silver, top 1/3 Bronze per class.
    ///
    /// Medals are awarded per AgeGenderClass within each WeaponClass.
    /// Shooters must be pre-sorted by the SpringskytteTieBreaker.
    /// </summary>
    public class SpringskytteMedalService
    {
        private readonly SpringskytteTieBreaker _tieBreaker = new();

        /// <summary>
        /// Calculate standard medals for all shooters in a competition.
        /// Groups by WeaponClass + AgeGenderClass and awards within each group.
        /// </summary>
        public void CalculateStandardMedals(List<SpringskytteShooterResult> allShooters)
        {
            if (allShooters == null || !allShooters.Any())
                return;

            // Group by WeaponClass + AgeGenderClass
            var groups = allShooters
                .Where(s => s.Status == null && s.TotalTimeSeconds.HasValue)
                .GroupBy(s => $"{s.WeaponClass}|{s.AgeGenderClass}")
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s, _tieBreaker).ToList());

            foreach (var group in groups)
            {
                ApplyPercentageMedals(group.Value);
            }
        }

        private void ApplyPercentageMedals(List<SpringskytteShooterResult> sortedShooters)
        {
            if (!sortedShooters.Any()) return;

            int count = sortedShooters.Count;
            int silverQuota = count / 9;
            int bronzeQuota = count / 3;

            // Award Silver to top 1/9
            for (int i = 0; i < silverQuota && i < sortedShooters.Count; i++)
            {
                sortedShooters[i].StandardMedal = "S";
            }

            // Award Bronze to top 1/3 (skip those already with Silver)
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
                    if (sortedShooters[i].TotalTimeSeconds == lastBronze.TotalTimeSeconds &&
                        sortedShooters[i].ShootingScore == lastBronze.ShootingScore &&
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
                    if (sortedShooters[i].TotalTimeSeconds == lastSilver.TotalTimeSeconds &&
                        sortedShooters[i].ShootingScore == lastSilver.ShootingScore)
                    {
                        sortedShooters[i].StandardMedal = "S";
                    }
                    else break;
                }
            }
        }
    }
}
