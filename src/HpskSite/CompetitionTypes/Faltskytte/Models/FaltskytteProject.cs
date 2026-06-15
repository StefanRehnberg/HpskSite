using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// A "Projekt" groups one or more standalone Fältskytte configurations for
    /// organisation, shared access, and archiving. Phase 1 is intentionally
    /// lightweight: a named container with a flat member list (no roles) whose
    /// members get view + edit on every config in the project, plus an
    /// Active/Archived status. Manager role + responsible Banläggare + rollup
    /// "approve all" are deferred to Phase 2.
    /// </summary>
    [TableName("FaltskytteProject")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskytteProject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int OwnerMemberId { get; set; }
        public int? OwnerClubId { get; set; }
        /// <summary>Active | Archived. Null treated as Active for legacy rows.</summary>
        public string Status { get; set; } = "Active";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Member-list entry on a project. No roles (Phase 1) — anyone in the list
    /// can view + edit every config in the project, regardless of each config's
    /// own Visibility / SecretUntil.
    /// </summary>
    [TableName("FaltskytteProjectMember")]
    [PrimaryKey("ProjectId,MemberId", AutoIncrement = false)]
    public class FaltskytteProjectMember
    {
        public int ProjectId { get; set; }
        public int MemberId { get; set; }
        public DateTime AddedDate { get; set; } = DateTime.Now;
    }

    // ─── API view models ────────────────────────────────────────────────

    /// <summary>API-side view of a project with derived authorization fields + rollup status.</summary>
    public class FaltskytteProjectView
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int OwnerMemberId { get; set; }
        public string OwnerMemberName { get; set; } = "";
        public int? OwnerClubId { get; set; }
        public string? OwnerClubName { get; set; }
        public string Status { get; set; } = "Active";
        public bool IsArchived { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public List<ProjectMemberView> Members { get; set; } = new();
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        // ── Rollup over the project's configs (read-only in Phase 1) ──
        public int ConfigCount { get; set; }
        public int ApprovedConfigCount { get; set; }
        public int PendingConfigCount { get; set; }
    }

    public class ProjectMemberView
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public DateTime AddedDate { get; set; }
    }

    // ─── Request DTOs ───────────────────────────────────────────────────

    public class CreateFaltskytteProjectRequest
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int? OwnerClubId { get; set; }
    }

    public class UpdateFaltskytteProjectRequest
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? OwnerClubId { get; set; }
    }

    public class ProjectMemberRequest
    {
        public int ProjectId { get; set; }
        public int MemberId { get; set; }
    }

    /// <summary>Body for setting (or clearing) a configuration's project.</summary>
    public class AssignConfigToProjectRequest
    {
        public int ConfigId { get; set; }
        /// <summary>Null clears the assignment (config becomes standalone again).</summary>
        public int? ProjectId { get; set; }
    }

    /// <summary>Body for Archive / Unarchive — no extra payload beyond the project id.</summary>
    public class ProjectStatusRequest
    {
        public int ProjectId { get; set; }
    }
}
