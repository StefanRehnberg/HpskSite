namespace HpskSite.Models.Utilities
{
    /// <summary>
    /// Shared validation and parsing logic for shot values across competition and training systems
    /// </summary>
    public static class ShotValidator
    {
        /// <summary>
        /// Validates if a shot string is valid (X, 0-10)
        /// </summary>
        public static bool IsValidShot(string shot)
        {
            if (string.IsNullOrWhiteSpace(shot))
                return false;

            // Handle X (inner ten)
            if (shot.Trim().ToUpper() == "X")
                return true;

            // Handle numeric values 0-10
            if (int.TryParse(shot.Trim(), out int value))
            {
                return value >= 0 && value <= 10;
            }

            return false;
        }

        /// <summary>
        /// Parses a shot string to its numeric value (X = 10)
        /// </summary>
        public static int ParseShot(string shot)
        {
            if (string.IsNullOrWhiteSpace(shot))
                throw new ArgumentException("Shot cannot be null or empty", nameof(shot));

            var trimmedShot = shot.Trim().ToUpper();

            // X counts as 10 points
            if (trimmedShot == "X")
                return 10;

            if (int.TryParse(trimmedShot, out int value))
            {
                if (value < 0 || value > 10)
                    throw new ArgumentOutOfRangeException(nameof(shot), "Shot value must be between 0 and 10");

                return value;
            }

            throw new ArgumentException($"Invalid shot value: {shot}", nameof(shot));
        }

        /// <summary>
        /// Counts the number of X shots in a list
        /// </summary>
        public static int CountXShots(IEnumerable<string> shots)
        {
            return shots.Count(s => !string.IsNullOrWhiteSpace(s) && s.Trim().ToUpper() == "X");
        }

        /// <summary>
        /// Calculates total score from a list of shots
        /// </summary>
        public static int CalculateTotalScore(IEnumerable<string> shots)
        {
            return shots.Where(s => !string.IsNullOrWhiteSpace(s))
                        .Sum(s => ParseShot(s));
        }
    }
}
