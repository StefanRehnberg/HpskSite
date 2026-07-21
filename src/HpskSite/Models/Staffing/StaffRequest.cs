using NPoco;

namespace HpskSite.Models.Staffing
{
    [TableName("StaffRequestLog")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffRequestLog
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string Mode { get; set; } = "";     // Relay | Direct
        public int RecipientCount { get; set; }
        public int SentCount { get; set; }
        public int PushCount { get; set; }
        public int ByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    public static class StaffRequestMode
    {
        public const string Relay = "Relay";    // to club/region admins, who distribute
        public const string Direct = "Direct";  // straight to members (Brevo + web-push)
    }

    /// <summary>Preview of who a mail-out would reach, per mode, before sending.</summary>
    public class StaffRequestPreview
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool HasScopes { get; set; }
        public int RelayCount { get; set; }       // club/region admins
        public int DirectCount { get; set; }       // members
        public bool DirectAvailable { get; set; }  // hosting club has a Brevo key (else direct email is limited)
        public List<string> AudienceLabels { get; set; } = new();
        public string? LastSent { get; set; }       // "Relay · 4 · 2026-07-21" or null
    }

    public class SendStaffRequestRequest
    {
        public int CompetitionId { get; set; }
        public string Mode { get; set; } = StaffRequestMode.Relay;
        public string? Message { get; set; }
    }
}
