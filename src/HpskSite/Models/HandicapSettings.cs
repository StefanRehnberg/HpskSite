namespace HpskSite.Models
{
    /// <summary>
    /// Configuration settings for the handicap system.
    /// Loaded from appsettings.json under "HandicapSettings" section.
    /// </summary>
    public class HandicapSettings
    {
        /// <summary>
        /// The reference score per series that handicap is calculated against.
        /// Default: 48.0 (high-performance baseline)
        /// </summary>
        public decimal ReferenceSeriesScore { get; set; } = 48.0m;

        /// <summary>
        /// Maximum handicap bonus per series.
        /// Prevents excessive handicaps for very low-average shooters.
        /// Per specification: negative handicaps have no lower bound.
        /// Default: 5.0
        /// </summary>
        public decimal MaxHandicapPerSeries { get; set; } = 5.0m;

        /// <summary>
        /// Number of completed matches required before shooter is no longer provisional.
        /// Until this threshold is met, a weighted convergence formula is used.
        /// Default: 8
        /// </summary>
        public int RequiredMatches { get; set; } = 8;

        /// <summary>
        /// Number of most recent matches to include in handicap calculation (rolling window).
        /// Only the most recent N matches contribute to the average.
        /// Default: 10
        /// </summary>
        public int RollingWindowMatchCount { get; set; } = 10;

        /// <summary>
        /// Provisional averages by shooter class (for new shooters with no history).
        /// Keys should match the precisionShooterClass member property values.
        /// Shooter class MUST be set - there is no default fallback.
        /// </summary>
        public Dictionary<string, decimal> ProvisionalAverages { get; set; } = new()
        {
            { "Klass 1 - Nybörjare", 40.0m },
            { "Klass 2 - Guldmärkesskytt", 44.0m },
            { "Klass 3 - Riksmästare", 47.0m }
        };
    }
}
