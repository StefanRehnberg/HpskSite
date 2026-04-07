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
    }

    public class PublishResultsRequest
    {
        public int CompetitionId { get; set; }
        public bool IsOfficial { get; set; }
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
        public List<FaltskyttePatrolMemberView> Members { get; set; } = new();
        /// <summary>How many members have results entered at the queried station</summary>
        public int CompletedCount { get; set; }
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
        public List<FaltskytteShooterResult> Shooters { get; set; } = new();
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
    }
}
