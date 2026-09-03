using HpskSite.Models.Firearms;
using NPoco;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    public static class FirearmBookingStatus
    {
        /// <summary>Bokad, ännu inte utlämnad.</summary>
        public const string Reserverad = "Reserverad";

        /// <summary>Vapnet är fysiskt utlämnat.</summary>
        public const string Utlamnad = "Utlamnad";

        /// <summary>Vapnet är återlämnat. Bokningen är avslutad.</summary>
        public const string Aterlamnad = "Aterlamnad";

        public const string Avbokad = "Avbokad";

        public static readonly string[] All = { Reserverad, Utlamnad, Aterlamnad, Avbokad };

        /// <summary>
        /// Statusar som BLOCKERAR vapnet i krockkontrollen.
        ///
        /// <para><b>⚠️ `Aterlamnad` blockerar INTE.</b> Vapnet är tillbaka i skåpet — att en avslutad
        /// bokning fortsatte blockera sitt tidsfönster hade gjort varje vapen obokbart för alltid
        /// bakåt i tiden, vilket låter harmlöst men bryter varje efterhandsrättelse.</para>
        ///
        /// <para><b>⚠️ `Utlamnad` blockerar.</b> Vapnet är ute — även om fönstret hunnit passera.</para>
        /// </summary>
        public static readonly string[] Blocking = { Reserverad, Utlamnad };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            Reserverad => "Reserverad",
            Utlamnad => "Utlämnad",
            Aterlamnad => "Återlämnad",
            Avbokad => "Avbokad",
            _ => v ?? "",
        };
    }

    /// <summary>
    /// Vad bokningen gäller. <b>Sammansatt nyckel</b> med <c>OccasionId</c> — en tävlingsnod och en
    /// evenemangsnod kommer ur samma id-serie i Umbraco men betyder olika saker.
    /// </summary>
    public static class FirearmOccasionKind
    {
        /// <summary>Bara ett tidsfönster. Har inget id, och det är det vanligaste fallet.</summary>
        public const string Fritt = "Fritt";

        /// <summary>En <c>clubSimpleEvent</c>-nod (träning, annat).</summary>
        public const string Event = "Event";

        /// <summary>En tävlingsnod.</summary>
        public const string Competition = "Competition";

        /// <summary>
        /// Ett arrangemang hos NÅGON ANNAN, som inte finns som nod hos oss — därför fritext i
        /// <c>OccasionLabel</c> och <c>OccasionId = 0</c>.
        ///
        /// <para><b>⚠️ Det enda slaget där vapnet lämnar klubbens område</b>, och därmed det enda
        /// som kräver en namngiven medföljande som har accepterat. Nybörjaren får inte
        /// transportera eller inneha vapnet själv, så lånet är en bokning av ett vapen OCH en
        /// person.</para>
        /// </summary>
        public const string Externt = "Externt";

        public static readonly string[] All = { Fritt, Event, Competition, Externt };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        /// <summary>
        /// Lämnar vapnet klubbens område? Avgör vilka regler som gäller, och är avsiktligt en
        /// funktion av SLAGET och inte en egen flagga — två fält som ska hållas i takt glider isär.
        /// </summary>
        public static bool LeavesTheClub(string? v) =>
            string.Equals((v ?? "").Trim(), Externt, StringComparison.Ordinal);

        /// <summary>Har slaget ett id i vår egen innehållsträd?</summary>
        public static bool HasNodeId(string? v)
        {
            var k = (v ?? "").Trim();
            return k == Event || k == Competition;
        }

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            Fritt => "Egen tid",
            Event => "Klubbhändelse",
            Competition => "Tävling",
            Externt => "Utanför klubben",
            _ => v ?? "",
        };
    }

    [TableName("FirearmBooking")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FirearmBooking
    {
        public int Id { get; set; }

        /// <summary>
        /// ÖNSKAT vapen. <c>null</c> betyder <b>vilket som helst</b>.
        ///
        /// <para><b>⚠️ Det här är hoppet, inte fakta.</b> Vapnet som faktiskt lämnades ut står i
        /// <see cref="AssignedFirearmId"/>. Skälet att de är skilda: en nybörjare kan inte svara på
        /// "vilket vapen?", men den som skjutit in nr 7 mot sig själv kommer inte om hen inte får
        /// just det — två faser hos samma skytt, båda standardfall. Och ett
        /// <em>vilket-som-helst</em>-lån får INTE tilldelas i förväg, för då tar en nybörjare som
        /// inte bryr sig nr 7 från den som är beroende av det, dagar i förväg och osynligt.</para>
        /// </summary>
        public int? FirearmId { get; set; }

        /// <summary>Vapnet som FAKTISKT lämnades ut. Tomt så länge lånet bara är reserverat.</summary>
        public int? AssignedFirearmId { get; set; }

        /// <summary>
        /// Önskad vapengrupp när inget bestämt vapen valts, så vapenansvarig vet VAD som ska hämtas
        /// ur valvet ("ett C-vapen") utan att bokningen låser ett nummer.
        /// </summary>
        public string? WeaponClass { get; set; }

        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public string OccasionKind { get; set; } = FirearmOccasionKind.Fritt;
        public int OccasionId { get; set; }

        /// <summary>Fritext för ett externt tillfälle, som inte finns som nod hos oss.</summary>
        public string? OccasionLabel { get; set; }

        public DateTime FromTime { get; set; }
        public DateTime ToTime { get; set; }
        public string Status { get; set; } = FirearmBookingStatus.Reserverad;
        public string? Note { get; set; }

        /// <summary>
        /// Medföljande med rätt att hantera vapnet. Krävs för <c>Externt</c>, och bokningen gäller
        /// först när <see cref="EscortAcceptedAt"/> är satt.
        /// </summary>
        public int? EscortMemberId { get; set; }
        public DateTime? EscortAcceptedAt { get; set; }

        /// <summary>
        /// Hur lånet uppstod: <c>Web</c> · <c>Skanning</c> · <c>Valv</c> · <c>Tilldelad</c>.
        /// Låter valvlistan skilja den som var väntad från den som dök upp.
        /// </summary>
        public string Source { get; set; } = FirearmBookingSource.Web;

        public DateTime? HandedOutAt { get; set; }
        public int? HandedOutByMemberId { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public int? ReturnedByMemberId { get; set; }
        public DateTime? CancelledAt { get; set; }
        public int? CancelledByMemberId { get; set; }
        public string? CancelReason { get; set; }
        public DateTime CreatedAt { get; set; }

        // Visningsfält, inte kolumner.
        [ResultColumn] public string? MemberName { get; set; }
        [ResultColumn] public string? FirearmAlias { get; set; }
        [ResultColumn] public int? ClubWeaponNumber { get; set; }
        [ResultColumn] public string? WishedAlias { get; set; }
        [ResultColumn] public int? WishedWeaponNumber { get; set; }
        [ResultColumn] public string? EscortName { get; set; }

        /// <summary>
        /// Vapnet bokningen HÅLLER just nu: det tilldelade om det finns, annars det önskade.
        ///
        /// <para><b>⚠️ Krockkontrollen måste gå på den här</b>, inte på <see cref="FirearmId"/>.
        /// En bokning som önskade nr 7 men fick nr 4 håller nr 4 — och släpper nr 7.</para>
        /// </summary>
        [Ignore] public int? EffectiveFirearmId => AssignedFirearmId ?? FirearmId;

        [Ignore] public string StatusLabel => FirearmBookingStatus.Label(Status);
        [Ignore] public string OccasionKindLabel => FirearmOccasionKind.Label(OccasionKind);

        /// <summary>
        /// Vad tillfället ska KALLAS på skärmen: fritexten för ett externt tillfälle, annars
        /// slagets etikett.
        ///
        /// <para><b>⚠️ Läs den här, inte <see cref="OccasionLabel"/>.</b> Den senare hette förut
        /// den härledda etiketten och är nu en riktig kolumn (fritext för externa tillfällen) —
        /// två anropare läste den som etikett och hade tyst börjat visa tomt.</para>
        /// </summary>
        [Ignore] public string OccasionDisplay =>
            string.IsNullOrWhiteSpace(OccasionLabel)
                ? FirearmOccasionKind.Label(OccasionKind)
                : OccasionLabel!;
        [Ignore] public bool IsActive => FirearmBookingStatus.Blocking.Contains(Status, StringComparer.Ordinal);
        [Ignore] public bool IsOut => Status == FirearmBookingStatus.Utlamnad;

        /// <summary>Bad medlemmen om ett bestämt vapen?</summary>
        [Ignore] public bool WantsSpecificFirearm => FirearmId.HasValue;

        /// <summary>Fick hen något ANNAT än hen önskade? Vapenansvarig ska kunna se det.</summary>
        [Ignore] public bool AssignmentDiffersFromWish =>
            FirearmId.HasValue && AssignedFirearmId.HasValue && FirearmId != AssignedFirearmId;

        /// <summary>
        /// Registrerade skytten sitt eget lån (skanning) i stället för en funktionär?
        ///
        /// <para><b>HÄRLETT, ingen kolumn</b> — samma mönster som evenemangsuppropets
        /// självregistrering. En skanning är svagare bevis än en funktionärs tryck, och de två
        /// måste gå att skilja åt i efterhand.</para>
        /// </summary>
        [Ignore] public bool HandedOutBySelf =>
            HandedOutByMemberId.HasValue && HandedOutByMemberId == MemberId;

        /// <summary>Lämnar vapnet klubbens område?</summary>
        [Ignore] public bool LeavesTheClub => FirearmOccasionKind.LeavesTheClub(OccasionKind);

        /// <summary>
        /// Väntar lånet på att den medföljande accepterar? Bara meningsfullt för externa lån.
        /// </summary>
        [Ignore] public bool AwaitsEscort =>
            LeavesTheClub && EscortMemberId.HasValue && EscortAcceptedAt is null;
    }

    /// <summary>Hur ett lån uppstod. Bär ingen behörighet — bara ursprunget, för valvlistan.</summary>
    public static class FirearmBookingSource
    {
        /// <summary>Medlemmen bokade själv i förväg.</summary>
        public const string Web = "Web";

        /// <summary>Skapades av en skanning i valvet — alltså någon som inte hade bokat.</summary>
        public const string Skanning = "Skanning";

        /// <summary>Vapenansvarig lade in det på plats.</summary>
        public const string Valv = "Valv";

        /// <summary>Klubben tilldelade, t.ex. för en nybörjarkurs.</summary>
        public const string Tilldelad = "Tilldelad";

        public static readonly string[] All = { Web, Skanning, Valv, Tilldelad };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            Web => "Bokad i förväg",
            Skanning => "Skannade på plats",
            Valv => "Inlagd i valvet",
            Tilldelad => "Tilldelad av klubben",
            _ => v ?? "",
        };
    }

    /// <summary>
    /// Bokning av klubbens lånevapen.
    ///
    /// <para><b>⚠️ INGET GODKÄNNANDESTEG i v1, och det är ett val.</b> Backloggen nämnde
    /// "klubbadmin/styrelse godkänner eller autogodkänn". Men den verkliga grinden är den FYSISKA
    /// utlämningen: någon på plats hämtar vapnet ur skåpet och lämnar över det. Ett
    /// godkännandesteg ovanpå det hade lagt ett administrativt moment mellan medlemmen och en
    /// handling som ändå kräver en människa på banan — och en obehandlad bokningsförfrågan hade
    /// blivit ett nytt sätt att stå utan vapen på tävlingsdagen. Krockkontrollen hindrar
    /// dubbelbokning; klubben ser listan och kan avboka. <c>Status</c>-kolumnen rymmer ett
    /// godkännandesteg utan migrering den dag det efterfrågas.</para>
    /// </summary>
    public class FirearmBookingService
    {
        // ⚠️ Taken (365 dagar framåt, 14 dagars längsta bokning) bor i FirearmBookingWindow
        // tillsammans med fönstertolkningen, så tillgänglighetslistan och bokningen inte kan vara
        // oense om vad som är bokbart. Lägg dem inte tillbaka här som ett andra exemplar.

        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly FirearmService _firearms;
        private readonly MemberClubService _memberClubs;
        private readonly LoanWeaponClubRules _clubRules;
        private readonly ILogger<FirearmBookingService> _logger;

        public FirearmBookingService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            FirearmService firearms,
            MemberClubService memberClubs,
            LoanWeaponClubRules clubRules,
            ILogger<FirearmBookingService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _firearms = firearms;
            _memberClubs = memberClubs;
            _clubRules = clubRules;
            _logger = logger;
        }

        // ── Krockkontrollen ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bokningar som överlappar fönstret för ett vapen.
        ///
        /// <para><b>⚠️ Överlappsregeln är STRIKT här</b>, till skillnad från konfliktkontrollen i
        /// <c>MyScheduleService</c> som medvetet är försiktig. Skälet: ett schemakrock är en varning
        /// till en människa, men två personer kan fysiskt inte hålla samma vapen. Regeln är
        /// <c>NOT (befintlig.Till &lt;= ny.Från OR befintlig.Från &gt;= ny.Till)</c> — alltså tillåts
        /// exakt kant-i-kant (en bokning som slutar 12:00 och en som börjar 12:00), eftersom
        /// överlämningen sker då.</para>
        /// </summary>
        public List<FirearmBooking> Conflicts(int firearmId, DateTime from, DateTime to, int? excludeBookingId = null)
        {
            if (firearmId <= 0) return new List<FirearmBooking>();

            var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.Fetch<FirearmBooking>(
                $@"SELECT * FROM FirearmBooking
                    WHERE COALESCE(AssignedFirearmId, FirearmId) = @0
                      AND Status IN ({blocking})
                      AND NOT (ToTime <= @1 OR FromTime >= @2)
                      AND (@3 IS NULL OR Id <> @3)
                    ORDER BY FromTime",
                firearmId, from, to, excludeBookingId);
        }

        /// <summary>
        /// Hur många av klubbens lånevapen som är upptagna i ett fönster — <b>oavsett om bokningen
        /// pekar på ett bestämt vapen eller bara på en plats</b>.
        ///
        /// <para><b>⚠️ Kapacitet är en ANNAN fråga än överlapp.</b> Ett <em>vilket-som-helst</em>-lån
        /// blockerar inget bestämt vapen, men det tar en plats — och utan den räkningen kan fem
        /// platsbokningar plus ett namngivet önskemål ge sex lån på fem vapen.</para>
        ///
        /// <para><b>⚠️ Räkningen är AVSIKTLIGT inte vapengruppsuppdelad.</b> Att svara exakt på
        /// "finns det ett ledigt C-vapen" när platsbokningar kan tas av vilket vapen som helst är
        /// ett matchningsproblem, och att lösa det här vore fel sorts precision: valvskärmen är
        /// säkerhetsnätet, och vapenansvarig fördelar om på plats. Att en klass tar slut är alltså
        /// synligt för människan som ändå står vid hyllan, inte något systemet låtsas kunna räkna.</para>
        /// </summary>
        public int CountOccupiedInWindow(int clubId, DateTime from, DateTime to, int? excludeBookingId = null)
        {
            if (clubId <= 0) return 0;

            var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.ExecuteScalar<int>(
                    $@"SELECT COUNT(*) FROM FirearmBooking b
                        WHERE b.ClubId = @0
                          AND b.Status IN ({blocking})
                          AND NOT (b.ToTime <= @1 OR b.FromTime >= @2)
                          AND (@3 IS NULL OR b.Id <> @3)
                          AND (b.FirearmId IS NULL AND b.AssignedFirearmId IS NULL
                               OR EXISTS (SELECT 1 FROM Firearm f
                                           WHERE f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                                             AND f.IsActive = 1))",
                    clubId, from, to, excludeBookingId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte räkna upptagna lånevapen i klubb {ClubId}.", clubId);
                return 0;
            }
        }

        /// <summary>
        /// Vapnet medlemmen brukar få i klubben, eller <c>null</c>.
        ///
        /// <para>Driver förvalet <em>"nr 7, som förra gången"</em>. <b>Vanligast, inte senast</b> —
        /// en enstaka ersättning för att nr 7 var trasigt ska inte flytta skyttens vapen. Vid lika
        /// antal vinner det senast utlämnade.</para>
        /// </summary>
        public int? UsualFirearmFor(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return null;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.FirstOrDefault<int?>(
                    @"SELECT TOP 1 AssignedFirearmId FROM FirearmBooking
                       WHERE MemberId = @0 AND ClubId = @1 AND AssignedFirearmId IS NOT NULL
                       GROUP BY AssignedFirearmId
                       ORDER BY COUNT(*) DESC, MAX(HandedOutAt) DESC",
                    memberId, clubId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa vanligaste vapen för medlem {MemberId}.", memberId);
                return null;
            }
        }

        /// <summary>
        /// Vilka av klubbens lånevapen som är LEDIGA i ett fönster. Driver /lanevapen.
        ///
        /// <para><b>En fråga för hela klubben</b>, inte en krockkontroll per vapen — listan kan ha
        /// femtio rader och mönstret ska inte behöva ändras då.</para>
        /// </summary>
        public HashSet<int> BookedFirearmIds(int clubId, DateTime from, DateTime to)
        {
            if (clubId <= 0) return new HashSet<int>();

            var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                // ⚠️ Det EFFEKTIVA vapnet, och NULL utesluts: en platsbokning håller inget
                // bestämt vapen, så den får inte göra något vapen obokbart. Kapaciteten den tar
                // räknas av CountOccupiedInWindow i stället.
                return uow.Database.Fetch<int>(
                    $@"SELECT DISTINCT COALESCE(AssignedFirearmId, FirearmId) FROM FirearmBooking
                        WHERE ClubId = @0
                          AND Status IN ({blocking})
                          AND NOT (ToTime <= @1 OR FromTime >= @2)
                          AND COALESCE(AssignedFirearmId, FirearmId) IS NOT NULL",
                    clubId, from, to).ToHashSet();
            }
            catch (Exception ex)
            {
                // ⚠️ Tabellen saknas = migreringen inte körd. Att svara "inget är bokat" är rätt
                // degradering: listan visar allt som ledigt, vilket är exakt läget före punkt 6.
                _logger.LogDebug(ex, "Kunde inte läsa bokningar för klubb {ClubId}.", clubId);
                return new HashSet<int>();
            }
        }

        // ── Skapa ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bokar ett lånevapen — antingen ett BESTÄMT vapen eller bara en PLATS.
        ///
        /// <para><b>⚠️ Krockkontrollen görs INNE I samma transaktion som insättningen, med
        /// UPDLOCK/HOLDLOCK.</b> Läses den utanför kan två samtidiga bokningar båda se ett ledigt
        /// vapen och båda landa — och två personer som tror att de har samma pistol på
        /// tävlingsdagen är precis det funktionen finns för att förhindra. Ett unikt index kan inte
        /// uttrycka regeln, eftersom "överlappar i tid" är ett intervallvillkor och inte en
        /// likhet.</para>
        ///
        /// <para><b>⚠️ TVÅ SLAGS SPÄRR, och de mäter olika saker.</b> Ett namngivet önskemål
        /// blockeras av att just det vapnet är taget (överlapp). En platsbokning blockeras bara av
        /// att klubbens vapen är slut (kapacitet). Båda gäller för båda: ett namngivet önskemål tar
        /// också en plats.</para>
        /// </summary>
        public (int BookingId, string? Error) Create(FirearmBookingRequest request)
        {
            if (request is null) return (0, "Ogiltig begäran.");
            if (request.MemberId <= 0) return (0, "Du måste vara inloggad.");

            var clubId = request.ClubId;
            Firearm? wished = null;

            if (request.FirearmId is int wishedId && wishedId > 0)
            {
                wished = _firearms.GetById(wishedId);
                if (wished is null) return (0, "Vapnet hittades inte.");

                var vapenError = ValidateLoanable(wished);
                if (vapenError is not null) return (0, vapenError);

                // ⚠️ Klubben tas från VAPNET när ett vapen är valt, aldrig från begäran — annars
                // vore ett klubb-id i anropet ett sätt att boka i en klubb man inte tillhör.
                clubId = wished.ScopeId;
            }

            if (clubId <= 0) return (0, "Välj vilken klubbs vapen det gäller.");

            // Medlemskapet är grinden — klubbens lånevapen är för klubbens egna medlemmar. Det är
            // den carve-out som gör funktionen förenlig med "inget publikt bokningssystem".
            var member = _memberService.GetById(request.MemberId);
            if (!_memberClubs.GetAllClubIds(member).Contains(clubId))
                return (0, "Du kan bara boka lånevapen i en klubb du är medlem i.");

            if (!FirearmOccasionKind.IsValid(request.OccasionKind))
                return (0, "Okänt slag av tillfälle.");

            var kind = request.OccasionKind.Trim();
            var occId = FirearmOccasionKind.HasNodeId(kind) ? Math.Max(0, request.OccasionId) : 0;
            if (FirearmOccasionKind.HasNodeId(kind) && occId == 0)
                return (0, "Välj vilket tillfälle bokningen gäller.");

            var label = Trim(request.OccasionLabel, 200);
            if (FirearmOccasionKind.LeavesTheClub(kind) && string.IsNullOrWhiteSpace(label))
                return (0, "Skriv vilket arrangemang lånet gäller.");

            var rules = _clubRules.For(clubId);

            // ⚠️ Klubbens val, av som standard. Ett vapen får inte lämna banan utan att klubben
            // beslutat att det ska vara möjligt — och saknas egenskapen är svaret nej, vilket är
            // den försiktiga defaulten.
            if (FirearmOccasionKind.LeavesTheClub(kind) && !rules.AllowExternal)
                return (0, "Klubben tillåter inte att lånevapen tas utanför banan. " +
                           "Prata med klubbens styrelse om det behövs.");

            // ⚠️ Ett lån som lämnar klubben kräver en namngiven medföljande. Nybörjaren får inte
            // transportera eller inneha vapnet själv, och den fysiska utlämningen är INTE grinden
            // här — vapnet är borta i flera dagar. Det är det enda stället ett godkännandesteg är
            // motiverat, och därför det enda som har ett.
            if (FirearmOccasionKind.LeavesTheClub(kind))
            {
                if (request.EscortMemberId is null or <= 0)
                    return (0, "Ange vem från klubben som följer med och ansvarar för vapnet.");
                if (request.EscortMemberId == request.MemberId)
                    return (0, "Den som lånar kan inte vara sin egen ansvariga.");
                if (!_memberClubs.GetAllClubIds(_memberService.GetById(request.EscortMemberId.Value))
                                 .Contains(clubId))
                    return (0, "Den ansvariga måste vara medlem i samma klubb.");
            }

            var vapengrupp = Trim(request.WeaponClass, 20);
            if (!FirearmWeaponGroups.IsValid(vapengrupp))
                return (0, $"Okänd vapengrupp '{vapengrupp}'.");

            var from = request.From;
            var to = request.To;
            var windowError = NormaliseWindow(ref from, ref to);
            if (windowError is not null) return (0, windowError);

            // ⚠️ Horisonten gäller MEDLEMMENS egna bokningar, inte klubbens tilldelningar. Hela
            // poängen med en horisont är att hindra att en person låser ett vapen en hel säsong —
            // medan en nybörjarkurs SKA kunna tilldelas alla åtta gångerna på en gång. Skiljer vi
            // inte på dem blir kurstilldelningen omöjlig i just de klubbar som satt en gräns.
            var isClubAssignment = string.Equals(
                request.Source, FirearmBookingSource.Tilldelad, StringComparison.Ordinal);

            if (!isClubAssignment && !rules.WithinHorizon(from, DateTime.Now))
            {
                return (0, $"Klubben tar bokningar högst {rules.HorizonDays} dagar framåt. " +
                           "Prova igen närmare tillfället.");
            }

            var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            if (wished is not null)
            {
                var clash = db.Fetch<FirearmBooking>(
                    $@"SELECT * FROM FirearmBooking WITH (UPDLOCK, HOLDLOCK)
                        WHERE COALESCE(AssignedFirearmId, FirearmId) = @0
                          AND Status IN ({blocking})
                          AND NOT (ToTime <= @1 OR FromTime >= @2)",
                    wished.Id, from, to);

                if (clash.Count > 0)
                {
                    var c = clash[0];
                    // Namnge NÄR det krockar OCH vad alternativet är. "Vapnet är bokat" utan tid
                    // lämnar medlemmen utan nästa steg — och det är just den medlem som funderar
                    // på om det är värt att komma.
                    var nr = wished.ClubWeaponNumber.HasValue ? $"Nr {wished.ClubWeaponNumber}" : "Vapnet";
                    return (0, $"{nr} är redan bokat {c.FromTime:yyyy-MM-dd HH:mm}–{c.ToTime:HH:mm}. " +
                               "Välj ett annat vapen, eller \"vilket som helst\".");
                }
            }

            // Kapaciteten. Läses i SAMMA transaktion och med samma lås som ovan, av samma skäl.
            var loanable = _firearms.CountLoanable(clubId);
            var occupied = db.ExecuteScalar<int>(
                $@"SELECT COUNT(*) FROM FirearmBooking b WITH (UPDLOCK, HOLDLOCK)
                    WHERE b.ClubId = @0
                      AND b.Status IN ({blocking})
                      AND NOT (b.ToTime <= @1 OR b.FromTime >= @2)
                      -- ⚠️ En bokning vars önskade vapen är utgallrat eller borttaget ur klubbens
                      -- lista kan aldrig infrias, och ska därför inte äta en plats från någon
                      -- annan. Platsbokningar (inget vapen) räknas alltid.
                      AND (b.FirearmId IS NULL AND b.AssignedFirearmId IS NULL
                           OR EXISTS (SELECT 1 FROM Firearm f
                                       WHERE f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                                         AND f.IsActive = 1))",
                clubId, from, to);

            if (occupied >= loanable)
            {
                return (0, loanable == 0
                    ? "Klubben har inga lånevapen att boka."
                    : $"Alla klubbens {loanable} lånevapen är bokade då. Prova en annan tid.");
            }

            var row = new FirearmBooking
            {
                FirearmId = wished?.Id,
                AssignedFirearmId = null,
                WeaponClass = wished is not null ? null : NullIfBlank(vapengrupp),
                MemberId = request.MemberId,
                ClubId = clubId,
                OccasionKind = kind,
                OccasionId = occId,
                OccasionLabel = label,
                FromTime = from,
                ToTime = to,
                Status = FirearmBookingStatus.Reserverad,
                Note = Trim(request.Note, 500),
                EscortMemberId = FirearmOccasionKind.LeavesTheClub(kind) ? request.EscortMemberId : null,
                EscortAcceptedAt = null,
                Source = FirearmBookingSource.IsValid(request.Source)
                    ? request.Source!.Trim() : FirearmBookingSource.Web,
                CreatedAt = DateTime.Now,
            };
            db.Insert(row);

            _logger.LogInformation(
                "Lånevapen bokat: {Vapen}, medlem {MemberId}, {From}–{To}, källa {Source}.",
                wished is null ? "plats (vilket som helst)" : $"vapen {wished.Id}",
                request.MemberId, from, to, row.Source);

            return (row.Id, null);
        }

        /// <summary>
        /// Får vapnet lånas ut alls? Samma svar oavsett om det bokas i förväg eller skannas i valvet
        /// — annars kan skanningen släppa igenom ett vapen bokningen vägrar.
        /// </summary>
        private static string? ValidateLoanable(Firearm firearm)
        {
            if (firearm.Scope.Kind != FirearmOwnerKind.Club)
                return "Bara klubbens vapen kan lånas.";
            if (!firearm.IsLoanable)
                return "Vapnet är inte utlånbart.";
            if (!firearm.IsActive)
                return "Vapnet finns inte längre i klubbens lista.";

            // ⚠️ Service och utgallrat blockerar OAVSETT kalender — det är ett fysiskt läge.
            // 'Utlanat' gör det inte: det är en grov administrativ flagga, och den verkliga
            // tillgängligheten är bokningskalendern.
            if (firearm.Status is FirearmStatus.Service or FirearmStatus.Utgallrat)
                return $"Vapnet är markerat \"{firearm.Status}\" och kan inte lånas ut.";

            return null;
        }

        private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

        /// <summary>
        /// Normaliserar fönstret och vägrar det orimliga.
        ///
        /// <para><b>⚠️ Regeln ägs av <see cref="FirearmBookingWindow.TryNormalise"/>, inte av den
        /// här metoden.</b> Den låg tidigare i två handskrivna kopior — här och i
        /// <c>LoanWeaponApiController.TryWindow</c> — som hade glidit isär om ett bakvänt fönster:
        /// listan visade vapnet som ledigt hela dagen medan bokningen vägrade samma fönster.</para>
        /// </summary>
        private static string? NormaliseWindow(ref DateTime from, ref DateTime to)
        {
            var ok = FirearmBookingWindow.TryNormalise(
                from, to, DateTime.Now, out var f, out var t, out var error);
            if (!ok) return error ?? "Ogiltigt bokningsfönster.";

            from = f;
            to = t;
            return null;
        }

        // ── Valvet ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lånen som hör till ETT tillfälle. Driver valvskärmen, som är den enda ytan som måste
        /// fungera för en teknikrädd vapenansvarig.
        ///
        /// <para>Ordningen är arbetsordningen: de som inte lämnats ut först, sedan de utlämnade,
        /// sist de avslutade. Inom varje grupp namnordning, för listan läses mot en kö av personer.</para>
        /// </summary>
        public List<FirearmBooking> GetForOccasion(int clubId, string occasionKind, int occasionId)
        {
            if (clubId <= 0) return new List<FirearmBooking>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<FirearmBooking>(
                    @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber,
                             w.Alias AS WishedAlias, w.ClubWeaponNumber AS WishedWeaponNumber
                        FROM FirearmBooking b
                        LEFT JOIN Firearm f ON f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                        LEFT JOIN Firearm w ON w.Id = b.FirearmId
                       WHERE b.ClubId = @0 AND b.OccasionKind = @1 AND b.OccasionId = @2
                       ORDER BY CASE b.Status
                                  WHEN 'Reserverad' THEN 0
                                  WHEN 'Utlamnad'   THEN 1
                                  ELSE 2 END,
                                b.Id",
                    clubId, (occasionKind ?? "").Trim(), occasionId);
                ResolveNames(rows);
                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa lån för tillfälle {Kind}:{Id}.", occasionKind, occasionId);
                return new List<FirearmBooking>();
            }
        }

        /// <summary>
        /// Vilka av klubbens lånevapen som är LEDIGA i ett fönster, som hela rader.
        /// Driver vapenväljaren i valvet och kontrollen vid skanning.
        /// </summary>
        public List<Firearm> AvailableInWindow(int clubId, DateTime from, DateTime to)
        {
            if (clubId <= 0) return new List<Firearm>();

            var taken = BookedFirearmIds(clubId, from, to);
            return _firearms.GetForScope(FirearmScope.Club(clubId))
                .Where(f => f.IsLoanable
                            && f.Status is not (FirearmStatus.Service or FirearmStatus.Utgallrat)
                            && !taken.Contains(f.Id))
                .OrderBy(f => f.ClubWeaponNumber ?? int.MaxValue)
                .ThenBy(f => f.Alias, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Stänger kvällen: allt som är utlämnat eller reserverat på tillfället blir återlämnat.
        ///
        /// <para><b>⚠️ Det här är knappen som avgör om registret överlever.</b> Ingen skannar när
        /// de ska hem, och sex tryck klockan nio är exakt när registerföringen upphör. Knappen
        /// knyts till en ritual vapenansvarig redan har — att låsa valvet — och den är fysiskt sann
        /// just då, för vapnen ligger framför honom.</para>
        ///
        /// <para><b>⚠️ ALDRIG automatiskt.</b> Ett tidsstyrt "alla tillbaka" hade påstått att ett
        /// vapen kommit tillbaka, vilket är en FALSK uppgift och sämre än en gammal. Och aldrig för
        /// externa lån: där finns ingen som låser ett valv, och vapnet är faktiskt borta i dagar.</para>
        ///
        /// <para><paramref name="exceptBookingIds"/> är de som ligger kvar — undantaget får kosta
        /// ett tryck.</para>
        /// </summary>
        public (int Closed, string? Error) ReturnAllForOccasion(
            int clubId, string occasionKind, int occasionId, int actorMemberId,
            IEnumerable<int>? exceptBookingIds = null)
        {
            if (clubId <= 0) return (0, "Ogiltig klubb.");
            if (FirearmOccasionKind.LeavesTheClub(occasionKind))
                return (0, "Lån utanför klubben återlämnas ett i taget, inte i klump.");

            var except = (exceptBookingIds ?? Enumerable.Empty<int>()).ToHashSet();
            var rows = GetForOccasion(clubId, occasionKind, occasionId)
                .Where(b => b.IsActive && !except.Contains(b.Id))
                .ToList();

            var closed = 0;
            foreach (var b in rows)
            {
                if (MarkReturned(b.Id, actorMemberId) is null) closed++;
            }

            _logger.LogInformation(
                "Valvet stängt för {Kind}:{Id} i klubb {ClubId}: {Closed} lån återlämnade av {Actor}.",
                occasionKind, occasionId, clubId, closed, actorMemberId);

            return (closed, null);
        }

        /// <summary>
        /// Vad en skanning av ett vapen ska leda till för den inloggade medlemmen.
        ///
        /// <para><b>⚠️ Skanningen får ALDRIG bli en grind.</b> Dyker någon upp utan bokning vill
        /// vapenansvarig låna ut ändå — det är normalfallet på en träningskväll. Nekar vi "du har
        /// ingen bokning" har vi byggt precis den spärr som kommer att kringgås, och då ljuger
        /// registret. Därför erbjuder svaret att SKAPA lånet.</para>
        ///
        /// <para>Metoden SKRIVER ingenting. Den svarar på vad som är möjligt, så gränssnittet kan
        /// fråga innan något händer — en skanning av misstag i en kameraförhandsvisning får inte
        /// lämna ut ett vapen.</para>
        /// </summary>
        public FirearmScanResult ResolveScan(int memberId, int firearmId, DateTime now)
        {
            var firearm = _firearms.GetById(firearmId);
            if (firearm is null)
                return FirearmScanResult.Refused("Vapnet hittades inte.");

            var loanError = ValidateLoanable(firearm);
            if (loanError is not null) return FirearmScanResult.Refused(loanError);

            var clubId = firearm.ScopeId;
            var member = _memberService.GetById(memberId);
            if (!_memberClubs.GetAllClubIds(member).Contains(clubId))
                return FirearmScanResult.Refused(
                    "Du är inte medlem i klubben som äger vapnet. Prata med vapenansvarig.");

            // Är vapnet redan ute hos någon? Då är det inte ledigt, och det är viktigare att säga
            // än vem som bokat.
            var out_ = ActiveLoanFor(firearmId);
            if (out_ is not null && out_.MemberId != memberId)
                return FirearmScanResult.Refused(
                    $"Nr {firearm.ClubWeaponNumber} är utlämnat till någon annan. Prata med vapenansvarig.");

            // Medlemmens egna aktiva bokningar som täcker nu.
            var mine = GetForMember(memberId)
                .Where(b => b.ClubId == clubId && b.IsActive
                            && FirearmBookingWindow.Overlaps(b.FromTime, b.ToTime, now, now.AddMinutes(1)))
                .ToList();

            // Redan utlämnat till mig — skanningen är då en återlämning.
            // Redan utlämnat till mig. ⚠️ Skanningen är då INGEN återlämning — se regeln vid
            // `FirearmScanResult`. Den bekräftar bara att vapnet står på mig, och säger vem som
            // registrerar återlämningen.
            var already = mine.FirstOrDefault(b => b.IsOut && b.AssignedFirearmId == firearmId);
            if (already is not null)
                return FirearmScanResult.OutToYou(already, firearm);

            var reserved = mine.FirstOrDefault(b => b.Status == FirearmBookingStatus.Reserverad);
            if (reserved is not null)
            {
                // ⚠️ Här ligger halva värdet i skanningen: den vet TVÅ saker som vapenansvarig inte
                // vet. Att skytten önskade ett annat vapen, OCH att det skannade vapnet är bokat av
                // någon annan i kväll — utan det andra tar Anna Bengts vapen och Bengt kommer till
                // en tom hylla.
                var wishedElsewhere = reserved.FirearmId.HasValue && reserved.FirearmId != firearmId
                    ? reserved.FirearmId
                    : null;
                var claimedByOther = Conflicts(firearmId, reserved.FromTime, reserved.ToTime, reserved.Id)
                    .FirstOrDefault();

                return FirearmScanResult.HandOut(reserved, firearm, wishedElsewhere, claimedByOther);
            }

            return FirearmScanResult.Offer(firearm, clubId);
        }

        /// <summary>Det aktiva lånet som HAR vapnet ute just nu, eller null.</summary>
        public FirearmBooking? ActiveLoanFor(int firearmId)
        {
            if (firearmId <= 0) return null;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.FirstOrDefault<FirearmBooking>(
                    @"SELECT TOP 1 * FROM FirearmBooking
                       WHERE AssignedFirearmId = @0 AND Status = @1
                       ORDER BY HandedOutAt DESC",
                    firearmId, FirearmBookingStatus.Utlamnad);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa aktivt lån för vapen {FirearmId}.", firearmId);
                return null;
            }
        }

        /// <summary>
        /// Vem som håller klubbens vapen just nu — <b>ett anrop för hela klubben</b>, nycklat på
        /// det EFFEKTIVA vapnet.
        ///
        /// <para><b>⚠️ Ett uppslag, inte ett per rad.</b> Klubbvapenlistan renderar femtio rader,
        /// och <see cref="ActiveLoanFor"/> per rad hade blivit femtio frågor på en flik som redan
        /// hämtar vapen, relationer och behörigheter. Samma skäl som relationerna i
        /// <c>FirearmService.GetForScope</c> hämtas i två frågor för hela listan.</para>
        ///
        /// <para><b>⚠️ Läs ALDRIG <c>Firearm.Status</c> för att svara på den här frågan.</b> Den
        /// är en grov manuell flagga som någon sätter för hand och glömmer; bokningarna är det som
        /// gäller. Att visa flaggan som om den vore ett svar är precis hur en lista slutar gå att
        /// lita på.</para>
        ///
        /// <para><b>⚠️ De två blockerande lägena betyder OLIKA saker och båda behövs.</b>
        /// <c>Utlämnad</c> = vapnet är fysiskt ute, oavsett om fönstret hunnit passera — därför
        /// finns ingen tidsvillkor på den grenen. <c>Reserverad</c> tas bara med när fönstret
        /// täcker <paramref name="now"/>: en bokning nästa månad gör inte vapnet upptaget i dag,
        /// och att visa den hade fått hela listan att se utlånad ut. Är ett vapen både utlämnat
        /// och reserverat vinner utlämningen — det är den som är sann om var vapnet ÄR.</para>
        /// </summary>
        public Dictionary<int, FirearmBooking> ActiveClaimsForClub(int clubId, DateTime now)
        {
            var result = new Dictionary<int, FirearmBooking>();
            if (clubId <= 0) return result;

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<FirearmBooking>(
                    @"SELECT b.* FROM FirearmBooking b
                       WHERE b.ClubId = @0
                         AND b.Status IN (@1, @2)
                         AND COALESCE(b.AssignedFirearmId, b.FirearmId) IS NOT NULL
                         AND (b.Status = @1 OR (b.FromTime <= @3 AND b.ToTime >= @3))",
                    clubId, FirearmBookingStatus.Utlamnad, FirearmBookingStatus.Reserverad, now);

                if (rows.Count == 0) return result;
                ResolveNames(rows);

                foreach (var b in rows)
                {
                    var id = b.EffectiveFirearmId;
                    if (id is null or <= 0) continue;

                    if (!result.TryGetValue(id.Value, out var held)) { result[id.Value] = b; continue; }

                    // Utlämnad slår reserverad; i övrigt den som börjat senast — det är den
                    // pågående, inte en kvarglömd rad från i morse.
                    var better =
                        b.Status == FirearmBookingStatus.Utlamnad
                        && held.Status != FirearmBookingStatus.Utlamnad
                        || b.Status == held.Status && b.FromTime > held.FromTime;

                    if (better) result[id.Value] = b;
                }
            }
            catch (Exception ex)
            {
                // ⚠️ Tom karta, inte ett undantag. En vapenlista utan lånekolumn är användbar; en
                // flik som inte renderar alls för att en kolumn inte gick att fylla är den inte.
                _logger.LogWarning(ex, "Kunde inte läsa aktiva lån för klubb {ClubId}.", clubId);
            }

            return result;
        }

        /// <summary>
        /// Den medföljande accepterar ansvaret för ett externt lån.
        ///
        /// <para><b>⚠️ Bara den utpekade personen själv.</b> Ett accepterande någon annan klickar
        /// åt hen är inget accepterande — och det är hela det spår vi kan lämna.</para>
        /// </summary>
        public string? AcceptEscort(int bookingId, int memberId)
        {
            var b = GetById(bookingId);
            if (b is null) return "Bokningen hittades inte.";
            if (!b.LeavesTheClub) return "Bokningen gäller inte ett lån utanför klubben.";
            if (b.EscortMemberId != memberId)
                return "Bara den som är utpekad som ansvarig kan acceptera.";
            if (b.EscortAcceptedAt is not null) return null;   // idempotent
            if (!b.IsActive) return "Bokningen är inte längre aktiv.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                "UPDATE FirearmBooking SET EscortAcceptedAt = @0 WHERE Id = @1",
                DateTime.Now, bookingId);

            _logger.LogInformation(
                "Externt lån {Id}: medlem {MemberId} accepterade ansvaret.", bookingId, memberId);
            return null;
        }

        // ── Läsning ──────────────────────────────────────────────────────────────────────────────

        public FirearmBooking? GetById(int bookingId)
        {
            if (bookingId <= 0) return null;
            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.FirstOrDefault<FirearmBooking>(
                "SELECT * FROM FirearmBooking WHERE Id = @0", bookingId);
        }

        /// <summary>Klubbens bokningar. Aktiva först, sedan avslutade — det är arbetsordningen.</summary>
        public List<FirearmBooking> GetForClub(int clubId, bool activeOnly = false)
        {
            if (clubId <= 0) return new List<FirearmBooking>();

            // ⚠️ LEFT JOIN, och på det EFFEKTIVA vapnet. En platsbokning har inget vapen alls —
            // med en INNER JOIN försvann den ur klubbens lista, tyst. Önskemålet joinas separat så
            // vapenansvarig kan se "önskade nr 7, fick nr 4".
            var sql = @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber,
                               w.Alias AS WishedAlias, w.ClubWeaponNumber AS WishedWeaponNumber
                          FROM FirearmBooking b
                          LEFT JOIN Firearm f ON f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                          LEFT JOIN Firearm w ON w.Id = b.FirearmId
                         WHERE b.ClubId = @0";
            if (activeOnly)
                sql += " AND b.Status IN (" +
                       string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'")) + ")";
            sql += @" ORDER BY CASE WHEN b.Status IN ('Reserverad','Utlamnad') THEN 0 ELSE 1 END,
                               b.FromTime";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var rows = uow.Database.Fetch<FirearmBooking>(sql, clubId);
            ResolveNames(rows);
            return rows;
        }

        /// <summary>Medlemmens egna bokningar, kommande först.</summary>
        public List<FirearmBooking> GetForMember(int memberId)
        {
            if (memberId <= 0) return new List<FirearmBooking>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<FirearmBooking>(
                    @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber,
                             w.Alias AS WishedAlias, w.ClubWeaponNumber AS WishedWeaponNumber
                        FROM FirearmBooking b
                        LEFT JOIN Firearm f ON f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                        LEFT JOIN Firearm w ON w.Id = b.FirearmId
                       WHERE b.MemberId = @0
                       ORDER BY CASE WHEN b.Status IN ('Reserverad','Utlamnad') THEN 0 ELSE 1 END,
                                b.FromTime", memberId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa bokningar för medlem {MemberId}.", memberId);
                return new List<FirearmBooking>();
            }
        }

        /// <summary>
        /// Lånen där <paramref name="memberId"/> är utsedd medföljande.
        ///
        /// <para><b>⚠️ Den utsedde har inte sagt ja bara för att någon skrev in hans namn.</b> Ett
        /// externt lån vilar på att en person med rätt att hantera vapnet FAKTISKT följer med, och
        /// den personen kan vara helt ovetande om att han står som ansvarig. Utan den här listan
        /// finns ingen yta där han får frågan alls — och då blir hans ansvar en uppgift i en
        /// databas i stället för ett åtagande.</para>
        /// </summary>
        public List<FirearmBooking> GetForEscort(int memberId, bool pendingOnly = false)
        {
            if (memberId <= 0) return new List<FirearmBooking>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<FirearmBooking>(
                    @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber,
                             w.Alias AS WishedAlias, w.ClubWeaponNumber AS WishedWeaponNumber
                        FROM FirearmBooking b
                        LEFT JOIN Firearm f ON f.Id = COALESCE(b.AssignedFirearmId, b.FirearmId)
                        LEFT JOIN Firearm w ON w.Id = b.FirearmId
                       WHERE b.EscortMemberId = @0
                         AND b.Status IN ('Reserverad','Utlamnad')
                       ORDER BY b.FromTime", memberId);

                if (pendingOnly)
                    rows = rows.Where(r => r.EscortAcceptedAt is null).ToList();

                ResolveNames(rows);
                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa medföljandelån för {MemberId}.", memberId);
                return new List<FirearmBooking>();
            }
        }

        // ── Ändra ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Avbokar.
        ///
        /// <para><b>⚠️ En UTLÄMNAD bokning kan inte avbokas.</b> Vapnet är fysiskt hos medlemmen, och
        /// att låta bokningen försvinna vore att tappa spåret till vem som har det. Den ska
        /// återlämnas i stället, och felmeddelandet säger det.</para>
        /// </summary>
        public string? Cancel(int bookingId, int actorMemberId, bool actorIsClubStaff, string? reason)
        {
            var b = GetById(bookingId);
            if (b is null) return "Bokningen hittades inte.";

            if (b.Status == FirearmBookingStatus.Avbokad) return null;   // idempotent
            if (b.Status == FirearmBookingStatus.Aterlamnad)
                return "Bokningen är redan avslutad.";
            if (b.Status == FirearmBookingStatus.Utlamnad)
                return "Vapnet är utlämnat och måste återlämnas, inte avbokas.";

            if (!actorIsClubStaff && b.MemberId != actorMemberId)
                return "Du kan bara avboka dina egna bokningar.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                @"UPDATE FirearmBooking
                     SET Status = @0, CancelledAt = @1, CancelledByMemberId = @2, CancelReason = @3
                   WHERE Id = @4",
                FirearmBookingStatus.Avbokad, DateTime.Now, actorMemberId, Trim(reason, 500), bookingId);

            _logger.LogInformation("Bokning {Id} avbokad av medlem {Actor}.", bookingId, actorMemberId);
            return null;
        }

        /// <summary>
        /// Registrerar utlämning och <b>vilket vapen som faktiskt gick ut</b>.
        ///
        /// <para><b>⚠️ Vapnet är ett argument, inte något som läses ur bokningen.</b> Önskemålet
        /// är hoppet; det utlämnade är fakta. Anna kan ha önskat nr 7 och få nr 4 för att nr 7:s
        /// slutstycke ligger isär — och registret måste peka på nr 4, annars tror klubben att nr 7
        /// är ute resten av kvällen.</para>
        ///
        /// <para><paramref name="actorMemberId"/> är den som registrerar. Är det medlemmen själv
        /// (en skanning) blir <c>HandedOutBySelf</c> sant av sig själv — en skanning är svagare
        /// bevis än en funktionärs tryck, och de två ska gå att skilja åt i efterhand.</para>
        /// </summary>
        public string? MarkHandedOut(int bookingId, int actorMemberId, int assignedFirearmId)
        {
            var b = GetById(bookingId);
            if (b is null) return "Bokningen hittades inte.";
            if (b.Status == FirearmBookingStatus.Utlamnad) return null;  // idempotent
            if (b.Status != FirearmBookingStatus.Reserverad)
                return $"Bokningen är {FirearmBookingStatus.Label(b.Status).ToLowerInvariant()} och kan inte lämnas ut.";

            if (assignedFirearmId <= 0) return "Ange vilket vapen som lämnas ut.";

            var firearm = _firearms.GetById(assignedFirearmId);
            if (firearm is null) return "Vapnet hittades inte.";
            if (firearm.ScopeId != b.ClubId || firearm.Scope.Kind != FirearmOwnerKind.Club)
                return "Vapnet tillhör inte samma klubb som bokningen.";

            var loanError = ValidateLoanable(firearm);
            if (loanError is not null) return loanError;

            // ⚠️ Två personer kan fysiskt inte hålla samma vapen. Den här kontrollen finns för att
            // vapenansvarig får byta vapen fritt — och då måste det bytta vapnet vara ledigt.
            var already = ActiveLoanFor(assignedFirearmId);
            if (already is not null && already.Id != bookingId)
            {
                var nr = firearm.ClubWeaponNumber.HasValue ? $"Nr {firearm.ClubWeaponNumber}" : "Vapnet";
                return $"{nr} är redan utlämnat och inte återlämnat.";
            }

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                @"UPDATE FirearmBooking
                     SET Status = @0, HandedOutAt = @1, HandedOutByMemberId = @2, AssignedFirearmId = @3
                   WHERE Id = @4",
                FirearmBookingStatus.Utlamnad, DateTime.Now, actorMemberId, assignedFirearmId, bookingId);

            _logger.LogInformation(
                "Lånevapen utlämnat: bokning {Id}, vapen {Firearm}, registrerat av {Actor}{Self}.",
                bookingId, assignedFirearmId, actorMemberId,
                actorMemberId == b.MemberId ? " (skytten själv)" : "");
            return null;
        }

        /// <summary>
        /// Registrerar återlämning.
        ///
        /// <para><b>⚠️ Går även från <c>Reserverad</c>.</b> En återlämning som kräver att utlämningen
        /// registrerats först skulle stranda varje vapen där funktionären glömde första klicket — och
        /// då är alternativet att låta bokningen blockera vapnet i evighet.</para>
        /// </summary>
        public string? MarkReturned(int bookingId, int actorMemberId)
        {
            var b = GetById(bookingId);
            if (b is null) return "Bokningen hittades inte.";
            if (b.Status == FirearmBookingStatus.Aterlamnad) return null;  // idempotent
            if (b.Status == FirearmBookingStatus.Avbokad)
                return "Bokningen är avbokad.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                "UPDATE FirearmBooking SET Status = @0, ReturnedAt = @1, ReturnedByMemberId = @2 WHERE Id = @3",
                FirearmBookingStatus.Aterlamnad, DateTime.Now, actorMemberId, bookingId);
            return null;
        }

        /// <summary>Antal aktiva bokningar — badgen på klubbens flik.</summary>
        public int CountActiveForClub(int clubId)
        {
            if (clubId <= 0) return 0;
            try
            {
                var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.ExecuteScalar<int>(
                    $"SELECT COUNT(*) FROM FirearmBooking WHERE ClubId = @0 AND Status IN ({blocking})",
                    clubId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte räkna bokningar för klubb {ClubId}.", clubId);
                return 0;
            }
        }

        private void ResolveNames(List<FirearmBooking> rows)
        {
            // ⚠️ EN uppslagning per medlem, inte per rad. Valvlistan kan ha ett tjugotal lån och
            // samma person flera gånger.
            var wanted = rows.Select(r => r.MemberId)
                .Concat(rows.Where(r => r.EscortMemberId.HasValue).Select(r => r.EscortMemberId!.Value))
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var names = new Dictionary<int, string>();
            foreach (var id in wanted)
            {
                var m = _memberService.GetById(id);
                if (m is null) continue;
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                names[id] = string.IsNullOrWhiteSpace(name) ? m.Name ?? $"Medlem {id}" : name;
            }

            foreach (var r in rows)
            {
                if (names.TryGetValue(r.MemberId, out var n)) r.MemberName = n;
                // Den medföljandes namn är hela poängen med raden på ett externt lån — ett id
                // säger inte vem som ansvarar för vapnet.
                if (r.EscortMemberId is int e && names.TryGetValue(e, out var en)) r.EscortName = en;
            }
        }

        private static string? Trim(string? v, int max) =>
            string.IsNullOrWhiteSpace(v) ? null : (v.Length <= max ? v.Trim() : v.Trim()[..max]);
    }
    /// <summary>
    /// En begäran om att boka ett lånevapen.
    ///
    /// <para>Ett objekt i stället för nio parametrar, eftersom fälten hänger ihop i grupper:
    /// vapnet <em>eller</em> vapengruppen, tillfället <em>eller</em> fritexten, och den
    /// medföljande bara för externa lån.</para>
    /// </summary>
    public class FirearmBookingRequest
    {
        public int MemberId { get; set; }

        /// <summary>Krävs bara när inget bestämt vapen valts — annars tas den ur vapnet.</summary>
        public int ClubId { get; set; }

        /// <summary>Önskat vapen. <c>null</c> = vilket som helst.</summary>
        public int? FirearmId { get; set; }

        /// <summary>Önskad vapengrupp när inget bestämt vapen valts.</summary>
        public string? WeaponClass { get; set; }

        public string OccasionKind { get; set; } = FirearmOccasionKind.Fritt;
        public int OccasionId { get; set; }

        /// <summary>Fritext, krävs för <c>Externt</c>.</summary>
        public string? OccasionLabel { get; set; }

        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string? Note { get; set; }

        /// <summary>Medföljande med rätt att hantera vapnet. Krävs för <c>Externt</c>.</summary>
        public int? EscortMemberId { get; set; }

        public string? Source { get; set; } = FirearmBookingSource.Web;
    }
    /// <summary>Vad en skanning av ett vapen betyder för den inloggade medlemmen.</summary>
    public enum FirearmScanAction
    {
        /// <summary>Går inte. <see cref="FirearmScanResult.Message"/> säger varför.</summary>
        Refused,

        /// <summary>Medlemmen har en reservation — vapnet kan lämnas ut mot den.</summary>
        HandOut,

        /// <summary>
        /// Vapnet är redan ute hos den som skannar. <b>Ett upplysningsläge, ingen handling</b> —
        /// se regeln vid <c>FirearmScanResult</c>: en skanning får bara öka det man svarar för.
        /// </summary>
        OutToYou,

        /// <summary>
        /// Ingen bokning, men lånet kan skapas på plats.
        ///
        /// <para><b>⚠️ Det här är varför skanningen inte är en grind.</b> Dyker någon upp utan
        /// bokning vill vapenansvarig låna ut ändå — nekar vi har vi byggt en spärr som kommer att
        /// kringgås, och då ljuger registret.</para>
        /// </summary>
        Offer,
    }

    // ⚠️⚠️ REGELN SOM STYR HELA SKANNINGEN, tillagd 2026-09-02 efter Stefans fråga:
    //
    //     EN SKANNING FÅR BARA ÖKA DET MAN SVARAR FÖR, ALDRIG MINSKA DET.
    //
    // Utlämning via egen skanning är säker eftersom den bara lägger ansvar PÅ skytten — hen säger
    // "jag har nr 7", och en lögn där är mot hens eget intresse. Återlämning är det motsatta: den
    // tar bort ansvar, och lånet stängs då av den enda person som har intresse av att det ser
    // stängt ut. Skytten kunde skanna vid bilen och åka hem, och registret hade sagt att vapnet
    // står i skåpet.
    //
    // ⚠️ Skadan är INTE att skanningen möjliggör stölden — inget i systemet hindrar någon från att
    // bära ut ett vapen, och den som inte skannar alls lämnar tvärtom ett ÖPPET lån som syns.
    // Skadan är att den DÖLJER den: felet biasas mot "vi tror att vapnet är tillbaka", vilket för
    // ett vapenregister är det enda felet som inte får finnas. Ett kvarglömt öppet lån är billigt;
    // ett falskt återlämnat är det som gör hela registret oanvändbart som underlag.
    //
    // Därför: en återlämning registreras av den som TAR EMOT vapnet — vapenansvarigs "Tillbaka" på
    // raden i valvet, eller "Kvällen är klar" när hen låser. Det är också det enda som fysiskt
    // motsvarar en överlämning av ansvar.

    /// <summary>
    /// Svaret på en skanning. <b>Beskriver bara vad som är möjligt</b> — ingenting är skrivet, så
    /// gränssnittet kan fråga innan något händer. En skanning av misstag i en
    /// kameraförhandsvisning får inte lämna ut ett vapen.
    /// </summary>
    public class FirearmScanResult
    {
        public FirearmScanAction Action { get; private init; }
        public string? Message { get; private init; }
        public FirearmBooking? Booking { get; private init; }
        public Firearm? Firearm { get; private init; }
        public int ClubId { get; private init; }

        /// <summary>Medlemmen önskade ett ANNAT vapen än det skannade. Varna, men tillåt.</summary>
        public int? WishedFirearmId { get; private init; }

        /// <summary>
        /// Det skannade vapnet är bokat av någon ANNAN i fönstret.
        ///
        /// <para><b>⚠️ Det är den viktigare halvan av varningen</b> — vapenansvarig skulle inte
        /// heller ha vetat det, och utan den tar den ena skytten den andres vapen och den andre
        /// kommer till en tom hylla.</para>
        /// </summary>
        public FirearmBooking? ClaimedByOther { get; private init; }

        public static FirearmScanResult Refused(string message) =>
            new() { Action = FirearmScanAction.Refused, Message = message };

        public static FirearmScanResult HandOut(
            FirearmBooking booking, Firearm firearm, int? wishedFirearmId, FirearmBooking? claimedByOther) =>
            new()
            {
                Action = FirearmScanAction.HandOut,
                Booking = booking,
                Firearm = firearm,
                ClubId = booking.ClubId,
                WishedFirearmId = wishedFirearmId,
                ClaimedByOther = claimedByOther,
            };

        /// <summary>
        /// Vapnet är redan utlämnat till den som skannar.
        ///
        /// <para><b>⚠️ Det här är ett UPPLYSNINGSLÄGE, inte en handling.</b> Hette tidigare
        /// <c>Return</c> och stängde lånet — se regeln ovanför <c>FirearmScanResult</c>. Namnet är
        /// bytt just för att ingen ska läsa <c>Return</c> och bygga tillbaka knappen.</para>
        /// </summary>
        public static FirearmScanResult OutToYou(FirearmBooking booking, Firearm firearm) =>
            new()
            {
                Action = FirearmScanAction.OutToYou,
                Booking = booking,
                Firearm = firearm,
                ClubId = booking.ClubId,
            };

        public static FirearmScanResult Offer(Firearm firearm, int clubId) =>
            new() { Action = FirearmScanAction.Offer, Firearm = firearm, ClubId = clubId };
    }


}
