using System.Text.Json.Serialization;

namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a training match session between multiple shooters
    /// Maps to the TrainingMatches database table
    /// </summary>
    public class TrainingMatch
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Short unique code for joining the match (e.g., "ABC123")
        /// </summary>
        public string MatchCode { get; set; } = string.Empty;

        /// <summary>
        /// Optional name for the match
        /// </summary>
        public string? MatchName { get; set; }

        /// <summary>
        /// Member ID of the match creator
        /// </summary>
        public int CreatedByMemberId { get; set; }

        /// <summary>
        /// Name of the creator (for display)
        /// </summary>
        [JsonPropertyName("createdByName")]
        public string? CreatedByName { get; set; }

        /// <summary>
        /// Weapon class for this match (A, B, C, R, P)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// When the match was created
        /// </summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// Match status: Active, Completed
        /// </summary>
        public string Status { get; set; } = "Active";

        /// <summary>
        /// When the match was completed (null if still active)
        /// </summary>
        public DateTime? CompletedDate { get; set; }

        /// <summary>
        /// Number of series in this match
        /// </summary>
        public int SeriesCount { get; set; } = 2;

        /// <summary>
        /// Number of shots per series
        /// </summary>
        public int ShotsPerSeries { get; set; } = 5;

        /// <summary>
        /// Whether handicap scoring is enabled
        /// </summary>
        public bool IsHandicapEnabled { get; set; }

        /// <summary>
        /// Whether the match is open for anyone to join (true) or requires approval (false)
        /// </summary>
        [JsonPropertyName("isOpen")]
        public bool IsOpen { get; set; } = true;

        /// <summary>
        /// Member ID of the match host (same as CreatedByMemberId)
        /// </summary>
        [JsonPropertyName("hostMemberId")]
        public int HostMemberId => CreatedByMemberId;

        /// <summary>
        /// Scheduled start date/time for the match
        /// If null or in the past, match can be joined immediately
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Maximum number of series to include in total score calculations.
        /// When set, limits each participant's total to this many series (or their actual count if less).
        /// When null, falls back to using the minimum series count among all participants.
        /// </summary>
        [JsonPropertyName("maxSeriesCount")]
        public int? MaxSeriesCount { get; set; }

        /// <summary>
        /// Whether this is a team match
        /// </summary>
        [JsonPropertyName("isTeamMatch")]
        public bool IsTeamMatch { get; set; }

        /// <summary>
        /// Maximum number of shooters per team (required for team matches)
        /// </summary>
        [JsonPropertyName("maxShootersPerTeam")]
        public int? MaxShootersPerTeam { get; set; }

        /// <summary>
        /// List of teams in this match (only populated for team matches)
        /// </summary>
        [JsonPropertyName("teams")]
        public List<TrainingMatchTeam> Teams { get; set; } = new List<TrainingMatchTeam>();

        /// <summary>
        /// List of participants in this match
        /// </summary>
        [JsonPropertyName("participants")]
        public List<TrainingMatchParticipant> Participants { get; set; } = new List<TrainingMatchParticipant>();

        /// <summary>
        /// Check if match is still active
        /// </summary>
        [JsonIgnore]
        public bool IsActive => Status == "Active";

        /// <summary>
        /// Check if match has started (StartDate is null or in the past)
        /// </summary>
        [JsonPropertyName("hasStarted")]
        public bool HasStarted => StartDate == null || StartDate <= DateTime.Now;

        /// <summary>
        /// Check if match can be joined (started and still active)
        /// </summary>
        [JsonPropertyName("canBeJoined")]
        public bool CanBeJoined => HasStarted && IsActive;

        /// <summary>
        /// Display name - returns MatchName if available, otherwise MatchCode
        /// </summary>
        [JsonIgnore]
        public string DisplayName => !string.IsNullOrWhiteSpace(MatchName) ? MatchName : MatchCode;

        /// <summary>
        /// Generate a random 6-character match code
        /// </summary>
        public static string GenerateMatchCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Excluding similar-looking characters
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        /// <summary>
        /// Get the join URL for this match
        /// </summary>
        public string GetJoinUrl(string baseUrl)
        {
            return $"{baseUrl.TrimEnd('/')}/traningsmatch/?join={MatchCode}";
        }
    }
}
