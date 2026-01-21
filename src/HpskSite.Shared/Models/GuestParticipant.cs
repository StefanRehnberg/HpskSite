using System.Text.Json.Serialization;

namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a guest participant identity for training matches
    /// Maps to the TrainingMatchGuests database table
    /// </summary>
    public class GuestParticipant
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Guest's display name
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// One-time token for claiming guest spot via QR code
        /// </summary>
        [JsonPropertyName("claimToken")]
        public Guid? ClaimToken { get; set; }

        /// <summary>
        /// When the claim token expires (30 minutes from creation)
        /// </summary>
        [JsonPropertyName("claimTokenExpiresAt")]
        public DateTime? ClaimTokenExpiresAt { get; set; }

        /// <summary>
        /// Session token for authenticated guest access after claiming
        /// </summary>
        [JsonPropertyName("sessionToken")]
        public Guid? SessionToken { get; set; }

        /// <summary>
        /// When the session token expires (when match ends or 24 hours)
        /// </summary>
        [JsonPropertyName("sessionExpiresAt")]
        public DateTime? SessionExpiresAt { get; set; }

        /// <summary>
        /// For Path B (invite): links to the pending Umbraco member
        /// </summary>
        [JsonPropertyName("pendingMemberId")]
        public int? PendingMemberId { get; set; }

        /// <summary>
        /// For member claim: links to an existing Umbraco member (for members who forgot their password)
        /// When set, the claim creates a participant with MemberId instead of GuestParticipantId
        /// </summary>
        [JsonPropertyName("linkedMemberId")]
        public int? LinkedMemberId { get; set; }

        /// <summary>
        /// Member ID of who created/added this guest
        /// </summary>
        [JsonPropertyName("createdByMemberId")]
        public int CreatedByMemberId { get; set; }

        /// <summary>
        /// When this guest was created
        /// </summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Whether the claim token is still valid
        /// </summary>
        [JsonIgnore]
        public bool IsClaimTokenValid =>
            ClaimToken.HasValue &&
            ClaimTokenExpiresAt.HasValue &&
            ClaimTokenExpiresAt.Value > DateTime.UtcNow;

        /// <summary>
        /// Whether the session token is still valid
        /// </summary>
        [JsonIgnore]
        public bool IsSessionValid =>
            SessionToken.HasValue &&
            SessionExpiresAt.HasValue &&
            SessionExpiresAt.Value > DateTime.UtcNow;
    }
}
