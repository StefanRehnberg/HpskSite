using System.Text.Json.Serialization;

namespace HpskSite.Models.ViewModels.TrainingScoring
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
        /// Scheduled start date/time for the match
        /// If null or in the past, match can be joined immediately
        /// </summary>
        public DateTime? StartDate { get; set; }

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
