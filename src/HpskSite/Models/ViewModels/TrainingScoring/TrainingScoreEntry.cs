using System.Text.Json;
using System.Text.Json.Serialization;

namespace HpskSite.Models.ViewModels.TrainingScoring
{
    /// <summary>
    /// Represents a complete training session with multiple series
    /// Maps to the TrainingScores database table
    /// </summary>
    public class TrainingScoreEntry
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Member who logged this training session
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Member name (for display purposes)
        /// </summary>
        public string? MemberName { get; set; }

        /// <summary>
        /// Date and time of the training session
        /// </summary>
        public DateTime TrainingDate { get; set; }

        /// <summary>
        /// Weapon class used for this training (A, B, C, R, P)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if this is a result from an external competition
        /// (competitions in other regions/countries not tracked in main system)
        /// </summary>
        public bool IsCompetition { get; set; }

        /// <summary>
        /// Competition placement/position (only for IsCompetition = true)
        /// Example: 1 = first place, 2 = second place, etc.
        /// </summary>
        public int? CompetitionPlace { get; set; }

        /// <summary>
        /// Competition shooting class (only for IsCompetition = true)
        /// Example: "A1", "B2", "C3", "R1", etc.
        /// </summary>
        public string? CompetitionShootingClass { get; set; }

        /// <summary>
        /// Competition standard medal (only for IsCompetition = true)
        /// "B" = Brons, "S" = Silver
        /// </summary>
        public string? CompetitionStdMedal { get; set; }

        /// <summary>
        /// List of all series shot during this training session
        /// </summary>
        public List<TrainingSeries> Series { get; set; } = new List<TrainingSeries>();

        /// <summary>
        /// Total score across all series
        /// </summary>
        public int TotalScore { get; set; }

        /// <summary>
        /// Total X-count across all series
        /// </summary>
        public int XCount { get; set; }

        /// <summary>
        /// Optional notes about the training session
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// When this entry was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When this entry was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Number of series in this training session
        /// For TotalOnly entries, returns the seriesCount from the series object
        /// For other entries, returns the actual number of series in the array
        /// </summary>
        [JsonPropertyName("seriesCount")]
        public int SeriesCount
        {
            get
            {
                if (Series == null || Series.Count == 0)
                    return 0;

                // For TotalOnly entries, return the seriesCount from the series object
                if (Series.Count == 1 && Series[0].EntryMethod == "TotalOnly" && Series[0].SeriesCount.HasValue)
                    return Series[0].SeriesCount.Value;

                // For other entries, return the array length
                return Series.Count;
            }
        }

        /// <summary>
        /// Average score per series
        /// </summary>
        [JsonIgnore]
        public double AverageScore => SeriesCount > 0 ? (double)TotalScore / SeriesCount : 0;

        /// <summary>
        /// Calculate total score and X-count from all series
        /// </summary>
        public void CalculateTotals()
        {
            TotalScore = 0;
            XCount = 0;

            if (Series == null || Series.Count == 0)
                return;

            foreach (var series in Series)
            {
                series.CalculateScore();
                TotalScore += series.Total;
                XCount += series.XCount;
            }
        }

        /// <summary>
        /// Validate the entire training score entry
        /// </summary>
        public bool IsValid(out string errorMessage)
        {
            errorMessage = string.Empty;

            if (MemberId <= 0)
            {
                errorMessage = "Invalid member ID";
                return false;
            }

            if (string.IsNullOrWhiteSpace(WeaponClass))
            {
                errorMessage = "Weapon class is required";
                return false;
            }

            if (TrainingDate == default(DateTime) || TrainingDate > DateTime.Now)
            {
                errorMessage = "Training date must be in the past";
                return false;
            }

            if (Series == null || Series.Count == 0)
            {
                errorMessage = "At least one series is required";
                return false;
            }

            if (Series.Count > 24)
            {
                errorMessage = "Maximum 24 series allowed";
                return false;
            }

            foreach (var series in Series)
            {
                if (!series.IsValid())
                {
                    errorMessage = $"Series {series.SeriesNumber} is invalid - must have exactly 5 valid shots";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Convert series list to JSON for database storage
        /// </summary>
        public string SerializeSeries()
        {
            return JsonSerializer.Serialize(Series, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        /// <summary>
        /// Load series from JSON stored in database
        /// </summary>
        public void DeserializeSeries(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                Series = new List<TrainingSeries>();
                return;
            }

            try
            {
                Series = JsonSerializer.Deserialize<List<TrainingSeries>>(json) ?? new List<TrainingSeries>();
            }
            catch
            {
                Series = new List<TrainingSeries>();
            }
        }

        /// <summary>
        /// Create a summary description of this training session
        /// </summary>
        public string GetSummary()
        {
            var type = IsCompetition ? "Tävling" : "Träning";
            return $"{TrainingDate:yyyy-MM-dd} - {type} - {WeaponClass}-Vapen - {SeriesCount} serier - {TotalScore}p ({XCount} X)";
        }
    }
}
