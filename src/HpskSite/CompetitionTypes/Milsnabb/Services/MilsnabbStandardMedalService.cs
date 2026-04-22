using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Milsnabb.Services
{
    /// <summary>
    /// Standard medal calculation for Milsnabb competitions.
    /// Reuses the percentage-based logic from StandardMedalCalculationService,
    /// but overrides the fixed-score thresholds with Milsnabb-specific values.
    /// Milsnabb always has 12 series and includes weapon group R.
    /// </summary>
    public class MilsnabbStandardMedalService
    {
        private readonly StandardMedalCalculationService _baseService = new();

        /// <summary>
        /// Calculate standard medals for Milsnabb shooters.
        /// Applies percentage-based medals (same as Precision) plus Milsnabb fixed-score thresholds.
        /// </summary>
        public void CalculateStandardMedals(List<PrecisionShooterResult> shooters, StandardMedalConfig config)
        {
            if (shooters == null || !shooters.Any() || config.SeriesCount < 6)
                return;

            // Use base service for percentage-based medals (same algorithm)
            _baseService.CalculateStandardMedals(shooters, config);

            // Now override with Milsnabb fixed-score thresholds
            // (The base service applied Precision thresholds which returned -1 for unknown configs;
            //  we need to apply Milsnabb-specific thresholds for 12-series and R group)
            ApplyMilsnabbFixedScoreMedals(shooters, config.SeriesCount);
        }

        private void ApplyMilsnabbFixedScoreMedals(List<PrecisionShooterResult> shooters, int seriesCount)
        {
            foreach (var shooter in shooters)
            {
                var weaponGroup = ExtractWeaponGroup(shooter.ShootingClass);
                var (bronze, silver) = GetMilsnabbFixedScoreRequirements(weaponGroup, seriesCount);

                if (bronze < 0 || silver < 0)
                    continue;

                if (shooter.TotalScore >= silver)
                {
                    shooter.StandardMedal = "S";
                }
                else if (shooter.TotalScore >= bronze)
                {
                    // Only upgrade to Bronze if no Silver
                    if (shooter.StandardMedal != "S")
                        shooter.StandardMedal = "B";
                }
            }
        }

        /// <summary>
        /// Get Milsnabb fixed-score requirements.
        /// Returns (Bronze, Silver) thresholds, or (-1, -1) for unknown configuration.
        /// </summary>
        private static (int Bronze, int Silver) GetMilsnabbFixedScoreRequirements(string weaponGroup, int seriesCount)
        {
            // Milsnabb fixed score thresholds (12 series only). A_Opt follows A's thresholds.
            return (weaponGroup, seriesCount) switch
            {
                ("A", 12) => (516, 540),
                ("A_Opt", 12) => (516, 540),
                ("R", 12) => (528, 552),
                ("B", 12) => (537, 561),
                ("C", 12) => (540, 564),

                // Unknown configuration
                _ => (-1, -1)
            };
        }

        private static string ExtractWeaponGroup(string shootingClass)
        {
            var code = ShootingClasses.GetWeaponClassCode(shootingClass);
            if (code == "A" || code == "A_Opt" || code == "B" || code == "C" || code == "R")
                return code;
            return "C";
        }
    }
}
