namespace HpskSite.Models.Schedule
{
    /// <summary>
    /// What kind of thing a schedule row is. Drives the icon + colour, and the reminder copy.
    /// </summary>
    public static class ScheduleItemKind
    {
        /// <summary>The member is shooting — from a published start list / patrol.</summary>
        public const string Skytte = "Skytte";
        /// <summary>The member is working — from a StaffAssignment.</summary>
        public const string Funktionar = "Funktionar";
        /// <summary>Everyone's programme — upprop, lunch, prisutdelning (CompetitionAgendaItem).</summary>
        public const string Praktiskt = "Praktiskt";
    }

    /// <summary>
    /// One row on a member's personal competition itinerary.
    ///
    /// Times are deliberately two-track. <see cref="StartsAt"/> is an ABSOLUTE moment and is only set
    /// when the source data actually pins one down; <see cref="TimeLabel"/> is always human-readable
    /// ("11:00", "13:00–16:00", "Heldag", "Lör FM · 06:00–13:00"). Anything that needs real arithmetic
    /// — conflict detection, reminders, the .ics export — must use StartsAt and skip null ones rather
    /// than invent a time. A precision skjutlag on an undated multi-day list genuinely has no absolute
    /// moment, and guessing one is worse than admitting it.
    /// </summary>
    public class ScheduleItem
    {
        public string Kind { get; set; } = ScheduleItemKind.Skytte;

        /// <summary>"Klass A", "Markör", "Prisutdelning".</summary>
        public string Title { get; set; } = "";
        /// <summary>"Skjutlag 3 · plats 32", "Station 3 · tavlor 12–18", "Patrull 4".</summary>
        public string? Where { get; set; }
        /// <summary>Secondary line — weapon class, start number, note.</summary>
        public string? Detail { get; set; }

        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        /// <summary>Always populated. The only time string the UI should print.</summary>
        public string TimeLabel { get; set; } = "";

        /// <summary>Sortable grouping key: "2026-08-08" for dated days, "z:Lördag fm" for label-only.</summary>
        public string DayKey { get; set; } = "";
        /// <summary>"Lördag 8 augusti" or the verbatim skjutlag label.</summary>
        public string DayLabel { get; set; } = "";

        /// <summary>Funktionär only: Planned | Invited | Accepted | Declined.</summary>
        public string? Status { get; set; }
        public bool NeedsResponse { get; set; }
        public bool IsResponsible { get; set; }

        public string? Link { get; set; }
        public string? LinkText { get; set; }
        public string? Icon { get; set; }

        /// <summary>Human descriptions of the items this one overlaps ("Markör station 3, 13:00–16:00").</summary>
        public List<string> ConflictsWith { get; set; } = new();
        public bool HasConflict => ConflictsWith.Count > 0;

        /// <summary>Tie-break within a day when StartsAt is null. Minutes-since-midnight when known.</summary>
        public int SortHint { get; set; }

        /// <summary>Stable identity for the .ics UID and the reminder de-dup key.</summary>
        public string SourceKey { get; set; } = "";

        // --- Presentation helpers ---
        // These live here, not in the Razor partial, because local helper functions and mid-markup
        // @{ } blocks in a view are a reliable way to break runtime Razor compilation (messageless
        // UmbracoCompilationException). Plain properties keep the partial to markup only.

        /// <summary>Bootstrap icon class for the row.</summary>
        public string IconClass => !string.IsNullOrWhiteSpace(Icon)
            ? Icon!
            : Kind switch
            {
                ScheduleItemKind.Funktionar => "bi-person-badge",
                ScheduleItemKind.Praktiskt => "bi-info-circle",
                _ => "bi-bullseye",
            };

        public string AccentClass => Kind switch
        {
            ScheduleItemKind.Funktionar => "text-success",
            ScheduleItemKind.Praktiskt => "text-secondary",
            _ => "text-primary",
        };

        public string KindLabel => Kind switch
        {
            ScheduleItemKind.Funktionar => "Funktionär",
            ScheduleItemKind.Praktiskt => "Program",
            _ => "Skytte",
        };

        /// <summary>Already finished — the view renders these muted.</summary>
        public bool IsPast => EndsAt != null ? EndsAt < DateTime.Now : StartsAt != null && StartsAt < DateTime.Now;

