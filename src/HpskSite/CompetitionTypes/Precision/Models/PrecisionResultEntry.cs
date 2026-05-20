using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Precision competition result entry - IDENTITY-BASED SYSTEM
    ///
    /// Results are stored by MEMBER, not by position. This allows:
    /// - Start lists to be regenerated without losing results
    /// - Late registrations after results entry has started
    /// - Shooters to move between teams/positions
    ///
    /// UNIQUE CONSTRAINT: (CompetitionId, MemberId, ShootingClass, SeriesNumber)
    /// This ensures one result per shooter per class per series (supports multi-class shooters).
    ///
    /// TeamNumber and Position are INFORMATIONAL ONLY - they reflect the shooter's
    /// position at the time of result entry, but results are looked up by MemberId.
    /// </summary>
    [TableName("PrecisionResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PrecisionResultEntry
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int SeriesNumber { get; set; }  // 1-10 (typically)

        /// <summary>
        /// IDENTITY FIELD - Primary lookup for results
        /// Results belong to the SHOOTER, not their position
        /// </summary>
        [Required]
        public int MemberId { get; set; }

        /// <summary>
        /// INFORMATIONAL - Team number at time of result entry
        /// Used for display/reference, NOT for lookups
        /// </summary>
        [Required]
        public int TeamNumber { get; set; }

        /// <summary>
        /// INFORMATIONAL - Position within team at time of result entry
        /// Used for display/reference, NOT for lookups
        /// </summary>
        [Required]
        public int Position { get; set; }

        [Required]
        [MaxLength(50)]
        public string ShootingClass { get; set; } = "";

        [Required]
        [MaxLength(50)] // JSON array of 5 shots: ["X","10","9","8","7"]
        public string Shots { get; set; } = "";

        [Required]
        public int EnteredBy { get; set; } // MemberId of range officer

        public DateTime EnteredAt { get; set; } = DateTime.Now;

        public DateTime LastModified { get; set; } = DateTime.Now;

        // Navigation properties (if using EF Core)
        // public Competition Competition { get; set; }
        // public Member Member { get; set; }
        // public Member EnteredByMember { get; set; }
    }

    // Request/Response models for API
    public class PrecisionResultEntryRequest
    {
        public int CompetitionId { get; set; }
        public int SeriesNumber { get; set; }
        public int TeamNumber { get; set; }
        public int Position { get; set; }
        public string[] Shots { get; set; } = new string[5];
        public int RangeOfficerId { get; set; }
        public int ShooterMemberId { get; set; } // Added to store validated shooter MemberId
        public string ShooterClass { get; set; } = ""; // Added to store validated shooting class
    }

    public class PrecisionResultEntryResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? ResultId { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
    }

    public class PrecisionDeleteResultRequest
    {
        public int CompetitionId { get; set; }
        public int SeriesNumber { get; set; }
        public int TeamNumber { get; set; }  // Informational only (for backwards compatibility)
        public int Position { get; set; }     // Informational only (for backwards compatibility)
        public int MemberId { get; set; }     // Identity field for delete (required)
        public string ShootingClass { get; set; } = "";  // Required for multi-class shooters
    }

    public class PrecisionResultUpdate
    {
        public int CompetitionId { get; set; }
        public int TeamNumber { get; set; }
        public int Position { get; set; }
        public int SeriesNumber { get; set; }
        public int MemberId { get; set; }
        public string Shots { get; set; } = "";
        public int Total { get; set; }
        public int XCount { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
    }

    // New simplified models for final results
    public class PrecisionShooterResult
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public List<PrecisionResultEntry> Results { get; set; } = new();

        // Standard Medal Award (Standardmedalj): null/""/B/S
        public string? StandardMedal { get; set; }

        /// <summary>Cumulative shoot-off total across all rounds the shooter participated in. Null when no shoot-off.</summary>
        public int? ShootOffScore { get; set; }

        /// <summary>Cumulative shoot-off X-count. Display-only; does not influence placement. Null when no shoot-off.</summary>
        public int? ShootOffXCount { get; set; }

        /// <summary>Highest round number the shooter has a shoot-off entry for. Null when no shoot-off.</summary>
        public int? ShootOffRound { get; set; }

        /// <summary>Per-round shoot-off totals in chronological order (round 1 first). Null/empty
        /// when the shooter has no shoot-off entries. The qualification TotalScore is unchanged;
        /// the shoot-off scores are shown separately to make placement decisions transparent.</summary>
        public List<int>? ShootOffRoundTotals { get; set; }

        /// <summary>True when this shooter's placement is uniquely decided by the rounds shot so far.
        /// Only meaningful for shooters inside a tied medal group.</summary>
        public bool ShootOffIsResolved { get; set; }

        /// <summary>The next round this shooter needs to shoot to break a remaining tie.
        /// Null when their placement is already decided OR when they're waiting for other tied
        /// shooters to enter the current round.</summary>
        public int? ShootOffNextRound { get; set; }

        // Calculated properties
        public int TotalScore => Results.Sum(r => CalculateTotalFromShots(r.Shots));
        public int TotalXCount => Results.Sum(r => CalculateXCountFromShots(r.Shots));
        public int SeriesCount => Results.Count;
        
        private static int CalculateTotalFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                return shots.Sum(shot => shot.ToUpper() == "X" ? 10 : (int.TryParse(shot, out int value) ? value : 0));
            }
            catch
            {
                return 0;
            }
        }
        
        private static int CalculateXCountFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                return shots.Count(shot => shot.ToUpper() == "X");
            }
            catch
            {
                return 0;
            }
        }
    }

    public class PrecisionClassGroup
    {
        /// <summary>Auto-generated class name — either the original class (when not merged) or the
        /// combined name produced by <see cref="ClassMergingService"/> (e.g. "C2+Dam+Vet").
        /// Stable as long as the merge config doesn't change. Used as the lookup key for
        /// custom-name overrides.</summary>
        public string ClassName { get; set; } = "";

        /// <summary>Admin-set custom name shown to the public. Null when no override is set —
        /// the view then falls back to <see cref="ClassName"/>.</summary>
        public string? DisplayClassName { get; set; }

        public List<PrecisionShooterResult> Shooters { get; set; } = new();

        /// <summary>Tied medal-tier groups currently unresolved (Resolved=false) or resolved (Resolved=true) by Särskjutning. Empty for non-championship competitions and for classes with no medal-tier ties.</summary>
        public List<PrecisionTiedMedalGroup> TiedMedalGroups { get; set; } = new();

        /// <summary>Human-readable footnotes appended below the class table on the public result page (e.g. "Särskjutning avgjorde guldet: Anna 50 vs Berit 47").</summary>
        public List<string> ShootOffNotes { get; set; } = new();
    }

    public class PrecisionTiedMedalGroup
    {
        public string MedalTier { get; set; } = "";    // "Guld" / "Silver" / "Brons"
        public int FirstRank { get; set; }
        public int LastRank { get; set; }
        public int TotalScore { get; set; }
        public int RoundsCompleted { get; set; }
        public bool Resolved { get; set; }
        public List<PrecisionTiedMedalShooter> Shooters { get; set; } = new();
    }

    public class PrecisionTiedMedalShooter
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public int TotalScore { get; set; }
        public int XCount { get; set; }

        /// <summary>True when this shooter's placement is uniquely decided by the rounds shot so far.</summary>
        public bool IsResolved { get; set; }

        /// <summary>The next round this shooter must shoot. Null when:
        /// (a) IsResolved=true — their placement is decided, or
        /// (b) they have already shot the current round but tied opponents have not — they're waiting.</summary>
        public int? NextRound { get; set; }

        /// <summary>Per-round entered totals so the admin UI can show progress (round 1 -> 50, round 2 -> 48, etc.).</summary>
        public List<PrecisionShootOffRoundEntry> Rounds { get; set; } = new();
    }

    public class PrecisionShootOffRoundEntry
    {
        public int Round { get; set; }
        public string Shots { get; set; } = "";   // JSON: ["X","10",...]
        public int Total { get; set; }
        public int XCount { get; set; }
    }

    public class PrecisionFinalResults
    {
        public int CompetitionId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsOfficial { get; set; } = true;
        public List<PrecisionClassGroup> ClassGroups { get; set; } = new();
    }

    /// <summary>
    /// Shooter information for results entry - loaded from registrations
    /// Allows results entry to work without a start list
    /// </summary>
    public class ShooterEntryInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";

        // For start list ordering (optional - only populated when orderBy=startlist)
        public int? TeamNumber { get; set; }
        public int? Position { get; set; }
        public string? StartTime { get; set; }
    }

    /// <summary>
    /// Response for GetShootersForResultsEntry endpoint
    /// </summary>
    public class ShootersForResultsEntryResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public bool HasStartList { get; set; }
        public string OrderBy { get; set; } = "registration";
        public List<ShooterEntryInfo> Shooters { get; set; } = new();
    }

    /// <summary>
    /// Request model for distributed (self-reporting) result entry.
    /// Used by club admins/skjutledare at the range to report results for their shooters.
    /// </summary>
    public class DistributedResultRequest
    {
        public int CompetitionId { get; set; }
        public int SeriesNumber { get; set; }
        public string[] Shots { get; set; } = new string[5];
        public string ShootingClass { get; set; } = "";
        public int TargetMemberId { get; set; }
    }

    /// <summary>
    /// Response for GetDistributedStatus — returns members the caller can enter for
    /// and their already-saved series.
    /// </summary>
    public class DistributedStatusResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public bool IsActive { get; set; }
        public int MaxSeries { get; set; }
        public List<DistributedMemberStatus> Members { get; set; } = new();
        public List<AvailableClass> AvailableClasses { get; set; } = new();
        public List<AuthorizedClub> AuthorizedClubs { get; set; } = new();
        public int CallerClubId { get; set; }
    }

    public class AvailableClass
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class AuthorizedClub
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class DistributedMemberStatus
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public List<DistributedSeriesStatus> CompletedSeries { get; set; } = new();
    }

    public class DistributedSeriesStatus
    {
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
        public string[] Shots { get; set; } = Array.Empty<string>();
        public string EnteredByName { get; set; } = "";
    }

    /// <summary>
    /// Request model for quick-registering a new shooter from the distributed result entry modal.
    /// </summary>
    public class QuickRegisterRequest
    {
        public int CompetitionId { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public int ClubId { get; set; }
        public string ShootingClass { get; set; } = "";
    }
}
