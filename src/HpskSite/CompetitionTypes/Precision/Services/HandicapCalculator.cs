using HpskSite.Models;
using Microsoft.Extensions.Options;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Interface for the handicap calculator.
    /// Pure functions, no database access, no side effects.
    /// </summary>
    public interface IHandicapCalculator
    {
        /// <summary>
        /// Calculate handicap profile for a shooter based on their statistics and class.
        /// </summary>
        /// <param name="stats">Shooter's statistics (can be null for new shooters)</param>
        /// <param name="shooterClass">Shooter's class value from member profile (e.g., "Klass 1 - Nybörjare")</param>
        /// <returns>Calculated handicap profile</returns>
        HandicapProfile CalculateHandicap(ShooterStatistics? stats, string? shooterClass);

        /// <summary>
        /// Apply handicap to a single series raw score.
        /// </summary>
        /// <param name="rawSeriesScore">The raw score for the series</param>
        /// <param name="handicapPerSeries">The handicap bonus per series</param>
        /// <returns>Final score (raw + handicap)</returns>
        decimal GetSeriesFinalScore(decimal rawSeriesScore, decimal handicapPerSeries);

        /// <summary>
        /// Calculate total final score for a match.
        /// </summary>
        /// <param name="rawTotal">Total raw score across all series</param>
        /// <param name="handicapPerSeries">The handicap bonus per series</param>
        /// <param name="seriesCount">Number of series completed</param>
        /// <returns>Final score (raw + total handicap)</returns>
        decimal GetMatchFinalScore(decimal rawTotal, decimal handicapPerSeries, int seriesCount);

        /// <summary>
        /// Calculate effective average for a provisional shooter using weighted convergence.
        /// </summary>
        /// <param name="actualAverage">Shooter's actual average per series</param>
        /// <param name="completedMatches">Number of completed matches</param>
        /// <param name="provisionalAverage">Provisional average based on shooter class</param>
        /// <returns>Weighted effective average</returns>
        decimal GetEffectiveAverage(decimal actualAverage, int completedMatches, decimal provisionalAverage);

        /// <summary>
        /// Get the provisional average for a given shooter class.
        /// </summary>
        /// <param name="shooterClass">Shooter's class value (e.g., "Klass 1 - Nybörjare")</param>
        /// <returns>Provisional average for that class</returns>
        decimal GetProvisionalAverage(string? shooterClass);

        /// <summary>
        /// Get the current handicap settings.
        /// </summary>
        HandicapSettings Settings { get; }
    }

    /// <summary>
    /// Pure, stateless calculator for handicap system.
    /// This service has no database access - it only performs calculations.
    /// Inject IHandicapCalculator where needed to calculate handicaps on the fly.
    /// </summary>
    public class HandicapCalculator : IHandicapCalculator
    {
        private readonly HandicapSettings _settings;

        public HandicapCalculator(IOptions<HandicapSettings> settings)
        {
            _settings = settings.Value;
        }

        /// <summary>
        /// Gets the current handicap settings.
        /// </summary>
        public HandicapSettings Settings => _settings;

        /// <summary>
        /// Calculate handicap profile for a shooter.
        /// </summary>
        public HandicapProfile CalculateHandicap(ShooterStatistics? stats, string? shooterClass)
        {
            var completedMatches = stats?.CompletedMatches ?? 0;
            var actualAverage = stats?.AveragePerSeries ?? 0;
            var provisionalAverage = GetProvisionalAverage(shooterClass);

            bool isProvisional = completedMatches < _settings.RequiredMatches;

            decimal effectiveAverage;
            if (isProvisional)
            {
                effectiveAverage = GetEffectiveAverage(actualAverage, completedMatches, provisionalAverage);
            }
            else
            {
                effectiveAverage = actualAverage;
            }

            // Calculate handicap: Reference - Effective Average
            decimal rawHandicap = _settings.ReferenceSeriesScore - effectiveAverage;

            // Apply cap: handicap cannot exceed MaxHandicapPerSeries
            // Note: Per specification, negative handicap values are allowed (no lower bound)
            decimal handicap = Math.Min(rawHandicap, _settings.MaxHandicapPerSeries);

            // Round to quarter-point (0.25 increments)
            handicap = RoundToQuarter(handicap);

            return new HandicapProfile
            {
                EffectiveAverage = Math.Round(effectiveAverage, 2),
                HandicapPerSeries = handicap,
                IsProvisional = isProvisional,
                CompletedMatches = completedMatches,
                MatchesUntilFullHandicap = isProvisional ? _settings.RequiredMatches - completedMatches : 0,
                ActualAverage = Math.Round(actualAverage, 2),
                ProvisionalAverage = provisionalAverage
            };
        }

        /// <summary>
        /// Maximum possible score per series (5 shots × 10 points = 50)
        /// </summary>
        private const decimal MAX_SCORE_PER_SERIES = 50.0m;

        /// <summary>
        /// Apply handicap to a single series raw score.
        /// The final score is capped at the maximum possible (50 per series).
        /// </summary>
        public decimal GetSeriesFinalScore(decimal rawSeriesScore, decimal handicapPerSeries)
        {
            // Use the quarter-point handicap value, then round final score to integer
            var finalScore = rawSeriesScore + handicapPerSeries;
            // Cap at maximum possible score - you can never score more than 50 per series
            finalScore = Math.Min(finalScore, MAX_SCORE_PER_SERIES);
            return Math.Round(finalScore, 0);
        }

        /// <summary>
        /// Calculate total final score for a match.
        /// Each series is capped at the maximum possible (50), then summed.
        /// </summary>
        public decimal GetMatchFinalScore(decimal rawTotal, decimal handicapPerSeries, int seriesCount)
        {
            // Cap the total at max possible (50 × seriesCount)
            var maxPossibleTotal = MAX_SCORE_PER_SERIES * seriesCount;
            // Use the quarter-point handicap value, then round final score to integer
            var finalScore = rawTotal + (handicapPerSeries * seriesCount);
            finalScore = Math.Min(finalScore, maxPossibleTotal);
            return Math.Round(finalScore, 0);
        }

        /// <summary>
        /// Calculate effective average using weighted convergence formula.
        /// Formula: (ProvisionalAvg × (Required - Completed) + ActualAvg × Completed) / Required
        /// </summary>
        public decimal GetEffectiveAverage(decimal actualAverage, int completedMatches, decimal provisionalAverage)
        {
            if (completedMatches >= _settings.RequiredMatches)
            {
                // No longer provisional - use actual average
                return actualAverage;
            }

            if (completedMatches == 0)
            {
                // No history - use provisional average
                return provisionalAverage;
            }

            // Weighted convergence formula
            var remainingMatches = _settings.RequiredMatches - completedMatches;
            var weightedSum = (provisionalAverage * remainingMatches) + (actualAverage * completedMatches);
            var effectiveAverage = weightedSum / _settings.RequiredMatches;

            return Math.Round(effectiveAverage, 2);
        }

        /// <summary>
        /// Round a value to the nearest quarter-point (0.25 increments).
        /// Valid results: x.0, x.25, x.5, x.75
        /// </summary>
        private decimal RoundToQuarter(decimal value)
        {
            return Math.Round(value * 4, MidpointRounding.AwayFromZero) / 4;
        }

        /// <summary>
        /// Get provisional average for a shooter class.
        /// Throws if shooter class is not set or unknown - caller must validate beforehand.
        /// </summary>
        public decimal GetProvisionalAverage(string? shooterClass)
        {
            if (string.IsNullOrEmpty(shooterClass))
            {
                throw new InvalidOperationException(
                    "Shooter class must be set before calculating handicap. " +
                    "User should select their class in their profile.");
            }

            if (_settings.ProvisionalAverages.TryGetValue(shooterClass, out var average))
            {
                return average;
            }

            throw new InvalidOperationException(
                $"Unknown shooter class: '{shooterClass}'. " +
                $"Valid classes are: {string.Join(", ", _settings.ProvisionalAverages.Keys)}");
        }
    }
}
