using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// En tävling som ingår i kretsens avgift för ett avgiftsår — underlaget för raderna
    /// "per start" och "per tävling" (Hallands startavgifter och lagavgift).
    /// </summary>
    [TableName("RegionFeeCompetition")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RegionFeeCompetition
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int Year { get; set; }
        public int CompetitionId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public int CreatedByMemberId { get; set; }
    }

    /// <summary>
    /// Kretsens taxa för ett år. Alla fyra delarna kan vara noll; en del som är noll ger ingen rad.
    /// Etiketterna är kretsens egna namn på raderna (Halland: "Startavgifter" och "Lagavgift") —
    /// klubbens kassör känner igen sin räkning på dem.
    /// </summary>
    public record RegionFeeRate(
        decimal BaseAmount,
        decimal PerMember,
        decimal PerStart = 0m,
        decimal PerCompetition = 0m,
        string StartLabel = RegionFeeRate.DefaultStartLabel,
        string CompetitionLabel = RegionFeeRate.DefaultCompetitionLabel)
    {
        public const string DefaultStartLabel = "Startavgifter";
        public const string DefaultCompetitionLabel = "Lagavgift";

        /// <summary>Taxan tar betalt för starter eller tävlingar — då behövs ett val av tävlingar.</summary>
        public bool UsesCompetitions => PerStart > 0 || PerCompetition > 0;

        public bool IsEmpty => BaseAmount <= 0 && PerMember <= 0 && !UsesCompetitions;
    }

    /// <summary>En klubbs räknade starter i kretsens valda tävlingar.</summary>
    public class RegionClubStarts
    {
        public int Starts { get; set; }

        /// <summary>Antalet valda tävlingar där klubben hade minst en start.</summary>
        public int Competitions { get; set; }

        /// <summary>Underlaget: tävlings-id → antal starter.</summary>
        public Dictionary<int, int> ByCompetition { get; } = new();
    }
}
