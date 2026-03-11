using System.ComponentModel.DataAnnotations;
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

        public int? ShootingScore { get; set; }  // Total penalty points

        public int PenaltyMultiplier { get; set; } = 1;  // 1 normal, 2 for markestagning (class C)

        public decimal? TotalTimeSeconds { get; set; }  // Sprint + (ShootingScore * Multiplier * 60)

        [MaxLength(10)]
        public string? Status { get; set; }  // null=normal, "DNS", "DNF"

        [Required]
        public int EnteredBy { get; set; }

        public DateTime EnteredAt { get; set; } = DateTime.Now;

        public DateTime LastModified { get; set; } = DateTime.Now;
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

        // Shot details per stop/series
        public List<List<string>> ShotSeries { get; set; } = new();

        // Status
        public string? Status { get; set; }  // null, "DNS", "DNF"

        // Medal
        public string? StandardMedal { get; set; }

        // Calculated display properties
        public string SprintTimeDisplay => FormatTime(SprintTimeSeconds);
        public string PenaltyTimeDisplay => ShootingScore > 0
            ? FormatTime(ShootingScore * PenaltyMultiplier * 60m)
            : "0:00";
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
    /// Configuration for Springskytte start list generation.
    /// </summary>
    public class SpringskytteStartListConfig
    {
        public string FirstStartTime { get; set; } = "10:00";
        public string DefaultInterval { get; set; } = "01:00";  // MM:SS between starts
        public int BreakAfterEvery { get; set; } = 10;  // Long break after N starters
        public string BreakDuration { get; set; } = "05:00";  // MM:SS for long break
        public string ListName { get; set; } = "";  // User-assigned label (e.g., "Vapengrupp A")
        public List<string> CoveredClasses { get; set; } = new();  // Registration class patterns (e.g., ["A-D 21","A-H 35"])
        public List<SpringskytteStartListEntry> Starters { get; set; } = new();
    }

    public class SpringskytteStartListRequest
    {
        public int CompetitionId { get; set; }
        public string FirstStartTime { get; set; } = "10:00";
        public string DefaultInterval { get; set; } = "01:00";
        public int BreakAfterEvery { get; set; } = 10;
        public string BreakDuration { get; set; } = "05:00";
        public List<string> CoveredClasses { get; set; } = new();  // Which classes to include (empty = all)
        public string ListName { get; set; } = "";  // Name for this list
        public int? ExistingNodeId { get; set; }  // If set, replace this specific node; if null, create new
    }

    public class SpringskytteDeleteStartListRequest
    {
        public int CompetitionId { get; set; }
        public int NodeId { get; set; }
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
