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
        /// Result is clamped between 0 and 50.
        /// </summary>
        /// <param name="rawScore">Raw series score (0-50)</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <returns>Adjusted score (clamped between 0 and 50)</returns>
        public static int CalculateAdjustedSeriesScore(int rawScore, decimal handicapPerSeries)
        {
            var adjusted = rawScore + handicapPerSeries;
            var rounded = (int)Math.Round(adjusted, StandardRounding);
            return Math.Clamp(rounded, 0, MaxScorePerSeries);
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
        /// Per spec: For each series, apply handicap and clamp between 0-50, then sum all adjusted series.
        /// This ensures high-scoring shooters don't lose handicap advantage to the 50-cap.
        /// </summary>
        /// <param name="seriesScores">List of series with Total property</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <param name="equalizedCount">Optional limit on number of series to include</param>
        /// <returns>Total adjusted score</returns>
        public static int CalculateAdjustedTotal<T>(IEnumerable<T> seriesScores, decimal handicapPerSeries, int? equalizedCount = null)
            where T : ISeriesScore
        {
            var scores = GetEffectiveScores(seriesScores, equalizedCount).ToList();

            // Short-circuit for zero handicap
            if (handicapPerSeries == 0)
            {
                return scores.Sum(s => Math.Min(s.Total, MaxScorePerSeries));
            }

            // Apply handicap per series and clamp each between 0 and 50
            int total = 0;
            foreach (var s in scores)
            {
                var rawCapped = Math.Min(s.Total, MaxScorePerSeries);
                var adjusted = rawCapped + handicapPerSeries;
                var rounded = (int)Math.Round(adjusted, StandardRounding);
                var clamped = Math.Clamp(rounded, 0, MaxScorePerSeries);
                total += clamped;
            }
            return total;
        }

        /// <summary>
        /// Calculate the effective handicap applied (may be less than theoretical due to clamping at 0-50 per series).
        /// For example, if a shooter scores 49 with handicap 3, only 1 point is effectively applied (50-49).
        /// With negative handicap, if shooter scores 5 with handicap -10, only -5 is effective (clamped at 0).
        /// </summary>
        /// <param name="seriesScores">List of series with Total property</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <param name="equalizedCount">Optional limit on number of series to include</param>
        /// <returns>Total effective handicap applied (can be negative for negative handicaps)</returns>
        public static decimal CalculateEffectiveHandicap<T>(IEnumerable<T> seriesScores, decimal handicapPerSeries, int? equalizedCount = null)
            where T : ISeriesScore
        {
            // Short-circuit for zero handicap
            if (handicapPerSeries == 0)
            {
                return 0;
            }

            var scores = GetEffectiveScores(seriesScores, equalizedCount).ToList();
            decimal effectiveTotal = 0;
            foreach (var s in scores)
            {
                var rawCapped = Math.Min(s.Total, MaxScorePerSeries);
                var adjusted = rawCapped + handicapPerSeries;
                var rounded = (int)Math.Round(adjusted, StandardRounding);
                var clamped = Math.Clamp(rounded, 0, MaxScorePerSeries);
                // Effective handicap for this series is what we actually added
                effectiveTotal += (clamped - rawCapped);
            }
            return effectiveTotal;
        }

        /// <summary>
        /// Calculate final match score with handicap applied.
        /// DEPRECATED: This method doesn't respect per-series 50-cap correctly.
        /// Use CalculateAdjustedTotal&lt;T&gt;() with individual series scores instead.
        ///
        /// This method assumes average distribution across series, which is not accurate
        /// for high-scoring shooters. Kept for backward compatibility.
        /// </summary>
        /// <param name="rawTotal">Raw total score across all series</param>
        /// <param name="handicapPerSeries">Handicap bonus per series</param>
        /// <param name="seriesCount">Number of series</param>
        /// <returns>Adjusted total score</returns>
        public static int CalculateAdjustedMatchTotal(int rawTotal, decimal handicapPerSeries, int seriesCount)
        {
            // With zero handicap, just return raw total
            if (handicapPerSeries == 0)
            {
                return rawTotal;
            }

            // Fallback: assume average distribution across series (not perfectly accurate)
            // This approximation applies per-series clamping to average scores
            var avgRawPerSeries = seriesCount > 0 ? (decimal)rawTotal / seriesCount : 0;
            int total = 0;
            for (int i = 0; i < seriesCount; i++)
            {
                var adjusted = avgRawPerSeries + handicapPerSeries;
                var clamped = Math.Clamp((int)Math.Round(adjusted, StandardRounding), 0, MaxScorePerSeries);
                total += clamped;
            }
            return total;
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
