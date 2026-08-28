using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One awarded Märke (proficiency badge) for a member — the durable system of record.
    /// Phase 1: Pistolskyttemärket base valörer (Brons/Silver/Guld). The Guld valör carries the
    /// national registration <see cref="UniqueNumber"/>. Årtalsmärken are derived from the
    /// <see cref="MemberBadgeQualification"/> fulfilled-year count (not stored here unless an
    /// admin chooses to materialize one).
    /// </summary>
    [TableName("MemberBadge")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberBadge
    {
        public int Id { get; set; }
        public int MemberId { get; set; }

        /// <summary>See <see cref="Marken"/> family constants (Phase 1: "Pistolskytte").</summary>
        public string BadgeFamily { get; set; } = "";

        /// <summary>Base valör ("Brons"/"Silver"/"Guld") or an årtalsmärke step display name.</summary>
        public string Level { get; set; } = "";

        /// <summary>Sortable rank within the family (Brons=1, Silver=2, Guld=3, ladder 4+).</summary>
        public int LevelOrdinal { get; set; }

        /// <summary>Which discipline's series proved it, when relevant.</summary>
        public string? Discipline { get; set; }

        public int AchievedYear { get; set; }
        public DateTime? AchievedDate { get; set; }

        /// <summary>Functionary who signed off (board member or, if the club enabled it, Skjutledare).</summary>
        public int? SignedOffByMemberId { get; set; }
        public DateTime? SignedOffDate { get; set; }

        /// <summary>National registration number — ONLY for Pistolskyttemärket Guld.</summary>
        public string? UniqueNumber { get; set; }

        /// <summary>'SelfReported' | 'OnSite' | 'Admin'.</summary>
        public string Source { get; set; } = "";

        /// <summary>'Reported' (= "Ej verifierad") | 'Verified' | 'Rejected'.</summary>
        public string Status { get; set; } = Marken.StatusReported;

        /// <summary>Opaque reference to a stored proof file (App_Data), resolved by an authorized endpoint.</summary>
        public string? ProofFileRef { get; set; }

        public string? Notes { get; set; }

        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A member's yearly upholding of a märke's Guld requirements (for Pistolskyttemärket this is a
    /// "Guldfodring"). Always two parts: a precision part and a speed/tillämpning part. A year
    /// "counts" toward the årtalsmärke ladder when both parts are met AND the row is signed off.
    /// One row per (member, family, year).
    /// </summary>
    [TableName("MemberBadgeQualification")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberBadgeQualification
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string BadgeFamily { get; set; } = "";
        public int Year { get; set; }

        // Part 1 — precision
        public bool Part1Met { get; set; }
        public string? Part1Source { get; set; }   // see Marken.PartSource* constants
        public DateTime? Part1Date { get; set; }
        public int? Part1RefId { get; set; }        // TrainingScores row id when auto-detected
        public string? Part1Note { get; set; }

        // Part 2 — speed / tillämpning
        public bool Part2Met { get; set; }
        public string? Part2Source { get; set; }
        public DateTime? Part2Date { get; set; }
        public int? Part2RefId { get; set; }
        public string? Part2Note { get; set; }

        /// <summary>Both parts met (the year is complete; still needs sign-off to count).</summary>
        public bool Fulfilled { get; set; }

        public int? SignedOffByMemberId { get; set; }
        public DateTime? SignedOffDate { get; set; }

        /// <summary>'Reported' | 'Verified' | 'Rejected'. Verified + Fulfilled = counts toward årtalsmärke.</summary>
        public string Status { get; set; } = Marken.StatusReported;

        public string? ProofFileRef { get; set; }
        public string? Notes { get; set; }

        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A single validated series a shooter submits as märke evidence:
    ///   • <b>Precision</b> ("Guldserie") — a 5-shot series entered shot-by-shot.
    ///   • <b>Speed</b> ("Snabbserie") — a tillämpningsserie declared by target + claimed valör
    ///     (hits-in-time, pass/fail per valör — no shot-by-shot).
    /// Entered by the shooter, placed in a validation queue, verified on the spot by a board member /
    /// Skjutledare (in-app or via QR) — optionally with a phone photo. Only Verified + Qualifying
    /// rows count toward a Guldfodring. Generalized so future higher badges (Elit etc.) reuse it.
    /// (Validated evidence only — self-entered training logs are NOT used.)
    /// </summary>
    [TableName("MarkenSeries")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MarkenSeries
    {
        public int Id { get; set; }
        public int MemberId { get; set; }

        /// <summary>Club the shooter chose for validation — scopes the queue / QR-verify authority.</summary>
        public int ClubId { get; set; }

        public string BadgeFamily { get; set; } = Marken.FamilyPistolskytte;

        /// <summary>'Precision' (Guldserie) | 'Speed' (Snabbserie).</summary>
        public string SeriesType { get; set; } = Marken.SeriesTypePrecision;

        public int Year { get; set; }
        public DateTime SeriesDate { get; set; }

        /// <summary>Weapon group: A / B / C / R.</summary>
        public string WeaponGroup { get; set; } = "";

        /// <summary>The valör the shooter claims (Brons/Silver/Guld). Precision Guldserier claim Guld.</summary>
        public string ClaimedLevel { get; set; } = Marken.LevelGuld;

        // ── Precision-only ──
        /// <summary>JSON array of the 5 shot values ("X","10".."0"). Empty for Speed series.</summary>
        public string Shots { get; set; } = "[]";
        public int Total { get; set; }
        /// <summary>The age-adjusted requirement that applied when entered (Precision).</summary>
        public int Threshold { get; set; }
        /// <summary>Total ≥ Threshold (Precision) — only qualifying series count.</summary>
        public bool Qualifies { get; set; }

        // ── Speed-only ──
        /// <summary>Tillämpningsmål (e.g. 'B100_50m' / 'C30_25m'). Null for Precision.</summary>
        public string? Target { get; set; }

        /// <summary>'Pending' | 'Verified' | 'Rejected'.</summary>
        public string Status { get; set; } = Marken.SeriesStatusPending;

        public int? ValidatedByMemberId { get; set; }
        public DateTime? ValidatedDate { get; set; }

        /// <summary>Opaque reference to a stored target photo (App_Data), resolved by an authorized endpoint.</summary>
        public string? PhotoFileRef { get; set; }

        public string? Notes { get; set; }

        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// The <c>PrecisionResultEntry.Id</c> this series was materialised from, or null when a human
        /// entered it (the shooter's own submission, or a klubbliggare import).
        /// <para>
        /// A qualifying precision series shot at a hosted pistol.nu competition IS a guldserie, and
        /// used to be read live out of the result table instead of being a row here — which is why the
        /// club Guldserie-ligan could not see it and the Guldfodring counted it twice when the shooter
        /// had also submitted it by hand. A UNIQUE FILTERED INDEX on this column makes the second copy
        /// of one result row impossible in the schema, not merely unlikely in the code.
        /// </para>
        /// </summary>
        public int? SourceResultId { get; set; }

        /// <summary>The competition the series was shot at. Null for human-entered series.</summary>
        public int? SourceCompetitionId { get; set; }

        /// <summary>
        /// Whether this series counts toward the Guldfodring. Default true.
        /// <para>
        /// Set to false to resolve a duplicate, or to drop a series entered by mistake: the series was
        /// really shot, so the record stays and only the counting changes. Deleting a
        /// competition-sourced row would be futile — the next reconciliation recreates it from the
        /// result row — so exclusion, not deletion, is the operation that works for both kinds.
        /// </para>
        /// </summary>
        public bool CountsTowardGuldfodring { get; set; } = true;

        /// <summary>True when a competition result produced this series rather than a person.</summary>
        [Ignore]
        public bool IsFromCompetition => SourceResultId.HasValue;

        /// <summary>
        /// Identity of the PHYSICAL series a row describes: same shooter, weapon group, day and score.
        /// Used to WARN about a probable duplicate at submit time.
        /// <para>
        /// ⚠️ Deliberately NOT used to collapse rows automatically. Two different series can share this
        /// signature — a shooter who fires 47 twice in weapon group C on the same day is ordinary in a
        /// 10-series competition — so automatic merging would silently UNDER-count. A human decides;
        /// the code only points.
        /// </para>
        /// </summary>
        [Ignore]
        public string DuplicateSignature => $"{MemberId}|{WeaponGroup}|{SeriesDate:yyyy-MM-dd}|{Total}";
    }
}
