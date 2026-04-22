using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.MagnumPrecision.Services
{
    /// <summary>
    /// Standard medal calculation for Magnum Precision competitions.
    /// Same algorithm as Precision (percentage + fixed-score, best-of logic)
    /// but with M-class-specific fixed-score thresholds (6 series only).
    ///
    /// Medal thresholds by weapon group:
    ///   M1-M4, M8: Silver 282, Bronze 274
    ///   M5 (Frigrupp): Silver 294, Bronze 288
    ///   M6-M7, M9: Silver 270, Bronze 253
    /// </summary>
    public class MagnumPrecisionStandardMedalService
    {
        /// <summary>
        /// Main entry point: Calculate and assign standard medals to all shooters
        /// </summary>
        public void CalculateStandardMedals(List<PrecisionShooterResult> shooters, StandardMedalConfig config)
        {
            if (shooters == null || !shooters.Any() || config.SeriesCount < 6)
                return;

            // Group shooters by weapon group (individual M class for medals)
            var groups = GroupByWeaponGroup(shooters, config.ShouldSplitGroupC);

            foreach (var group in groups)
            {
                CalculateMedalsForGroup(group.Value, config.SeriesCount);
            }
        }

        private void CalculateMedalsForGroup(List<PrecisionShooterResult> groupShooters, int seriesCount)
        {
            if (!groupShooters.Any())
                return;

            var sortedShooters = groupShooters
                .OrderByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.TotalXCount)
                .ToList();

            // Method A: Percentage-based medals (top 1/9 silver, top 1/3 bronze)
            ApplyPercentageMedals(sortedShooters);

            // Method B: Fixed score medals (M-class-specific thresholds, 6 series only)
            var weaponGroup = ExtractWeaponGroup(sortedShooters.First().ShootingClass);
            ApplyFixedScoreMedals(sortedShooters, weaponGroup, seriesCount);
        }

        /// <summary>
        /// Method A: Apply percentage-based medals (top 1/9 Silver, top 1/3 Bronze)
        /// </summary>
        private void ApplyPercentageMedals(List<PrecisionShooterResult> sortedShooters)
        {
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
                var lastBronzeShooter = sortedShooters[bronzeQuota - 1];
                for (int i = bronzeQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastBronzeShooter.TotalScore &&
                        sortedShooters[i].TotalXCount == lastBronzeShooter.TotalXCount &&
                        string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    {
                        sortedShooters[i].StandardMedal = "B";
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // Handle ties at Silver boundary
            if (silverQuota > 0 && silverQuota < sortedShooters.Count)
            {
                var lastSilverShooter = sortedShooters[silverQuota - 1];
                for (int i = silverQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastSilverShooter.TotalScore &&
                        sortedShooters[i].TotalXCount == lastSilverShooter.TotalXCount)
                    {
                        sortedShooters[i].StandardMedal = "S";
                    }
                    else if (sortedShooters[i].TotalScore != lastSilverShooter.TotalScore ||
                             sortedShooters[i].TotalXCount != lastSilverShooter.TotalXCount)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Method B: Apply fixed score medals based on M-class thresholds
        /// </summary>
        private void ApplyFixedScoreMedals(List<PrecisionShooterResult> shooters, string weaponGroup, int seriesCount)
        {
            foreach (var shooter in shooters)
            {
                var fixedMedal = GetFixedScoreMedal(shooter.TotalScore, weaponGroup, seriesCount);

                if (fixedMedal != null)
                {
                    // Apply "best of" logic
                    if (fixedMedal == "B" && shooter.StandardMedal != "S")
                    {
                        shooter.StandardMedal = "B";
                    }
                    else if (fixedMedal == "S")
                    {
                        shooter.StandardMedal = "S"; // Silver overrides everything
                    }
                }
            }
        }

        private string? GetFixedScoreMedal(int score, string weaponGroup, int seriesCount)
        {
            (int Bronze, int Silver) requirements = GetFixedScoreRequirements(weaponGroup, seriesCount);

            if (requirements.Bronze < 0 || requirements.Silver < 0)
                return null;

            if (score >= requirements.Silver)
                return "S";
            else if (score >= requirements.Bronze)
                return "B";
            else
                return null;
        }

        /// <summary>
        /// Magnum Precision fixed score thresholds (6 series only).
        /// M1-M4, M8: Bronze 274, Silver 282
        /// M5 (Frigrupp): Bronze 288, Silver 294
        /// M6-M7, M9: Bronze 253, Silver 270
        /// </summary>
        private (int Bronze, int Silver) GetFixedScoreRequirements(string weaponGroup, int seriesCount)
        {
            if (seriesCount != 6)
                return (-1, -1); // Fixed-score only applies to 6 series

            return weaponGroup switch
            {
                "M1" or "M2" or "M3" or "M4" or "M8" => (274, 282),
                "M5" => (288, 294),
                "M6" or "M7" or "M9" => (253, 270),
                _ => (-1, -1)
            };
        }

        /// <summary>
        /// Group shooters by individual M class (M1-M9) for medal calculation.
        /// Each M class is its own medal group.
        /// </summary>
        private Dictionary<string, List<PrecisionShooterResult>> GroupByWeaponGroup(
            List<PrecisionShooterResult> shooters,
            bool shouldSplitGroupC)
        {
            var groups = new Dictionary<string, List<PrecisionShooterResult>>();

            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);

                if (!groups.ContainsKey(weaponGroup))
                    groups[weaponGroup] = new List<PrecisionShooterResult>();

                groups[weaponGroup].Add(shooter);
            }

            return groups;
        }

        /// <summary>
        /// Extract weapon group from shooting class (e.g., "M1", "M5", "M9").
        /// For Magnum Precision this returns the full M-class identifier.
        /// Uses the ShootingClasses registry to validate the input is an M-class.
        /// </summary>
        private string ExtractWeaponGroup(string shootingClass)
        {
            var sc = ShootingClasses.GetById(shootingClass) ?? ShootingClasses.GetByName(shootingClass);
            if (sc != null && sc.Weapon == WeaponClass.M)
                return sc.Id.ToUpper(); // "M1" .. "M9"
            return "M1"; // Default
        }
    }
}
