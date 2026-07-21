using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>Where a competition draws its crew from (Phase 3). A row opens the comp for member
    /// self-sign-up from that club/region.</summary>
    [TableName("StaffingSourceScope")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffingSourceScope
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string ScopeType { get; set; } = "";   // Club | Region
        public string ScopeKey { get; set; } = "";     // clubId | regionCode
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    public static class SourceScopeType
    {
        public const string Club = "Club";
        public const string Region = "Region";
    }

    public class SourceScopeView
    {
        public int Id { get; set; }
        public string ScopeType { get; set; } = "";
        public string ScopeKey { get; set; } = "";
        public string Label { get; set; } = "";   // resolved club/region name
    }

    public class SaveSourceScopeRequest
    {
        public int CompetitionId { get; set; }
        public string ScopeType { get; set; } = "";
        public string ScopeKey { get; set; } = "";
    }

    // --- Self-sign-up (member-facing) ---

    public class OpenSignupView
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public string? CompDate { get; set; }
        public string Discipline { get; set; } = "";
        public List<RoleOption> Roles { get; set; } = new();
    }

    public class RoleOption
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class SelfSignUpRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
    }
}
