using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En version av föreningens budget för ett räkenskapsår.
    ///
    /// <para><b>⚠️⚠️ BUDGETEN ÄR ETT BESLUT, INTE EN PROGNOS.</b> Skissens egna ord
    /// (<c>Ekonomi Exempel/Ekonomi_Wireframes.pdf</c>, sida 4): <i>"Den antogs på årsmötet 14 mars
    /// och kan inte skrivas om i efterhand — vill styrelsen ändra den blir ändringen ett eget,
    /// spårbart beslut."</i> Formen är därför densamma som verifikationsliggarens: en antagen
    /// budget är oföränderlig, och en revidering är en <b>ny rad</b> med nästa
    /// <see cref="Revision"/>. Version 1 står kvar och går att förklara för en revisor som undrar
    /// varför siffran ändrades i augusti.</para>
    ///
    /// <para><b>⚠️ Spärren ligger i databasen</b> (<c>TR_LedgerBudget_AdoptedIsFinal</c>), inte i
    /// koden. Ett <c>UPDATE</c> mot en antagen budget kastar 50130. Utan triggern är
    /// "kan inte skrivas om" en överenskommelse, och överenskommelser ruttnar tyst.</para>
    ///
    /// <para><b>Ett UTKAST går fritt att ändra</b> — <see cref="AdoptedDate"/> är null. Kassören
    /// skriver in tjugo kontorader och måste kunna rätta en felskrivning innan årsmötet. Ett
    /// filtrerat unikt index tillåter bara ETT utkast per räkenskapsår: två halvskrivna budgetar
    /// sida vid sida är inte en valmöjlighet, det är en fråga om vilken som gällde.</para>
    /// </summary>
    [TableName("LedgerBudget")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerBudget
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public int FiscalYearId { get; set; }

        /// <summary>1, 2, 3 … En revidering är en ny rad, aldrig en ändring av en gammal.</summary>
        public int Revision { get; set; } = 1;

        /// <summary>Null = utkast. Sätts en gång, och då fryser triggern raden.</summary>
        public DateTime? AdoptedDate { get; set; }

        /// <summary>Ur <see cref="LedgerBudgetAdoptedBy"/>.</summary>
        public string? AdoptedBody { get; set; }

        /// <summary>Kassörens egen formulering, t.ex. "Årsmötet 14 mars".</summary>
        public string? AdoptedNote { get; set; }

        public int? AdoptedByMemberId { get; set; }

        /// <summary>
        /// Mötet i styrelsemodulen, när beslutet finns där. <b>Frivilligt med flit</b> — en
        /// förening som inte för sina möten hos oss ska ändå kunna ha en budget.
        /// </summary>
        public int? BoardMeetingId { get; set; }

        public int CreatedByMemberId { get; set; }

        public DateTime CreatedUtc { get; set; }

        [Ignore]
        public bool IsAdopted => AdoptedDate is not null;
    }

    /// <summary>Vem som antog budgeten. Vem som FÅR göra det är föreningens sak.</summary>
    public static class LedgerBudgetAdoptedBy
    {
        public const string AnnualMeeting = "arsmote";
        public const string Board = "styrelse";

        public static string Label(string? value) => value switch
        {
            AnnualMeeting => "Årsmötet",
            Board => "Styrelsen",
            _ => "Beslut"
        };
    }

    /// <summary>
    /// Ett budgeterat belopp för ett konto.
    ///
    /// <para><b>⚠️ Beloppet anges i kontots EGEN riktning, alltid positivt:</b> en intäkt på
    /// 146 000 skrivs <c>146000</c>, en kostnad på 95 000 skrivs <c>95000</c>. Utfallet räknas
    /// likadant (klass 3 och 8: <c>Credit − Debit</c>, klass 4–7: <c>Debit − Credit</c>), så de två
    /// talen går att jämföra rakt av. Teckenlös budget mot tecknat utfall är ett sätt att få varje
    /// diff att peka åt fel håll.</para>
    /// </summary>
    [TableName("LedgerBudgetLine")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerBudgetLine
    {
        public int Id { get; set; }

        public int BudgetId { get; set; }

        public int AccountNumber { get; set; }

        /// <summary>
        /// Snapshot. Döper föreningen om kontot ska den antagna budgeten ändå läsa som den gjorde
        /// när den beslutades — samma regel som verifikationsradens <c>ProjectName</c>.
        /// </summary>
        public string AccountName { get; set; } = "";

        public decimal Amount { get; set; }
    }
}
