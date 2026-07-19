using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.Models.Messaging
{
    /// <summary>
    /// One in-app message between competition functionaries, addressed by a generic
    /// (ScopeType, ScopeKey) pair rather than by shooter/registration. This keeps the store
    /// transport- and addressing-agnostic:
    ///   - the functionary channel (this feature) delivers over the ~10 s poll each staff screen
    ///     already runs;
    ///   - the later shooter-facing channel reads the same rows addressed to shooter-shaped scopes
    ///     (e.g. Klass/Gren) and delivers them over web-push instead — no schema change.
    ///
    /// ScopeType values: Station | Klass | Skjutlag | Role | All | Person.
    /// ScopeKey holds the station number / weapon class / lag number / role name / member id;
    /// it is null (ignored) for the All broadcast.
    /// </summary>
    [TableName("EventMessage")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class EventMessage
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }

        [MaxLength(50)]
        public string Discipline { get; set; } = "";

        // --- Addressing (who the message is FOR) ---
        [MaxLength(20)]
        public string ScopeType { get; set; } = "";   // Station | Klass | Skjutlag | Role | All | Person
        [MaxLength(100)]
        public string? ScopeKey { get; set; }          // station no. / weapon class / lag no. / role / memberId; null for All

        // --- Sender ---
        public int FromMemberId { get; set; }
        [MaxLength(200)]
        public string FromName { get; set; } = "";     // denormalized so the poll needs no member join
        [MaxLength(20)]
        public string? FromScopeType { get; set; }      // where the sender sat when posting (reply context / ops-log)
        [MaxLength(100)]
        public string? FromScopeKey { get; set; }

        // --- Payload ---
        public string Body { get; set; } = "";
        [MaxLength(20)]
        public string Urgency { get; set; } = MessageUrgency.Normal;   // Normal | Urgent | Safety

        public DateTime CreatedDate { get; set; }
    }

    /// <summary>
    /// Per-recipient read-receipt (mottaget-kvittens). One row per (message, member); the unique
    /// index makes re-acking a no-op. Append-only.
    /// </summary>
    [TableName("EventMessageAck")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class EventMessageAck
    {
        public int Id { get; set; }
        public int MessageId { get; set; }
        public int MemberId { get; set; }
        [MaxLength(200)]
        public string MemberName { get; set; } = "";
        public DateTime AckDate { get; set; }
    }

    public static class MessageScopeType
    {
        public const string Station = "Station";
        public const string Klass = "Klass";
        public const string Skjutlag = "Skjutlag";
        public const string Role = "Role";
        public const string All = "All";
        public const string Person = "Person";
    }

    public static class MessageUrgency
    {
        public const string Normal = "Normal";
        public const string Urgent = "Urgent";
        public const string Safety = "Safety";
    }

    /// <summary>
    /// A single addressing selector a viewer belongs to. A staff screen declares the scopes it
    /// represents (e.g. Station:5); the service always adds All and Person:me on top.
    /// </summary>
    public class EventMessageScope
    {
        public string ScopeType { get; set; } = "";
        public string? ScopeKey { get; set; }

        public EventMessageScope() { }
        public EventMessageScope(string type, string? key)
        {
            ScopeType = type;
            ScopeKey = key;
        }

        /// <summary>Parse a compact "Type:Key" token (e.g. "Station:5", "Role:stationschef").</summary>
        public static EventMessageScope? Parse(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var i = token.IndexOf(':');
            if (i < 0) return new EventMessageScope(token.Trim(), null);
            var type = token.Substring(0, i).Trim();
            var key = token.Substring(i + 1).Trim();
            return string.IsNullOrEmpty(type) ? null : new EventMessageScope(type, string.IsNullOrEmpty(key) ? null : key);
        }
    }

    // --- Output DTOs (not table-mapped) ---

    public class EventMessageView
    {
        public int Id { get; set; }
        public string ScopeType { get; set; } = "";
        public string? ScopeKey { get; set; }
        public int FromMemberId { get; set; }
        public string FromName { get; set; } = "";
        public string? FromScopeType { get; set; }
        public string? FromScopeKey { get; set; }
        public string Body { get; set; } = "";
        public string Urgency { get; set; } = MessageUrgency.Normal;
        public DateTime CreatedDate { get; set; }
        public bool Mine { get; set; }
        public bool AckedByMe { get; set; }
        public int AckCount { get; set; }
        public List<EventMessageAckView> Acks { get; set; } = new();
    }

    public class EventMessageAckView
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public DateTime AckDate { get; set; }
    }

    public class EventMessageFeed
    {
        public List<EventMessageView> Messages { get; set; } = new();
        public DateTime ServerTime { get; set; }
    }

    // --- Request DTOs ---

    public class PostEventMessageRequest
    {
        public int CompetitionId { get; set; }
        public string ScopeType { get; set; } = "";
        public string? ScopeKey { get; set; }
        public string Body { get; set; } = "";
        public string? Urgency { get; set; }
        public string? FromScopeType { get; set; }
        public string? FromScopeKey { get; set; }
    }

    public class AckEventMessageRequest
    {
        public int CompetitionId { get; set; }
        public int MessageId { get; set; }
    }
}
