namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Förvalsmallen för en förenings kontoplan, plus mappningen från kontoroll till konto.
    ///
    /// <para><b>⚠️⚠️ DET HÄR ÄR EN LITEN, EGEN KONTOPLAN — INTE EN KOPIA AV BAS.</b> Två skäl, och
    /// båda pekar åt samma håll:</para>
    /// <list type="number">
    /// <item><b>Upphovsrätt.</b> BAS-kontoplanen ges ut av en förening som säljer den. Att bygga in
    /// och distribuera hela listan i en produkt är inte självklart fritt. <b>Nummerrymden är det
    /// däremot</b> — fyra siffror med kontoklassen först är en struktur, inte någons egendom, och
    /// den är dessutom vad SIE-formatet förutsätter. Vi följer numreringskonventionen utan att
    /// reproducera listan.</item>
    /// <item><b>Produkten blir bättre av det.</b> En förtroendevald kassör som möter tolvhundra
    /// konton första dagen slutar samma dag. Trettio konton som täcker en skytteförenings faktiska
    /// verksamhet är begripliga, och den som behöver fler lägger till dem — det är hela poängen
    /// med att kontoplanen är data.</item>
    /// </list>
    ///
    /// <para><b>⚠️ NUMREN ÄR VÄLDA EFTER BAS-KONVENTION MEN INTE GRANSKADE AV EN REDOVISNINGS-
    /// KONSULT.</b> Det är acceptabelt just för att de är data: ett fel här rättas genom att ändra
    /// en rad, inte genom att migrera levande bokföring. Ta upp listan när konsulttimmen ändå
    /// köps — men vänta inte på den för att komma igång.</para>
    ///
    /// <para>Föreningen äger sin kontoplan efter att mallen kopierats in: konton kan döpas om,
    /// stängas av och läggas till fritt. <b>Egna underkonton är ett krav</b>, inte en finess — det
    /// är så ändamålsbestämda medel löses (1931/1932/1933-mönstret) i stället för med en egen
    /// fond-mekanism.</para>
    /// </summary>
    public static class LedgerChartTemplate
    {
        /// <summary>Ett konto i mallen.</summary>
        public readonly record struct TemplateAccount(int Number, string Name);

        /// <summary>
        /// Startuppsättningen. Kontoklasserna följer BAS-konventionen: 1 tillgångar, 2 eget kapital
        /// och skulder, 3 intäkter, 4–7 kostnader, 8 finansiellt.
        /// </summary>
        public static readonly TemplateAccount[] Accounts =
        {
            // 1 — Tillgångar
            // ⚠️ Varje tillgångskonto har sitt minuskonto (x9) för ackumulerade avskrivningar.
            //    Anskaffningsvärdet står kvar på tillgångskontot — se LedgerAsset.
            new(1110, "Klubbstuga och byggnader"),
            new(1119, "Ackumulerade avskrivningar byggnader"),
            new(1150, "Markanläggningar och skjutvallar"),
            new(1159, "Ackumulerade avskrivningar markanläggningar"),
            new(1220, "Inventarier och utrustning"),
            new(1221, "Klubbvapen"),
            new(1229, "Ackumulerade avskrivningar inventarier"),
            new(1510, "Kundfordringar"),
            new(1910, "Kontantkassa"),
            new(1920, "Plusgiro"),
            new(1930, "Föreningskonto"),
            // ⚠️⚠️ INGET EGET SWISH-KONTO (ändrat 2026-09-25). Mallen hade 1931 "Swish", men Swish
            //    är ett betalsätt, inte ett konto: pengarna landar på föreningskontot. Michael
            //    Henriksson (Åmåls PK): "Swish är ju jättebra men är ju inget eget konto." Med 1931
            //    växte ett saldo som aldrig nollades, och bankavstämningen av 1930 visade varje
            //    Swish-betalning som "hänt på kontot men inte bokfört". Swish-rollen pekar nu på
            //    1930; en förening med ett EGET konto för Swish lägger upp det själv.
            // 1940 i stället — de flesta föreningar har ett sparkonto (Michaels förslag).
            new(1940, "Sparkonto"),

            // 2 — Eget kapital och skulder
            new(2060, "Eget kapital"),
            new(2069, "Årets resultat"),
            new(2440, "Leverantörsskulder"),
            new(2610, "Utgående moms"),
            new(2640, "Ingående moms"),
            new(2990, "Upplupna kostnader"),

            // 3 — Intäkter
            new(3010, "Medlemsavgifter"),
            new(3020, "Anmälningsavgifter"),
            new(3030, "Träningsavgifter"),
            new(3040, "Kiosk och försäljning"),
            new(3050, "Sponsring"),
            new(3060, "Bidrag"),
            new(3890, "Övriga intäkter"),
            new(3970, "Vinst vid försäljning av anläggningstillgångar"),

            // 4–7 — Kostnader
            new(4010, "Ammunition och skjutmateriel"),
            new(4020, "Tavlor, figurer och markeringsmateriel"),
            new(4030, "Priser och medaljer"),
            new(5010, "Lokalkostnader"),
            new(5020, "El, vatten och sophämtning"),
            new(5170, "Underhåll av anläggning"),
            new(6110, "Kontorsmateriel och porto"),
            new(6250, "Telefon och internet"),
            new(6310, "Försäkringar"),
            new(6570, "Bankkostnader"),
            new(6980, "Förbunds- och kretsavgifter"),
            // Licensavgifter är INTÄKT + KOSTNAD, inte en skuld (avgjort 2026-09-16): de flesta
            // föreningar vet inte ens att skuldvarianten finns, och bruttoredovisningen är
            // vedertagen praxis. Därför ett vanligt kostnadskonto och inget genomströmningsbegrepp.
            new(6990, "Licensavgifter till förbundet"),
            new(7820, "Avskrivningar byggnader och mark"),
            new(7830, "Avskrivningar inventarier"),
            new(7970, "Förlust vid utrangering av anläggningstillgångar"),

            // 8 — Finansiellt
            new(8310, "Ränteintäkter"),
            new(8410, "Räntekostnader"),
            new(8999, "Öresavrundning")
        };

        /// <summary>
        /// Förslaget på konto för förlusten när en tillgång utrangeras med ett bokfört värde kvar.
        /// <para>⚠️ Ett FÖRSLAG i utrangeringsdialogen, aldrig ett konto koden bokför på utan att
        /// kassören sett det — samma princip som tillgångens egna konton.</para>
        /// </summary>
        public const int DisposalLossAccount = 7970;

        /// <summary>Mallens konto med numret, eller null.</summary>
        public static TemplateAccount? Find(int number)
            => Accounts.Any(a => a.Number == number) ? Accounts.First(a => a.Number == number) : null;

        /// <summary>
        /// Förvalsmappningen roll → kontonummer.
        ///
        /// <para><b>⚠️ Varje roll i <see cref="LedgerAccountRoles.All"/> MÅSTE finnas här.</b> En
        /// roll utan konto är en betalning som inte går att bokföra, och det ska inte upptäckas av
        /// en kassör mitt i en tävlingsdag. Startkontrollen larmar om en utställare bokför med en
        /// ofullständig mappning; den här listan är vad som gör att det aldrig behöver hända för en
        /// nyuppsatt förening.</para>
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> RoleDefaults =
            new Dictionary<string, int>
            {
                [LedgerAccountRoles.RevenueParticipationFee] = 3020,
                [LedgerAccountRoles.RevenueMembershipFee]    = 3010,
                [LedgerAccountRoles.RevenueRegionFee]        = 3010,
                [LedgerAccountRoles.RevenueOther]            = 3890,
                [LedgerAccountRoles.BankAccount]             = 1930,
                // Swish landar på föreningskontot — se kontolistan ovan.
                [LedgerAccountRoles.Swish]                   = 1930,
                [LedgerAccountRoles.CashBox]                 = 1910,
                [LedgerAccountRoles.AccountsReceivable]      = 1510,
                [LedgerAccountRoles.AccountsPayable]         = 2440,
                [LedgerAccountRoles.Rounding]                = 8999,
                [LedgerAccountRoles.VatOutgoing]             = 2610,
                [LedgerAccountRoles.VatIncoming]             = 2640
            };

        /// <summary>
        /// Rollerna som saknar ett förval — ska alltid vara tom.
        /// <para>Finns här för att ett tillägg i <see cref="LedgerAccountRoles"/> utan ett förval
        /// ska upptäckas av ett test i stället för av en kassör.</para>
        /// </summary>
        public static IEnumerable<string> RolesWithoutDefault()
            => LedgerAccountRoles.All.Where(r => !RoleDefaults.ContainsKey(r));
    }
}
