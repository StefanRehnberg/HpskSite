using System.Linq;
using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ett konto i en förenings kontoplan.
    ///
    /// <para><b>⚠️⚠️ KONTOPLANEN ÄR DATA, INTE STRUKTUR.</b> Det som är enkelriktat i bygget är inte
    /// listan utan tre andra saker: (1) den fyrsiffriga BAS-nummerrymden, som ändå krävs av
    /// SIE-formatet och därför inte är ett val utan en följd; (2) att kontot lagras som NUMMER på
    /// konteringsraden, aldrig som en referens hit (se
    /// <see cref="LedgerJournalEntryLine.AccountNumber"/>); (3) att koden aldrig refererar ett
    /// kontonummer utan en ROLL (<see cref="LedgerAccountRole"/>).</para>
    ///
    /// <para>Följden: flera mallar att välja mellan, egna konton när som helst, och en förening som
    /// bygger om hela sin kontoplan utan att en kodrad ändras. <b>Egna underkonton MÅSTE gå</b> —
    /// det är så ändamålsbestämda medel löses (1931 transaktionskonto, 1932 skatter, 1933
    /// renoveringsfond), inte med en egen fond-mekanism.</para>
    ///
    /// <para>⚠️ Kontrollera upphovsrätten till den BAS-mall vi levererar innan den skeppas. Det
    /// gäller listan, inte nummerrymden — klasstrukturen är ingens egendom.</para>
    /// </summary>
    [TableName("LedgerAccount")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAccount
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Fyrsiffrigt BAS-konto. Första siffran är kontoklassen.</summary>
        public int Number { get; set; }

        public string Name { get; set; } = "";

        /// <summary>
        /// Avstängt konto. <b>Raderas aldrig</b> — historiska rader bär numret som text, men ett
        /// konto som använts ska ändå kunna slås upp och förklaras för en revisor.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Sant för konton som kom ur förvalsmallen. Falskt för föreningens egna tillägg.</summary>
        public bool FromTemplate { get; set; }

        /// <summary>
        /// Momssats i procent som normalt gäller för det här kontot. Null = momsfritt.
        ///
        /// <para><b>⚠️ DET ÄR HÄR BLANDAD VERKSAMHET BOR.</b> En förening är sällan momspliktig rakt
        /// av — kiosken och sponsringen kan vara det medan deltagaravgifterna inte är det (Michael
        /// 2026-09-17: <i>"en del kanske behöver kunna redovisa moms för viss del av
        /// verksamheten"</i>). Genom att satsen hänger på KONTOT slipper kassören ta ställning per
        /// post: hen väljer "Kiosk", och momsen följer med. En inställning per förening hade
        /// tvingat fram ett val som inte finns i verkligheten.</para>
        ///
        /// <para><b>Förval: null på ALLA konton i mallen.</b> De allra flesta föreningar är inte
        /// momsregistrerade, och den som är väljer själv vilka grenar det gäller. Ett gissat förval
        /// hade skrivit moms på handlingar där den inte hör hemma.</para>
        /// </summary>
        public decimal? DefaultVatRate { get; set; }
    }

    /// <summary>
    /// Mappningen från en kontoroll till föreningens faktiska konto.
    ///
    /// <para><b>Det här är den bärande designen i hela ekonomibygget.</b> Ingenstans i C#-koden får
    /// det stå ett kontonummer. Systemgenererade postningar pekar på en roll
    /// (<see cref="LedgerAccountRoles"/>), och föreningen mappar rollen hit. Därmed blir en
    /// felaktig förvalsmappning en <b>datarättelse</b> i stället för en migrering, och en klubb kan
    /// bygga om sin kontoplan utan att något går sönder.</para>
    /// </summary>
    [TableName("LedgerAccountRole")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAccountRole
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Nyckel ur <see cref="LedgerAccountRoles"/>.</summary>
        public string RoleKey { get; set; } = "";

        /// <summary>Kontonumret rollen pekar på hos den här föreningen.</summary>
        public int AccountNumber { get; set; }
    }

    /// <summary>
    /// Rollerna koden får referera. <b>Listan är en konstant; mappningen är data per förening.</b>
    ///
    /// <para>⚠️ Roller får läggas till, men aldrig döpas om och aldrig återanvändas till något
    /// annat — en mappning i prod pekar på nyckeln. Samma regel som borttagna frågor i
    /// ekonomienkäten: id:t återanvänds aldrig.</para>
    ///
    /// <para>⚠️ En saknad rollmappning är en betalning som inte går att bokföra. Därför kontrollerar
    /// startkontrollen att varje aktiv utställare har en fullständig uppsättning.</para>
    /// </summary>
    public static class LedgerAccountRoles
    {
        // Intäkter
        public const string RevenueParticipationFee = "revenue-participation-fee";
        public const string RevenueMembershipFee = "revenue-membership-fee";
        public const string RevenueRegionFee = "revenue-region-fee";
        public const string RevenueOther = "revenue-other";

        // Tillgångar och betalvägar
        public const string BankAccount = "bank-account";
        public const string Swish = "swish";
        public const string CashBox = "cash-box";

        /// <summary>
        /// Kundfordran. Används av bokslutssteget: obetalda dokument vid årsskiftet bokförs hit.
        /// Kontantmetoden är metoden (avgjord 2026-09-16), och därmed är bokslutssteget obligatoriskt.
        /// </summary>
        public const string AccountsReceivable = "accounts-receivable";

        /// <summary>
        /// Kundfordran för <b>deltagaravgifter</b>. <b>Valfri.</b>
        ///
        /// <para><b>⚠️⚠️ SKÄLET ÄR INTE ORDNING — DET ÄR ATT SALDOT SKA GÅ ATT LÄSA.</b> Fredrik
        /// (Varbergs PK, 2026-09-23): <i>"Medlemskapen tar i värsta fall ett halvår att få in och
        /// då är det dumt att blanda dessa med tävlingsintäkter som oftast kommer in med en
        /// gång."</i> Ligger båda på samma konto går det inte att skilja normal eftersläpning
        /// från en avgift som aldrig kom — och då säger fordringssaldot ingenting.</para>
        /// </summary>
        public const string ReceivableParticipationFee = "receivable-participation-fee";

        /// <summary>Kundfordran för <b>medlemsavgifter</b>. <b>Valfri.</b> Se ovan.</summary>
        public const string ReceivableMembershipFee = "receivable-membership-fee";

        public const string AccountsPayable = "accounts-payable";

        /// <summary>Öresavrundning. Finns för att en balanserad verifikation alltid ska gå att skriva.</summary>
        public const string Rounding = "rounding";

        /// <summary>
        /// Utgående moms — momsen på det föreningen säljer. Egen konteringsrad i verifikationen;
        /// <see cref="LedgerJournalEntryLine.VatRate"/> på intäktsraden är bara metadata.
        /// </summary>
        public const string VatOutgoing = "vat-outgoing";

        /// <summary>Ingående moms — momsen på det föreningen köper, när den får dra av den.</summary>
        public const string VatIncoming = "vat-incoming";

        /// <summary>
        /// Alla roller, i den ordning de ska visas vid uppsättning.
        /// <para>⚠️ Momsrollerna ligger sist och mappas alltid, även för en förening som inte är
        /// momsregistrerad. Det kostar två rader och gör att den dag styrelsen beslutar att
        /// momsregistrera kiosken finns kontot redan — i stället för att bokföringen felar mitt i
        /// en tävlingsdag.</para>
        /// </summary>
        /// <summary>
        /// Roller föreningen KAN mappa, men inte måste.
        ///
        /// <para><b>⚠️⚠️ FÅR ALDRIG LIGGA I <see cref="All"/>.</b> Den listan driver
        /// <c>rolesMapped/rolesTotal</c> och startkontrollen — läggs de här dit rapporteras varje
        /// befintlig förening plötsligt som ofullständigt uppsatt, för något de inte bett om och
        /// inte behöver. "Valbart" var Fredriks eget ord, och det är ett krav, inte en artighet.</para>
        ///
        /// <para>Omappad ⇒ fordran hamnar på <see cref="AccountsReceivable"/> som förut.</para>
        /// </summary>
        public static readonly string[] Optional =
        {
            ReceivableParticipationFee,
            ReceivableMembershipFee
        };

        /// <summary>
        /// Fordringsrollen för ett intäktsslag — den specifika när den finns, annars den allmänna.
        ///
        /// <para><b>⚠️ Paras med intäktsrollen ur SAMMA fråga.</b> Fordran och intäkt är två ben i
        /// samma verifikation; härleds de ur olika klassificeringar kan en deltagaravgift bokföras
        /// mot medlemsfordran, och det syns först när någon försöker läsa saldot.</para>
        /// </summary>
        public static string ReceivableFor(string revenueRole) => revenueRole switch
        {
            RevenueParticipationFee => ReceivableParticipationFee,
            RevenueMembershipFee => ReceivableMembershipFee,
            _ => AccountsReceivable
        };

        public static readonly string[] All =
        {
            RevenueParticipationFee,
            RevenueMembershipFee,
            RevenueRegionFee,
            RevenueOther,
            BankAccount,
            Swish,
            CashBox,
            AccountsReceivable,
            AccountsPayable,
            Rounding,
            VatOutgoing,
            VatIncoming
        };

        /// <summary>
        /// Varje roll som går att peka ut — obligatoriska OCH valfria.
        ///
        /// <para><b>⚠️⚠️ VALIDERING OCH KRAVLISTA ÄR TVÅ OLIKA FRÅGOR.</b> <see cref="All"/> svarar
        /// på <i>vad måste vara mappat för att vi ska kunna bokföra</i> och driver
        /// <c>rolesMapped/rolesTotal</c>. Den här svarar på <i>vad får en förening peka ut</i>.
        /// Att använda <see cref="All"/> som validering gjorde Fredriks valbara fordringskonton
        /// <b>omöjliga att sätta</b> — <c>SetRole</c> svarade "Okänd roll" och kontoplanen erbjöd
        /// dem aldrig, så funktionen fanns men var oåtkomlig från ytan. Mätt i genomgången
        /// 2026-09-22.</para>
        /// </summary>
        public static readonly string[] Known = All.Concat(Optional).ToArray();

        /// <summary>Är rollen valfri? Styr att en omappad roll inte rapporteras som en lucka.</summary>
        public static bool IsOptional(string roleKey) => Optional.Contains(roleKey);

        /// <summary>
        /// Rollen som en HÄNDELSE, på svenska.
        ///
        /// <para><b>⚠️⚠️ "KONTOROLLER" OCH "12 MAPPADE" BETYDER INGENTING för den som inte kan
        /// bokföring</b> — och det är precis den personen modulen finns för. Stefan under provning
        /// 2026-09-22: <i>"Jag förstår inte vad 'Kontoroller' är för nåt och vad de 12 mappade är,
        /// men jag förstår inte bokföring…"</i> Om vår egen byggare inte förstår rubriken gör ingen
        /// kassör det heller.</para>
        ///
        /// <para>Texterna är skrivna som frågan de svarar på: <i>när det här händer, vart går
        /// pengarna?</i> — aldrig som ett kontobegrepp.</para>
        ///
        /// <para><b>⚠️⚠️ FÖRENINGEN ÄR "VI". PISTOL.NU SKRIVS UT. "NI" ANVÄNDS INTE.</b> Hela
        /// ekonomiytan följer den regeln.
        ///
        /// <para>Den blev fel åt båda hållen innan den satt: först blandade ytan "vi" (pistol.nu)
        /// och "ni" (föreningen) i samma stycke — Stefan 2026-09-22: <i>"vilka är ni och vi?"</i>
        /// — och sedan rättade jag det åt fel håll genom att göra föreningen till "ni". Hans svar
        /// på det: <i>"En kassör som läser det läser det som att ni är någon annan, vi är
        /// föreningen."</i> <b>Läsaren ÄR föreningen</b>, och den som skriver in sin egen
        /// förenings uppgifter talar om sig själv i första person.</para></para>
        /// </summary>
        public static string Label(string? roleKey) => roleKey switch
        {
            RevenueParticipationFee => "När någon betalar en deltagaravgift",
            RevenueMembershipFee    => "När någon betalar medlemsavgiften",
            RevenueRegionFee        => "När en klubb betalar kretsavgiften",
            RevenueOther            => "Övriga intäkter",
            BankAccount             => "Pengar som kommer in på bankkontot",
            Swish                   => "Pengar som kommer in via Swish",
            CashBox                 => "Kontanter i kassan",
            AccountsReceivable      => "Någon är skyldig oss pengar vid årsskiftet",
            ReceivableParticipationFee => "Obetalda deltagaravgifter",
            ReceivableMembershipFee => "Obetalda medlemsavgifter",
            AccountsPayable         => "Vi är skyldiga någon pengar vid årsskiftet",
            Rounding                => "Ören som blir över vid avrundning",
            VatOutgoing             => "Moms vi tar ut när vi säljer",
            VatIncoming             => "Moms vi betalar när vi köper",
            _                       => roleKey ?? ""
        };

        /// <summary>
        /// Vad rollen används till, när etiketten inte räcker. Tom sträng = behövs ingen.
        /// <para>⚠️ Bara där det faktiskt hjälper. En förklaring på varje rad blir en vägg av
        /// text, och då läses ingen av dem.</para>
        /// </summary>
        public static string Hint(string? roleKey) => roleKey switch
        {
            AccountsReceivable => "Obetalda avgifter vid bokslutet hamnar här, så de syns i balansräkningen.",

            // ⚠️ Förklaringen säger VARFÖR man skulle vilja dela, inte bara vad fältet gör. Utan
            //    skälet ser de två raderna ut som onödig administration — och då mappar ingen dem.
            ReceivableParticipationFee =>
                "Frivilligt. Egna konton gör att medlemsavgifter som normalt dröjer inte blandas "
                + "med anmälningsavgifter som skulle ha kommit direkt. Lämnas den tom hamnar allt "
                + "på kontot ovan.",
            ReceivableMembershipFee =>
                "Frivilligt. Medlemsavgifter kan ta ett halvår att få in — på ett eget konto syns "
                + "det som normal eftersläpning i stället för som en obetald avgift.",
            Rounding           => "Behövs för att en verifikation alltid ska gå ihop på öret.",
            // ⚠️ Swish är ett betalsätt, inte ett konto (Michael Henriksson 2026-09-25) — se
            //    LedgerChartTemplate. Raden säger det, så att ingen lägger upp ett konto i onödan.
            Swish              => "Swish kommer in på föreningskontot hos de flesta — välj då samma konto som "
                                  + "bankkontot. Ett eget konto behövs bara om Swish är kopplat till ett eget bankkonto.",
            VatOutgoing        => "Används bara om föreningen är momsregistrerad.",
            VatIncoming        => "Används bara om föreningen är momsregistrerad.",
            _                  => ""
        };

    }
}
