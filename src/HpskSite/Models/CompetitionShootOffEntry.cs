using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Stores Särskjutning (shoot-off) shots used to resolve tied medal positions
    /// (1–3) in Championship competitions for precision-family disciplines.
    ///
    /// Identity-based: keyed by (CompetitionId, MemberId, ShootingClass, Round, SeriesNumber)
    /// so start-list or class regeneration cannot orphan entered shoot-off scores.
    ///
    /// A "round" is typically a single 5-shot series. Round 2+ exists only when the
    /// previous round did not separate the tied shooters.
    /// </summary>
    [TableName("CompetitionShootOffEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionShootOffEntry
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int Round { get; set; }
        public int SeriesNumber { get; set; } = 1;

        /// <summary>JSON: ["X","10","9","8","7"]. Identical format to result-entry tables.</summary>
        public string Shots { get; set; } = "";

        public int EnteredBy { get; set; }
        public DateTime EnteredAt { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
