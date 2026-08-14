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
        /// <summary>Which DAY this row belongs to when it claims no clock time ("heldag på lördag").
        /// Resolution order everywhere: StartsAt.Date → DayDate → linked StaffPass.PassDate.</summary>
        public DateTime? DayDate { get; set; }
        public int? PassId { get; set; }                 // structured shift (StaffPass); NULL = ad-hoc

        public bool IsResponsible { get; set; }
        // May manage the competition in pistol.nu. Allowed on ANY role (needs a MemberId); only
        // Tävlingsledning rows additionally mirror into the competition's competitionManagers list.
        public bool HasAdminAccess { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public string? Note { get; set; }
        public DateTime? CheckedInAt { get; set; }      // roll-call/upprop: set when the person shows up on the day

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

    // --- Day-of cockpit (roll-call / upprop + planned-vs-actual overlay) ---

    /// <summary>One planned functionary in a scope unit, for the day-of cockpit.</summary>
    public class DayOfPersonView
    {
        public int Id { get; set; }
        public int? MemberId { get; set; }
        public string Name { get; set; } = "";
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string? FunctionTitle { get; set; }
        public string? ShiftLabel { get; set; }
        public bool IsResponsible { get; set; }
        public bool CheckedIn { get; set; }
        public bool ReadOnly { get; set; }        // mirror row (Fält station chief) — no check-in toggle
        public string? Phone { get; set; }
        public bool ActiveNow { get; set; }        // #1b: matched to a live-load "active" signal in this scope
    }

    /// <summary>A scope unit (Skjutlag/Station/Klass/…/Hela tävlingen) with its planned crew + roll-call tally.</summary>
    public class DayOfScopeGroup
    {
        public string ScopeType { get; set; } = "";
        public string? ScopeKey { get; set; }
        public string ScopeLabel { get; set; } = "";
        public int SortKey { get; set; }
        public List<DayOfPersonView> Planned { get; set; } = new();
        public int PlannedCount { get; set; }
        public int CheckedInCount { get; set; }
        public int ActiveNotPlanned { get; set; }   // #1b: active people in this scope with no plan row
    }

    public class DayOfCockpitResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Discipline { get; set; } = "";
        public List<DayOfScopeGroup> Groups { get; set; } = new();
        public int TotalPlanned { get; set; }
        public int TotalCheckedIn { get; set; }
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
        public int? PassId { get; set; }
        public string? PassLabel { get; set; }         // "Lör FM 06–13" resolved from the pass
        /// <summary>"yyyy-MM-dd" — which competition DAY this row belongs to, from StartsAt or the linked
        /// pass. NULL when neither pins a day down; the grid buckets those under "Utan datum" rather than
        /// guessing, in keeping with the never-invent-a-time rule.</summary>
        public string? DateKey { get; set; }
        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public string? Note { get; set; }
        public bool CheckedIn { get; set; }
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
        /// <summary>"yyyy-MM-dd" — the day an untimed (heldag) row belongs to.</summary>
        public string? DayDate { get; set; }
        public int? PassId { get; set; }                 // structured shift
        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        /// <summary>Set by the dialog's "lägg till ändå" confirmation to bypass the same-person-same-role-
        /// same-scope-same-pass duplicate guard in StaffingController.SaveAssignment.</summary>
        public bool AllowDuplicate { get; set; }
    }

    public class DeleteStaffAssignmentRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    public class CheckInRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public bool CheckedIn { get; set; }
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
