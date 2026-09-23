namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Verifikationslistan och huvudboken — de två handlingar en revisor faktiskt bläddrar i.
    ///
    /// <para><b>⚠️⚠️ SAMMA DATA, TVÅ FRÅGOR, OCH DE ÄR INTE UTBYTBARA.</b>
    /// Verifikations<i>listan</i> svarar "vad hände, i nummerordning" — den följer
    /// registreringen och är luckfri, vilket är det första en revisor kontrollerar.
    /// <i>Huvudboken</i> svarar "vad hände på det här kontot, i datumordning, med löpande
    /// saldo" — det är den man går till när en post ser fel ut. Att bygga bara den ena tvingar
    /// revisorn att räkna för hand.</para>
    ///
    /// <para><b>⚠️ Listan är läsvyer, inte lagring.</b> Ingenting här får skrivas tillbaka;
    /// verifikationen är oföränderlig och bevakas av en databastrigger.</para>
    /// </summary>
    public class LedgerJournalPage
    {
        public List<LedgerJournalRow> Rows { get; } = new();

        /// <summary>Totala antalet träffar, inte sidans. Revisorn måste se att listan är komplett.</summary>
        public int TotalCount { get; set; }

        public int Skip { get; set; }

        public int Take { get; set; }

        /// <summary>
        /// Lägsta och högsta verifikationsnummer i urvalet, per serie.
        ///
        /// <para><b>⚠️ Finns för LUCKKONTROLLEN.</b> Numreringen är luckfri per konstruktion
        /// (<c>LedgerNumberAllocator</c> räknar i samma transaktion som insert:en), och det
        /// påståendet är värdelöst om det inte går att pröva. Se <see cref="HasGaps"/>.</para>
        /// </summary>
        public List<LedgerSeriesSpan> Series { get; } = new();

        /// <summary>
        /// Sant när något nummer saknas i en serie inom hela året.
        ///
        /// <para><b>⚠️ Räknas på HELA serien, aldrig på den filtrerade sidan</b> — ett filter tar
        /// naturligtvis bort nummer, och att kalla det en lucka vore falskt alarm på varje sökning.</para>
        /// </summary>
        public bool HasGaps => Series.Any(s => s.HasGap);
    }

    /// <summary>Numrens omfång i en serie, och om något fattas.</summary>
    public class LedgerSeriesSpan
    {
        public string Prefix { get; set; } = "";

        public int First { get; set; }

        public int Last { get; set; }

        /// <summary>Hur många verifikationer serien faktiskt bär i året.</summary>
        public int Count { get; set; }

        /// <summary>Förväntat antal om numreringen är luckfri.</summary>
        public int Expected => Last >= First ? Last - First + 1 : 0;

        public bool HasGap => Count != Expected;
    }

    /// <summary>En rad i verifikationslistan.</summary>
    public class LedgerJournalRow
    {
        public int EntryId { get; set; }

        /// <summary>Formaterat verifikationsnummer, <c>V-14</c>.</summary>
        public string Number { get; set; } = "";

        /// <summary>Sorteringsnyckeln bakom numret. Sandlådans Id räknar nedåt — numret gör inte det.</summary>
        public int NumberValue { get; set; }

        public DateTime AccountingDate { get; set; }

        public DateTime EventDate { get; set; }

        public string Description { get; set; } = "";

        public string Counterparty { get; set; } = "";

        /// <summary>Omslutningen — summan av debetsidan. Se <see cref="LedgerJournalRow"/>-parets kommentar i JournalRow.</summary>
        public decimal Amount { get; set; }

        /// <summary>Antal bilagor som INTE är makulerade.</summary>
        public int AttachmentCount { get; set; }

        /// <summary>
        /// Hur många av verifikationens rader som har en motpart i ett inläst kontoutdrag.
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR REVISORNS TREDJE LED.</b> Skatteverket kräver att kedjan
        /// <i>bokförd transaktion → verifikation → faktisk betalning</i> går att följa åt båda
        /// hållen. Bilagan är led två, den här siffran är led tre — och utan den syns kopplingen
        /// bara för den som öppnar avstämningen och letar.</para>
        /// </summary>
        public int BankMatchedLines { get; set; }

        /// <summary>Verifikationen den här rättar, när den är en rättelse.</summary>
        public int? CorrectsEntryId { get; set; }

        public string SourceType { get; set; } = "";

        /// <summary>Sant när posten skrevs av en människa, falskt när den föll ut ur en avgift.</summary>
        public bool IsManual => SourceType == LedgerSourceType.Manual;
    }

    /// <summary>En verifikation med allt som hänger på den — det revisorn öppnar.</summary>
    public class LedgerJournalDetail
    {
        public LedgerJournalRow Head { get; set; } = new();

        /// <summary>
        /// Rättelsen som tar ut den här posten, om någon finns. ⚠️ Visas i detaljen: en rättad post
        /// som ser ut som vilken post som helst läses som att felet står kvar, och den som letar
        /// bokför om den en gång till.
        /// </summary>
        public int? CorrectedByEntryId { get; set; }
        public string? CorrectedByNumber { get; set; }

        /// <summary>Numret på posten den här rättar, när den själv är en rättelse.</summary>
        public string? CorrectsNumber { get; set; }

        public List<LedgerJournalEntryLine> Lines { get; } = new();

        public List<LedgerAttachmentView> Attachments { get; } = new();

        /// <summary>Kontoutdragsrader som pekar på någon av verifikationens rader.</summary>
        public List<LedgerBankMatchView> BankRows { get; } = new();

        public decimal DebitTotal => Lines.Sum(l => l.Debit);

        public decimal CreditTotal => Lines.Sum(l => l.Credit);

        /// <summary>
        /// ⚠️ Visas ALLTID, även när den är sann. En revisor ska kunna se att verifikationen går
        /// ihop utan att addera i huvudet — det är den enklaste kontrollen som finns och den
        /// enda som kan avslöja en trasig import.
        /// </summary>
        public bool Balances => DebitTotal == CreditTotal;
    }

    /// <summary>En bilaga så som ytan visar den. Bär aldrig sökvägen.</summary>
    public class LedgerAttachmentView
    {
        public int Id { get; set; }

        public string FileName { get; set; } = "";

        public string ContentType { get; set; } = "";

        public long SizeBytes { get; set; }

        public DateTime UploadedUtc { get; set; }

        public string UploadedByName { get; set; } = "";

        public bool IsVoided { get; set; }

        public string? VoidReason { get; set; }

        /// <summary>Kan visas inbäddad i stället för att laddas ner.</summary>
        public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
    }

    /// <summary>En kontoutdragsrad som matchats mot verifikationen.</summary>
    public class LedgerBankMatchView
    {
        public DateTime BookedDate { get; set; }

        public string Text { get; set; } = "";

        public decimal Amount { get; set; }

        public string? Reference { get; set; }

        /// <summary>Hur matchningen gjordes — automatiskt entydigt, eller av en människa.</summary>
        public string? MatchKind { get; set; }

        public string MatchedByName { get; set; } = "";

        public DateTime? MatchedUtc { get; set; }
    }

    /// <summary>Huvudboken för ett konto: raderna i datumordning med löpande saldo.</summary>
    public class LedgerAccountLedger
    {
        public int AccountNumber { get; set; }

        public string AccountName { get; set; } = "";

        /// <summary>
        /// Saldot vid periodens början.
        /// <para><b>⚠️ Räknas ur ALLT före perioden, inte ur årets rader.</b> Ett ingående saldo
        /// som bara ser innevarande år är noll för varje förening som funnits längre — samma
        /// urvalsfälla som balansräkningen redan bär en varning om.</para>
        /// </summary>
        public decimal OpeningBalance { get; set; }

        public List<LedgerAccountLedgerRow> Rows { get; } = new();

        public decimal ClosingBalance => Rows.Count == 0 ? OpeningBalance : Rows[^1].Balance;

        public decimal DebitTotal => Rows.Sum(r => r.Debit);

        public decimal CreditTotal => Rows.Sum(r => r.Credit);
    }

    /// <summary>En rad i huvudboken.</summary>
    public class LedgerAccountLedgerRow
    {
        public int EntryId { get; set; }

        public string Number { get; set; } = "";

        public DateTime AccountingDate { get; set; }

        public string Text { get; set; } = "";

        public string Counterparty { get; set; } = "";

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        /// <summary>Löpande saldo i kontots egen riktning — se <c>LedgerAccountClass</c>.</summary>
        public decimal Balance { get; set; }
    }
}
