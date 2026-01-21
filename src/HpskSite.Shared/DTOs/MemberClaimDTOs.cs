using System.Text.Json.Serialization;

namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Search result for member search (used by organizers to find members)
    /// </summary>
    public class MemberSearchResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("firstName")]
        public string FirstName { get; set; } = string.Empty;

        [JsonPropertyName("lastName")]
        public string LastName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName => $"{FirstName} {LastName}".Trim();

        [JsonPropertyName("clubName")]
        public string? ClubName { get; set; }
    }

    /// <summary>
    /// Request to create a member claim QR code
    /// </summary>
    public class CreateMemberClaimRequest
    {
        /// <summary>
        /// The member ID to create a claim for
        /// </summary>
        [JsonPropertyName("memberId")]
        public int MemberId { get; set; }

        /// <summary>
        /// Handicap class for the member (optional - for handicap matches)
        /// </summary>
        [JsonPropertyName("handicapClass")]
        public string? HandicapClass { get; set; }
    }

    /// <summary>
    /// Response after creating a member claim QR code
    /// </summary>
    public class CreateMemberClaimResponse
    {
        /// <summary>
        /// The claim record ID (in TrainingMatchGuests table)
        /// </summary>
        [JsonPropertyName("claimId")]
        public int ClaimId { get; set; }

        /// <summary>
        /// The member's display name
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// The member's club name (for verification)
        /// </summary>
        [JsonPropertyName("clubName")]
        public string? ClubName { get; set; }

        /// <summary>
        /// URL for the QR code that the member scans to claim their spot
        /// Format: https://site.com/match/{code}/member/{token}
        /// </summary>
        [JsonPropertyName("claimUrl")]
        public string ClaimUrl { get; set; } = string.Empty;

        /// <summary>
        /// When the claim token expires (30 minutes from creation)
        /// </summary>
        [JsonPropertyName("claimExpiresAt")]
        public DateTime ClaimExpiresAt { get; set; }
    }

    /// <summary>
    /// Information shown on member claim confirmation page
    /// </summary>
    public class MemberClaimInfo
    {
        /// <summary>
        /// The member's display name
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// The member's club name
        /// </summary>
        [JsonPropertyName("clubName")]
        public string? ClubName { get; set; }

        /// <summary>
        /// The match name
        /// </summary>
        [JsonPropertyName("matchName")]
        public string? MatchName { get; set; }

        /// <summary>
        /// The match code
        /// </summary>
        [JsonPropertyName("matchCode")]
        public string MatchCode { get; set; } = string.Empty;

        /// <summary>
        /// Whether the claim token is valid
        /// </summary>
        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        /// <summary>
        /// Error message if claim is not valid
        /// </summary>
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }
}
