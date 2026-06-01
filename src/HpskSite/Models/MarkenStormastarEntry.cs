using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One championship result a shooter logs toward the <b>Stormästarmärket</b> (SHB 5.3) — career
    /// inteckningspoäng. The shooter enters scope (Krets/Landsdel/Svenskt), deltagarantal and placering;
    /// the points are computed from Tabell 2 (<see cref="Marken.StormastarPoints"/>) at entry. A
    /// functionary validates it (same queue/QR as series + self-reported comp results). Only Verified
    /// rows count toward the 30-point eligibility threshold. The award itself is a manual club→SPSF
    /// nomination with a meritförteckning — this just accumulates and documents the merits.
    /// </summary>
    [TableName("MarkenStormastarEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MarkenStormastarEntry
    {
        public int Id { get; set; }
        public int MemberId { get; set; }

        /// <summary>Club chosen for validation — scopes the queue / QR-verify authority.</summary>
        public int ClubId { get; set; }

        public int Year { get; set; }

        /// <summary>Championship level: see <see cref="Marken.SmScopeKrets"/>/Landsdel/Svenskt.</summary>
        public string Scope { get; set; } = "";

        /// <summary>Antal deltagare i vapengruppen/klassen — drives the Tabell 2 band.</summary>
        public int Participants { get; set; }

        /// <summary>Placering (1 = winner).</summary>
        public int Place { get; set; }

        /// <summary>Inteckningspoäng, computed from Tabell 2 at entry/validation.</summary>
        public int Points { get; set; }

        /// <summary>Optional discipline label (Precision/Fält/…) for the meritförteckning — not used in the point calc.</summary>
        public string? Discipline { get; set; }

        public string? CompetitionName { get; set; }
        public string? Notes { get; set; }

        /// <summary>'Pending' | 'Verified' | 'Rejected'.</summary>
        public string Status { get; set; } = Marken.SeriesStatusPending;

        public int? ValidatedByMemberId { get; set; }
        public DateTime? ValidatedDate { get; set; }

        public string? ProofFileRef { get; set; }

        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
