namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    // ── Patrol Generation ───────────────────────────────────────────

    public class GeneratePatrolsRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolSize { get; set; } = 6;
        public int PatrolIntervalMinutes { get; set; } = 15;
        public DateTime? FirstStartTime { get; set; }
        /// <summary>"Separate", "CombineAR", "MixAll"</summary>
        public string WeaponGrouping { get; set; } = "MixAll";
        /// <summary>Which weapon classes to include (e.g. ["C"], ["A","R"]). Null = all.</summary>
        public List<string>? WeaponClasses { get; set; }
        /// <summary>Minimum minutes between patrols for a shooter with multiple weapon classes. 0 = no separation.</summary>
        public int MultiClassGapMinutes { get; set; }
    }

    public class DeletePatrolsByGroupRequest
    {
        public int CompetitionId { get; set; }
        public string WeaponGroup { get; set; } = "";
    }

    public class DeletePatrolsRequest
    {
        public int CompetitionId { get; set; }
    }

    // ── Patrol Editing ────────────────────────────────────────────

    public class CreatePatrolRequest
    {
        public int CompetitionId { get; set; }
        public DateTime? StartTime { get; set; }
        public string WeaponGroup { get; set; } = "";
        /// <summary>Insert after this patrol number. Null or 0 = append at end.</summary>
        public int? AfterPatrolNumber { get; set; }
    }

    public class DeletePatrolRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolId { get; set; }
    }

    public class AddShooterToPatrolRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string ClubName { get; set; } = "";
    }

    public class RemoveShooterFromPatrolRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolMemberId { get; set; }
    }

    public class MoveShooterToPatrolRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolMemberId { get; set; }
        public int TargetPatrolId { get; set; }
    }

    /// <summary>
    /// Walk-in assignment from the registration desk. Used during a rolling-start
    /// Fältskytte / MagnumFält competition to drop a freshly-registered shooter onto
    /// a patrol in one round-trip. The endpoint reads the registration to get an
    /// authoritative member name / club / class — the client only sends a target hint.
    /// </summary>
    public class AssignWalkInToPatrolRequest
    {
        public int CompetitionId { get; set; }
        public int RegistrationId { get; set; }
        /// <summary>"nextAvailable" | "newPatrol" | "&lt;patrolId&gt;" (an integer string for an explicit patrol).</summary>
        public string Target { get; set; } = "nextAvailable";
    }

    public class FaltskylteBulkMoveShootersRequest
    {
        public int CompetitionId { get; set; }
        public List<int> PatrolMemberIds { get; set; } = new();
        public int TargetPatrolId { get; set; }
    }

    public class UpdatePatrolTimeRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolId { get; set; }
        public DateTime? StartTime { get; set; }
        public string? Label { get; set; }
    }

    public class PublishPatrolListRequest
    {
        public int CompetitionId { get; set; }
        public bool Publish { get; set; }
    }

    public class SaveMergeConfigRequest
    {
        public int CompetitionId { get; set; }
        public string? MergeConfig { get; set; }
        /// <summary>When true, persist to the Deltävling slot (`subCompetitionMergeConfig`
        /// on the competitionResult node) instead of the main competition's `mergeConfig`.</summary>
        public bool IsSubCompetition { get; set; }
    }

    public class PublishResultsRequest
    {
        public int CompetitionId { get; set; }
        public bool IsOfficial { get; set; }
        /// <summary>When true, flip the Deltävling's published state (`subCompetitionIsOfficial`
        /// on the competitionResult node) instead of the main result list's official flag.</summary>
        public bool IsSubCompetition { get; set; }
    }

    public class FaltskytteShootOffConfigRequest
    {
        public int CompetitionId { get; set; }
        public bool IsSubCompetition { get; set; }
        /// <summary>JSON-serialised <see cref="FaltskytteCompetitionConfig"/> wrapping the single
        /// shoot-off station per-weapon-class. Empty string clears the config.</summary>
        public string? ConfigJson { get; set; }
    }

    public class FaltskytteShootOffEntryRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int Round { get; set; }

        // Normal/Poäng fields
        public int? Hits { get; set; }
        public int? Figures { get; set; }
        public string? HitDistribution { get; set; }

        // Poängmål — all three variations use these (Magnum uses ONLY these)
        public int? TiebreakerScore { get; set; }
        public string? PoangmalScores { get; set; }
    }

    public class FaltskytteShootOffDeleteRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int Round { get; set; }
    }

    public class JoinNextPatrolRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string ClubName { get; set; } = "";
        public int PatrolSize { get; set; } = 6;
    }

    // ── Station Config ────────────────────────────────────────────

    public class SaveStationConfigRequest
    {
        public int CompetitionId { get; set; }
        public string? StationConfigJson { get; set; }
    }

    // ── Result Entry ────────────────────────────────────────────────

    /// <summary>Save one shooter's result at one station</summary>
    public class FaltskylteSaveResultRequest
    {
        public int CompetitionId { get; set; }
        public int StationNumber { get; set; }
        public int MemberId { get; set; }
        public int PatrolNumber { get; set; }
        public string ShootingClass { get; set; } = "";
        /// <summary>Hits per figure, e.g. [3, 2, 1]</summary>
        public int[] HitsPerFigure { get; set; } = Array.Empty<int>();
        /// <summary>Poångmål total score (null if station has no poångmål)</summary>
        public int? TiebreakerScore { get; set; }
        /// <summary>Individual poångmål scores, e.g. [24, 20]</summary>
        public int[]? PoangmalScores { get; set; }
        /// <summary>Number of re-shoots at this station</summary>
        public int Reshoots { get; set; }
    }

    /// <summary>Response after saving a result</summary>
    public class FaltskylteSaveResultResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? ResultId { get; set; }
        public int TotalHits { get; set; }
        public int TotalFigures { get; set; }
    }

    // ── Patrol ──────────────────────────────────────────────────────

    public class FaltskyttePatrolView
    {
        public int PatrolId { get; set; }
        public int PatrolNumber { get; set; }
        public DateTime? StartTime { get; set; }
        public string? WeaponGroup { get; set; }
        public string? Label { get; set; }
        public List<FaltskyttePatrolMemberView> Members { get; set; } = new();
        /// <summary>How many members have results entered at the queried station</summary>
        public int CompletedCount { get; set; }
        /// <summary>
        /// Self-service cursor: the station this patrol is currently at. Null
        /// until the patrol's first scan. Used by the entry partial to lock
        /// older stations to read-only for shooters (staff always edits).
        /// </summary>
        public int? CurrentStation { get; set; }
    }

    public class FaltskyttePatrolMemberView
    {
        public int PatrolMemberId { get; set; }
        public int MemberId { get; set; }
        public int Position { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        /// <summary>Whether a result exists for this member at the queried station</summary>
        public bool HasResult { get; set; }
    }

    // ── Station Entry View ──────────────────────────────────────────

    /// <summary>Data for the station entry view</summary>
    public class FaltskytteStationView
    {
        public int CompetitionId { get; set; }
        public int StationNumber { get; set; }
        public int MaxReshoots { get; set; }
        public string ScoringMode { get; set; } = "Normal";
        /// <summary>Per-weapon-class station configs for this station number</summary>
        public Dictionary<string, FaltskytteStationConfig> WeaponClassStations { get; set; } = new();
        public List<FaltskyttePatrolView> Patrols { get; set; } = new();
    }

    /// <summary>Re-shoot info for a shooter across all stations</summary>
    public class FaltskytteReshootInfo
    {
        public int MemberId { get; set; }
        public int TotalReshoots { get; set; }
        public int MaxReshoots { get; set; }
        public bool LimitReached { get; set; }
        /// <summary>Which stations had re-shoots, e.g. [2, 5]</summary>
        public List<int> ReshootStations { get; set; } = new();
    }

    // ── Result List ─────────────────────────────────────────────────

    public class FaltskytteShooterResult
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public List<FaltskytteStationResult> Stations { get; set; } = new();
        public int TotalHits { get; set; }
        public int TotalFigures { get; set; }
        /// <summary>Only used in Poäng mode: sum of (hits + figures) per station</summary>
        public int TotalPoints { get; set; }
        /// <summary>Sum of poångmål scores across all stations</summary>
        public int TotalTiebreakerScore { get; set; }
        /// <summary>Standard medal: "S" (silver), "B" (bronze), or null</summary>
        public string? StandardMedal { get; set; }

        // ── Särskjutning (shoot-off) — populated only for championship medal-tied shooters ──

        /// <summary>Variation-formatted summary per round, e.g. ["5/4","4/4"] for Normal,
        /// ["10p"] for Poäng, ["23p","19p"] for Magnum. Null/empty when the shooter wasn't in a shoot-off.</summary>
        public List<string>? ShootOffRounds { get; set; }

        /// <summary>True once this shooter's placement is uniquely decided by the rounds shot so far.</summary>
        public bool ShootOffIsResolved { get; set; }

        /// <summary>The next round the shooter must shoot. Null when resolved OR when they have
        /// already shot the current round but tied opponents have not yet (waiting state).</summary>
        public int? ShootOffNextRound { get; set; }
    }

    public class FaltskytteStationResult
    {
        public int StationNumber { get; set; }
        public int Hits { get; set; }
        public int Figures { get; set; }
        public int? TiebreakerScore { get; set; }
        /// <summary>Poäng mode: hits + figures</summary>
        public int Points => Hits + Figures;
    }

    public class FaltskytteClassGroup
    {
        public string ClassName { get; set; } = "";

        /// <summary>Admin-set custom name shown to the public — falls back to <see cref="ClassName"/> when null.
        /// Same pattern as the Precision-family override system.</summary>
        public string? DisplayClassName { get; set; }

        public List<FaltskytteShooterResult> Shooters { get; set; } = new();

        /// <summary>Tied medal-tier groups detected for this class (rank ≤ 3, score-equal, championship-gated).</summary>
        public List<FaltskytteTiedMedalGroup> TiedMedalGroups { get; set; } = new();

        /// <summary>Human-readable footnote lines for the public result page, e.g.
        /// "Särskjutning avgjorde guldet: Anna A. 5/4 vs Berit B. 4/3".</summary>
        public List<string> ShootOffNotes { get; set; } = new();
    }

    public class FaltskytteTiedMedalGroup
    {
        public string MedalTier { get; set; } = "";   // "Guld" / "Silver" / "Brons" / combined like "Guld + Silver"
        public int FirstRank { get; set; }
        public int LastRank { get; set; }
        /// <summary>The score that ties the group (hits for Normal, points for Poäng/Magnum).</summary>
        public int TiedScore { get; set; }
        public int RoundsCompleted { get; set; }
        public bool Resolved { get; set; }
        public List<FaltskytteTiedMedalShooter> Shooters { get; set; } = new();
    }

    public class FaltskytteTiedMedalShooter
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public int TotalHits { get; set; }
        public int TotalFigures { get; set; }
        public int TotalPoints { get; set; }
        public int TotalTiebreakerScore { get; set; }
        public bool IsResolved { get; set; }
        public int? NextRound { get; set; }
        public List<FaltskytteShootOffRoundSummary> Rounds { get; set; } = new();
    }

    public class FaltskytteShootOffRoundSummary
    {
        public int Round { get; set; }
        public string Display { get; set; } = "";  // variation-formatted, e.g. "5/4" or "23p"
        public int? Hits { get; set; }
        public int? Figures { get; set; }
        public int? TiebreakerScore { get; set; }
        public string? PoangmalScores { get; set; }
        public string? HitDistribution { get; set; }
    }

    public class FaltskylteFinalResults
    {
        public int CompetitionId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsOfficial { get; set; }
        /// <summary>"Normal" or "Poang"</summary>
        public string ScoringMode { get; set; } = "Normal";
        public int StationCount { get; set; }
        /// <summary>Full per-weapon-class config (includes Förutsättningar, target groups, etc.)</summary>
        public FaltskytteCompetitionConfig Config { get; set; } = new();
        public List<FaltskytteClassGroup> ClassGroups { get; set; } = new();
        /// <summary>Competition name — surfaced so the result list / printout can show a proper header.</summary>
        public string CompetitionName { get; set; } = "";
        /// <summary>Competition date, formatted as YYYY-MM-DD by the server.</summary>
        public string CompetitionDate { get; set; } = "";
        /// <summary>Organising club name (or empty for non-club competitions).</summary>
        public string OrganizerName { get; set; } = "";
        /// <summary>True when this payload represents the Deltävling subset (filtered to
        /// IsSubCompetition shooters with sub-comp merge config). Lets the UI know which
        /// state badge / publish endpoint to use.</summary>
        public bool IsSubCompetition { get; set; }
        /// <summary>Display name of the Deltävling, from the competition's `subCompetitionName`
        /// property. Empty when this competition doesn't have a Deltävling configured.</summary>
        public string SubCompetitionName { get; set; } = "";
        /// <summary>Whether the competition awards Standardmedaljer. Drives Std column
        /// visibility in result list renderers; the medal calculator is gated on this
        /// (and !isClubOnly) server-side so when false, every shooter's StandardMedal is empty.</summary>
        public bool IsAwardingStandardMedals { get; set; }
    }
}
