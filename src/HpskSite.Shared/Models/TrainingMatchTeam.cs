using System.Text.Json.Serialization;

namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a team in a training match
    /// Maps to the TrainingMatchTeams database table
    /// </summary>
    public class TrainingMatchTeam
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
        /// Team number (1, 2, 3, 4)
        /// </summary>
        [JsonPropertyName("teamNumber")]
        public int TeamNumber { get; set; }

        /// <summary>
        /// Team name (e.g., "HPSK", "Gästlaget")
        /// </summary>
        [JsonPropertyName("teamName")]
        public string TeamName { get; set; } = string.Empty;

        /// <summary>
        /// Optional club affiliation ID
        /// </summary>
        [JsonPropertyName("clubId")]
        public int? ClubId { get; set; }

        /// <summary>
        /// Club name (populated via ClubService)
        /// </summary>
        [JsonPropertyName("clubName")]
        public string? ClubName { get; set; }

        /// <summary>
        /// Display order
        /// </summary>
        [JsonPropertyName("displayOrder")]
        public int DisplayOrder { get; set; }

        /// <summary>
        /// Number of participants in this team (calculated)
        /// </summary>
        [JsonPropertyName("participantCount")]
        public int ParticipantCount { get; set; }

        /// <summary>
        /// Combined raw score of all team members (calculated)
        /// </summary>
        [JsonPropertyName("teamScore")]
        public int TeamScore { get; set; }

        /// <summary>
        /// Combined adjusted score of all team members including handicap (calculated)
        /// </summary>
        [JsonPropertyName("adjustedTeamScore")]
        public int AdjustedTeamScore { get; set; }

        /// <summary>
        /// Combined X-count of all team members (calculated)
        /// </summary>
        [JsonPropertyName("totalXCount")]
        public int TotalXCount { get; set; }

        /// <summary>
        /// Team rank based on adjusted score (calculated)
        /// </summary>
        [JsonPropertyName("rank")]
        public int Rank { get; set; }
    }

    /// <summary>
    /// DTO for creating a team in a training match
    /// </summary>
    public class CreateTeamRequest
    {
        /// <summary>
        /// Team name
        /// </summary>
        [JsonPropertyName("teamName")]
        public string TeamName { get; set; } = string.Empty;

        /// <summary>
        /// Optional club affiliation ID
        /// </summary>
        [JsonPropertyName("clubId")]
        public int? ClubId { get; set; }
    }

    /// <summary>
    /// DTO for team definition when creating a team match
    /// </summary>
    public class TeamDefinition
    {
        /// <summary>
        /// Team number (1, 2, 3, 4)
        /// </summary>
        [JsonPropertyName("teamNumber")]
        public int TeamNumber { get; set; }

        /// <summary>
        /// Team name
        /// </summary>
        [JsonPropertyName("teamName")]
        public string TeamName { get; set; } = string.Empty;

        /// <summary>
        /// Optional club affiliation ID
        /// </summary>
        [JsonPropertyName("clubId")]
        public int? ClubId { get; set; }
    }
}
