namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Pure utility functions for scoring calculations.
    /// Can be used by any competition type.
    /// No business logic - just conversions and calculations.
    /// </summary>
    public static class ScoringUtilities
    {
        /// <summary>
        /// Convert shot value to points for precision shooting.
        /// X = 10, 10 = 10, 9-0 = numeric value
        /// </summary>
        /// <param name="shot">Shot value as string (X, 10, 9, etc.)</param>
        /// <returns>Points value (0-10)</returns>
        public static decimal ShotToPoints(string shot)
        {
            if (string.IsNullOrWhiteSpace(shot))
                return 0;

            var upper = shot.Trim().ToUpper();

            // X counts as 10
            if (upper == "X")
                return 10;

            // Try to parse numeric value
            if (decimal.TryParse(upper, out var value))
                return value >= 0 && value <= 10 ? value : 0;

            return 0;
        }

        /// <summary>
        /// Validate shot value format (0-10 or X)
        /// </summary>
        /// <param name="shotValue">Shot value to validate</param>
        /// <returns>True if valid shot value, false otherwise</returns>
        public static bool IsValidShotValue(string shotValue)
        {
            if (string.IsNullOrWhiteSpace(shotValue))
                return false;

            var upper = shotValue.Trim().ToUpper();

            // X is valid
            if (upper == "X")
                return true;

            // 0-10 are valid
            if (decimal.TryParse(upper, out var value))
                return value >= 0 && value <= 10;

            return false;
        }

        /// <summary>
        /// Calculate total points from a series of shots.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Sum of all shot points</returns>
        public static decimal CalculateTotal(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            return shots.Sum(s => ShotToPoints(s));
        }

        /// <summary>
        /// Count inner tens (X-shots) in a series.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Count of X shots</returns>
        public static int CountInnerTens(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            return shots.Count(s => IsValidShotValue(s) && s.Trim().ToUpper() == "X");
        }

        /// <summary>
        /// Count all tens (X and 10) in a series.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Count of X and 10 shots combined</returns>
        public static int CountAllTens(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            return shots.Count(s =>
            {
                var upper = s.Trim().ToUpper();
                return IsValidShotValue(s) && (upper == "X" || upper == "10");
            });
        }

        /// <summary>
        /// Count nines in a series (for some tie-breaking rules).
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Count of 9 shots</returns>
        public static int CountNines(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            return shots.Count(s =>
            {
                var upper = s.Trim().ToUpper();
                return IsValidShotValue(s) && upper == "9";
            });
        }

        /// <summary>
        /// Get all valid shots from a list, filtering out invalid values.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>List of valid shots only</returns>
        public static List<string> GetValidShots(IEnumerable<string> shots)
        {
            if (shots == null)
                return new List<string>();

            return shots.Where(IsValidShotValue).ToList();
        }

        /// <summary>
        /// Get all invalid shots from a list.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>List of invalid shots</returns>
        public static List<string> GetInvalidShots(IEnumerable<string> shots)
        {
            if (shots == null)
                return new List<string>();

            return shots.Where(s => !IsValidShotValue(s)).ToList();
        }

        /// <summary>
        /// Calculate average score per shot (total / number of shots).
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Average points per shot, or 0 if no shots</returns>
        public static decimal CalculateAverage(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            var shotList = shots.ToList();
            if (shotList.Count == 0)
                return 0;

            return CalculateTotal(shotList) / shotList.Count;
        }

        /// <summary>
        /// Get the lowest score shot in a series (for some tie-breaking rules).
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Lowest points value, or 0 if no shots</returns>
        public static decimal GetLowestShot(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            var points = shots.Select(ShotToPoints).ToList();
            return points.Any() ? points.Min() : 0;
        }

        /// <summary>
        /// Get the highest score shot in a series.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Highest points value, or 0 if no shots</returns>
        public static decimal GetHighestShot(IEnumerable<string> shots)
        {
            if (shots == null)
                return 0;

            var points = shots.Select(ShotToPoints).ToList();
            return points.Any() ? points.Max() : 0;
        }
    }
}
