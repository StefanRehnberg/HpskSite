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
        /// Default: 10.0
        /// </summary>
        public decimal MaxHandicapPerSeries { get; set; } = 10.0m;

        /// <summary>
        /// Number of completed matches required before shooter is no longer provisional.
        /// Until this threshold is met, the starting index fills in for missing results.
        /// Default: 5
        /// </summary>
        public int RequiredMatches { get; set; } = 5;

        /// <summary>
        /// Number of most recent matches to include in handicap calculation (rolling window).
        /// Only the most recent N matches contribute to the average.
        /// Default: 10
        /// </summary>
        public int RollingWindowMatchCount { get; set; } = 10;

        /// <summary>
        /// Starting index (expected average) by shooter class for new shooters.
        /// Used to fill in missing results until RequiredMatches is reached.
        /// Keys should match the precisionShooterClass member property values.
        /// Shooter class MUST be set - there is no default fallback.
        /// </summary>
        public Dictionary<string, decimal> ProvisionalAverages { get; set; } = new()
        {
            { "Klass 1 - Nybörjare", 44.0m },
            { "Klass 2 - Guldmärkesskytt", 46.0m },
            { "Klass 3 - Riksmästare", 48.0m }
        };
    }
}
