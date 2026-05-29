using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One Standard medal (Standardmedalj) won by a member. Silver = 2 p, Brons = 1 p.
    /// The durable system of record — on-site results compute medals at read-time, but
    /// reporting to SPSF and Guldmedalj accounting both need a stable ledger.
    /// </summary>
    [TableName("StandardMedalAward")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StandardMedalAward
    {
        public int Id { get; set; }

        public int MemberId { get; set; }

        /// <summary>Season = calendar year of the competition.</summary>
        public int Year { get; set; }

        /// <summary>See <see cref="StandardMedals"/> discipline constants.</summary>
        public string Discipline { get; set; } = "";

        /// <summary>'S' (Silver) or 'B' (Brons). Matches the existing TrainingScores convention.</summary>
        public string MedalType { get; set; } = "";

        /// <summary>2 for Silver, 1 for Brons. Denormalized from <see cref="MedalType"/> for safety.</summary>
        public int Points { get; set; }

        /// <summary>'OnSite' | 'SelfReported' | 'AdminEntered'.</summary>
        public string Source { get; set; } = "";

        /// <summary>Set for Source = 'OnSite' — links to our competition node (the result page is the proof).</summary>
        public int? CompetitionId { get; set; }

        public string? CompetitionName { get; set; }
        public DateTime? CompetitionDate { get; set; }
        public string? Location { get; set; }
        public string? ShootingClass { get; set; }

        /// <summary>'File' | 'OnSite' | 'Attestation' | null.</summary>
        public string? ProofType { get; set; }

        /// <summary>Opaque reference to a stored proof file, resolved by an authorized endpoint. NOT a public URL.</summary>
        public string? ProofFileRef { get; set; }

        /// <summary>'Reported' | 'Verified' | 'Rejected'.</summary>
        public string Status { get; set; } = StandardMedals.StatusReported;

        /// <summary>Set when an approved Guldmedalj application consumes this award's points.</summary>
        public int? GoldApplicationId { get; set; }

        /// <summary>Link to the self-entered TrainingScores row, so edits/deletes stay in sync.</summary>
        public int? TrainingScoreId { get; set; }

        public int? VerifiedByMemberId { get; set; }
        public DateTime? VerifiedAt { get; set; }
        public int EnteredByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A Guldmedalj application. Each approved application consumes 50 points pooled across
    /// ALL disciplines. <see cref="AwardIdsJson"/> snapshots the awards forming those 50 points
    /// so the club can attach the matching result lists to the SPSF application.
    /// </summary>
    [TableName("StandardMedalGoldApplication")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StandardMedalGoldApplication
    {
        public int Id { get; set; }
        public int MemberId { get; set; }

        /// <summary>Member's primary club at application time.</summary>
        public int ClubId { get; set; }

        /// <summary>1st gold, 2nd gold, ... per member.</summary>
        public int SequenceNumber { get; set; }

        /// <summary>'Draft' | 'Applied' | 'Approved' | 'Rejected'.</summary>
        public string Status { get; set; } = StandardMedals.GoldStatusDraft;

        public int PointsConsumed { get; set; } = StandardMedals.GoldThreshold;

        /// <summary>JSON array of <see cref="StandardMedalAward.Id"/> forming the 50 points (proof bundle).</summary>
        public string? AwardIdsJson { get; set; }

        public string? Notes { get; set; }

        public int AppliedByMemberId { get; set; }
        public DateTime? AppliedAt { get; set; }
        public int? ApprovedByMemberId { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
