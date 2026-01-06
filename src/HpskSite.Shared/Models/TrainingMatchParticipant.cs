using System.Text.Json.Serialization;

namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a participant in a training match
    /// Maps to the TrainingMatchParticipants database table
    /// </summary>
    public class TrainingMatchParticipant
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Reference to the training match
        /// </summary>
        public int TrainingMatchId { get; set; }

        /// <summary>
        /// Member ID of the participant
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Member's first name (for display)
        /// </summary>
        [JsonPropertyName("firstName")]
        public string? FirstName { get; set; }

        /// <summary>
        /// Member's last name (for display)
        /// </summary>
        [JsonPropertyName("lastName")]
        public string? LastName { get; set; }

        /// <summary>
        /// Member's profile picture URL (for display)
        /// </summary>
        [JsonPropertyName("profilePictureUrl")]
        public string? ProfilePictureUrl { get; set; }

        /// <summary>
        /// When the participant joined the match
        /// </summary>
        public DateTime JoinedDate { get; set; }

        /// <summary>
        /// Display order in the scoreboard (column position)
        /// </summary>
        public int DisplayOrder { get; set; }

        /// <summary>
        /// Frozen handicap per series (set when joining match)
        /// </summary>
        [JsonPropertyName("handicapPerSeries")]
        public decimal? HandicapPerSeries { get; set; }

        /// <summary>
        /// Whether the handicap is provisional (less than 8 matches)
        /// </summary>
        [JsonPropertyName("isProvisional")]
        public bool? IsProvisional { get; set; }

        /// <summary>
        /// Participant's series scores in this match
        /// </summary>
        [JsonPropertyName("scores")]
        public List<TrainingMatchScore> Scores { get; set; } = new List<TrainingMatchScore>();

        /// <summary>
        /// When set, limits totals to only sum the first N series.
        /// Used for fair comparison when participants have different series counts.
        /// </summary>
        [JsonPropertyName("equalizedSeriesCount")]
        public int? EqualizedSeriesCount { get; set; }

        /// <summary>
        /// The effective series count for calculating totals and display.
        /// Uses EqualizedSeriesCount if set, otherwise all series.
        /// </summary>
        [JsonPropertyName("effectiveSeriesCount")]
        public int EffectiveSeriesCount => EqualizedSeriesCount ?? Scores.Count;

        /// <summary>
        /// Get the scores that should be included in totals (up to EqualizedSeriesCount).
        /// </summary>
        [JsonIgnore]
        private IEnumerable<TrainingMatchScore> EffectiveScores =>
            EqualizedSeriesCount.HasValue
                ? Scores.OrderBy(s => s.SeriesNumber).Take(EqualizedSeriesCount.Value)
                : Scores;

        /// <summary>
        /// Total raw score (using equalized series count if set)
        /// </summary>
        [JsonPropertyName("totalScore")]
        public int TotalScore => EffectiveScores.Sum(s => s.Total);

        /// <summary>
        /// Total handicap adjustment (using equalized series count if set)
        /// </summary>
        [JsonPropertyName("totalHandicap")]
        public decimal TotalHandicap => (HandicapPerSeries ?? 0) * EffectiveSeriesCount;

        /// <summary>
        /// Final score including handicap (using equalized series count if set)
        /// </summary>
        [JsonPropertyName("adjustedTotalScore")]
        public int AdjustedTotalScore => (int)Math.Round(TotalScore + TotalHandicap);

        /// <summary>
        /// Total X-count (using equalized series count if set)
        /// </summary>
        [JsonPropertyName("totalXCount")]
        public int TotalXCount => EffectiveScores.Sum(s => s.XCount);

        /// <summary>
        /// Number of series entered (actual count, not equalized)
        /// </summary>
        [JsonPropertyName("seriesCount")]
        public int SeriesCount => Scores.Count;

        /// <summary>
        /// Get display name
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName => $"{FirstName} {LastName}".Trim();

        /// <summary>
        /// Get initials for avatar fallback
        /// </summary>
        [JsonPropertyName("initials")]
        public string Initials
        {
            get
            {
                var first = !string.IsNullOrEmpty(FirstName) ? FirstName[0].ToString().ToUpper() : "";
                var last = !string.IsNullOrEmpty(LastName) ? LastName[0].ToString().ToUpper() : "";
                return $"{first}{last}";
            }
        }
    }

    /// <summary>
    /// Represents a single series score for a participant in a match
    /// This is a view model that combines data from TrainingScores table
    /// </summary>
    public class TrainingMatchScore
    {
        /// <summary>
        /// TrainingScores table ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Series number (1-based)
        /// </summary>
        [JsonPropertyName("seriesNumber")]
        public int SeriesNumber { get; set; }

        /// <summary>
        /// Total points for this series
        /// </summary>
        [JsonPropertyName("total")]
        public int Total { get; set; }

        /// <summary>
        /// Number of X shots
        /// </summary>
        [JsonPropertyName("xCount")]
        public int XCount { get; set; }

        /// <summary>
        /// Individual shots (if entered shot-by-shot)
        /// </summary>
        [JsonPropertyName("shots")]
        public List<string>? Shots { get; set; }

        /// <summary>
        /// Entry method used: "ShotByShot" or "SeriesTotal"
        /// </summary>
        [JsonPropertyName("entryMethod")]
        public string EntryMethod { get; set; } = "ShotByShot";

        /// <summary>
        /// URL to the target photo for this series (if uploaded)
        /// </summary>
        [JsonPropertyName("targetPhotoUrl")]
        public string? TargetPhotoUrl { get; set; }

        /// <summary>
        /// Emoji reactions to the target photo from other participants
        /// </summary>
        [JsonPropertyName("reactions")]
        public List<PhotoReaction>? Reactions { get; set; }
    }
}
