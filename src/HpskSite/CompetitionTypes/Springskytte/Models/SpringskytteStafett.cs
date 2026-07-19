using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.CompetitionTypes.Springskytte.Models
{
    /// <summary>
    /// Springskytte STAFETT (relay) team result — COMPLETELY ISOLATED, and a third scoring
    /// model distinct from the individual per-shooter table and the regular sum-of-members
    /// team path. Per SHB 2026 Del L (L.6.1.3.2 + L.6.11.3):
    ///   - Mass start ("gemensam start"), one common start time per stafett class.
    ///   - Elapsed clock: result = finish (målgång) − common start; lowest elapsed wins.
    ///   - Shooting penalties are physical ~60 m straffrundor the runners already ran, so they
    ///     are inside the elapsed time — there is NO post-hoc "add penalty seconds" step.
    ///   - Ranking = elapsed ascending within stafett class (= finish order).
    ///
    /// One row per relay team. Members/legs live in CompetitionTeamMember (not duplicated here).
    /// UNIQUE CONSTRAINT: (CompetitionId, TeamId).
    /// </summary>
    [TableName("SpringskytteStafettResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class SpringskytteStafettResultEntry
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int TeamId { get; set; }  // CompetitionTeam.Id (IsRelay = true)

        [Required]
        [MaxLength(50)]
        public string StafettClass { get; set; } = "";  // "Stafett Senior Herr", "Stafett Junior", ...

        public int StartOrder { get; set; }

        [MaxLength(20)]
        public string? StartTime { get; set; }  // Common (mass) start time "HH:mm:ss"

        public decimal? ElapsedSeconds { get; set; }  // Finish − common start; lowest wins

        public int? PenaltyLoops { get; set; }  // Straffrundor tally (protocol record only; already in ElapsedSeconds)

        [MaxLength(10)]
        public string? Status { get; set; }  // null=normal, "DNS", "DNF"

        [Required]
        public int EnteredBy { get; set; }

        public DateTime EnteredAt { get; set; } = DateTime.Now;

        public DateTime LastModified { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A relay team leg / member, as shown on the stafett start list.
    /// </summary>
    public class SpringskytteStafettMember
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public int LegNumber { get; set; }   // 1-based leg order (informational)
        public bool IsSpare { get; set; }
    }

    /// <summary>
    /// Start-list entry for a Springskytte stafett — one row per TEAM (mass start), not per shooter.
    /// </summary>
    public class SpringskytteStafettStartListEntry
    {
        public int StartOrder { get; set; }
        public string StartTime { get; set; } = "";  // Common start for the team's class
        public int TeamId { get; set; }
        public string TeamName { get; set; } = "";
        public string Club { get; set; } = "";
        public string StafettClass { get; set; } = "";
        public List<SpringskytteStafettMember> Members { get; set; } = new();
    }

    /// <summary>
    /// Configuration for a Springskytte stafett start list. Stored as configurationData JSON on a
    /// precisionStartList child node, tagged TeamFormat="SpringskytteStafett" so the list loaders can
    /// tell it apart from the individual (Starters-based) lists. Mass start: no interval time-engine —
    /// every team in the list shares one common start time.
    /// </summary>
    public class SpringskytteStafettStartListConfig
    {
        public string TeamFormat { get; set; } = "SpringskytteStafett";  // discriminator for the loaders
        public string CommonStartTime { get; set; } = "10:00";  // gemensam start for this list
        public string ListName { get; set; } = "";
        public string ListDate { get; set; } = "";  // Optional date (yyyy-MM-dd) for multi-day comps
        public List<string> CoveredClasses { get; set; } = new();  // Stafett class(es) this list covers
        public List<SpringskytteStafettStartListEntry> Teams { get; set; } = new();
    }

    /// <summary>
    /// Lightweight probe used by the list loaders to read TeamFormat off configurationData
    /// before deciding whether to deserialize as an individual or a stafett config.
    /// </summary>
    public class SpringskytteStartListFormatProbe
    {
        public string? TeamFormat { get; set; }
    }

    /// <summary>
    /// Calculated stafett team result — used for ranking and display.
    /// </summary>
    public class SpringskytteStafettTeamResult
    {
        public int TeamId { get; set; }
        public string TeamName { get; set; } = "";
        public string Club { get; set; } = "";
        public string StafettClass { get; set; } = "";
        public int StartOrder { get; set; }
        public string? StartTime { get; set; }
        public decimal? ElapsedSeconds { get; set; }
        public int? PenaltyLoops { get; set; }
        public string? Status { get; set; }  // null, "DNS", "DNF"
        public int Rank { get; set; }
        public List<SpringskytteStafettMember> Members { get; set; } = new();

        public string ElapsedTimeDisplay => FormatTime(ElapsedSeconds);

        public static string FormatTime(decimal? totalSeconds)
        {
            if (totalSeconds == null) return "-";
            var ts = TimeSpan.FromSeconds((double)totalSeconds.Value);
            if (ts.Hours > 0)
                return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }

    /// <summary>
    /// Groups stafett teams by stafett class for results display.
    /// </summary>
    public class SpringskytteStafettClassGroup
    {
        public string ClassName { get; set; } = "";
        public List<SpringskytteStafettTeamResult> Teams { get; set; } = new();
    }

    // --- Request / response models ---

    public class SpringskytteStafettStartListRequest
    {
        public int CompetitionId { get; set; }
        public string CommonStartTime { get; set; } = "10:00";
        public List<string> CoveredClasses { get; set; } = new();  // Which stafett class(es) to include (empty = all)
        public string ListName { get; set; } = "";
        public string ListDate { get; set; } = "";
        public int? ExistingNodeId { get; set; }  // If set, replace this specific node; if null, create new
    }

    public class SpringskytteStafettResultRequest
    {
        public int CompetitionId { get; set; }
        public int TeamId { get; set; }
        public decimal? ElapsedSeconds { get; set; }
        public string? ElapsedInput { get; set; }      // "MM:SS" or "H:MM:SS"
        public string? FinishTimeInput { get; set; }   // "HH:MM:SS" — elapsed = finish − common start
        public int? PenaltyLoops { get; set; }
        public string? Status { get; set; }            // null, "DNS", "DNF"
    }

    public class SpringskytteStafettResultResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? ResultId { get; set; }
        public decimal? ElapsedSeconds { get; set; }
        public string ElapsedTimeDisplay { get; set; } = "";
    }

    public class SpringskytteStafettDeleteResultRequest
    {
        public int CompetitionId { get; set; }
        public int TeamId { get; set; }
    }

    public class SpringskytteStafettPublishRequest
    {
        public int CompetitionId { get; set; }
        public bool IsOfficial { get; set; }
    }
}
