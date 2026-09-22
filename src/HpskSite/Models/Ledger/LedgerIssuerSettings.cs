using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Föreningens ekonomiinställningar. En rad per utställare.
    ///
    /// <para><b>⚠️ MOMSREGISTRERING ÄR INTE ETT JA ELLER NEJ FÖR HELA FÖRENINGEN.</b> Michael
    /// (2026-09-17): <i>"en del kanske behöver kunna redovisa moms för viss del av verksamheten"</i>
    /// — blandad verksamhet är normalfallet för en förening som alls är registrerad. Kiosk och
    /// sponsring kan vara momspliktiga medan deltagaravgifterna är momsfria. Därför säger den här
    /// raden bara <b>om</b> föreningen är registrerad; <b>vad</b> som är momspliktigt avgörs per
    /// konto (<see cref="LedgerAccount.DefaultVatRate"/>).</para>
    ///
    /// <para><b>⚠️ Momspåståendet på kvittot ska läsas härifrån.</b> Dagens kvitto skriver hårdkodat
    /// att föreningen inte är momsregistrerad — på allas handlingar. Det är ett falskt påstående på
    /// en utfärdad handling så fort en enda klubb är registrerad (fel 8 i den gamla modellen).</para>
    /// </summary>
    [TableName("LedgerIssuerSettings")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerIssuerSettings
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>
        /// Registrerad för moms — för hela eller delar av verksamheten. Styr vad kvittot påstår,
        /// inte vilka konton som är momspliktiga.
        /// </summary>
        public bool IsVatRegistered { get; set; }

        /// <summary>Momsregistreringsnummer. Krävs på handlingar när föreningen är registrerad.</summary>
        public string? VatNumber { get; set; }

        /// <summary>
        /// Ur <see cref="LedgerIssuerShape"/> — vilken av de två klubbformerna (D1) föreningen är.
        /// Styr vad ekonomimodulen visar: hela bokföringen, eller bara avgifter och export.
        ///
        /// <para><b>⚠️ FÖRVALET ÄR TOMT, INTE <see cref="LedgerIssuerShape.FullLedger"/>.</b>
        /// Fram till 2026-09-21 stod <c>FullLedger</c> här, och det var samma fel som
        /// default-parametern i <c>EnsureIssuer</c> en gång var: ett objekt som skapas utan att
        /// någon valt hamnar tyst i bokföringsläge. Läsvägen tål tomt — både
        /// <c>LedgerPosting.DecidePosting</c> och <c>LedgerPostingService</c> behandlar allt som
        /// inte är exakt <c>full</c> som "bokför inte" — så tomt betyder <b>föreningen har inte
        /// valt</b>, och det är sant ända tills den gör det.</para>
        /// </summary>
        public string Shape { get; set; } = "";
    }

    /// <summary>
    /// De <b>tre</b> klubbformerna. <b>Ett verksamhetsval, inte en svårighetsgrad</b> — frågan i
    /// UI:t är <i>"Har ni ett bokföringsprogram i dag?"</i>, aldrig "enkelt eller avancerat".
    ///
    /// <para><b>⚠️ Det som skiljer de två sista är INTE storleken</b> utan om programmet läser
    /// bankkontot självt. Formerna var två fram till 2026-09-21; wireframen till Varbergs PK
    /// visade att det saknades en tredje, och att sammanslagningen av de två sista hade erbjudit
    /// en export som dubbelbokför.</para>
    ///
    /// <para>Valet ska gå att ändra åt alla håll utan att börja om (<c>LedgerSetupService.SetShape</c>).</para>
    /// </summary>
    public static class LedgerIssuerShape
    {
        /// <summary>Vi är bokföringsprogrammet. Hela modulen visas.</summary>
        public const string FullLedger = "full";

        /// <summary>
        /// Vi är avgifts- och kontrollsystemet; föreningen bokför i sitt eget program.
        /// Bokför-, avstämnings-, budget- och bokslutsytorna döljs och SIE-exporten flyttar fram.
        /// <para>⚠️ Den här föreningen får aldrig se en halvtom bokföringsmodul — det är snabbaste
        /// vägen till slutsatsen "det här är inte färdigt".</para>
        /// </summary>
        public const string FeesAndExport = "export";

        /// <summary>
        /// Föreningens bokföringsprogram läser <b>banken självt</b>. Vi skickar ingenting som ska
        /// bokföras — vi är bara det som säger vad som borde komma in.
        ///
        /// <para><b>⚠️⚠️ SKILLNADEN MOT <see cref="FeesAndExport"/> ÄR EXPORTEN, OCH DEN ÄR INTE
        /// KOSMETISK.</b> Anmälningsavgifterna landar på klubbens konto, och ett program med
        /// bankkoppling bokför dem redan därifrån. Lämnade vi en SIE-fil med samma transaktioner
        /// skulle de bokföras <b>två gånger</b>. Exporten är alltså inte en funktion för den här
        /// klubben — den är en risk, och den ska inte erbjudas.</para>
        ///
        /// <para>Skiljelinjen mellan de tre sorterna är alltså <b>inte storleken</b> utan om
        /// programmet hämtar bankdatat självt. Källa: underlaget till Varbergs PK
        /// (<c>/ekonomifragor/wf</c>), som bygger på Fredriks eget svar.</para>
        /// </summary>
        public const string FeesOnly = "fees";

        /// <summary>Alla former, i den ordning uppsättningen ska visa dem.</summary>
        public static readonly string[] All = { FullLedger, FeesAndExport, FeesOnly };

        /// <summary>Känd form? <b>Tomt är inte känt</b> — det betyder att föreningen inte valt.</summary>
        public static bool IsValid(string? shape)
            => !string.IsNullOrWhiteSpace(shape) && All.Contains(shape);

        /// <summary>
        /// Bokför föreningen hos oss? Bara <see cref="FullLedger"/> gör det.
        /// <para>⚠️ Frågan ställs så här och aldrig som <c>!= FeesAndExport</c> — den formen hade
        /// svarat fel dagen en tredje form tillkom, vilket den gjorde 2026-09-21.</para>
        /// </summary>
        public static bool KeepsBooks(string? shape) => shape == FullLedger;

        /// <summary>
        /// Ska SIE-exporten erbjudas? Ja för alla utom <see cref="FeesOnly"/>, där den skulle
        /// leda till dubbelbokföring.
        /// </summary>
        public static bool OffersExport(string? shape) => shape == FullLedger || shape == FeesAndExport;
    }
}
