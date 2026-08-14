using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// A role the arrangör named themselves. See <c>Migrations/create-staff-role-table.sql</c> for the
    /// reasoning: the built-in <see cref="FunctionaryRoles"/> catalog is a closed set, and clubs use their
    /// own vocabulary for the same job (and sometimes the same word for a different job). Forcing them onto
    /// our word doesn't just annoy — it makes the data wrong.
    ///
    /// <para>A row whose <see cref="RoleKey"/> matches a built-in key <b>overrides</b> that built-in's
    /// display name for its owner scope. A new key is a new role. Merge order + resolution lives in
    /// <c>Services/Staffing/RoleCatalogService.cs</c>, which is the ONE place roles are read from — never
    /// call <see cref="FunctionaryRoles"/> directly from a surface again.</para>
    /// </summary>
    [TableName("StaffRole")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffRole
    {
        public int Id { get; set; }

        /// <summary>System | Region | Club | Competition — see <see cref="RoleOwnerType"/>.</summary>
        public string OwnerType { get; set; } = RoleOwnerType.Competition;
        public int OwnerId { get; set; }

        public string RoleKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? PluralName { get; set; }
        public string? DefaultScopeType { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? Description { get; set; }
        public string? NeedsJson { get; set; }
        public string? Disciplines { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;

        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    public static class RoleOwnerType
    {
        public const string System = "System";
        public const string Region = "Region";
        public const string Club = "Club";
        public const string Competition = "Competition";
    }

    // --- Request DTOs ---

    /// <summary>
    /// Create or rename a role. <see cref="RoleKey"/> empty = create (the key is generated from the name);
    /// set = update/override that key for this competition, which is how a built-in gets renamed.
    /// </summary>
    public class SaveStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string? RoleKey { get; set; }
        public string DisplayName { get; set; } = "";
        public string? PluralName { get; set; }
        public string? DefaultScopeType { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? Description { get; set; }
        public int SortOrder { get; set; }
    }

    public class DeleteStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
    }

    /// <summary>Clone a whole competition day's crew onto another day ("fyll höger" in the grid).</summary>
    public class CopyDayRequest
    {
        public int CompetitionId { get; set; }
        public string? FromDate { get; set; }   // "yyyy-MM-dd"
        public string? ToDate { get; set; }
    }

    // --- Grid DTOs (Bemanning → rutnätsvyn) ---

    /// <summary>One person in one grid cell. A thin projection of <see cref="StaffAssignmentView"/> —
    /// the grid shows name + club + time and nothing else; detail lives in the row editor.</summary>
    public class GridEntry
    {
        public int Id { get; set; }
        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? ClubName { get; set; }
        public string? TimeLabel { get; set; }      // "08–09"; null = heldag (shown as bare name)
        public string? ScopeLabel { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public bool IsResponsible { get; set; }
        public bool IsExternal { get; set; }        // no member account → no schedule, no push
        public bool ReadOnly { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>A column: one competition day. Passes on that day are the drill-in level (not built yet).</summary>
    public class GridColumn
    {
        public string Key { get; set; } = "";        // "yyyy-MM-dd", or "" for the undated bucket
        public string Label { get; set; } = "";      // "Fre 21 aug"
        public string? TimeLabel { get; set; }       // "09:00–19:00" from the day's passes
        public List<int> PassIds { get; set; } = new();
    }

    /// <summary>A row: one role (optionally narrowed to one scope, e.g. "Skjutledare · Bana 3").</summary>
    public class GridRow
    {
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public string? ScopeLabel { get; set; }
        public bool IsCustom { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? DefaultScopeType { get; set; }
        /// <summary>Column key → the people in that cell.</summary>
        public Dictionary<string, List<GridEntry>> Cells { get; set; } = new();
        public int Filled { get; set; }
    }

    public class GridResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Discipline { get; set; } = "";
        public List<GridColumn> Columns { get; set; } = new();
        public List<GridRow> Rows { get; set; } = new();
        public int TotalAssigned { get; set; }
        /// <summary>Rows with no member account — they get no Mitt schema and no push.</summary>
        public int ExternalCount { get; set; }
    }
}
