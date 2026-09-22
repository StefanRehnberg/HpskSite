using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ett uppladdat kontoutdrag.
    ///
    /// <para><b>⚠️ Ett kontoutdrag är ett UNDERLAG, inte en verifikation</b> — därför ingen
    /// oföränderlighetstrigger. Laddar kassören upp fel fil ska den kunna tas bort och göras om.
    /// Verifikationerna raderna matchas MOT är oföränderliga, och det är där spärren hör hemma.</para>
    /// </summary>
    [TableName("LedgerBankImport")]
    [PrimaryKey("Id")]
    public class LedgerBankImport
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        /// <summary>Kontot utdraget gäller, som nummer ur föreningens egen kontoplan.</summary>
        public int AccountNumber { get; set; }

        public string FileName { get; set; } = "";
        public DateTime? PeriodFrom { get; set; }
        public DateTime? PeriodTo { get; set; }

        /// <summary>
        /// Bankens egna saldon när filen bar dem.
        /// <para><b>⚠️ Det är de här som gör differensen bevisbar.</b> Utan dem kan ytan bara säga
        /// "dessa rader matchade", aldrig "kontot stämmer".</para>
        /// </summary>
        public decimal? OpeningBalance { get; set; }
        public decimal? ClosingBalance { get; set; }

        [Column("RowCount_")]
        public int RowCount { get; set; }

        public int ImportedByMemberId { get; set; }
        public DateTime ImportedUtc { get; set; }
    }

    /// <summary>En rad ur utdraget, med sin eventuella matchning.</summary>
    [TableName("LedgerBankRow")]
    [PrimaryKey("Id")]
    public class LedgerBankRow
    {
        public int Id { get; set; }
        public int ImportId { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        /// <summary>
        /// Radens ordning i filen.
        /// <para>⚠️ Behövs för att kunna visa utdraget som det SÅG UT — två rader kan ha samma
        /// datum, text och belopp och ändå vara två olika betalningar.</para>
        /// </summary>
        public int LineNumber { get; set; }

        public DateTime BookedDate { get; set; }
        public string Text { get; set; } = "";

        /// <summary>Tecknat: positivt in, negativt ut.</summary>
        public decimal Amount { get; set; }

        public decimal? Balance { get; set; }
        public string? Reference { get; set; }

        /// <summary>
        /// Bokföringsraden den matchats mot, eller null.
        /// <para><b>⚠️ Pekar på RADEN, inte på verifikationen</b> — en verifikation kan ha flera
        /// rader på bankkontot, och då är raden motparten.</para>
        /// </summary>
        public int? MatchedLineId { get; set; }

        /// <summary>
        /// <c>auto</c> eller <c>manual</c>.
        /// <para>⚠️ En automatisk träff är en GISSNING operatören kan ångra; en manuell är ett
        /// beslut. Slås de ihop går det inte att i efterhand se vad en människa faktiskt
        /// granskat.</para>
        /// </summary>
        public string? MatchKind { get; set; }

        public int? MatchedByMemberId { get; set; }
        public DateTime? MatchedUtc { get; set; }

        public bool IsMatched => MatchedLineId is > 0;
    }

    /// <summary>Hur en matchning kom till.</summary>
    public static class LedgerBankMatchKind
    {
        public const string Auto = "auto";
        public const string Manual = "manual";
    }

    /// <summary>
    /// Reglerna för att para ihop en bankrad med en bokföringsrad, samlade där de går att pröva
    /// utan databas.
    ///
    /// <para><b>⚠️⚠️ EN AUTOMATISK MATCHNING SOM KAN VARA FEL ÄR VÄRRE ÄN INGEN.</b> Den ser
    /// granskad ut. Därför matchas bara det som är <b>entydigt</b>: finns två lika bra kandidater
    /// lämnas båda åt operatören, och ytan säger varför.</para>
    /// </summary>
    public static class LedgerBankMatching
    {
        /// <summary>
        /// Hur många dagar bankens datum får skilja sig från bokföringsdagen.
        ///
        /// <para><b>⚠️ Generöst med FLIT.</b> Liggaren bokför på <c>ConfirmedUtc</c> — dagen
        /// arrangören bekräftade betalningen — och det kan vara en vecka efter att pengarna
        /// landade. Ett snävt fönster hade lämnat de flesta riktiga par omatchade, alltså gjort
        /// automatiken meningslös. Det som bär säkerheten här är <b>entydighetskravet</b>, inte
        /// fönstret.</para>
        /// </summary>
        public const int DateToleranceDays = 7;

        /// <summary>
        /// En bokföringsrads rörelse på kontot, tecknad som banken ser den.
        /// <para>⚠️ Debet ökar en tillgång, alltså pengar IN. Vänds tecknet matchar varje
        /// insättning mot ett uttag på samma belopp — och det ser ut att stämma.</para>
        /// </summary>
        public static decimal SignedMovement(decimal debit, decimal credit) => debit - credit;

        /// <summary>Kan de två vara samma händelse?</summary>
        public static bool CouldMatch(
            decimal bankAmount, DateTime bankDate,
            decimal lineMovement, DateTime lineDate,
            int toleranceDays = DateToleranceDays)
            => bankAmount == lineMovement
               && Math.Abs((bankDate.Date - lineDate.Date).TotalDays) <= toleranceDays;

        /// <summary>
        /// Väljer den enda kandidaten, eller ingen.
        ///
        /// <para><b>⚠️ Returnerar null vid flera kandidater, med flit.</b> Två betalningar på
        /// 270 kr samma vecka är vardag i en klubb, och att ta den första är ett myntkast som
        /// ser auktoritativt ut. Operatören ser dem båda i stället.</para>
        /// </summary>
        public static T? SingleCandidate<T>(IReadOnlyList<T> candidates) where T : class
            => candidates.Count == 1 ? candidates[0] : null;
    }
}
