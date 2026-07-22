using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>A named time-block for a competition day ("Lör FM 06–13"). Roster assignments slot into a
    /// pass (StaffAssignment.PassId) instead of retyped from/to times, and coverage is computed per pass.</summary>
    [TableName("StaffPass")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffPass
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public DateTime PassDate { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Label { get; set; } = "";
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>How many of a role a scope-kind needs (per pass). ScopeKind 'Station' applies to every
    /// station; 'All' is comp-wide. Drives the coverage matrix's "needed" numbers.</summary>
    [TableName("StaffCrewNeed")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffCrewNeed
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string ScopeKind { get; set; } = "";   // Station | All
        public string RoleKey { get; set; } = "";
        public int Count { get; set; }
    }

    public static class CrewNeedScope
    {
        public const string Station = "Station";
        public const string All = "All";
    }

    // --- Output DTOs ---

    public class StaffPassView
    {
        public int Id { get; set; }
        public string Date { get; set; } = "";      // "yyyy-MM-dd"
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Label { get; set; } = "";
        public string DisplayLabel { get; set; } = "";   // "Lör FM · 06:00–13:00"
    }

    public class CrewNeedRow
    {
        public string ScopeKind { get; set; } = "";
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public int Count { get; set; }
    }

    public class CoverageRole
    {
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public int Needed { get; set; }
        public int Filled { get; set; }
    }

    public class CoverageUnit
    {
        public string ScopeKey { get; set; } = "";       // station number
        public List<CoverageRole> Roles { get; set; } = new();
        public int Needed { get; set; }
        public int Filled { get; set; }
    }

    public class CoveragePass
    {
        public int PassId { get; set; }
        public string Label { get; set; } = "";
        public string Date { get; set; } = "";
        public string? TimeLabel { get; set; }
        public List<CoverageUnit> Stations { get; set; } = new();
        public List<CoverageRole> General { get; set; } = new();
        public int Needed { get; set; }
        public int Filled { get; set; }
    }

    public class CoverageResponse
    {
        public bool Success { get; set; } = true;
        public string Discipline { get; set; } = "";
        public int StationCount { get; set; }
        public bool HasNeeds { get; set; }
        public List<CoveragePass> Passes { get; set; } = new();
        public int TotalNeeded { get; set; }
        public int TotalFilled { get; set; }
    }

    // --- Request DTOs ---

    public class SavePassRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Label { get; set; } = "";
    }

    public class SaveCrewNeedsRequest
    {
        public int CompetitionId { get; set; }
        public List<CrewNeedRow> Needs { get; set; } = new();   // full replace of the comp's crew needs
    }

    public class CopyPassRequest
    {
        public int CompetitionId { get; set; }
        public int FromPassId { get; set; }
        public int ToPassId { get; set; }
    }
}
