using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// One Särskjutning (shoot-off) round result for a single shooter in a Fältskytte
    /// competition. Used by all three variations (Normal, Poäng, Magnumfält).
    ///
    /// Identity-based — keyed by (CompetitionId, MemberId, ShootingClass, Round) so
    /// start-list / class regeneration cannot orphan entries. Round 2+ exists only
    /// when prior rounds failed to separate the tied shooters.
    ///
    /// Variation-specific column usage:
    /// - Normal / Poäng: Hits + Figures + (optional) PoangmalScores/TiebreakerScore.
    /// - Magnumfält: only PoangmalScores + TiebreakerScore (one hit per figure max).
    /// </summary>
    [TableName("FaltskytteShootOffEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskytteShootOffEntry
    {
        public int Id { get; set; }

        [Required]
        public int CompetitionId { get; set; }

        [Required]
        public int MemberId { get; set; }

        [Required]
        public string ShootingClass { get; set; } = "";

        /// <summary>Round number, 1-based. Round 2+ only exists when prior rounds left ties unresolved.</summary>
        [Required]
        public int Round { get; set; }

        /// <summary>Total hits in this round (Normal/Poäng only). Null for Magnum.</summary>
        public int? Hits { get; set; }

        /// <summary>Number of distinct figures hit (Normal/Poäng only). Null for Magnum.</summary>
        public int? Figures { get; set; }

        /// <summary>JSON array of hits per figure (Normal/Poäng), e.g. ["3","2","1"].</summary>
        public string? HitDistribution { get; set; }

        /// <summary>Poängmål aggregate (sum of PoangmalScores). For Normal/Poäng this is the
        /// tiebreaker after Hits/Figures; for Magnum this IS the round score.</summary>
        public int? TiebreakerScore { get; set; }

        /// <summary>JSON array of individual poängmål scores, e.g. [5,4,0,3,5,2].</summary>
        public string? PoangmalScores { get; set; }

        public int EnteredBy { get; set; }
        public DateTime EnteredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }
}
