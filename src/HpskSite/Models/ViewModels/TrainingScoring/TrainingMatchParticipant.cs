using System.Text.Json.Serialization;

namespace HpskSite.Models.ViewModels.TrainingScoring
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
        /// Participant's series scores in this match
        /// </summary>
        [JsonPropertyName("scores")]
        public List<TrainingMatchScore> Scores { get; set; } = new List<TrainingMatchScore>();

        /// <summary>
        /// Total score across all series
        /// </summary>
        [JsonPropertyName("totalScore")]
        public int TotalScore => Scores.Sum(s => s.Total);

        /// <summary>
        /// Total X-count across all series
        /// </summary>
        [JsonPropertyName("totalXCount")]
        public int TotalXCount => Scores.Sum(s => s.XCount);

        /// <summary>
        /// Number of series entered
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
    }
}
