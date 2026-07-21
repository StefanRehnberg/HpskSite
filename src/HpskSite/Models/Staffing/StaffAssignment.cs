using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// One person in one role, on one scope, for one event (Phase 1 = a competition). Addressed by the
    /// same generic (ScopeType, ScopeKey) pair as EventMessage, so plan → coordinate → observe share one
    /// vocabulary. Shifts (StartsAt/EndsAt) let the same role+scope hold several people at different
    /// times (handover). See COMPETITION_STAFFING_SYSTEM.md §4.
    /// </summary>
    [TableName("StaffAssignment")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffAssignment
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }

        public int? MemberId { get; set; }              // NULL = free-text external helper
        public string DisplayName { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }

        public string RoleKey { get; set; } = "";
        public string? FunctionTitle { get; set; }

        public string? ScopeType { get; set; }          // Skjutlag | Station | Klass | Patrull | Bana | All
        public string? ScopeKey { get; set; }

        public int? TargetFrom { get; set; }            // Markör target range (tavlor); NULL = whole scope
        public int? TargetTo { get; set; }

        public DateTime? StartsAt { get; set; }         // shift start; NULL = heldag
        public DateTime? EndsAt { get; set; }

        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }        // Tävlingsledning: mirror into competitionManagers
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public string? Note { get; set; }

        public int AssignedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    /// <summary>A member's declared availability window for a competition (P3 sign-up + tillgänglighet).
    /// Several rows per person = several windows. NULL/NULL = whole event.</summary>
    [TableName("StaffAvailability")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffAvailability
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public DateTime? AvailableFrom { get; set; }
        public DateTime? AvailableTo { get; set; }
        public string? Note { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    // --- Output DTOs (not table-mapped) ---

    public class StaffAvailabilityView
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";   // "Lör 13:00–17:00" or "Heldag"
        public string? Note { get; set; }
    }

    /// <summary>One of the current member's own assignments, for the /mina-uppdrag page.</summary>
    public class MyAssignmentView
    {
        public int Id { get; set; }
        public string RoleName { get; set; } = "";
        public string? FunctionTitle { get; set; }
        public string ScopeLabel { get; set; } = "";
        public string? ShiftLabel { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public bool IsResponsible { get; set; }
    }

    /// <summary>The current member's assignments + availability for one competition.</summary>
    public class MyCompetitionGroup
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public string? CompDate { get; set; }
        public List<MyAssignmentView> Assignments { get; set; } = new();
        public List<StaffAvailabilityView> Availability { get; set; } = new();
    }

    /// <summary>A single assignment with its resolved role metadata for the roster UI.</summary>
    public class StaffAssignmentView
    {
        public int Id { get; set; }
        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string? FunctionTitle { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public string ScopeLabel { get; set; } = "";
        public int? TargetFrom { get; set; }
        public int? TargetTo { get; set; }
        public string? ShiftLabel { get; set; }       // "13:00–15:00" or null (heldag)
        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public string? Note { get; set; }
        /// <summary>Read-only mirror row (not a StaffAssignment) — e.g. a Fältskytte station chief pulled in
        /// from faltskytteStationManagers on the Stationer tab. No edit/delete/notify in the UI.</summary>
        public bool ReadOnly { get; set; }
        public string? SourceLabel { get; set; }   // e.g. "Stationer-fliken"
        public List<string> AvailabilityLabels { get; set; } = new();   // the member's declared windows (organiser view)
    }

    /// <summary>A role group (one section in the roster) with its assignments.</summary>
    public class StaffRoleGroup
    {
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string RolePlural { get; set; } = "";
        public string DefaultScopeType { get; set; } = "";
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string Description { get; set; } = "";
        public string[] Needs { get; set; } = Array.Empty<string>();
        public List<StaffAssignmentView> Assignments { get; set; } = new();
    }

    public class StaffRosterResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public string Discipline { get; set; } = "";
        public bool CanEdit { get; set; }
        public List<StaffRoleGroup> Groups { get; set; } = new();
        public int TotalAssigned { get; set; }
    }

    // --- Request DTOs ---

    public class SaveStaffAssignmentRequest
    {
        public int Id { get; set; }                    // 0 = create
        public int CompetitionId { get; set; }
        public int? MemberId { get; set; }
        public string? DisplayName { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string RoleKey { get; set; } = "";
        public string? FunctionTitle { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public int? TargetFrom { get; set; }
        public int? TargetTo { get; set; }
        public string? StartsAt { get; set; }          // Flatpickr "Y-m-d H:i" or null
        public string? EndsAt { get; set; }
        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
    }

    public class DeleteStaffAssignmentRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    // --- Member-facing (/mina-uppdrag) request DTOs ---

    public class RespondAssignmentRequest
    {
        public int AssignmentId { get; set; }
        public string Status { get; set; } = "";   // Accepted | Declined (or back to Planned)
    }

    public class SaveAvailabilityRequest
    {
        public int CompetitionId { get; set; }
        public string? From { get; set; }           // Flatpickr "Y-m-d H:i" or null
        public string? To { get; set; }
        public string? Note { get; set; }
    }
}
