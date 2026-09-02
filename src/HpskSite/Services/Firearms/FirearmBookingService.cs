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

        public static readonly string[] All = { Fritt, Event, Competition };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            Fritt => "Egen tid",
            Event => "Klubbhändelse",
            Competition => "Tävling",
            _ => v ?? "",
        };
    }

    [TableName("FirearmBooking")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FirearmBooking
    {
        public int Id { get; set; }
        public int FirearmId { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public string OccasionKind { get; set; } = FirearmOccasionKind.Fritt;
        public int OccasionId { get; set; }
        public DateTime FromTime { get; set; }
        public DateTime ToTime { get; set; }
        public string Status { get; set; } = FirearmBookingStatus.Reserverad;
        public string? Note { get; set; }
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

        [Ignore] public string StatusLabel => FirearmBookingStatus.Label(Status);
        [Ignore] public string OccasionLabel => FirearmOccasionKind.Label(OccasionKind);
        [Ignore] public bool IsActive => FirearmBookingStatus.Blocking.Contains(Status, StringComparer.Ordinal);
        [Ignore] public bool IsOut => Status == FirearmBookingStatus.Utlamnad;
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
        private readonly ILogger<FirearmBookingService> _logger;

        public FirearmBookingService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            FirearmService firearms,
            MemberClubService memberClubs,
            ILogger<FirearmBookingService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _firearms = firearms;
            _memberClubs = memberClubs;
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
                    WHERE FirearmId = @0
                      AND Status IN ({blocking})
                      AND NOT (ToTime <= @1 OR FromTime >= @2)
                      AND (@3 IS NULL OR Id <> @3)
                    ORDER BY FromTime",
                firearmId, from, to, excludeBookingId);
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
                return uow.Database.Fetch<int>(
                    $@"SELECT DISTINCT FirearmId FROM FirearmBooking
                        WHERE ClubId = @0
                          AND Status IN ({blocking})
                          AND NOT (ToTime <= @1 OR FromTime >= @2)",
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
        /// Bokar ett lånevapen.
        ///
        /// <para><b>⚠️ Krockkontrollen görs INNE I samma transaktion som insättningen, med
        /// UPDLOCK/HOLDLOCK.</b> Läses den utanför kan två samtidiga bokningar båda se ett ledigt
        /// vapen och båda landa — och två personer som tror att de har samma pistol på
        /// tävlingsdagen är precis det funktionen finns för att förhindra. Ett unikt index kan inte
        /// uttrycka regeln, eftersom "överlappar i tid" är ett intervallvillkor och inte en
        /// likhet.</para>
        /// </summary>
        public (int BookingId, string? Error) Create(
            int memberId, int firearmId, string occasionKind, int occasionId,
            DateTime from, DateTime to, string? note)
        {
            if (memberId <= 0) return (0, "Ogiltig medlem.");

            var firearm = _firearms.GetById(firearmId);
            if (firearm is null) return (0, "Vapnet hittades inte.");

            if (firearm.Scope.Kind != FirearmOwnerKind.Club)
                return (0, "Bara klubbens vapen kan bokas.");
            if (!firearm.IsLoanable)
                return (0, "Vapnet är inte utlånbart.");
            if (!firearm.IsActive)
                return (0, "Vapnet finns inte längre i klubbens lista.");

            // ⚠️ Service och utgallrat blockerar NY bokning. 'Utlanat' gör det inte — det är ett
            // grovt administrativt läge, och den verkliga tillgängligheten är bokningskalendern.
            if (firearm.Status is FirearmStatus.Service or FirearmStatus.Utgallrat)
                return (0, $"Vapnet är markerat \"{firearm.Status}\" och kan inte bokas.");

            var clubId = firearm.ScopeId;

            // Medlemskapet är grinden — klubbens lånevapen är för klubbens egna medlemmar. Det är
            // den carve-out som gör funktionen förenlig med "inget publikt bokningssystem".
            var member = _memberService.GetById(memberId);
            if (!_memberClubs.GetAllClubIds(member).Contains(clubId))
                return (0, "Du kan bara boka lånevapen i en klubb du är medlem i.");

            if (!FirearmOccasionKind.IsValid(occasionKind))
                return (0, "Okänt slag av tillfälle.");

            var windowError = NormaliseWindow(ref from, ref to);
            if (windowError is not null) return (0, windowError);

            var kind = occasionKind.Trim();
            // 'Fritt' har inget id. Att lagra ett skräpvärde där skulle göra den sammansatta nyckeln
            // meningslös och en framtida uppslagning tvetydig.
            var occId = kind == FirearmOccasionKind.Fritt ? 0 : Math.Max(0, occasionId);
            if (kind != FirearmOccasionKind.Fritt && occId == 0)
                return (0, "Välj vilket tillfälle bokningen gäller.");

            var blocking = string.Join(",", FirearmBookingStatus.Blocking.Select(s => $"'{s}'"));

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var clash = db.Fetch<FirearmBooking>(
                $@"SELECT * FROM FirearmBooking WITH (UPDLOCK, HOLDLOCK)
                    WHERE FirearmId = @0
                      AND Status IN ({blocking})
                      AND NOT (ToTime <= @1 OR FromTime >= @2)",
                firearmId, from, to);

            if (clash.Count > 0)
            {
                var c = clash[0];
                // Namnge NÄR det krockar, inte bara att det gör det. "Vapnet är bokat" utan tid
                // lämnar medlemmen utan nästa steg.
                return (0, $"Vapnet är redan bokat {c.FromTime:yyyy-MM-dd HH:mm}–{c.ToTime:HH:mm}. " +
                           "Välj en annan tid eller ett annat vapen.");
            }

            var row = new FirearmBooking
            {
                FirearmId = firearmId,
                MemberId = memberId,
                ClubId = clubId,
                OccasionKind = kind,
                OccasionId = occId,
                FromTime = from,
                ToTime = to,
                Status = FirearmBookingStatus.Reserverad,
                Note = Trim(note, 500),
                CreatedAt = DateTime.Now,
            };
            db.Insert(row);

            _logger.LogInformation(
                "Lånevapen bokat: vapen {FirearmId}, medlem {MemberId}, {From}–{To}.",
                firearmId, memberId, from, to);

            return (row.Id, null);
        }

        /// <summary>
        /// Normaliserar fönstret och vägrar det orimliga.
        ///
        /// <para><b>⚠️ Regeln ägs av <see cref="FirearmBookingWindow.TryNormalise"/>, inte av den
        /// här metoden.</b> Den låg tidigare i två handskrivna kopior — här och i
        /// <c>LoanWeaponApiController.TryWindow</c> — som hade glidit isär om ett bakvänt fönster:
        /// listan visade vapnet som ledigt hela dagen medan bokningen vägrade samma fönster. En
        /// rad som ser bokbar ut och nekas i nästa klick.</para>
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

            var sql = @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber
                          FROM FirearmBooking b
                          JOIN Firearm f ON f.Id = b.FirearmId
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
                    @"SELECT b.*, f.Alias AS FirearmAlias, f.ClubWeaponNumber
                        FROM FirearmBooking b
                        JOIN Firearm f ON f.Id = b.FirearmId
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
        /// Registrerar utlämning. <b>Bara klubbens funktionärer</b> — det är en fysisk händelse som
        /// någon på plats bevittnar.
        /// </summary>
        public string? MarkHandedOut(int bookingId, int actorMemberId)
        {
            var b = GetById(bookingId);
            if (b is null) return "Bokningen hittades inte.";
            if (b.Status == FirearmBookingStatus.Utlamnad) return null;  // idempotent
            if (b.Status != FirearmBookingStatus.Reserverad)
                return $"Bokningen är {FirearmBookingStatus.Label(b.Status).ToLowerInvariant()} och kan inte lämnas ut.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                "UPDATE FirearmBooking SET Status = @0, HandedOutAt = @1, HandedOutByMemberId = @2 WHERE Id = @3",
                FirearmBookingStatus.Utlamnad, DateTime.Now, actorMemberId, bookingId);
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
            foreach (var id in rows.Select(r => r.MemberId).Distinct())
            {
                var m = _memberService.GetById(id);
                if (m is null) continue;
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                var resolved = string.IsNullOrWhiteSpace(name) ? m.Name ?? $"Medlem {id}" : name;
                foreach (var r in rows.Where(r => r.MemberId == id)) r.MemberName = resolved;
            }
        }

        private static string? Trim(string? v, int max) =>
            string.IsNullOrWhiteSpace(v) ? null : (v.Length <= max ? v.Trim() : v.Trim()[..max]);
    }
}
