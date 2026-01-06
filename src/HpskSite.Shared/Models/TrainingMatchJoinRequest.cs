using System.Text.Json.Serialization;

namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a request to join a training match
    /// Maps to the TrainingMatchJoinRequests database table
    /// </summary>
    public class TrainingMatchJoinRequest
    {
        /// <summary>
        /// Database ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// ID of the match being requested to join
        /// </summary>
        public int TrainingMatchId { get; set; }

        /// <summary>
        /// ID of the member requesting to join
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Name of the requesting member (for display)
        /// </summary>
        public string? MemberName { get; set; }

        /// <summary>
        /// Profile picture URL of the requesting member
        /// </summary>
        public string? MemberProfilePictureUrl { get; set; }

        /// <summary>
        /// Request status: Pending, Accepted, Blocked
        /// </summary>
        public string Status { get; set; } = "Pending";

        /// <summary>
        /// When the request was created
        /// </summary>
        public DateTime RequestDate { get; set; }

        /// <summary>
        /// When the request was responded to (null if pending)
        /// </summary>
        public DateTime? ResponseDate { get; set; }

        /// <summary>
        /// ID of the member who responded to the request
        /// </summary>
        public int? ResponseByMemberId { get; set; }

        /// <summary>
        /// Optional notes about the request/response
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Check if request is still pending
        /// </summary>
        [JsonIgnore]
        public bool IsPending => Status == "Pending";

        /// <summary>
        /// Check if request was accepted
        /// </summary>
        [JsonIgnore]
        public bool IsAccepted => Status == "Accepted";

        /// <summary>
        /// Check if request was blocked
        /// </summary>
        [JsonIgnore]
        public bool IsBlocked => Status == "Blocked";
    }

    /// <summary>
    /// Request status constants
    /// </summary>
    public static class JoinRequestStatus
    {
        public const string Pending = "Pending";
        public const string Accepted = "Accepted";
        public const string Blocked = "Blocked";
    }
}
