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
    /// UNIQUE CONSTRAINT: (CompetitionId, MemberId, SeriesNumber)
    /// This ensures one result per shooter per series, regardless of position changes.
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

    public class PrecisionResultEntrySession
    {
        public int Id { get; set; }
        
        [Required]
        public int CompetitionId { get; set; }
        
        [Required]
        public int Position { get; set; }
        
        [Required]
        public int SeriesNumber { get; set; }
        
        [Required]
        public int RangeOfficerId { get; set; }
        
        public DateTime SessionStart { get; set; } = DateTime.Now;
        
        public DateTime LastActivity { get; set; } = DateTime.Now;
        
        public bool IsActive { get; set; } = true;
        
        // Navigation properties (if using EF Core)
        // public Competition Competition { get; set; }
        // public Member RangeOfficer { get; set; }
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

    public class PrecisionSessionRequest
    {
        public int CompetitionId { get; set; }
        public int TeamNumber { get; set; }
        public int Position { get; set; }
        public int SeriesNumber { get; set; }
        public int RangeOfficerId { get; set; }
    }

    public class PrecisionSessionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? SessionId { get; set; }
        public bool IsAvailable { get; set; }
    }

    public class PrecisionDeleteResultRequest
    {
        public int CompetitionId { get; set; }
        public int SeriesNumber { get; set; }
        public int TeamNumber { get; set; }  // Informational only (for backwards compatibility)
        public int Position { get; set; }     // Informational only (for backwards compatibility)
        public int MemberId { get; set; }     // Identity field for delete (required)
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
        public string ClassName { get; set; } = "";
        public List<PrecisionShooterResult> Shooters { get; set; } = new();
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
}
