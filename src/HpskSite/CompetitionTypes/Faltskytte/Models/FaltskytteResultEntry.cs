using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    [TableName("FaltskytteResultEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskytteResultEntry
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int StationNumber { get; set; }

        [Required]
        public int MemberId { get; set; }

        [Required]
        public int PatrolNumber { get; set; }

        [Required]
        public string ShootingClass { get; set; } = "";

        /// <summary>Total hits at this station (0-6)</summary>
        public int Hits { get; set; }

        /// <summary>Number of distinct figures hit</summary>
        public int Figures { get; set; }

        /// <summary>JSON array of hits per figure, e.g. ["3","2","1"]</summary>
        public string? HitDistribution { get; set; }

        /// <summary>Score from poångmål (ringed figures) for tiebreaking. Null if station has no poångmål.</summary>
        public int? TiebreakerScore { get; set; }

        /// <summary>JSON array of individual poångmål scores, e.g. [24,20]. Null if no poångmål.</summary>
        public string? PoangmalScores { get; set; }

        /// <summary>Number of re-shoots (malfunction) used at this station</summary>
        public int Reshoots { get; set; }

        public int EnteredBy { get; set; }
        public DateTime EnteredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }
}
