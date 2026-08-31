using System.ComponentModel.DataAnnotations;
using System.Linq;
using NPoco;

namespace HpskSite.CompetitionTypes.Springskytte.Models
{
    /// <summary>
    /// Springskytte competition result entry - COMPLETELY ISOLATED from other competition types.
    ///
    /// Key differences from Precision-based types:
    /// - Time-based scoring (fastest total time wins)
    /// - One row per shooter per competition (not per series)
    /// - Age/gender classes (D 15, H 21, etc.) instead of shooter classes (C1, C2)
    /// - Two weapon classes with different recording mechanics:
    ///   Class C: Falling targets, 6 stops x 5 shots, each hit or miss
    ///   Class A: Cardboard targets with zones (0-3), configurable series, always 30 shots total
    ///
    /// UNIQUE CONSTRAINT: (CompetitionId, MemberId, WeaponClass)
    /// </summary>
    [TableName("SpringskytteResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class SpringskytteResultEntry
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int MemberId { get; set; }

        [Required]
        [MaxLength(10)]
        public string WeaponClass { get; set; } = "";  // "A" or "C"

        [Required]
        [MaxLength(20)]
        public string AgeGenderClass { get; set; } = "";  // "D 15", "H 21", "D 50", etc.

        public int StartOrder { get; set; }

        [MaxLength(20)]
        public string? StartTime { get; set; }  // Scheduled start time "HH:mm:ss"

        public decimal? SprintTimeSeconds { get; set; }  // Running time in seconds

        /// <summary>
        /// JSON shots data. Format depends on weapon class:
        /// Class C: [["H","H","B","H","H"],["H","B","H","H","H"],...] (6 stops x 5 shots)
        /// Class A: [["3","5","2","1","1"],...] (N targets, each = [ring1,ring2,ring3,ring4,bom] zone counts)
        /// </summary>
        public string Shots { get; set; } = "[]";

        /// <summary>
        /// Per-station grip: JSON string array parallel to the stations, "1" = one hand (enhand),
        /// "2" = two hands (stödhand/tvåhand). E.g. ["1","2","1","1","1","2"]. Null/empty = not
        /// recorded. Springskytte alternates one/two hand per station; a shooter must use one hand
        /// on at least 3 of 6 stations unless their age class is 65+ (then two hands allowed on all).
        /// </summary>
        public string? StationHands { get; set; }

        public int? ShootingScore { get; set; }  // Total penalty points

        public int PenaltyMultiplier { get; set; } = 1;  // 1 normal, 2 for markestagning (class C)

        public decimal? TotalTimeSeconds { get; set; }  // Sprint + (ShootingScore * Multiplier * 60)

        [MaxLength(10)]
        public string? Status { get; set; }  // null=normal, "DNS", "DNF"

        [Required]
        public int EnteredBy { get; set; }

        public DateTime EnteredAt { get; set; } = DateTime.Now;

        public DateTime LastModified { get; set; } = DateTime.Now;

        // Per-role attribution for the Funktionärer hub's live load view. The score (scorer) and the
        // måltid (timekeeper) are written to this same row by different people, so LastModified alone
        // can't separate the two roles. ScoreModified is stamped only by the scoring save; TimeEnteredBy
        // + TimeModified only by the finish-time save. All nullable (backfilled null for legacy rows).
        public DateTime? ScoreModified { get; set; }
        public int? TimeEnteredBy { get; set; }
        public DateTime? TimeModified { get; set; }
    }

    /// <summary>
    /// Calculated result for a single shooter - used for ranking and display.
    /// </summary>
    public class SpringskytteShooterResult
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string WeaponClass { get; set; } = "";
        public string AgeGenderClass { get; set; } = "";
        public int StartOrder { get; set; }
        public string? StartTime { get; set; }

        // Time components
        public decimal? SprintTimeSeconds { get; set; }
        public int ShootingScore { get; set; }
        public int PenaltyMultiplier { get; set; } = 1;
        public decimal? TotalTimeSeconds { get; set; }

        // Manual range-master time adjustments (SpringskytteTimeAdjustment ledger).
        // Folded into TotalTimeSeconds by ApplyTimeAdjustments so ranking reflects them.
        public int PenaltyPoints { get; set; }      // rule-offence penalty points (each = 60 s)
        public int ReductionSeconds { get; set; }   // compensation subtracted (stored positive)

        // Shot details per stop/series
        public List<List<string>> ShotSeries { get; set; } = new();

        // Per-station grip ("1" one hand / "2" two hands), parallel to the stations. Empty = not recorded.
        public List<string> StationHands { get; set; } = new();

        // Per-station grip counts. "At least 3 one-hand" ⇔ "at most 3 two-hand"; recording 4+ two-hand
        // stations is a definite violation regardless of any still-unset stations. Waived for 65+.
        public int OneHandStationCount => StationHands.Count(h => h == "1");
        public int TwoHandStationCount => StationHands.Count(h => h == "2");
        public bool OneHandWarning => !SpringskytteClasses.IsTwoHandExempt(AgeGenderClass)
            && TwoHandStationCount >= 4;

        // Status
        public string? Status { get; set; }  // null, "DNS", "DNF"

        // Medal
        public string? StandardMedal { get; set; }

        // Calculated display properties
        public string SprintTimeDisplay => FormatTime(SprintTimeSeconds);
        public string PenaltyTimeDisplay => ShootingScore > 0
            ? FormatTime(ShootingScore * PenaltyMultiplier * 60m)
            : "0:00";
        // Manual penalty minutes (rule offences) and reductions, shown separately from Skjutpoäng.
        public string PenaltyMinutesDisplay => PenaltyPoints > 0 ? FormatTime(PenaltyPoints * 60m) : "0:00";
        public string ReductionDisplay => ReductionSeconds > 0 ? "-" + FormatTime(ReductionSeconds) : "0:00";
        public string TotalTimeDisplay => FormatTime(TotalTimeSeconds);

        // For tiebreaker: hits per stop from last to first
        public List<int> HitsPerStop { get; set; } = new();

        private static string FormatTime(decimal? totalSeconds)
        {
            if (totalSeconds == null) return "-";
            var ts = TimeSpan.FromSeconds((double)totalSeconds.Value);
            if (ts.Hours > 0)
                return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }

    /// <summary>
    /// Groups shooters by age/gender class for results display.
    /// </summary>
    public class SpringskytteClassGroup
    {
        public string ClassName { get; set; } = "";
        public List<SpringskytteShooterResult> Shooters { get; set; } = new();
    }

    /// <summary>
    /// Complete results container for a Springskytte competition.
    /// </summary>
    public class SpringskytteFinalResults
    {
        public int CompetitionId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsOfficial { get; set; } = true;
        public List<SpringskytteClassGroup> ClassGroups { get; set; } = new();

        /// <summary>
        /// Weapon groups whose results are PUBLIC. A and C finish at different times and are published
        /// independently (Stefan, 2026-08-04), so "official" is a set, not a boolean. Kept inside this
        /// blob — which the result node already stores — on purpose: no new doctype property means no
        /// operator step that can be left unrun before SM, and `IsOfficial` still answers "is anything
        /// public?" for every older consumer. Empty on a legacy blob → fall back to IsOfficial.
        /// </summary>
        public List<string> OfficialWeaponClasses { get; set; } = new();
    }

    // --- Request/Response models ---

    public class SpringskytteResultRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
        public string AgeGenderClass { get; set; } = "";
        public decimal? SprintTimeSeconds { get; set; }
        public string? SprintTimeInput { get; set; }  // "MM:SS" or "H:MM:SS" for UI parsing
        public string? FinishTimeInput { get; set; }  // "HH:MM:SS" finish time — sprint = finish - start
        public List<List<string>>? ShotSeries { get; set; }
        public List<string>? StationHands { get; set; }  // per-station "1"/"2" (one/two hands); null = leave the stored value untouched
        public int PenaltyMultiplier { get; set; } = 1;
        public string? Status { get; set; }  // null, "DNS", "DNF"
    }

