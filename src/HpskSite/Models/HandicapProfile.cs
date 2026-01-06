namespace HpskSite.Models
{
    /// <summary>
    /// Represents a calculated handicap profile for a shooter.
    /// This is a DTO returned by the HandicapCalculator - not stored in database.
    /// </summary>
    public class HandicapProfile
    {
        /// <summary>
        /// The effective average used for handicap calculation.
        /// For provisional shooters, this is a weighted blend of actual and provisional averages.
        /// For established shooters, this equals their actual average.
        /// </summary>
        public decimal EffectiveAverage { get; set; }

        /// <summary>
        /// The handicap bonus points applied per series.
        /// Calculated as: Reference (48.0) - EffectiveAverage, capped at MaxHandicapPerSeries (5.0)
        /// </summary>
        public decimal HandicapPerSeries { get; set; }

        /// <summary>
        /// True if the shooter has fewer than RequiredMatches completed.
        /// Provisional shooters use a weighted convergence formula.
        /// </summary>
        public bool IsProvisional { get; set; }

        /// <summary>
        /// Number of matches completed (for display purposes)
        /// </summary>
        public int CompletedMatches { get; set; }

        /// <summary>
        /// Number of additional matches required for full handicap (non-provisional status)
        /// </summary>
        public int MatchesUntilFullHandicap { get; set; }

        /// <summary>
        /// The shooter's actual average per series (RAW, before any weighting)
        /// </summary>
        public decimal ActualAverage { get; set; }

        /// <summary>
        /// The provisional average based on shooter's class (if applicable)
        /// </summary>
        public decimal ProvisionalAverage { get; set; }
    }
}