        /// <summary>"Krockar med: …" text, empty when there's no clash.</summary>
        public string ConflictText => ConflictsWith.Count == 0 ? "" : string.Join("; ", ConflictsWith);

        /// <summary>Minutes until this starts; null when there's no absolute time or it's passed.</summary>
        public int? MinutesUntil
        {
            get
            {
                if (StartsAt == null) return null;
                var m = (int)Math.Round((StartsAt.Value - DateTime.Now).TotalMinutes);
                return m < 0 ? null : m;
            }
        }
    }

    /// <summary>One day of the itinerary. <see cref="Date"/> is null for label-only groups.</summary>
    public class ScheduleDay
    {
        public string DayKey { get; set; } = "";
        public string DayLabel { get; set; } = "";
        public DateTime? Date { get; set; }
        public bool IsToday { get; set; }
        public List<ScheduleItem> Items { get; set; } = new();
    }

    /// <summary>A member's itinerary for ONE competition.</summary>
    public class MySchedule
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public DateTime? CompDate { get; set; }
        public DateTime? CompEndDate { get; set; }
        public string Discipline { get; set; } = "";
        public string CompetitionUrl { get; set; } = "";

        public List<ScheduleDay> Days { get; set; } = new();

        /// <summary>True when the member has any row at all (shooting or working).</summary>
        public bool HasAny => Days.Any(d => d.Items.Count > 0);
        public int ItemCount => Days.Sum(d => d.Items.Count);
        public int ConflictCount => Days.Sum(d => d.Items.Count(i => i.HasConflict));

        /// <summary>The member is registered but no start list is published yet.</summary>
        public bool StartListPending { get; set; }
        /// <summary>The member is registered as a shooter (regardless of start-list state).</summary>
        public bool IsRegistered { get; set; }
        /// <summary>The member holds at least one functionary assignment.</summary>
        public bool IsFunctionary { get; set; }

        /// <summary>Non-fatal things the member should know ("startlistan är inte publicerad än").</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Next item with an absolute start in the future — the hero line. Null when nothing is pinned.</summary>
        public ScheduleItem? NextItem { get; set; }

        public bool IsToday => CompDate?.Date == DateTime.Today
            || (CompDate != null && CompEndDate != null
                && CompDate.Value.Date <= DateTime.Today && DateTime.Today <= CompEndDate.Value.Date);

        /// <summary>Whole-comp span is more than one calendar day.</summary>
        public bool IsMultiDay => CompEndDate != null && CompDate != null
            && CompEndDate.Value.Date > CompDate.Value.Date;
    }

    /// <summary>Home-page / cross-competition summary row.</summary>
    public class ScheduleHubItem
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public DateTime? CompDate { get; set; }
        public string DateLabel { get; set; } = "";      // "Idag", "Imorgon", "lör 8 aug"
        public bool IsToday { get; set; }
        public bool IsTomorrow { get; set; }
        public int ItemCount { get; set; }
        public int ConflictCount { get; set; }
        public bool StartListPending { get; set; }
        /// <summary>Up to a handful of rows for the home card preview.</summary>
        public List<ScheduleItem> Preview { get; set; } = new();
        public ScheduleItem? NextItem { get; set; }

        /// <summary>
        /// True when a preview row sits on a different day than the competition itself — a funktionär's
        /// build day weeks before the shooting, typically. The card header shows the COMPETITION date, so
        /// without this the rows must print their own day or "Nästa 09:00" reads as 09:00 on competition
        /// day. Computed in the service, not the view: the equivalent Razor comparison inside HomePage's
        /// markup is the kind of code block that breaks its runtime compilation.
        /// </summary>
        public bool PreviewSpansOtherDays { get; set; }
    }

    public class ScheduleHubSummary
    {
        public bool HasAny { get; set; }
        public List<ScheduleHubItem> Items { get; set; } = new();
        /// <summary>Competition ids the schedule card is showing — the funktionär card suppresses these.</summary>
        public HashSet<int> ShownCompetitionIds { get; set; } = new();
        public bool AnyToday => Items.Any(i => i.IsToday);
        public int TotalConflicts => Items.Sum(i => i.ConflictCount);
    }
}
