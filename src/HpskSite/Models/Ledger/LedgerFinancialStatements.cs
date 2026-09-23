namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Resultat- och balansräkningen för ett räkenskapsår — handlingarna till årsmötet.
    ///
    /// <para><b>⚠️ Det här är INTE Rapport.</b> Rapport svarar på <i>håller vi budgeten?</i> och
    /// ställs på varje styrelsemöte. Det här svarar på <i>vad blev året?</i> och görs en gång.
    /// Samma tal, två frågor — och de får aldrig slås ihop till en yta.</para>
    /// </summary>
    public class LedgerFinancialStatements
    {
        public int FiscalYearId { get; set; }
        public int Year { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Status { get; set; } = "";

        /// <summary>Intäkter, positiva.</summary>
        public List<StatementRow> Revenue { get; } = new();

        /// <summary>Kostnader, positiva.</summary>
        public List<StatementRow> Costs { get; } = new();

        /// <summary>Tillgångar, debet-positiva.</summary>
        public List<StatementRow> Assets { get; } = new();

        /// <summary>Eget kapital och skulder, kredit-positiva.</summary>
        public List<StatementRow> EquityAndLiabilities { get; } = new();

        public decimal RevenueTotal => Revenue.Sum(r => r.Amount);
        public decimal CostTotal => Costs.Sum(r => r.Amount);

        /// <summary>Årets resultat: intäkter minus kostnader.</summary>
        public decimal Result => RevenueTotal - CostTotal;

        public decimal AssetTotal => Assets.Sum(r => r.Amount);
        public decimal EquityAndLiabilityTotal => EquityAndLiabilities.Sum(r => r.Amount);

        /// <summary>
        /// Tidigare års resultat, ackumulerat — intäkter minus kostnader FÖRE årets början.
        ///
        /// <para><b>⚠️⚠️ UTAN DEN HÄR GÅR ÅR TVÅ ALDRIG IHOP.</b> Liggaren gör ingen
        /// årsskiftesöverföring: förra årets överskott står kvar på tillgångssidan (pengarna finns
        /// på kontot) men har aldrig bokförts mot eget kapital. Differensen blev exakt förra årets
        /// resultat, bokslutssteget "balansräkningen går ihop" föll, och därmed gick det andra året
        /// aldrig att fastställa — för varje förening, från och med dess andra år. Beloppet
        /// HÄRLEDS ur liggaren i stället för att bokföras, av samma skäl som saldona är
        /// kumulativa: en överföringsverifikation är en till sak som kan glömmas eller göras två
        /// gånger.</para>
        /// </summary>
        public decimal PriorResult { get; set; }

        /// <summary>
        /// Går balansräkningen ihop?
        ///
        /// <para><b>⚠️⚠️ ÅRETS RESULTAT MÅSTE MED PÅ SKULDSIDAN.</b> Resultatet är inte bokfört mot
        /// eget kapital förrän året stängs, så tillgångarna är redan påverkade av årets affärer
        /// medan kapitalet inte är det. Jämförs sidorna utan resultatet blir differensen exakt
        /// årets resultat — varje år, för varje förening — och det ser ut som ett fel i
        /// bokföringen i stället för i jämförelsen.</para>
        /// </summary>
        public decimal BalanceDifference => AssetTotal - (EquityAndLiabilityTotal + PriorResult + Result);

        /// <summary>
        /// Balanserar den?
        /// <para>⚠️ Exakt noll, ingen tolerans. Dubbel bokföring balanserar per konstruktion —
        /// gör den inte det är något genuint fel, och att vifta bort ett öre döljer det.</para>
        /// </summary>
        public bool Balances => BalanceDifference == 0m;

        public class StatementRow
        {
            public int AccountNumber { get; set; }
            public string AccountName { get; set; } = "";
            public decimal Amount { get; set; }
        }
    }

    /// <summary>
    /// Bokslutets sju steg, härledda ur verkligt tillstånd.
    ///
    /// <para><b>⚠️ HÄRLEDDA, aldrig avbockade för hand.</b> En kryssruta kassören själv sätter
    /// säger att hen tror att steget är gjort; det här säger om det ÄR gjort. Skillnaden är hela
    /// poängen med en checklista i ett bokslut — den ska kunna säga emot.</para>
    ///
    /// <para>⚠️ Steg som vi ännu inte kan mäta redovisas som <see cref="StepState.Unknown"/> och
    /// säger det rakt ut. Ett omätt steg som visas som klart är värre än inget steg.</para>
    /// </summary>
    public class LedgerClosingChecklist
    {
        public int FiscalYearId { get; set; }
        public int Year { get; set; }
        public string Status { get; set; } = "";
        public List<Step> Steps { get; } = new();

        /// <summary>Får året stängas? Alla mätbara steg klara.</summary>
        public bool ReadyToEstablish => Steps.All(s => s.State != StepState.Todo);

        public enum StepState { Todo, Done, Unknown }

        public class Step
        {
            public string Key { get; set; } = "";
            public string Title { get; set; } = "";

            /// <summary>Vad som är kvar, i klartext. Tomt när steget är klart.</summary>
            public string Detail { get; set; } = "";

            public StepState State { get; set; }

            /// <summary>Railposten som löser steget, när en sådan finns.</summary>
            public string? GoTo { get; set; }
        }
    }
}
