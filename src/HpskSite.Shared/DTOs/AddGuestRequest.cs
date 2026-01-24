using System.Text.Json.Serialization;

namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Request to add a guest participant to a training match
    /// </summary>
    public class AddGuestRequest
    {
        /// <summary>
        /// Guest's display name (first and last name)
        /// </summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Handicap class for guest (optional - for handicap matches)
        /// Values: "Klass 1 - Nybörjare", "Klass 2 - Guldmärkesskytt", "Klass 3 - Riksmästare"
        /// </summary>
        [JsonPropertyName("handicapClass")]
        public string? HandicapClass { get; set; }

        /// <summary>
        /// Guest's email address (optional - Path B: Full invite)
        /// If provided, creates a member account in "PendingInvite" state and sends invite email
        /// Requires admin/club admin permissions
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        /// <summary>
        /// Club ID for the invited member (required if Email is provided)
        /// </summary>
        [JsonPropertyName("clubId")]
        public int? ClubId { get; set; }

        /// <summary>
        /// Team ID for team matches (optional)
        /// If the match is a team match, this assigns the guest to a specific team
        /// </summary>
        [JsonPropertyName("teamId")]
        public int? TeamId { get; set; }
    }
}
