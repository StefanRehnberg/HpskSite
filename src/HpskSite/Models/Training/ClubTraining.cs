using NPoco;

namespace HpskSite.Models.Training
{
    /// <summary>
    /// One training occasion (fas B1, notes/traningsmodell-plan-2026-10-03.md). Training is broken out
    /// of <c>clubSimpleEvent</c> into its own SQL model WITHOUT a landing page — it is opened in a
    /// panel. Events become meetings, cleaning days and social occasions.
    ///
    /// <para>⚠️ CLUB ONLY. Kretsar never have training (Stefan 2026-10-03); there is no RegionId on
    /// purpose. ⚠️ Not wired to any surface yet — the panel (B4) and the generalised participation
    /// (B2) come after the wireframe is approved.</para>
    ///
    /// <para>The registration fields mirror the event's so that nothing is lost when existing
    /// Träning-events are migrated (B3) — which of them training ultimately keeps is decided after
    /// the dry run (Migrations/dryrun-training-events-2026-10-04.sql). Columns are cheap; a field
    /// that a migrated event used but the training cannot hold is data loss.</para>
    /// </summary>
    [TableName("ClubTraining")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubTraining
    {
        public int Id { get; set; }
        public int ClubId { get; set; }

        /// <summary>The schedule that created the occasion; null for a one-off.</summary>
        public int? ScheduleId { get; set; }

        public DateTime Date { get; set; }

        /// <summary>"HH:mm". Null = no time given (an all-day occasion is never given a fake 00:00).</summary>
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        /// <summary>Canonical discipline id (<see cref="ActivityDiscipline"/>), null = not specified.</summary>
        public string? Discipline { get; set; }

        public string Name { get; set; } = "";
        public string? Venue { get; set; }
        public int? RangeId { get; set; }
        public int? SkjutledareMemberId { get; set; }
        public string? Description { get; set; }
        public bool IsCancelled { get; set; }

        // ── Registration (same meaning as on clubSimpleEvent) ──
        public bool RegistrationRequired { get; set; }
        public int? MaxParticipants { get; set; }
        public DateTime? RegistrationDeadline { get; set; }
        /// <summary>JSON in the <see cref="EventPrices"/> shape; null = free.</summary>
        public string? Prices { get; set; }
        public string? SwishNumber { get; set; }
        /// <summary><see cref="EventAudience"/> key; null = Club (the narrowest).</summary>
        public string? Audience { get; set; }
        public bool LoanWeaponsOffered { get; set; }

        /// <summary>Attendance is mandatory (beslut 2026-10-05: own column, migrated from isMandatory).
        /// A course group can still deviate per occasion (fas D). Missed = shown, never blocks.</summary>
        public bool IsMandatory { get; set; }

        /// <summary>The clubSimpleEvent node this occasion was migrated from (B3); null for new ones.</summary>
        public int? LegacyEventNodeId { get; set; }

        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A recurring training plan (fas B1): weekdays, time, period, breaks, and who leads. It CREATES
    /// <see cref="ClubTraining"/> occasions (<c>TrainingSchedulePlanner</c>); the occasions are then
    /// edited one by one — a skjutledare can be swapped per occasion, which is what a rotating year
    /// schedule needs.
    /// </summary>
    [TableName("ClubTrainingSchedule")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubTrainingSchedule
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public string Name { get; set; } = "";
        public string? Discipline { get; set; }

        /// <summary>ISO weekdays, comma-separated: 1 = Monday … 7 = Sunday ("2,4" = Tue + Thu).</summary>
        public string Weekdays { get; set; } = "";
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }

        /// <summary>JSON list of {from, to} date ranges with no training (uppehåll), e.g. midsummer.</summary>
        public string? Breaks { get; set; }

        public int? DefaultSkjutledareMemberId { get; set; }
        /// <summary>JSON list of member ids taking turns, in order. Empty = everyone gets the default.</summary>
        public string? RotatingSkjutledare { get; set; }

        public string? Venue { get; set; }
        public int? RangeId { get; set; }

        // Defaults copied to each occasion the schedule creates.
        public bool RegistrationRequired { get; set; }
        public int? MaxParticipants { get; set; }
        public bool LoanWeaponsOffered { get; set; }

        public bool IsActive { get; set; } = true;
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;
    }
}
