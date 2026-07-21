using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// A named, editable planning template owned by a club or region (Phase 1.5). Holds a snapshot of the
    /// preparation work-breakdown + suggested crew counts, so a club that runs the same kind of competition
    /// can seed a fresh comp from its own plan instead of the generic built-in defaults. System defaults stay
    /// in code (PrepTemplates / FunctionaryRoles).
    /// </summary>
    [TableName("StaffingTemplate")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffingTemplate
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string OwnerType { get; set; } = "";   // Club | Region
        public string OwnerKey { get; set; } = "";
        public string Discipline { get; set; } = "*";
        public string RowsJson { get; set; } = "{}";
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    public static class StaffingTemplateOwner
    {
        public const string Club = "Club";
        public const string Region = "Region";
    }

    // --- RowsJson shape ---

    public class StaffingTemplateRows
    {
        public List<TemplatePrepArea> Prep { get; set; } = new();
        public List<TemplateStaffRow> Staffing { get; set; } = new();
    }

    public class TemplatePrepArea
    {
        public string Area { get; set; } = "";
        public List<TemplatePrepItem> Items { get; set; } = new();
    }

    public class TemplatePrepItem
    {
        public string Title { get; set; } = "";
        public int? DaysBeforeComp { get; set; }
    }

    public class TemplateStaffRow
    {
        public string RoleKey { get; set; } = "";
        public string? ScopeType { get; set; }
        public int Count { get; set; }
    }

    // --- Output / request DTOs ---

    public class StaffingTemplateView
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string OwnerType { get; set; } = "";
        public string Discipline { get; set; } = "*";
        public int AreaCount { get; set; }
        public int ItemCount { get; set; }
        public int StaffRowCount { get; set; }
        public bool CanManage { get; set; }
    }

    public class SaveAsTemplateRequest
    {
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public string? OwnerType { get; set; }   // Club | Region; defaults to the comp's host
    }

    public class DeleteTemplateRequest
    {
        public int CompetitionId { get; set; }
        public int TemplateId { get; set; }
    }
}
