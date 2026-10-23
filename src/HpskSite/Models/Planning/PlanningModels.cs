using System;
using NPoco;

namespace HpskSite.Models.Planning
{
    // ============================================================================================
    //  Tävlingsplanering, P1 — POCO:erna. Se notes/tavlingsplanering-fristaende-plan-2026-10-21.md.
    //
    //  ⚠️ NPoco skriver ALLA egenskaper vid INSERT och UPDATE — databasens standardvärden gäller
    //  aldrig. C#-standardvärdena här måste därför vara rätt.
    //  ⚠️ All tid är svensk tid (DateTime.Now). Inga …Utc-kolumner.
    //  ⚠️ Svar, röster och läsmarkering pekar på DELTAGAREN (PlanningMember.Id), aldrig på medlemmen.
    // ============================================================================================

    /// <summary>
    /// Planeringens slag. Lagras som text — byt aldrig en befintlig sträng. ⚠️ Frågas inte längre
    /// (Stefan 2026-10-22): valet ändrade ingenting, så nya planeringar får <see cref="Group"/>. Kolumnen
    /// står kvar för den dag ett slag faktiskt styr något (t.ex. mallar för SM).
    /// </summary>
    public static class PlanningKind
    {
        public const string Group = "Planeringsgrupp";
        public const string ClubCompetition = "Klubbtavling";
        public const string RegionChampionship = "Kretsmasterskap";
        public const string SmWeek = "SmVecka";
        public const string Other = "Annat";

        public static readonly string[] All = { Group, ClubCompetition, RegionChampionship, SmWeek, Other };

