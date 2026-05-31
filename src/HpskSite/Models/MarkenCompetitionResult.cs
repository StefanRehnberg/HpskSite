using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A self-reported external competition result submitted as evidence toward a competition-driven
    /// märke (Precision / Fält / Milsnabb / Nationell helmatch). The shooter enters the competition +
    /// their total; a functionary validates it (same queue/QR as series). Results from hosted
    /// pistol.nu competitions are NOT stored here — they're harvested live from the discipline result
    /// tables (always validated). Only Verified rows count toward a märke.
    /// </summary>
    [TableName("MarkenCompetitionResult")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MarkenCompetitionResult
    {
        public int Id { get; set; }
        public int MemberId { get; set; }

        /// <summary>Club chosen for validation — scopes the queue / QR-verify authority.</summary>
        public int ClubId { get; set; }

        /// <summary>See <see cref="MarkenFamilies"/> keys (Precision/Falt/Milsnabb/NationellHelmatch).</summary>
        public string BadgeFamily { get; set; } = "";

        public int Year { get; set; }
        public DateTime CompetitionDate { get; set; }
        public string CompetitionName { get; set; } = "";
        public string? Location { get; set; }

        /// <summary>Weapon group: A / B / C / R.</summary>
        public string WeaponGroup { get; set; } = "";

        /// <summary>Series count (precision-shape) or station count (Fält). 0 = dimension-independent.</summary>
        public int Dim { get; set; }

        /// <summary>Points total (precision-shape) or hit count (Fält).</summary>
        public int Total { get; set; }

        /// <summary>The valör this result reaches, computed from the family table at entry/validation.</summary>
        public string? ReachedLevel { get; set; }

        /// <summary>'Pending' | 'Verified' | 'Rejected'.</summary>
        public string Status { get; set; } = Marken.SeriesStatusPending;

        public int? ValidatedByMemberId { get; set; }
        public DateTime? ValidatedDate { get; set; }

        public string? ProofFileRef { get; set; }
        public string? Notes { get; set; }

        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
