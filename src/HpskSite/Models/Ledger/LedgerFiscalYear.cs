using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ett räkenskapsår per utställare. Bär periodlåsningen och de ingående balanserna.
    ///
    /// <para><b>⚠️ <see cref="Status"/> är ett TRELÄGES-fält, inte en boolean — och det är med
    /// flit.</b> Frågan om vad låset ska släppa igenom efter fastställt bokslut (F3) är den enda
    /// kvarvarande enkelriktade frågan och är fortfarande obesvarad: bokförs en rättelse som
    /// upptäcks efteråt i det GAMLA året, som då måste kunna öppnas, eller i det innevarande? Den
    /// behövs först vid bokslutssteget. Det schemat måste göra nu är att inte omöjliggöra något av
    /// svaren — därför ett statusfält där ett <c>reopened</c> kan läggas till utan att levande
    /// pengadata konverteras.</para>
    ///
    /// <para><b>Ingående balans år N+1 låses till utgående balans år N</b> vid fastställandet, och
    /// räknas inte om vid läsning. Annars kan systemet och den balansräkning årsmötet fastställde
    /// säga olika saker utan att någon kan avgöra vilket som gäller.</para>
    ///
    /// <para><b>Brutet räkenskapsår antas inte bort</b> — därför egna start- och slutdatum i stället
    /// för att härleda dem ur <see cref="Year"/>.</para>
    /// </summary>
    [TableName("LedgerFiscalYear")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerFiscalYear
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public int Year { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        /// <summary>Ur <see cref="LedgerFiscalYearStatus"/>.</summary>
        public string Status { get; set; } = LedgerFiscalYearStatus.Open;

        /// <summary>När årsmötet fastställde resultat- och balansräkningen.</summary>
        public DateTime? EstablishedDate { get; set; }

        public int? EstablishedByMemberId { get; set; }
    }

    /// <summary>
    /// Årets tillstånd. <b>Bara <see cref="Established"/> spärrar skrivning</b> — spärren ligger i
    /// databastriggern, inte i koden.
    /// </summary>
    public static class LedgerFiscalYearStatus
    {
        /// <summary>Löpande bokföring pågår.</summary>
        public const string Open = "open";

        /// <summary>
        /// Bokslutsarbetet är gjort men årsmötet har inte fastställt. Skrivning tillåten —
        /// det är fortfarande kassörens år.
        /// </summary>
        public const string Closing = "closing";

        /// <summary>
        /// Årsmötet har fastställt. Året är fryst.
        /// <para>⚠️ Vad låset släpper igenom härifrån är F3, fortfarande obesvarad.</para>
        /// </summary>
        public const string Established = "established";
    }
}
