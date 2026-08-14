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

    /// <summary>Hide a built-in role for this competition (or unhide it).</summary>
    public class HideStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
        public bool Hidden { get; set; } = true;
    }
}
