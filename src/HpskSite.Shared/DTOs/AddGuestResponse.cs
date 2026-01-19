using System.Text.Json.Serialization;

namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Response after adding a guest participant to a training match
    /// </summary>
    public class AddGuestResponse
    {
        /// <summary>
        /// The guest participant ID
        /// </summary>
        [JsonPropertyName("guestId")]
        public int GuestId { get; set; }

        /// <summary>
        /// The guest's display name
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// URL for the QR code that the guest scans to claim their spot
        /// Format: https://site.com/match/{code}/guest/{token}
        /// </summary>
        [JsonPropertyName("claimUrl")]
        public string ClaimUrl { get; set; } = string.Empty;

        /// <summary>
        /// When the claim token expires (30 minutes from creation)
        /// </summary>
        [JsonPropertyName("claimExpiresAt")]
        public DateTime ClaimExpiresAt { get; set; }

        /// <summary>
        /// Whether an invitation email was sent (true for Path B - full invite)
        /// </summary>
        [JsonPropertyName("inviteSent")]
        public bool InviteSent { get; set; }

        /// <summary>
        /// Member ID if Path B (email invite sent), null for Path A (simple guest)
        /// </summary>
        [JsonPropertyName("pendingMemberId")]
        public int? PendingMemberId { get; set; }

        /// <summary>
        /// The participant ID in the TrainingMatchParticipants table
        /// </summary>
        [JsonPropertyName("participantId")]
        public int ParticipantId { get; set; }
    }
}
