namespace HpskSite.Models.ViewModels.TrainingScoring
{
    /// <summary>
    /// Unified model representing a result from any source:
    /// - Training scores (TrainingScores table, IsCompetition=false)
    /// - Competition scores (TrainingScores table, IsCompetition=true)
    /// - Official competition results (Competition Result document type)
    /// </summary>
    public class UnifiedResultEntry
    {
        /// <summary>
        /// Unique identifier (TrainingScore ID or PrecisionResultEntry ID)
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Date of the training session or competition
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// Source type: "Training", "Competition", or "Official"
        /// </summary>
        public string SourceType { get; set; } = string.Empty;

        /// <summary>
        /// Weapon class (A, B, C, R, P) or shooting class (C1, C2, A1, etc.)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// Total score across all series
        /// </summary>
        public int TotalScore { get; set; }

        /// <summary>
        /// Total X-count across all series
        /// </summary>
        public int XCount { get; set; }

        /// <summary>
        /// Number of series shot
        /// </summary>
        public int SeriesCount { get; set; }

        /// <summary>
        /// Average score per series
        /// </summary>
        public double AverageScore { get; set; }

        /// <summary>
        /// Competition name (for competition results only)
        /// </summary>
        public string? CompetitionName { get; set; }

        /// <summary>
        /// Competition ID (for linking to competition page)
        /// </summary>
        public int? CompetitionId { get; set; }

        /// <summary>
        /// Whether this result can be edited (false for official results)
        /// </summary>
        public bool CanEdit { get; set; }

        /// <summary>
        /// Whether this result can be deleted (false for official results)
        /// </summary>
        public bool CanDelete { get; set; }

        /// <summary>
        /// Optional notes (for training scores)
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Detailed series data for viewing individual shots
        /// </summary>
        public List<SeriesDetail> Series { get; set; } = new List<SeriesDetail>();
    }

    /// <summary>
    /// Detailed series information for a unified result entry
    /// </summary>
    public class SeriesDetail
    {
        /// <summary>
        /// Series number (1, 2, 3, etc.)
        /// </summary>
        public int SeriesNumber { get; set; }

        /// <summary>
        /// Individual shots as strings ("X", "10", "9", etc.)
        /// Null for SeriesTotal and TotalOnly entry methods
        /// </summary>
        public List<string>? Shots { get; set; }

        /// <summary>
        /// Total score for this series
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// X-count for this series
        /// </summary>
        public int XCount { get; set; }
    }
}
