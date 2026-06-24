namespace HpskSite.Models.Ranking
{
    /// <summary>
    /// One persisted snapshot row — mirrors the RankingSnapshot table.
    /// Denormalised so the read path never loops member/club lookups.
    /// </summary>
    public class RankingSnapshotRow
    {
        public int Id { get; set; }
        public DateTime SnapshotDate { get; set; }
        public int MemberId { get; set; }
        public string Discipline { get; set; } = "Precision";
        public string WeaponGroup { get; set; } = "";
        public decimal HandicapIndex { get; set; }
        public bool IsProvisional { get; set; }
        public int SessionCount { get; set; }
        public string? ClubIds { get; set; }
        public string? RegionCodes { get; set; }
        public decimal? ImprovementDelta30 { get; set; }
        public decimal? ImprovementDeltaSeason { get; set; }
        public string? FullName { get; set; }
        public string? Initials { get; set; }
        public string? ClubName { get; set; }
        public string? AvatarUrl { get; set; }
        public string IdentityVisibility { get; set; } = "Full";
        public bool ShowClub { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Which board is being asked for.</summary>
    public enum RankingBoard
    {
        Index,          // Topplista — by handicap index, ascending (best first)
        Improvement30,  // Störst förbättring (rullande 30 dagar)
        ImprovementSeason // Störst förbättring (säsong / år)
    }

    /// <summary>One row as rendered for a specific viewer (identity already resolved).</summary>
    public class RankingEntry
    {
        public int Rank { get; set; }
        public int MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? ClubName { get; set; }
        public string? AvatarUrl { get; set; }
        public decimal HandicapIndex { get; set; }
        public decimal? ImprovementDelta { get; set; }
        public bool IsProvisional { get; set; }
        public int SessionCount { get; set; }
        public int? Movement { get; set; }   // previous rank - current rank (positive = climbed); null if no prior
        public bool IsYou { get; set; }
    }

    /// <summary>Full payload for a rendered board.</summary>
    public class RankingResult
    {
        public bool HasData { get; set; }
        public string? EmptyReason { get; set; }      // e.g. "För få deltagare i den här klassen än"
        public string Discipline { get; set; } = "Precision";
        public string WeaponGroup { get; set; } = "";
        public string Scope { get; set; } = "club";   // club | region | national
        public string? ScopeLabel { get; set; }
        public string Board { get; set; } = "index";
        public int TotalShooters { get; set; }
        public DateTime? SnapshotDate { get; set; }
        public List<RankingEntry> Entries { get; set; } = new();
        public RankingEntry? You { get; set; }         // the viewer's own row (may be outside the returned slice)
    }

    /// <summary>A (discipline, weapon group) combo that currently has snapshot rows + how many.</summary>
    public class ClassCombo
    {
        public string Discipline { get; set; } = "";
        public string WeaponGroup { get; set; } = "";
        public int Cnt { get; set; }
    }

    /// <summary>One discipline/weapon-group line on the Min sida private teaser.</summary>
    public class MyRankingLine
    {
        public string Discipline { get; set; } = "Precision";
        public string WeaponGroup { get; set; } = "";
        public decimal HandicapIndex { get; set; }
        public bool IsProvisional { get; set; }
        public int? ClubRank { get; set; }
        public int? ClubTotal { get; set; }
        public string? ClubName { get; set; }
        public int? ClubMovement { get; set; }
        public int? NationalRank { get; set; }
        public int? NationalTotal { get; set; }
        public decimal? ImprovementDelta30 { get; set; }
    }
}
