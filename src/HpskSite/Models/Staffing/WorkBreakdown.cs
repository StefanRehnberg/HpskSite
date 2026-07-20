using NPoco;

namespace HpskSite.Models.Staffing
{
    public static class WorkItemStatus
    {
        public const string Planerad = "Planerad";
        public const string Pagar = "Pagar";
        public const string Blockerad = "Blockerad";
        public const string Klar = "Klar";
    }

    /// <summary>A workstream / område in the preparation work-breakdown (spec §4, mirrors BoardYearWheel).</summary>
    [TableName("WorkArea")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class WorkArea
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public int? ResponsibleMemberId { get; set; }
        public string? ResponsibleName { get; set; }
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>An uppgift (task) inside an område. Mirrors BoardMeetingAction (assignee + due + status).</summary>
    [TableName("WorkItem")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class WorkItem
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int WorkAreaId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int? AssignedMemberId { get; set; }
        public string? AssignedName { get; set; }
        public DateTime? DueDate { get; set; }
        public string Status { get; set; } = WorkItemStatus.Planerad;
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    // --- Output DTOs ---

    public class WorkItemView
    {
        public int Id { get; set; }
        public int WorkAreaId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int? AssignedMemberId { get; set; }
        public string? AssignedName { get; set; }
        public string? DueDate { get; set; }          // "yyyy-MM-dd" for the client
        public string Status { get; set; } = WorkItemStatus.Planerad;
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public bool IsOverdue { get; set; }           // past DueDate and not Klar
        public int SortOrder { get; set; }
    }

    public class WorkAreaView
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int? ResponsibleMemberId { get; set; }
        public string? ResponsibleName { get; set; }
        public int SortOrder { get; set; }
        public int DoneCount { get; set; }
        public int TotalCount { get; set; }
        public int OverdueCount { get; set; }
        public List<WorkItemView> Items { get; set; } = new();
    }

    public class WorkBreakdownResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public List<WorkAreaView> Areas { get; set; } = new();
    }

    // --- Request DTOs ---

    public class SaveWorkAreaRequest
    {
        public int Id { get; set; }                   // 0 = create
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public int? ResponsibleMemberId { get; set; }
        public string? ResponsibleName { get; set; }
    }

    public class SaveWorkItemRequest
    {
        public int Id { get; set; }                   // 0 = create
        public int CompetitionId { get; set; }
        public int WorkAreaId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int? AssignedMemberId { get; set; }
        public string? AssignedName { get; set; }
        public string? DueDate { get; set; }          // Flatpickr "Y-m-d" or null
        public string? Status { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
    }

    public class DeleteWorkRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    public class SeedPrepTemplateRequest
    {
        public int CompetitionId { get; set; }
        public string? Size { get; set; }             // klubb | krets | sm
    }
}