    public class SpringskytteResultResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? ResultId { get; set; }
        public int ShootingScore { get; set; }
        public decimal? SprintTimeSeconds { get; set; }
        public decimal? TotalTimeSeconds { get; set; }
        public string TotalTimeDisplay { get; set; } = "";
        public int PenaltyMultiplier { get; set; }
        /// <summary>
        /// Verification: shots as stored in DB, returned for client-side integrity check.
        /// </summary>
        public List<List<string>>? VerificationShots { get; set; }
    }

    public class SpringskytteDeleteResultRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
    }

    /// <summary>
    /// Start list entry for Springskytte - time-interval based, not team-based.
    /// </summary>
    public class SpringskytteStartListEntry
    {
        public int StartOrder { get; set; }
        public string StartTime { get; set; } = "";
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string WeaponClass { get; set; } = "";
        public string AgeGenderClass { get; set; } = "";
    }

    /// <summary>
    /// One start pass (omgång) within a list: a start time and how many starters begin from it.
    /// Everything else — interval, break rules, classes — is shared by every pass in the list.
    /// </summary>
    public class SpringskytteStartPass
    {
        /// <summary>"HH:mm" (or "HH:mm:ss") — when this pass's first shooter starts.</summary>
        public string FirstStartTime { get; set; } = "10:00";

        /// <summary>
        /// How many starters begin from this pass. Null or 0 means "resten" — every remaining
        /// starter. The LAST pass is normally left open so the list can absorb an efteranmälan
        /// without re-typing the counts.
        /// </summary>
        public int? Count { get; set; }
    }

    /// <summary>
    /// Configuration for Springskytte start list generation.
    /// </summary>
    public class SpringskytteStartListConfig
    {
        /// <summary>
        /// The list's FIRST start time. Kept as a plain field even though <see cref="Passes"/> can
        /// hold several: it is the list's sort key across the competition (renumber ordering,
        /// admin cards, "fortsätt på föregående lista"), and every legacy config has it. When
        /// Passes is set this always mirrors Passes[0].FirstStartTime.
        /// </summary>
        public string FirstStartTime { get; set; } = "10:00";
        public string DefaultInterval { get; set; } = "01:00";  // MM:SS between starts
        public int BreakAfterEvery { get; set; } = 10;  // Long break after N starters
        public string BreakDuration { get; set; } = "05:00";  // MM:SS for long break
        public string ListName { get; set; } = "";  // User-assigned label (e.g., "Vapengrupp A")
        public string ListDate { get; set; } = "";  // Optional date (yyyy-MM-dd) — multi-day comps: same time on different days
        public List<string> CoveredClasses { get; set; } = new();  // Registration class patterns (e.g., ["A-D 21","A-H 35"])
        public List<SpringskytteStartListEntry> Starters { get; set; } = new();

        /// <summary>
        /// Start passes (omgångar) for this list — e.g. 25 starters from 10:00 and the rest from
        /// 12:00. EMPTY means the legacy single-pass list: one pass at <see cref="FirstStartTime"/>.
        /// Every legacy config therefore keeps working untouched, and <see cref="EffectivePasses"/>
        /// is the only thing anyone should read.
        ///
        /// <para>Pass MEMBERSHIP is deliberately NOT stored per starter. It is derived from the
        /// actual start time (see <see cref="PassIndexFor"/>), because an organiser can move a
        /// single shooter to another time long after generation — a stored index would drift and
        /// nothing would notice.</para>
        /// </summary>
        public List<SpringskytteStartPass> Passes { get; set; } = new();

        /// <summary>
        /// The passes to actually use: the configured ones, or a single legacy pass built from
        /// <see cref="FirstStartTime"/>. Never returns empty.
        /// </summary>
        public List<SpringskytteStartPass> EffectivePasses()
        {
            var real = (Passes ?? new List<SpringskytteStartPass>())
                .Where(p => !string.IsNullOrWhiteSpace(p?.FirstStartTime))
                .ToList();
            return real.Count > 0
                ? real
                : new List<SpringskytteStartPass> { new() { FirstStartTime = FirstStartTime, Count = null } };
        }

        /// <summary>True when this list actually runs in more than one pass.</summary>
        public bool HasMultiplePasses() => EffectivePasses().Count > 1;

        // ===== Start-number assignment (per-list running sequence) =====
        // Numbering is a single running sequence within each list (NOT per weapon class), and numbers
        // are globally unique across the competition. StartNumberBase is where THIS list starts; when
        // ContinueFromPrevious is true the base is derived from the previous list's last number instead
        // (auto-follow-on). Field initializers double as the legacy-config defaults: an old list missing
        // these keys deserializes to base 1 + follow-on, i.e. one continuous 1..N sequence across lists.
        //
        // ContinueFromPrevious is an INTENT flag read ONLY by the explicit "Numrera om" modal.
        // Generating a list must never re-derive another list's numbers from it — that is exactly the
        // 2026-08-03 SM-rehearsal fault (a manually numbered C list silently came back renumbered when
        // the A list was generated). StartNumberBase is written back with the numbers actually applied,
        // so the stored settings always describe what is on the list.
        public int StartNumberBase { get; set; } = 1;
        public bool ContinueFromPrevious { get; set; } = true;

        /// <summary>
        /// True once a human has edited a start number on this list by hand. Sticky: automatic
        /// numbering (generation, regeneration, follow-on) must never overwrite a flagged list —
        /// only an explicit, per-list opt-in in the "Numrera om" modal may.
        /// </summary>
        public bool ManualNumbering { get; set; }

        /// <summary>
        /// Append-only audit trail of every start-number change on this list, newest last, capped at
        /// <see cref="MaxNumberingHistory"/>. Lives in the config JSON on purpose — no migration, so
        /// it cannot be dead-on-arrival through an unrun SQL script before SM.
        /// </summary>
        public List<SpringskytteNumberingEvent> NumberingHistory { get; set; } = new();

        public const int MaxNumberingHistory = 60;
    }

    /// <summary>
    /// Pass-boundary helpers. Extension methods rather than instance members so every reader —
    /// controller, timeline, public view, print — resolves a boundary the same way.
    /// </summary>
    public static class SpringskyttePassHelper
    {
        /// <summary>How many bookable slots are offered after a pass's last starter.</summary>
        public const int TrailingSlotsPerPass = 3;

        /// <summary>Seconds since midnight for "HH:mm" / "HH:mm:ss"; int.MaxValue when unparsable.</summary>
        public static int TimeToSeconds(string? time)
        {
            if (string.IsNullOrWhiteSpace(time)) return int.MaxValue;
            var parts = time.Split(':');
            if (parts.Length < 2) return int.MaxValue;
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return int.MaxValue;
            var s = 0;
            if (parts.Length > 2) int.TryParse(parts[2], out s);
            return h * 3600 + m * 60 + s;
        }

        /// <summary>
        /// Each pass's start in seconds, ascending. One entry per pass; the first is the list's own
        /// first start.
        /// </summary>
        public static List<int> PassStartSeconds(this SpringskytteStartListConfig config) =>
            config.EffectivePasses()
                .Select(p => TimeToSeconds(p.FirstStartTime))
                .Where(t => t != int.MaxValue)
                .OrderBy(t => t)
                .ToList();

        /// <summary>
        /// Which pass a start time belongs to: the last pass beginning at or before it. A time
        /// before the first pass (possible after a manual move) belongs to pass 0.
        /// </summary>
        public static int PassIndexFor(this SpringskytteStartListConfig config, string? startTime)
        {
            var starts = config.PassStartSeconds();
            if (starts.Count == 0) return 0;
            var t = TimeToSeconds(startTime);
            if (t == int.MaxValue) return 0;
            var idx = 0;
            for (var i = 0; i < starts.Count; i++)
                if (t >= starts[i]) idx = i;
            return idx;
        }

        /// <summary>
        /// The start of the NEXT pass after the one containing <paramref name="startTime"/>, in
        /// seconds — or null when that is the last pass. This is the hard ceiling for offering
        /// bookable slots: past it we are in the next pass, not in a break.
        /// </summary>
        public static int? NextPassStartAfter(this SpringskytteStartListConfig config, string? startTime)
        {
            var starts = config.PassStartSeconds();
            var idx = config.PassIndexFor(startTime);
            return idx + 1 < starts.Count ? starts[idx + 1] : null;
        }
    }

    /// <summary>One entry in a start list's start-number audit trail.</summary>
    public class SpringskytteNumberingEvent
    {
        public string At { get; set; } = "";      // yyyy-MM-dd HH:mm:ss
        public string By { get; set; } = "";      // member name (or "" when unresolved)
        public string Action { get; set; } = "";  // generate | regenerate | manual | renumber | reset | walk-in
        public string Detail { get; set; } = "";  // human-readable, e.g. "1–3 → 120–122"
    }

    public class SpringskytteStartListRequest
    {
        public int CompetitionId { get; set; }

        /// <summary>
        /// Multi-pass split: e.g. 25 starters from 10:00 and the rest from 12:00. When null or
        /// empty the list is generated exactly as before from <see cref="FirstStartTime"/> — that
        /// is what keeps every existing caller (and every legacy list) behaving identically.
        /// </summary>
        public List<SpringskytteStartPass>? Passes { get; set; }

        /// <summary>Single-pass shorthand, and pass 1's time when Passes is supplied.</summary>
        public string FirstStartTime { get; set; } = "10:00";
        public string DefaultInterval { get; set; } = "01:00";
        public int BreakAfterEvery { get; set; } = 10;
        public string BreakDuration { get; set; } = "05:00";
        public List<string> CoveredClasses { get; set; } = new();  // Which classes to include (empty = all)
        public string ListName { get; set; } = "";  // Name for this list
        public string ListDate { get; set; } = "";  // Optional date (yyyy-MM-dd) for multi-day competitions
        public int? ExistingNodeId { get; set; }  // If set, replace this specific node; if null, create new
    }

    public class SpringskytteDeleteStartListRequest
    {
        public int CompetitionId { get; set; }
        public int NodeId { get; set; }
    }

    /// <summary>
    /// Renumber all individual (non-stafett) start lists with a per-list running sequence.
    /// The Lists are applied in the order given (which is the modal's display order = start-time order);
    /// each list either starts at its own StartNumberBase or continues from the previous list's last number.
    /// </summary>
    public class SpringskytteRenumberRequest
    {
        public int CompetitionId { get; set; }
        public List<SpringskytteRenumberListSetting> Lists { get; set; } = new();
    }

    public class SpringskytteRenumberListSetting
    {
        public int NodeId { get; set; }
        public int StartNumberBase { get; set; } = 1;
        public bool ContinueFromPrevious { get; set; }

        /// <summary>
        /// Whether the user actually asked for THIS list to be renumbered. Lists left unticked keep
        /// every number they have and are treated as fixed occupants when checking uniqueness.
        /// Defaults to false so an omitted flag can never renumber a list by accident.
        /// </summary>
        public bool Renumber { get; set; }
    }

    /// <summary>
    /// Calculate / publish / unpublish a Springskytte result list. WeaponClass scopes the action to one
    /// weapon group (A and C publish independently); empty means the whole competition, which is the
    /// pre-2026-08 behaviour and still what the fallback path uses when the per-class property is absent.
    /// </summary>
    public class SpringskytteResultsActionRequest
    {
        public int CompetitionId { get; set; }
        public string WeaponClass { get; set; } = "";
    }

    /// <summary>Update ONLY a start list's name + date (never rebuilds/reshuffles the starters).</summary>
    public class SpringskytteStartListMetaRequest
    {
        public int CompetitionId { get; set; }
        public int NodeId { get; set; }
        public string ListName { get; set; } = "";
        public string ListDate { get; set; } = "";
    }

    /// <summary>
    /// Edit a single starter's start number and/or start time within an existing start list,
    /// without regenerating (which would reshuffle everyone). Identity = (MemberId, WeaponClass).
    /// </summary>
    public class SpringskytteUpdateStarterRequest
    {
        public int CompetitionId { get; set; }
        public int NodeId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
        public int? StartOrder { get; set; }      // new start number (null = leave unchanged)
        public string? StartTime { get; set; }    // "HH:mm" or "HH:mm:ss" (null/empty = leave unchanged)
    }

    /// <summary>
    /// Drop a walk-in (rullande start) into an existing start list on the spot: assign the picked
    /// free start time + the next start number for that weapon class, without reshuffling anyone else.
    /// The desk "Anmäl och betala" flow calls this after creating the registration, per registered class.
    /// </summary>
    public class SpringskytteWalkInStartTimeRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";  // registration class, e.g. "A-D 21"
        public int? NodeId { get; set; }                  // the picked slot's list node (derived from CoveredClasses if absent)
        public string StartTime { get; set; } = "";       // "HH:mm" or "HH:mm:ss"
    }

    /// <summary>
    /// Clean up after a registration is deleted: remove the member from any start list (freeing the
    /// slot, preserving everyone else), re-publish official lists, and drop their result + adjustments.
    /// </summary>
    public class SpringskytteCleanupRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
    }

    /// <summary>
    /// Mark a shooter DNS (will not start) or clear it. DNS is what frees a start slot — distinct
    /// from Närvaro (arrival). Un-DNS restores the shooter as scheduled (RM re-assigns a slot if the
    /// old one was taken). Settable from the starter screen, timekeeper, and start-list edit modal.
    /// </summary>
    public class SpringskytteDnsRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
        public bool IsDns { get; set; }
    }

    /// <summary>
    /// Toggle a Springskytte start list between preliminary and official (published). Each list
    /// (Seniorer, Juniorer, …) is toggled independently — unlike Precision's single official list.
    /// </summary>
    public class SpringskytteSetOfficialRequest
    {
        public int CompetitionId { get; set; }
        public int NodeId { get; set; }
        public bool IsOfficial { get; set; }

        /// <summary>
        /// Stänga självanmälan på tävlingssidan i samma veva? Null = klienten sa ingenting, och då
        /// lämnas inställningen orörd. Se <c>StartListRegistrationGate</c>.
        ///
        /// ⚠️ Springskytte publicerar EN lista i taget (per vapenklass/dag), så frågan ställs bara
        /// när den första listan publiceras — annars skulle arrangören få samma fråga fem gånger.
        /// </summary>
        public bool? CloseRegistration { get; set; }
    }

    /// <summary>
    /// Time-adjustment ledger: manual range-master penalties (rule offences, +1 min each) and
    /// reductions (compensation, −time), kept separate from the automatic Skjutpoäng (miss) penalty.
    /// Each row is one adjustment with its own reason, folded into the shooter's total time.
    /// </summary>
    [TableName("SpringskytteTimeAdjustment")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class SpringskytteTimeAdjustment
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        [MaxLength(10)]
        public string WeaponClass { get; set; } = "";
        [MaxLength(20)]
        public string AdjustmentType { get; set; } = "";  // "Penalty" | "Reduction"
        public int? Points { get; set; }                   // penalty points (each 60 s); null for reduction
        public int Seconds { get; set; }                   // signed applied delta (penalty +, reduction −)
        [MaxLength(500)]
        public string? Reason { get; set; }
        public int EnteredBy { get; set; }
        public DateTime EnteredAt { get; set; }
    }

    public class SpringskytteAddAdjustmentRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
        public string AdjustmentType { get; set; } = "";  // "Penalty" | "Reduction"
        public int? Points { get; set; }                   // penalties: number of points (each 60 s)
        public string? TimeInput { get; set; }             // reductions: "MM:SS" or "M:SS"
        public string? Reason { get; set; }
    }

    public class SpringskytteDeleteAdjustmentRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    /// <summary>
    /// Field-scoped finish-time save for the timing role (item 5): updates only sprint/total time
    /// (sprint = finish − start) and preserves the shots/score the scoring role entered.
    /// </summary>
    public class SpringskytteFinishTimeRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
        public string? FinishTimeInput { get; set; }  // "HH:MM:SS"
        public string? Status { get; set; }           // null, "DNS", "DNF"
    }

    /// <summary>
    /// Available age/gender classes for Springskytte.
    /// </summary>
    public static class SpringskytteClasses
    {
        public static readonly List<string> All = new()
        {
            // Junior
            "D 15", "H 15",
            "D 18", "H 18",
            "D jun", "H jun",
            // Senior
            "D 21", "H 21",
            "D 35", "H 35",
            // Veteran
            "D 50", "H 50",
            "D 60", "H 60",
            "D 65", "H 65",
            "D 70", "H 70"
        };

        public static readonly List<string> WeaponClasses = new() { "A", "C" };

        /// <summary>
        /// True when the age class is 65 or older ("D 65"/"H 65"/"D 70"/"H 70", incl. "A-H 65"
        /// style ids). Such shooters may use two hands on all stations, so the "at least 3 one-hand
        /// stations" requirement does not apply to them.
        /// </summary>
        public static bool IsTwoHandExempt(string? ageGenderClass)
        {
            if (string.IsNullOrEmpty(ageGenderClass)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(ageGenderClass, @"(\d+)");
            return m.Success && int.TryParse(m.Value, out var age) && age >= 65;
        }

        public static string GetClassCategory(string ageGenderClass)
        {
            if (string.IsNullOrEmpty(ageGenderClass)) return "Okänd";
            var normalized = ageGenderClass.Trim();
            if (normalized.Contains("15") || normalized.Contains("18") || normalized.Contains("jun"))
                return "Junior";
            if (normalized.Contains("21") || normalized.Contains("35"))
                return "Senior";
            if (normalized.Contains("50") || normalized.Contains("60") || normalized.Contains("65") || normalized.Contains("70"))
                return "Veteran";
            return "Okänd";
        }

        /// <summary>
        /// Maps an age code to display format: (replacement, useParentheses).
        /// Codes where the range starts with the same number get inline replacement (e.g. "50" → "50-59").
        /// Others get parenthesized format to preserve the original label (e.g. "15" → "15 (-15 år)").
        /// </summary>
        private static readonly Dictionary<string, (string span, bool parens)> AgeSpanMap = new()
        {
            { "15", ("-15 år", true) }, { "18", ("16-18 år", true) }, { "jun", ("15-20 år", true) },
            { "21", ("21-34", false) }, { "35", ("35-49", false) },
            { "50", ("50-59", false) }, { "60", ("60-64", false) }, { "65", ("65-69", false) }, { "70", ("70+", false) }
        };

        /// <summary>
        /// Formats a class string with age span, e.g.:
        /// "H 50" → "H 50-59", "A-D 21" → "A-D 21-34", "H 15" → "H 15 (-15 år)", "D jun" → "D jun (15-20 år)"
        /// </summary>
        public static string FormatWithAgeSpan(string classStr)
        {
            if (string.IsNullOrEmpty(classStr)) return classStr;
            foreach (var (code, (span, parens)) in AgeSpanMap)
            {
                if (classStr.EndsWith(" " + code) || classStr.EndsWith("-" + code))
                {
                    if (parens)
                        return classStr + " (" + span + ")";
                    return classStr.Substring(0, classStr.Length - code.Length) + span;
                }
            }
            return classStr;
        }
    }
}
