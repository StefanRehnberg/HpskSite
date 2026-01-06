using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Service for calculating Standard Medal Awards (Standardmedalj) for precision shooting competitions.
    /// Implements SSF rules as documented in "Standard Medal Award (Precision Shooting).md"
    /// </summary>
    public class StandardMedalCalculationService
    {
        /// <summary>
        /// Main entry point: Calculate and assign standard medals to all shooters
        /// </summary>
        /// <param name="shooters">All shooters in the competition</param>
        /// <param name="config">Configuration containing series count and championship status</param>
        public void CalculateStandardMedals(List<PrecisionShooterResult> shooters, StandardMedalConfig config)
        {
            if (shooters == null || !shooters.Any() || config.SeriesCount < 6)
                return;

            // Group shooters by weapon group (and classification for Group C in SM/Landsdel)
            var groups = GroupByWeaponGroup(shooters, config.ShouldSplitGroupC);

            foreach (var group in groups)
            {
                // Calculate medals for this group
                CalculateMedalsForGroup(group.Value, config.SeriesCount);
            }
        }

        /// <summary>
        /// Group shooters by weapon group (A, B, C), with special handling for Group C in SM/Landsdel championships
        /// </summary>
        private Dictionary<string, List<PrecisionShooterResult>> GroupByWeaponGroup(
            List<PrecisionShooterResult> shooters,
            bool shouldSplitGroupC)
        {
            var groups = new Dictionary<string, List<PrecisionShooterResult>>();

            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);

                string groupKey;
                if (shouldSplitGroupC && weaponGroup == "C")
                {
                    // For SM/Landsdel championships, split Group C by classification
                    var classification = ExtractClassification(shooter.ShootingClass);
                    groupKey = classification != null ? $"C-{classification}" : "C-Öppen";
                }
                else
                {
                    // For other competitions, just use weapon group
                    groupKey = weaponGroup;
                }

                if (!groups.ContainsKey(groupKey))
                    groups[groupKey] = new List<PrecisionShooterResult>();

                groups[groupKey].Add(shooter);
            }

            return groups;
        }

        /// <summary>
        /// Calculate medals for a single group using both percentage and fixed score methods
        /// </summary>
        private void CalculateMedalsForGroup(List<PrecisionShooterResult> groupShooters, int seriesCount)
        {
            if (!groupShooters.Any())
                return;

            // Sort by score descending, then X-count descending
            var sortedShooters = groupShooters
                .OrderByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.TotalXCount)
                .ToList();

            // Method A: Percentage-based medals
            ApplyPercentageMedals(sortedShooters);

            // Method B: Fixed score medals
            var weaponGroup = ExtractWeaponGroup(sortedShooters.First().ShootingClass);
            ApplyFixedScoreMedals(sortedShooters, weaponGroup, seriesCount);

            // Apply "best of" logic: Each shooter keeps their best medal (B > S > none)
            // This is already handled since we only upgrade, never downgrade
        }

        /// <summary>
        /// Method A: Apply percentage-based medals (top 1/9 Silver, top 1/3 Bronze)
        /// </summary>
        private void ApplyPercentageMedals(List<PrecisionShooterResult> sortedShooters)
        {
            int shooterCount = sortedShooters.Count;

            // Calculate quotas (round DOWN per rules)
            int silverQuota = shooterCount / 9;    // Top 1/9
            int bronzeQuota = shooterCount / 3;    // Top 1/3

            // Award Silver to top 1/9
            for (int i = 0; i < silverQuota && i < sortedShooters.Count; i++)
            {
                // Upgrade to Silver if not already Bronze
                if (sortedShooters[i].StandardMedal != "B")
                    sortedShooters[i].StandardMedal = "S";
            }

            // Award Bronze to top 1/3 (including those already with Silver)
            for (int i = 0; i < bronzeQuota && i < sortedShooters.Count; i++)
            {
                // Only set Bronze if no medal yet (Silver is better)
                if (string.IsNullOrEmpty(sortedShooters[i].StandardMedal))
                    sortedShooters[i].StandardMedal = "B";
            }

            // Handle ties: If tied with last qualifying shooter, award same medal
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
                        break; // Stop at first non-tie
                    }
                }
            }

            if (silverQuota > 0 && silverQuota < sortedShooters.Count)
            {
                var lastSilverShooter = sortedShooters[silverQuota - 1];
                for (int i = silverQuota; i < sortedShooters.Count; i++)
                {
                    if (sortedShooters[i].TotalScore == lastSilverShooter.TotalScore &&
                        sortedShooters[i].TotalXCount == lastSilverShooter.TotalXCount)
                    {
                        // Upgrade to Silver (even if they already have Bronze)
                        sortedShooters[i].StandardMedal = "S";
                    }
                    else if (sortedShooters[i].TotalScore != lastSilverShooter.TotalScore ||
                             sortedShooters[i].TotalXCount != lastSilverShooter.TotalXCount)
                    {
                        break; // Stop at first non-tie
                    }
                }
            }
        }

        /// <summary>
        /// Method B: Apply fixed score medals based on score tables
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

        /// <summary>
        /// Get fixed score medal based on score tables (BR-PS.2.4.2)
        /// </summary>
        private string? GetFixedScoreMedal(int score, string weaponGroup, int seriesCount)
        {
            // Fixed score tables from BR-PS.2.4.2
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
        /// Get fixed score requirements for a weapon group and series count
        /// Returns -1 for Unknown configuration        
        /// </summary>
        private (int Bronze, int Silver) GetFixedScoreRequirements(string weaponGroup, int seriesCount)
        {
            // Table from BR-PS.2.4.2
            return (weaponGroup, seriesCount) switch
            {
                // 6 series
                ("A", 6) => (267, 277),
                ("B", 6) => (273, 282),
                ("C", 6) => (276, 283),

                // 7 series
                ("A", 7) => (312, 323),
                ("B", 7) => (319, 329),
                ("C", 7) => (322, 330),

                // 10 series
                ("A", 10) => (445, 461),
                ("B", 10) => (455, 470),
                ("C", 10) => (460, 471),

                // Unknown configuration
                _ => (-1, -1)
            };
        }

        /// <summary>
        /// Extract weapon group (A, B, or C) from shooting class like "B3", "A2", "C Dam"
        /// </summary>
        private string ExtractWeaponGroup(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass))
                return "C"; // Default to C if unknown

            var firstChar = shootingClass.Trim().ToUpper()[0];
            if (firstChar == 'A' || firstChar == 'B' || firstChar == 'C')
                return firstChar.ToString();

            return "C"; // Default to C
        }

        /// <summary>
        /// Extract classification (Dam, Jun, Vet Y, Vet Ä) from shooting class, or null for open
        /// </summary>
        private string? ExtractClassification(string shootingClass)
        {
            if (string.IsNullOrEmpty(shootingClass))
                return null;

            var upper = shootingClass.ToUpper().Trim();

            if (upper.Contains("DAM"))
                return "Dam";
            else if (upper.Contains("JUN"))
                return "Jun";
            else if (upper.Contains("VET Y") || upper.Contains("VETY"))
                return "Vet Y";
            else if (upper.Contains("VET Ä") || upper.Contains("VETÄ"))
                return "Vet Ä";
            else
                return null; // Open class
        }

        /// <summary>
        /// Determine if Group C should be split by classification based on competition scope
        /// </summary>
        /// <param name="competitionScope">Competition scope from Umbraco property</param>
        /// <returns>True if SM or Landsdel (split C by Dam/Jun/Vet/Open), False otherwise</returns>
        public bool ShouldSplitGroupC(string competitionScope)
        {
            if (string.IsNullOrEmpty(competitionScope))
                return false;

            // Only SM and Landsdel split Group C by classification
            return competitionScope == "Svenskt Mästerskap" ||
                   competitionScope == "Landsdelsmästerskap";
        }
    }

    /// <summary>
    /// Configuration for standard medal calculation
    /// </summary>
    public class StandardMedalConfig
    {
        /// <summary>
        /// Number of qualification series (not including finals)
        /// </summary>
        public int SeriesCount { get; set; }

        /// <summary>
        /// Whether to split Group C by classification (Dam/Jun/Vet Y/Vet Ä/Open)
        /// True for SM and Landsdel championships, False for KM and club competitions
        /// </summary>
        public bool ShouldSplitGroupC { get; set; }
    }
}
