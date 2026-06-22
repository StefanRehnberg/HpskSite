using NPoco;

namespace HpskSite.Models
{
    [TableName("BoardMeetings")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeeting
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string MeetingType { get; set; } = "Styrelsemote";
        public string Title { get; set; } = string.Empty;
        public DateTime MeetingDate { get; set; }
        public string? Location { get; set; }
        public string Status { get; set; } = "Planerat";
        public int? QuorumOverride { get; set; }
        public string? Notes { get; set; }
        public int? AdjusterMemberId { get; set; }
        public DateTime? JustifiedDate { get; set; }
        public DateTime? KallelseSentDate { get; set; }
        public int? KallelseSentByMemberId { get; set; }
        public int? KallelseRecipientCount { get; set; }
        public int? CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public bool IsActive { get; set; } = true;

        [Ignore]
        public string TypeLabel => BoardMeetingTemplates.GetLabel(MeetingType);
    }

    [TableName("BoardMeetingAgendaItems")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeetingAgendaItem
    {
        public int Id { get; set; }
        public int MeetingId { get; set; }
        public int SortOrder { get; set; }
        public string Heading { get; set; } = string.Empty;
        public string? Discussion { get; set; }
        public string? Decision { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [TableName("BoardMeetingAttendees")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeetingAttendee
    {
        public int Id { get; set; }
        public int MeetingId { get; set; }
        public int MemberId { get; set; }
        public string? RoleTitle { get; set; }
        public string AttendanceStatus { get; set; } = "Närvarande";
        public bool IsChairman { get; set; }
        public bool IsSecretary { get; set; }
        public bool IsAdjuster { get; set; }

        // Display-only
        [ResultColumn]
        public string? MemberName { get; set; }

        [Ignore]
        public bool IsPresent => AttendanceStatus == "Närvarande";
    }

    [TableName("BoardYearWheelItems")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardYearWheelItem
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int Year { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime? TargetDate { get; set; }
        public bool Done { get; set; }
        public DateTime? DoneDate { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;

        [Ignore]
        public bool IsOverdue => !Done && TargetDate.HasValue && TargetDate.Value.Date < DateTime.Today;
    }

    [TableName("BoardNominations")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardNomination
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int Year { get; set; }
        public string? PostKey { get; set; }
        public string PostLabel { get; set; } = string.Empty;
        public string CandidateName { get; set; } = string.Empty;
        public int? CandidateMemberId { get; set; }
        public string Status { get; set; } = "Föreslagen";
        public string? Notes { get; set; }
        public int SortOrder { get; set; }
        public int? CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [TableName("BoardMeetingAgendaLinks")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeetingAgendaLink
    {
        public int Id { get; set; }
        public int AgendaItemId { get; set; }
        public string Kind { get; set; } = "url";   // meeting / document / url
        public int? RefId { get; set; }              // target meeting id or document id
        public string? Url { get; set; }             // for kind=url
        public string Label { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
    }

    [TableName("BoardMeetingActions")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMeetingAction
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int? MeetingId { get; set; }
        public int? AgendaItemId { get; set; }
        public string Description { get; set; } = string.Empty;
        public int? AssignedToMemberId { get; set; }
        public DateTime? DueDate { get; set; }
        public string Status { get; set; } = "Öppen";
        public DateTime? CompletedDate { get; set; }
        public int? CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public bool IsActive { get; set; } = true;

        // Display-only
        [ResultColumn]
        public string? AssignedToName { get; set; }
        [ResultColumn]
        public string? MeetingTitle { get; set; }

        [Ignore]
        public bool IsDone => Status == "Klar";
        [Ignore]
        public bool IsOverdue => Status != "Klar" && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    }
}
