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
        public decimal? EstimatedCost { get; set; }   // budgeterad kostnad
        public decimal? ActualCost { get; set; }       // faktisk kostnad
        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    /// <summary>
    /// A document or link attached to a scope within a competition's prep — the whole competition
    /// (WorkAreaId + WorkItemId both null), an område, or a single uppgift. Either a URL or an uploaded
    /// file (stored via PrepDocumentStorage). Prep is document-heavy, so this closes the biggest gap.
    /// </summary>
    [TableName("WorkLink")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class WorkLink
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int? WorkAreaId { get; set; }
        public int? WorkItemId { get; set; }
        public string Title { get; set; } = "";
        public string? Url { get; set; }
        public string? StoredFileName { get; set; }
        public string? OriginalFileName { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    public static class WorkCommentKind
    {
        public const string Comment = "comment";  // a person wrote it
        public const string Audit = "audit";       // system event (status change, done, reminder sent)
    }

    /// <summary>A comment or audit event on a WorkItem (P2 — coordination). Doubles as the prep comms
    /// channel and the who-marked-done trail.</summary>
    [TableName("WorkItemComment")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class WorkItemComment
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int WorkItemId { get; set; }
        public string Kind { get; set; } = WorkCommentKind.Comment;
        public string Body { get; set; } = "";
        public int AuthorMemberId { get; set; }
        public string? AuthorName { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>"WorkItemId is blocked by BlockedByItemId" — a dependency link ("blockeras av").</summary>
    [TableName("WorkItemDependency")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class WorkItemDependency
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int WorkItemId { get; set; }
        public int BlockedByItemId { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    // --- Output DTOs ---

    public class WorkLinkView
    {
        public int Id { get; set; }
        public int? WorkAreaId { get; set; }
        public int? WorkItemId { get; set; }
        public string Title { get; set; } = "";
        public string? Url { get; set; }          // external link, OR the download URL for a stored file
        public bool IsFile { get; set; }          // true = uploaded document, false = plain URL
    }

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
        public decimal? EstimatedCost { get; set; }
        public decimal? ActualCost { get; set; }
        public int SortOrder { get; set; }
        public List<WorkLinkView> Links { get; set; } = new();
        public int CommentCount { get; set; }              // person-written comments (audit excluded)
        public List<WorkItemBlockerView> BlockedBy { get; set; } = new();  // dependency links
        public bool IsBlocked { get; set; }                // any blocker not yet Klar
    }

    /// <summary>A blocker reference shown on an item ("blockeras av …").</summary>
    public class WorkItemBlockerView
    {
        public int DependencyId { get; set; }  // WorkItemDependency.Id (for removal)
        public int ItemId { get; set; }         // the blocking WorkItem
        public string Title { get; set; } = "";
        public string Status { get; set; } = WorkItemStatus.Planerad;
        public bool Done { get; set; }
    }

    public class WorkItemCommentView
    {
        public int Id { get; set; }
        public string Kind { get; set; } = WorkCommentKind.Comment;
        public string Body { get; set; } = "";
        public int AuthorMemberId { get; set; }
        public string? AuthorName { get; set; }
        public string CreatedDate { get; set; } = "";  // "yyyy-MM-dd HH:mm"
    }

    /// <summary>Full per-uppgift thread (comments + audit + dependency mgmt), fetched on demand.</summary>
    public class WorkItemThreadResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Title { get; set; } = "";
        public List<WorkItemCommentView> Comments { get; set; } = new();
        public List<WorkItemBlockerView> BlockedBy { get; set; } = new();
        public List<CandidateItem> Candidates { get; set; } = new();  // other items that can be added as blockers
    }

    public class CandidateItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Area { get; set; } = "";
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
        public decimal EstimatedCostSum { get; set; }
        public decimal ActualCostSum { get; set; }
        public List<WorkItemView> Items { get; set; } = new();
        public List<WorkLinkView> Links { get; set; } = new();
    }

    /// <summary>Whether a Fältskytte comp can auto-seed one "Bygg station N" task per configured station.</summary>
    public class StationSeedInfo
    {
        public bool Available { get; set; }
        public int StationCount { get; set; }
        public int AttachedConfigId { get; set; }   // 0 = none; links station tasks to the Fältkonfigurator
    }

    public class WorkBreakdownResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Discipline { get; set; } = "";
        public string? CompDate { get; set; }       // "yyyy-MM-dd" or null
        public int? DaysUntilComp { get; set; }      // negative once the comp has passed
        public StationSeedInfo? StationSeed { get; set; }
        public decimal TotalEstimatedCost { get; set; }
        public decimal TotalActualCost { get; set; }
        public List<WorkLinkView> CompLinks { get; set; } = new();   // competition-level documents
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
        public decimal? EstimatedCost { get; set; }
        public decimal? ActualCost { get; set; }
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

    public class SaveWorkLinkRequest
    {
        public int CompetitionId { get; set; }
        public int? WorkAreaId { get; set; }
        public int? WorkItemId { get; set; }
        public string Title { get; set; } = "";
        public string? Url { get; set; }
    }

    public class SeedStationTasksRequest
    {
        public int CompetitionId { get; set; }
    }

    public class AddWorkItemCommentRequest
    {
        public int CompetitionId { get; set; }
        public int WorkItemId { get; set; }
        public string Body { get; set; } = "";
    }

    public class WorkItemDependencyRequest
    {
        public int CompetitionId { get; set; }
        public int WorkItemId { get; set; }
        public int BlockedByItemId { get; set; }  // add: the blocker; remove: ignored (use Id)
        public int Id { get; set; }                // remove: WorkItemDependency.Id
    }
}
