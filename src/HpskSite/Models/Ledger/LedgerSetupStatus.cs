namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Vad ekonomiytan behöver veta om en förening innan den ritar något.
    ///
    /// <para><b>⚠️ Läget "inte uppsatt" är ett eget tillstånd, inte ett fel.</b> Nästan varje
    /// förening kommer att ha det, och ytan ska då visa en uppsättning — inte en tom bokföring och
    /// inte ett felmeddelande. En halvtom bokföringsmodul är snabbaste vägen till slutsatsen
    /// "det här är inte färdigt" (<see cref="LedgerIssuerShape.FeesAndExport"/>).</para>
    ///
    /// <para><b>⚠️ Ingenting härleds hit.</b> <see cref="Shape"/> är tomt när föreningen inte har
    /// valt, aldrig <see cref="LedgerIssuerShape.FullLedger"/> — skillnaden mellan "valde" och
    /// "glömde välja" måste överleva hela vägen ut i vyn, annars fattar UI:t beslutet åt
    /// föreningen precis som en default-parameter hade gjort.</para>
    /// </summary>
    public class LedgerSetupStatus
    {
        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Föreningens namn, för rubriker. Läses ur noden, inte ur liggaren.</summary>
        public string IssuerName { get; set; } = "";

        /// <summary>
        /// Sant när det finns en inställningsrad. Falskt = föreningen har aldrig satts upp, och
        /// då är allt nedanför tomt.
        /// </summary>
        public bool IsSetUp { get; set; }

        /// <summary>
        /// Ur <see cref="LedgerIssuerShape"/>. <b>Tomt = föreningen har inte valt.</b> Vyn ska då
        /// ställa frågan, inte gissa.
        /// </summary>
        public string Shape { get; set; } = "";

        public bool IsVatRegistered { get; set; }

        public string? VatNumber { get; set; }

        /// <summary>Antal konton i föreningens kontoplan.</summary>
        public int AccountCount { get; set; }

        /// <summary>Antal roller som har ett konto. Full uppsättning = <see cref="RolesTotal"/>.</summary>
        public int RolesMapped { get; set; }

        /// <summary>Hur många roller som finns att mappa. Ur <c>LedgerAccountRoles.All</c>.</summary>
        public int RolesTotal { get; set; }

        /// <summary>
        /// Roller som saknar konto hos den här föreningen. <b>Varje rad här är en betalning som
        /// inte går att bokföra</b> — därför namnges de, aldrig bara räknas.
        /// </summary>
        public List<string> MissingRoles { get; } = new();

        /// <summary>Räkenskapsåren, nyast först.</summary>
        public List<LedgerFiscalYear> FiscalYears { get; } = new();

        /// <summary>
        /// Sant när allt som krävs för att ta emot en betalning finns: form vald, ett öppet
        /// räkenskapsår som täcker dagens datum, och fullständig rollmappning.
        /// <para>⚠️ Det här är ett SAMMANDRAG för vyn. Den bindande kontrollen är
        /// <c>LedgerPostingService.PostingBlockedReason</c>, och den frågas i betalvägen. Två
        /// uppfattningar om samma sak är fria att säga emot varandra, så den här får aldrig bli
        /// det som något beslut vilar på.</para>
        /// </summary>
        public bool CanPost { get; set; }

        /// <summary>
        /// Varför <see cref="CanPost"/> är falskt, på svenska och för en människa. Tom när allt är
        /// på plats, eller när föreningen inte bokför hos oss alls.
        /// </summary>
        public string? BlockedReason { get; set; }
    }
}
