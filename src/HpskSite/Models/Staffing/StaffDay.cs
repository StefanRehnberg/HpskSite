using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// One day of the PLAN — not necessarily a day of the competition. See
    /// <c>Migrations/create-staff-day-table.sql</c> for the reasoning: build days, materiel runs and
    /// teardown all carry real crew at real times and fall outside the competition span.
    /// This list is the single source of the day axis for the Bemanning grid AND for Dagsprogram.
    /// </summary>
    [TableName("StaffDay")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffDay
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public DateTime DayDate { get; set; }
        /// <summary>Arrangör's own words — "Iordningställande", "Vapengrupp C". Empty = just the date.</summary>
        public string Label { get; set; } = "";
        public string Kind { get; set; } = StaffDayKind.Competition;
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    public static class StaffDayKind
    {
        /// <summary>A day the competition is actually shot. Reaches the shooters' Dagsprogram.</summary>
        public const string Competition = "Tavlingsdag";
        /// <summary>Build-up / materiel / prep. Crew see it; shooters must not.</summary>
        public const string Prep = "Forberedelse";
        /// <summary>Teardown / återställning. Crew only.</summary>
        public const string After = "Efterarbete";

        public static string Label(string kind) => kind switch
        {
            Prep => "Förberedelse",
            After => "Efterarbete",
            _ => "Tävlingsdag",
        };

        /// <summary>Only competition days are published to participants.</summary>
        public static bool IsParticipantFacing(string? kind)
            => string.IsNullOrEmpty(kind) || string.Equals(kind, Competition, StringComparison.OrdinalIgnoreCase);

        public static string Normalise(string? kind)
            => string.Equals(kind, Prep, StringComparison.OrdinalIgnoreCase) ? Prep
             : string.Equals(kind, After, StringComparison.OrdinalIgnoreCase) ? After
             : Competition;
    }

    public class StaffDayView
    {
        public int Id { get; set; }
        public string Date { get; set; } = "";      // "yyyy-MM-dd"
        public string Label { get; set; } = "";
        public string Kind { get; set; } = StaffDayKind.Competition;
        public string KindLabel { get; set; } = "";
        public bool IsParticipantFacing { get; set; }
        /// <summary>True when the day carries crew — a day you cannot delete without losing work.</summary>
        public bool HasAssignments { get; set; }
    }

    public class SaveStaffDayRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string? Date { get; set; }           // "yyyy-MM-dd"
        public string? Label { get; set; }
        public string? Kind { get; set; }
    }

    public class DeleteStaffDayRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    /// <summary>Give a member-less roster row a way to be reached (e-post/mobil), from the grid cell.</summary>
    public class SetContactRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
    }

    /// <summary>Move one assignment to another day of the plan (keeps its clock time).</summary>
    public class MoveAssignmentRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string? Date { get; set; }   // "yyyy-MM-dd"
    }

    // --- person identity: one human, many rows -----------------------------------------------------

    /// <summary>A free-text name on the roster, and the members it might be.</summary>
    public class PersonMatchRow
    {
        public string Key { get; set; } = "";          // "n:{lowercased name}"
        public string Name { get; set; } = "";
        /// <summary>How many assignments carry this name — what a link or a rename will touch.</summary>
        public int RowCount { get; set; }
        public string? Email { get; set; }
        /// <summary>"Bert J / Hans R" is a note, not a person. Flagged instead of half-matched.</summary>
        public bool LooksLikeTwoPeople { get; set; }
        /// <summary>One clear winner — safe to pre-select. Never means "linked automatically".</summary>
        public bool Confident { get; set; }
        public List<PersonMatchCandidate> Candidates { get; set; } = new();
        /// <summary>Names already on THIS competition that look like the same person written twice — the
        /// commonest typo, and one the member register cannot help with.</summary>
        public List<PersonMatchInPlan> InPlan { get; set; } = new();
    }

    public class PersonMatchInPlan
    {
        public string Name { get; set; } = "";
        /// <summary>Non-zero when that other name is already linked to a member, so merging links too.</summary>
        public int MemberId { get; set; }
        public int RowCount { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; } = "";
    }

    public class PersonMatchCandidate
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string? ClubName { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Act on a PERSON, not a row. Name, e-mail and phone live on every assignment, so Hans Reschke is
    /// five rows — fixing a spelling or adding an address one row at a time is how the data drifts.
    /// </summary>
    public class PersonActionRequest
    {
        public int CompetitionId { get; set; }
        /// <summary>"m:{memberId}" or "n:{lowercased name}".</summary>
        public string PersonKey { get; set; } = "";
        /// <summary>Link/replace target. 0 = none.</summary>
        public int MemberId { get; set; }
        public string? NewName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        /// <summary>Replace = a DIFFERENT human takes the work over; their answers don't carry across, so
        /// statuses reset. Link = the same human, now identified; answers are kept.</summary>
        public bool IsReplacement { get; set; }
    }

    public class PersonActionResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int Affected { get; set; }
    }

    /// <summary>Hide a built-in role for this competition (or unhide it).</summary>
    public class HideStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
        public bool Hidden { get; set; } = true;
    }
}
