namespace HpskSite.Models
{
    /// <summary>
    /// Represents a shooter's RAW performance statistics for handicap calculation.
    /// Tracks completed matches and average scores per discipline and weapon class.
    /// Maps to the ShooterStatistics database table.
    /// </summary>
    public class ShooterStatistics
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Member ID of the shooter
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Discipline (e.g., "Precision")
        /// </summary>
        public string Discipline { get; set; } = "Precision";

        /// <summary>
        /// Weapon class (A, B, C, R, P, M, L)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// Number of completed matches in this discipline/weapon class
        /// </summary>
        public int CompletedMatches { get; set; }

        /// <summary>
        /// Total number of series across all completed matches
        /// </summary>
        public int TotalSeriesCount { get; set; }

        /// <summary>
        /// Total RAW points across all series (not handicap-adjusted)
        /// </summary>
        public decimal TotalSeriesPoints { get; set; }

        /// <summary>
        /// Average RAW points per series (calculated column in database)
        /// </summary>
        public decimal AveragePerSeries { get; set; }

        /// <summary>
        /// When the statistics were last calculated/updated
        /// </summary>
        public DateTime LastCalculated { get; set; }
    }
}
