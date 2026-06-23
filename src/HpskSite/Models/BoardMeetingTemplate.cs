using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A club/region's saved agenda template for a meeting type (one active row per owner+type).
    /// The ordered typed items are stored as JSON in <see cref="ItemsJson"/>. When absent, the
    /// built-in default from <see cref="BoardMeetingTemplates"/> is used.
    /// </summary>
    [TableName("BoardMeetingTemplates")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeetingTemplate
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string MeetingTypeKey { get; set; } = "";
        public string ItemsJson { get; set; } = "[]";
        public int? UpdatedByMemberId { get; set; }
        public DateTime UpdatedDate { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>One typed agenda row inside a template (and the wire shape for the editor).</summary>
    public class BoardTemplateItem
    {
        public string ItemType { get; set; } = "text";       // note / text / election
        public string Heading { get; set; } = "";
        public string? ElectionRole { get; set; }            // chairman / secretary / adjuster / "" (generic)
        public int ElectionCount { get; set; } = 1;
        public string ElectionSource { get; set; } = "attendees";   // attendees / members
    }
}
