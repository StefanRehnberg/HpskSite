namespace HpskSite.Shared.Services
{
    /// <summary>
    /// Centralized calculator for training match results.
    /// Uses consistent rounding mode (AwayFromZero) across all platforms.
    /// This ensures mobile app and web site produce identical results.
    /// </summary>
    public static class ResultCalculator
    {
        /// <summary>
        /// Maximum possible score per series (5 shots x 10 points)
        /// </summary>
        public const int MaxScorePerSeries = 50;

        /// <summary>
        /// Standard rounding mode used throughout the system.
        /// AwayFromZero matches JavaScript's Math.round() behavior.
        /// </summary>
        public const MidpointRounding StandardRounding = MidpointRounding.AwayFromZero;

        /// <summary>
        /// Calculate total score from a list of shot values.
        /// "X" is treated as 10 points.
        /// </summary>
        /// <param name="shots">List of shot values (0-10 or "X")</param>
        /// <returns>Tuple of (total points, x-count)</returns>
        public static (int Total, int XCount) CalculateShotsTotal(IEnumerable<string>? shots)
        {
            if (shots == null)
                return (0, 0);

            int total = 0;
            int xCount = 0;

            foreach (var shot in shots)
            {
                if (string.IsNullOrWhiteSpace(shot))
                    continue;

                var shotValue = shot.Trim().ToUpper();

                if (shotValue == "X")
                {
                    total += 10;
                    xCount++;
                }
                else if (int.TryParse(shotValue, out int value))
                {
                    total += value;
                }
            }

            return (total, xCount);
        }

        /// <summary>
        /// Apply handicap to a single series score and cap at maximum.
        /// Uses standard rounding (AwayFromZero) for consistency with JavaScript.
        /// </summary>
        /// <param name="rawScore">Raw series score (0-50)</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <returns>Adjusted score (capped at 50)</returns>
        public static int CalculateAdjustedSeriesScore(int rawScore, decimal handicapPerSeries)
        {
            var adjusted = rawScore + handicapPerSeries;
            var rounded = (int)Math.Round(adjusted, StandardRounding);
            return Math.Min(rounded, MaxScorePerSeries);
        }

        /// <summary>
        /// Calculate raw total score from a list of series scores.
        /// Each series is capped at the maximum (50).
        /// </summary>
        /// <param name="seriesScores">List of series with Total property</param>
        /// <param name="equalizedCount">Optional limit on number of series to include</param>
        /// <returns>Total raw score</returns>
        public static int CalculateRawTotal<T>(IEnumerable<T> seriesScores, int? equalizedCount = null)
            where T : ISeriesScore
        {
            var scores = GetEffectiveScores(seriesScores, equalizedCount);
            return scores.Sum(s => Math.Min(s.Total, MaxScorePerSeries));
        }

        /// <summary>
        /// Calculate total X-count from a list of series scores.
        /// </summary>
        /// <param name="seriesScores">List of series with XCount property</param>
        /// <param name="equalizedCount">Optional limit on number of series to include</param>
        /// <returns>Total X-count</returns>
        public static int CalculateTotalXCount<T>(IEnumerable<T> seriesScores, int? equalizedCount = null)
            where T : ISeriesScore
        {
            var scores = GetEffectiveScores(seriesScores, equalizedCount);
            return scores.Sum(s => s.XCount);
        }

        /// <summary>
        /// Calculate adjusted total score with handicap applied.
        /// Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
        /// The result is capped at (50 × seriesCount) and rounded to integer.
        /// </summary>
        /// <param name="seriesScores">List of series with Total property</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <param name="equalizedCount">Optional limit on number of series to include</param>
        /// <returns>Total adjusted score</returns>
        public static int CalculateAdjustedTotal<T>(IEnumerable<T> seriesScores, decimal handicapPerSeries, int? equalizedCount = null)
            where T : ISeriesScore
        {
            var scores = GetEffectiveScores(seriesScores, equalizedCount).ToList();
            var rawTotal = scores.Sum(s => Math.Min(s.Total, MaxScorePerSeries));
            var seriesCount = scores.Count;
            return CalculateAdjustedMatchTotal(rawTotal, handicapPerSeries, seriesCount);
        }

        /// <summary>
        /// Calculate final match score with handicap applied.
        /// Per spec: FinalScore = RawTotal + (HandicapPerSeries × SeriesCount)
        /// The result is capped at (50 × seriesCount) and rounded to integer.
        /// </summary>
        /// <param name="rawTotal">Raw total score across all series</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <param name="seriesCount">Number of series</param>
        /// <returns>Adjusted total score</returns>
        public static int CalculateAdjustedMatchTotal(int rawTotal, decimal handicapPerSeries, int seriesCount)
        {
            var handicapTotal = handicapPerSeries * seriesCount;
            var maxPossible = MaxScorePerSeries * seriesCount;
            var finalScore = rawTotal + handicapTotal;
            return Math.Min((int)Math.Round(finalScore, StandardRounding), maxPossible);
        }

        /// <summary>
        /// Get effective scores respecting equalized count and ordering.
        /// </summary>
        private static IEnumerable<T> GetEffectiveScores<T>(IEnumerable<T> scores, int? equalizedCount)
            where T : ISeriesScore
        {
            var ordered = scores.OrderBy(s => s.SeriesNumber);
            return equalizedCount.HasValue
                ? ordered.Take(equalizedCount.Value)
                : ordered;
        }

        /// <summary>
        /// Round a decimal value to the nearest integer using standard rounding.
        /// </summary>
        /// <param name="value">Value to round</param>
        /// <returns>Rounded integer</returns>
        public static int RoundToInt(decimal value)
        {
            return (int)Math.Round(value, StandardRounding);
        }

        /// <summary>
        /// Round to quarter-point (0.25 increments) using standard rounding.
        /// </summary>
        /// <param name="value">Value to round</param>
        /// <returns>Value rounded to nearest 0.25</returns>
        public static decimal RoundToQuarter(decimal value)
        {
            return Math.Round(value * 4, StandardRounding) / 4;
        }
    }

    /// <summary>
    /// Interface for series score data.
    /// Allows ResultCalculator to work with different score types.
    /// </summary>
    public interface ISeriesScore
    {
        /// <summary>
        /// Series number (1-based)
        /// </summary>
        int SeriesNumber { get; }

        /// <summary>
        /// Total points for this series
        /// </summary>
        int Total { get; }

        /// <summary>
        /// Number of X shots
        /// </summary>
        int XCount { get; }
    }
}
