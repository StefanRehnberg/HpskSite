namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Avstämningen av ett konto mot ett uppladdat kontoutdrag.
    ///
    /// <para><b>⚠️⚠️ TVÅ SORTERS OMATCHAT BETYDER TVÅ OLIKA SAKER, och de får aldrig slås ihop
    /// till en siffra.</b> En <i>bankrad utan motpart</i> är något som hänt på kontot men inte
    /// bokförts — där saknas en verifikation. En <i>bokföringsrad utan motpart</i> är något vi
    /// bokfört som banken inte visar — där är antingen beloppet fel, datumet utanför perioden,
    /// eller pengarna aldrig kom. Åtgärden är olika, så listorna är två.</para>
    ///
    /// <para><b>⚠️ F6: gör det AVVIKANDE hittbart, underlaget ett klick bort</b> — inte allt lika
    /// tydligt. Kasper Aase: <i>"att medlem 1, 2 och 3 betalt sin årsavgift och swishat 40 kr
    /// varje månad för en intern tävling, det tittar jag sällan djupare på."</i> Det matchade är
    /// därför hopfällt; det omatchade ligger överst.</para>
    /// </summary>
    public class LedgerReconciliation
    {
        public int ImportId { get; set; }
        public int AccountNumber { get; set; }
        public string AccountName { get; set; } = "";
        public string FileName { get; set; } = "";
        public DateTime? PeriodFrom { get; set; }
        public DateTime? PeriodTo { get; set; }

        public int RowCount { get; set; }
        public int MatchedCount { get; set; }

        /// <summary>Bankrader utan motpart — hänt på kontot, inte bokfört.</summary>
        public List<BankRowView> BankOnly { get; } = new();

        /// <summary>
        /// Bokföringsrader utan motpart — bokfört, syns inte på kontot.
        ///
        /// <para><b>⚠️⚠️ BARA RADER INOM UTDRAGETS PERIOD.</b> En bokning från mars kan omöjligen
        /// finnas i ett utdrag för september, och att lista den här vore brus — F6 säger att
        /// värdet ligger i att göra det AVVIKANDE hittbart, inte i att visa allt.</para>
        ///
        /// <para>Arbetsfördelningen: <b>raderna</b> stäms av inom perioden, <b>saldot</b> bevisar
        /// allt före den. En gammal bokning som aldrig nådde banken syns alltså ändå — som en
        /// differens, vilket är den enda plats den ärligt kan synas i det här utdraget.</para>
        /// </summary>
        public List<LedgerLineView> LedgerOnly { get; } = new();

        /// <summary>De hopparade, i filens ordning. Hopfällt i ytan.</summary>
        public List<MatchedPair> Matched { get; } = new();

        /// <summary>
        /// Bankens slutsaldo ur filen, när den bar ett.
        /// <para><b>⚠️ Null betyder att filen inte sa något</b> — inte noll. Utan det kan ytan
        /// bara säga hur många rader som är omatchade, aldrig att kontot stämmer.</para>
        /// </summary>
        public decimal? BankClosingBalance { get; set; }

        /// <summary>Bokföringens saldo på kontot t.o.m. periodens slut.</summary>
        public decimal? LedgerBalance { get; set; }

        /// <summary>Bankens saldo minus bokföringens. Null när något av dem saknas.</summary>
        public decimal? Difference =>
            BankClosingBalance.HasValue && LedgerBalance.HasValue
                ? BankClosingBalance.Value - LedgerBalance.Value
                : null;

        /// <summary>
        /// Får ytan säga "allt stämmer"?
        ///
        /// <para><b>⚠️⚠️ KRÄVER BÅDA HALVORNA: noll omatchade rader OCH en differens på noll.</b>
        /// Var för sig räcker ingen av dem. Alla rader kan vara hopparade medan saldot ändå
        /// skiljer sig, om bokföringen bär poster utanför utdragets period — och saldot kan råka
        /// stämma medan två fel tar ut varandra. Att påstå "allt stämmer" på halva beviset är
        /// värre än att inte påstå något.</para>
        ///
        /// <para>⚠️ Saknar filen saldo är svaret <b>false</b>, inte "förmodligen". Då säger ytan
        /// vad den faktiskt vet: att raderna går ihop.</para>
        /// </summary>
        public bool Balances =>
            BankOnly.Count == 0 && LedgerOnly.Count == 0 && Difference == 0m;

        /// <summary>Allt hopparat, men saldot går inte att bevisa ur filen.</summary>
        public bool AllRowsMatchedButNoBalance =>
            BankOnly.Count == 0 && LedgerOnly.Count == 0 && Difference is null;

        public class BankRowView
        {
            public int Id { get; set; }
            public int LineNumber { get; set; }
            public DateTime BookedDate { get; set; }
            public string Text { get; set; } = "";
            public decimal Amount { get; set; }
            public string? Reference { get; set; }
        }

        public class LedgerLineView
        {
            public int LineId { get; set; }
            public int EntryId { get; set; }

            /// <summary>Verifikationsnumret med sitt prefix, så raden går att slå upp.</summary>
            public string EntryNumber { get; set; } = "";

            public DateTime AccountingDate { get; set; }
            public string Description { get; set; } = "";

            /// <summary>Tecknat som banken ser det: positivt in, negativt ut.</summary>
            public decimal Movement { get; set; }
        }

        public class MatchedPair
        {
            public BankRowView Bank { get; set; } = new();
            public LedgerLineView Ledger { get; set; } = new();

            /// <summary><c>auto</c> eller <c>manual</c>.</summary>
            public string Kind { get; set; } = "";
        }
    }
}
