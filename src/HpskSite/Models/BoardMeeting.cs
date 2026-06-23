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
        public DateTime? JusteringRequestedDate { get; set; }   // when sent for digital justering
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

        // Typed agenda items (2026-06-23). ItemType decides which fields show + how it prints:
        //   "note"     — heading + Anteckningar only (no Beslut)
        //   "text"     — heading + Anteckningar + Beslut (the original behaviour; also "Övrigt")
        //   "election" — pick ElectionCount present persons; ElectionRole maps them to a signing role.
        public string ItemType { get; set; } = "text";
        // For ItemType="election": "chairman" / "secretary" / "adjuster" (sets the attendee flag that
        // drives the protokoll signatures), or "" for a generic election (e.g. valberedning).
        public string? ElectionRole { get; set; }
        public int ElectionCount { get; set; } = 1;
        // "attendees" = pick among present attendees (board); "members" = pick any club/region member
        // (common for justerare at an årsmöte). Role-mapped members get added as attendees automatically.
        public string ElectionSource { get; set; } = "attendees";
        // CSV of elected member ids (the persons chosen in an election item).
        public string? ElectedMemberIds { get; set; }
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
        public DateTime? ApprovedDate { get; set; }   // digital justering: when this signer approved
        public string? ApprovedVia { get; set; }      // qr / web / email

        // Display-only
        [ResultColumn]
        public string? MemberName { get; set; }

        [Ignore]
        public bool IsPresent => AttendanceStatus == "Närvarande";

        /// <summary>A required protocol signer: ordförande, sekreterare or justerare.</summary>
        [Ignore]
        public bool IsSigner => IsChairman || IsSecretary || IsAdjuster;
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
