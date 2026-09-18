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
        /// </summary>
        public string Shape { get; set; } = LedgerIssuerShape.FullLedger;
    }

    /// <summary>
    /// De två klubbformerna. <b>Ett verksamhetsval, inte en svårighetsgrad</b> — frågan i UI:t är
    /// <i>"Har ni ett bokföringsprogram i dag?"</i>, aldrig "enkelt eller avancerat".
    /// <para>Valet ska gå att ändra åt båda hållen utan att börja om.</para>
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
    }
}