        public static string Normalize(string? v) =>
            Array.Find(All, k => string.Equals(k, (v ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Group;

        public static string Label(string? v) => Normalize(v) switch
        {
            ClubCompetition => "Klubbens tävling",
            RegionChampionship => "Kretsens tävling",
            SmWeek => "SM eller landsdelsmästerskap",
            Other => "Annat",
            _ => "Planering",
        };
    }

    /// <summary>
    /// Nivåerna i planeringsgruppen. Lagras som text — byt aldrig en befintlig sträng ("Owner" visas
    /// som Sammankallande). ⚠️ Förklaringarna (<see cref="Help"/>) är det användaren väljer efter
    /// (Stefan 2026-10-23: "så man förstår och inte ger alla fulla rättigheter") — de visas vid varje
    /// val av nivå, och texten finns bara här.
    /// </summary>
    public static class PlanningAccessLevel
    {
        /// <summary>Sammankallande — leder gruppen: vem som är med, deras nivå, byta ut, arkivera, ta bort.</summary>
        public const string Owner = "Owner";
        /// <summary>Kan ändra — planeringens namn och datum, och bjuda in.</summary>
        public const string Editor = "Editor";
        /// <summary>Deltar — skriver meddelanden, svarar på tider, skapar och bokar möten.</summary>
        public const string Participant = "Participant";
        /// <summary>Följer — läser allt, även meddelandena, men skriver inte och svarar inte på tider.</summary>
        public const string Follower = "Follower";

        /// <summary>I den ordning de visas: från minst till mest.</summary>
        public static readonly string[] All = { Follower, Participant, Editor, Owner };

        public static string? Normalize(string? v) =>
            Array.Find(All, k => string.Equals(k, (v ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        public static string Label(string? v) => v switch
        {
            Owner => "Sammankallande",
            Editor => "Kan ändra",
            Follower => "Följer",
            _ => "Deltar",
        };

        public static string Help(string? v) => v switch
        {
            Owner => "Leder gruppen. Kan dessutom ändra vem som är med och deras nivå, byta ut den som inte kan vara med längre, arkivera och ta bort planeringen. Ge det till en eller två personer — inte till alla.",
            Editor => "Som Deltar, och kan dessutom bjuda in fler och ändra planeringens namn och datum.",
            Follower => "Läser allt, även meddelandena, men skriver inte och svarar inte på mötestider. För den som vill hålla sig informerad, till exempel förbundets kontaktperson.",
            _ => "Skriver meddelanden, svarar på mötestider, föreslår och bokar möten. Det vanliga — välj det för de flesta.",
        };

        public static int Rank(string? v) => v switch { Owner => 4, Editor => 3, Participant => 2, Follower => 1, _ => 0 };
    }

    /// <summary>
    /// Hur deltagaren vill bli aviserad (Stefan 2026-10-21: var och en ställer in sitt eget,
    /// <b>förval ett mejl om dagen</b>).
    /// </summary>
    public static class PlanningNotifyMode
    {
        /// <summary>Ett mejl om dagen med det som är nytt. Möten mejlas direkt. FÖRVAL.</summary>
        public const string Daily = "Daily";
        /// <summary>Push direkt när jag nämns, annars högst en gång i timmen, plus dagens mejl.</summary>
        public const string All = "All";
        /// <summary>Bara när någon nämner mig (push + mejl direkt). Möten mejlas.</summary>
        public const string Mentions = "Mentions";
        /// <summary>Ingenting.</summary>
        public const string Off = "Off";

        public const string Default = Daily;
        public static readonly string[] Values = { Daily, All, Mentions, Off };

        public static string Normalize(string? v) =>
            Array.Find(Values, k => string.Equals(k, (v ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;

        public static string Label(string? v) => Normalize(v) switch
        {
            All => "Allt direkt",
            Mentions => "Bara när någon nämner mig",
            Off => "Inga notiser",
            _ => "Ett mejl om dagen",
        };

        public static string Help(string? v) => Normalize(v) switch
        {
            All => "Notis i telefonen direkt när någon nämner dig, annars högst en gång i timmen — och ett mejl om dagen med det du inte läst.",
            Mentions => "Bara när någon nämner dig: en notis och ett mejl direkt. Möten mejlas som vanligt.",
            Off => "Inga notiser och inga mejl alls från planeringen, inte heller om möten.",
            _ => "Ett mejl på kvällen med det som är nytt och som du inte läst. Mötesförfrågningar och bokade möten mejlas direkt.",
        };
    }

    public static class PlanningMeetingStatus
    {
        public const string Proposed = "Proposed";
        public const string Scheduled = "Scheduled";
        public const string Cancelled = "Cancelled";
    }

    public static class PlanningVoteAnswer
    {
        public const string Yes = "Yes";
        public const string Maybe = "Maybe";
        public const string No = "No";
        public static string? Normalize(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
        {
            "yes" or "ja" => Yes,
            "maybe" or "kanske" => Maybe,
            "no" or "nej" => No,
            _ => null,
        };
    }

    [TableName("Planning")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = PlanningKind.Group;
        public string? TargetText { get; set; }
        public DateTime? TargetStart { get; set; }
        public DateTime? TargetEnd { get; set; }
        public string Status { get; set; } = "Active";
        /// <summary>Den sammankallandes val: planeringen syns i listan för alla medlemmar, som kan be om att få vara med.</summary>
        public bool ListedForMembers { get; set; }
        public int? CopiedFromPlanningId { get; set; }
        public int? SeededFromCompetitionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UpdatedBy { get; set; }
        public DateTime? DeletedAt { get; set; }
        public int? DeletedBy { get; set; }
    }

    [TableName("PlanningPart")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningPartRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public int? ParentPlanningPartId { get; set; }
        public string Kind { get; set; } = "Root";
        public string Name { get; set; } = "";
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    [TableName("PlanningMember")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMemberRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        /// <summary>Null = gäst med bara e-post (t.ex. någon som inte är skytt).</summary>
        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? GuestEmail { get; set; }
        public string Access { get; set; } = PlanningAccessLevel.Participant;
        public int? RepresentsClubId { get; set; }
        public int? RepresentsRegionId { get; set; }
        public string? RepresentsText { get; set; }
        public string NotifyMode { get; set; } = PlanningNotifyMode.Default;
        public DateTime AddedAt { get; set; }
        public int AddedBy { get; set; }
        public DateTime? InviteSentAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime? RemovedAt { get; set; }
        public int? RemovedBy { get; set; }

        [Ignore] public bool IsGuest => !(MemberId > 0);
        [Ignore] public bool IsActive => RemovedAt == null;
    }

    /// <summary>
    /// En förfrågan om att få vara med: en inbjudan med e-post som väntar på svar (<see cref="Invite"/>),
    /// eller en medlem som ber om att få vara med (<see cref="Request"/>). Stefan 2026-10-23: en länk i ett
    /// mejl ska aldrig ensam ge tillgång — den sammankallande (eller sajtadmin) godkänner och sätter då nivå
    /// och vad personen företräder. Först då skapas en <see cref="PlanningMemberRow"/>.
    /// </summary>
    public static class PlanningRequestKind
    {
        public const string Invite = "Invite";
        public const string Request = "Request";
    }

    public static class PlanningRequestStatus
    {
        /// <summary>Inbjudan skickad, inget svar ännu.</summary>
        public const string Sent = "Sent";
        /// <summary>Väntar på godkännande: den inbjudna har tackat ja, eller medlemmen har bett om att få vara med.</summary>
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        /// <summary>Den inbjudna tackade nej.</summary>
        public const string Declined = "Declined";
        /// <summary>Dragen tillbaka av den som bjöd in eller bad om att få vara med.</summary>
        public const string Withdrawn = "Withdrawn";

        public static bool IsOpen(string? s) => s == Sent || s == Pending;
    }

    /// <summary>Taket för inbjudningar, så att ingen kan skicka mejl till hela registret.</summary>
    public static class PlanningInviteRules
    {
        public const int MaxPerDay = 30;
        public static string LimitMessage => $"Planeringen har redan bjudit in {MaxPerDay} personer det senaste dygnet. Vänta till i morgon, eller hör av dig till sajtens administratör om ni behöver fler.";
    }

    [TableName("PlanningRequest")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningRequestRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public string Kind { get; set; } = PlanningRequestKind.Request;
        public int? MemberId { get; set; }
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? Message { get; set; }
        public string? ProposedRepresents { get; set; }
        public string Status { get; set; } = PlanningRequestStatus.Pending;
        public DateTime CreatedAt { get; set; }
        public int CreatedBy { get; set; }
        public DateTime? RespondedAt { get; set; }
        public DateTime? DecidedAt { get; set; }
        public int? DecidedBy { get; set; }
        public string? DecisionNote { get; set; }
        public int? ParticipantId { get; set; }

        [Ignore] public bool IsOpen => PlanningRequestStatus.IsOpen(Status);
    }

    [TableName("PlanningMessage")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMessageRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public int? ParticipantId { get; set; }
        public string Kind { get; set; } = "Message";
        public string Body { get; set; } = "";
        public string? SystemKey { get; set; }
        public int? SubjectParticipantId { get; set; }
        public string? RefKind { get; set; }
        public int? RefId { get; set; }
        public int? ReplyToMessageId { get; set; }
        public bool ViaEmail { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    [TableName("PlanningMessageMention")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMentionRow
    {
        public int Id { get; set; }
        public int MessageId { get; set; }
        public int PlanningId { get; set; }
        public int ParticipantId { get; set; }
        public DateTime? NotifiedAt { get; set; }
    }

    [TableName("PlanningMessageRead")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningReadRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public int ParticipantId { get; set; }
        public int LastReadMessageId { get; set; }
        public DateTime? ReadAt { get; set; }
        public DateTime? LastPushAt { get; set; }
        public int LastDigestMessageId { get; set; }
        public DateTime? LastDigestAt { get; set; }
    }

    [TableName("PlanningMeeting")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMeetingRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public string Title { get; set; } = "";
        public string Status { get; set; } = PlanningMeetingStatus.Proposed;
        public int? ChosenOptionId { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public string? Place { get; set; }
        public string? OnlineUrl { get; set; }
        public string? Agenda { get; set; }
        public string? Notes { get; set; }
        public DateTime? VoteDeadline { get; set; }
        public int Sequence { get; set; }
        public DateTime CreatedAt { get; set; }
        public int CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? BookedAt { get; set; }
        public int? BookedBy { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    [TableName("PlanningMeetingOption")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMeetingOptionRow
    {
        public int Id { get; set; }
        public int PlanningMeetingId { get; set; }
        public DateTime StartsAt { get; set; }
        public DateTime EndsAt { get; set; }
        public int SortOrder { get; set; }
    }

    [TableName("PlanningMeetingVote")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningMeetingVoteRow
    {
        public int Id { get; set; }
        public int PlanningMeetingId { get; set; }
        public int PlanningMeetingOptionId { get; set; }
        public int ParticipantId { get; set; }
        public string Answer { get; set; } = PlanningVoteAnswer.Yes;
        public DateTime AnsweredAt { get; set; }
        public bool ViaEmail { get; set; }
    }
}
