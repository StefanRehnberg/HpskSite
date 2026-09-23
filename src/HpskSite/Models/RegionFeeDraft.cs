using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Kretsens justering för en klubb INNAN avgiften skickats: ett rättat medlemsantal och/eller ett
    /// eget belopp. En rad per (krets, år, klubb).
    ///
    /// <para><b>⚠️ Justeringen måste sparas på servern.</b> Den låg först bara i webbläsaren och
    /// försvann vid årsbyte, omladdning och för varje annan person i kretsen — medan skärmen såg ut
    /// som om talet var sparat. Rapporterat 2026-09-23.</para>
    ///
    /// <para><b>Null betyder "ingen justering"</b>: medlemsantalet följer då registret och beloppet
    /// taxan. Ett värde som är LIKA med registret eller taxan sparas som null, så en rad inte blir
    /// märkt "ändrad" av att någon klickade och tryckte Enter. Båda null = raden tas bort.</para>
    ///
    /// <para>Raden raderas när avgiften skapas — från det ögonblicket är det avgiften som gäller.</para>
    /// </summary>
    [TableName("RegionFeeDraft")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RegionFeeDraft
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int Year { get; set; }
        public int ClubId { get; set; }
        public int? MemberCount { get; set; }
        public decimal? ManualAmount { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int UpdatedByMemberId { get; set; }
    }
}
