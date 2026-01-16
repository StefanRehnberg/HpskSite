using System.Text.Json.Serialization;
using HpskSite.Shared.Services;

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
        /// Total raw score (using equalized series count if set).
        /// Each series is capped at 50 (max possible score per series).
        /// </summary>
        [JsonPropertyName("totalScore")]
        public int TotalScore => ResultCalculator.CalculateRawTotal(Scores, EqualizedSeriesCount);

        /// <summary>
        /// Final score including handicap (using equalized series count if set).
        /// Handicap is applied per series, and each series (raw + handicap) is capped at 50.
        /// Uses standard rounding (AwayFromZero) for consistency with JavaScript.
        /// Example: 47 + 4 HCP = 51, capped to 50.
        /// </summary>
        [JsonPropertyName("adjustedTotalScore")]
        public int AdjustedTotalScore =>
            ResultCalculator.CalculateAdjustedTotal(Scores, HandicapPerSeries ?? 0, EqualizedSeriesCount);

        /// <summary>
        /// Total handicap adjustment actually applied (using equalized series count if set).
        /// This is the difference between AdjustedTotalScore and TotalScore.
        /// May be less than (HandicapPerSeries * SeriesCount) due to 50-point cap per series.
        /// </summary>
        [JsonPropertyName("totalHandicap")]
        public decimal TotalHandicap => AdjustedTotalScore - TotalScore;

        /// <summary>
        /// Total X-count (using equalized series count if set)
        /// </summary>
        [JsonPropertyName("totalXCount")]
        public int TotalXCount => ResultCalculator.CalculateTotalXCount(Scores, EqualizedSeriesCount);

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
    public class TrainingMatchScore : ISeriesScore
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
