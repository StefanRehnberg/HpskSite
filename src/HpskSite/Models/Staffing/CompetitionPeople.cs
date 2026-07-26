namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// The people layer: ONE row per human connected to a competition, unioned across every store that
    /// holds people today — the roster (<see cref="StaffAssignment"/>), self-sign-ups
    /// (<see cref="StaffHelpSignup"/>), declared availability (<see cref="StaffAvailability"/>), prep
    /// ownership (WorkArea.ResponsibleMemberId / WorkItem.AssignedMemberId), the Umbraco
    /// <c>competitionManagers</c> array, and Fältskytte's <c>faltskytteStationManagers</c> JSON.
    ///
    /// WHY THIS EXISTS: before it, each surface read exactly one of those stores, so a person assigned in
    /// Bemanning still looked "unassigned" under Värvning, a Tävlingsledare in the roster was invisible to
    /// the Förberedelser "Tävlingsledning" område, and the same person could be assigned twice with no
    /// warning. Every people-facing surface now renders a projection of this one shape instead.
    /// </summary>
    public class CompetitionPersonRow
    {
        /// <summary>Stable identity across stores: "m:{memberId}" for a member, "n:{lowercased name}" for a
        /// free-text helper. Two rows that resolve to the same key are the same person.</summary>
        public string Key { get; set; } = "";

        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? ClubName { get; set; }

        /// <summary>Free-text helper with no pistol.nu account (cannot be push-notified, needs a tokened link).</summary>
        public bool IsExternal { get; set; }

        // --- what they do on the day (roster) ---
        public List<CompetitionPersonAssignment> Assignments { get; set; } = new();

        /// <summary>Compact "Markör · Skjutlag 2 · tavlor 1–8" summary of every roster row, for one-line display.</summary>
        public List<string> RoleLabels { get; set; } = new();

        /// <summary>True when at least one roster row carries the Tävlingsledning app-access mirror.</summary>
        public bool HasAdminAccess { get; set; }

        /// <summary>True when they lead a role/område (IsResponsible on any row, or an områdesansvarig).</summary>
        public bool IsResponsible { get; set; }

        // --- what they said they could do (self-sign-up on /bemanna) ---
        public CompetitionPersonVolunteer? Volunteer { get; set; }

        /// <summary>Declared availability windows ("Lör 13:00–17:00"), organiser-visible.</summary>
        public List<string> AvailabilityLabels { get; set; } = new();

        // --- what they own before the day (Förberedelser) ---
        public List<CompetitionPersonPrepRef> Prep { get; set; } = new();
        public int PrepOpenCount { get; set; }
        public int PrepOverdueCount { get; set; }

        // --- rollups the UI sorts + filters on ---

        /// <summary>
        /// The one word that answers "where is this person in the process":
        /// Anmäld (volunteered, nothing assigned yet) · Planerad · Inbjuden · Accepterad · Avböjt ·
        /// Bekräftad · Förberedelser (prep owner only, no day-of role).
        /// </summary>
        public string State { get; set; } = "";

        /// <summary>Sort weight so the organiser's queue floats to the top: lower = needs attention sooner.</summary>
        public int StatePriority { get; set; }

        /// <summary>Where this person came from, for the "källa" filter: Roster | Anmald | Forberedelser | Stationer.</summary>
        public List<string> Sources { get; set; } = new();

        /// <summary>Only mirror rows (e.g. a Fält station chief owned by the Stationer tab) — not editable here.</summary>
        public bool ReadOnly { get; set; }
        public string? SourceLabel { get; set; }
    }

    /// <summary>One roster row, flattened for the people view (the editable identity is <see cref="Id"/>).</summary>
    public class CompetitionPersonAssignment
    {
        public int Id { get; set; }
        /// <summary>Who holds it. Redundant inside a person's own row, but required wherever assignments are
        /// listed away from the people table — e.g. the Tävlingsledning read-through on Förberedelser, which
        /// has to answer "who leads this competition", not just "which functions exist".</summary>
        public string PersonName { get; set; } = "";
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string? FunctionTitle { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public string ScopeLabel { get; set; } = "";
        public string? ShiftLabel { get; set; }
        public int? PassId { get; set; }
        public string? PassLabel { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public bool IsResponsible { get; set; }
        public bool HasAdminAccess { get; set; }
        public bool CheckedIn { get; set; }
        public bool ReadOnly { get; set; }
        /// <summary>"Markör · Skjutlag 2 · tavlor 1–8 · Lör FM" — the label the UI shows as a chip.</summary>
        public string Label { get; set; } = "";
    }

    /// <summary>What a member ticked on the /bemanna sign-up page.</summary>
    public class CompetitionPersonVolunteer
    {
        public string? Comment { get; set; }
        public string Updated { get; set; } = "";
        public List<CompetitionPersonVolunteerSlot> Slots { get; set; } = new();
        /// <summary>"Lör 06–13, Lör 13–20" — compact one-line form of the checked passes.</summary>
        public string SlotsSummary { get; set; } = "";
    }

    public class CompetitionPersonVolunteerSlot
    {
        public int SlotId { get; set; }
        public string Label { get; set; } = "";
        public string TimesText { get; set; } = "";
        /// <summary>The StaffPass this help-slot lines up with (same date + overlapping time), when there is
        /// one — lets "Tilldela" prefill the pass instead of making the organiser re-derive it.</summary>
        public int? SuggestedPassId { get; set; }
    }

    /// <summary>A prep responsibility (an område they lead, or an uppgift assigned to them).</summary>
    public class CompetitionPersonPrepRef
    {
        public int? AreaId { get; set; }
        public int? ItemId { get; set; }
        public string AreaName { get; set; } = "";
        public string? ItemTitle { get; set; }
        public string? DueDate { get; set; }
        public string Status { get; set; } = "";
        public bool IsAreaLead { get; set; }
        public bool IsOverdue { get; set; }
    }

    public class CompetitionPeopleResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Discipline { get; set; } = "";
        public List<CompetitionPersonRow> People { get; set; } = new();

        // headline counts for the tab badge + the empty/queue states
        public int TotalPeople { get; set; }
        public int AssignedCount { get; set; }
        /// <summary>Volunteered but holds no roster row — the organiser's actual to-do queue.</summary>
        public int UnassignedVolunteerCount { get; set; }
        public int NeedsResponseCount { get; set; }
        public int DeclinedCount { get; set; }
        public int ExternalCount { get; set; }

        /// <summary>Tävlingsledning read-through: who currently leads, sourced from the roster only, so
        /// Förberedelser can show the same fact without owning a second copy of it.</summary>
        public List<CompetitionPersonAssignment> Leadership { get; set; } = new();
    }

    public static class PersonState
    {
        public const string Anmald = "Anmäld";               // volunteered, no role yet → assign them
        public const string Declined = "Avböjt";
        public const string Invited = "Inbjuden";
        public const string Planned = "Planerad";
        public const string Accepted = "Accepterad";
        public const string Confirmed = "Bekräftad";
        public const string PrepOnly = "Förberedelser";      // owns prep work, no day-of role

        /// <summary>Lower sorts first — the organiser sees what needs doing before what is done.</summary>
        public static int Priority(string state) => state switch
        {
            Anmald => 0,
            Declined => 1,
            Invited => 2,
            Planned => 3,
            PrepOnly => 4,
            Accepted => 5,
            Confirmed => 6,
            _ => 7,
        };
    }

    public static class PersonSource
    {
        public const string Roster = "Roster";
        public const string Volunteer = "Anmald";
        public const string Prep = "Forberedelser";
        public const string Stationer = "Stationer";
    }
}
