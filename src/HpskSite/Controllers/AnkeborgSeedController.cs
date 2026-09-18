using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using HpskSite.Models.Firearms;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Controllers
{
    /// <summary>
    /// SLÄNG-SEEDER för demoklubben <b>Ankeborg</b> i dev — underlag till marknadsföringsfilmen
    /// "Allt er klubb får på pistol.nu" (<c>C:\Repos\HpskSite.Tutorials\klubbguide-2026</c>).
    ///
    /// ⚠️ DEV ONLY. Vägrar köra om inte ASPNETCORE_ENVIRONMENT=Development OCH
    /// anslutningssträngen pekar på en lokal server. Se
    /// memory/dotnet-run-no-launch-profile-hits-prod-db.
    ///
    /// ⚠️ ALLA MEJLADRESSER SLUTAR PÅ <c>@ankeborg.invalid</c>. <c>.invalid</c> är reserverat av
    /// RFC 2606 och kan aldrig levereras — inget utskick kan nå en påhittad person ens om
    /// seeddatan råkar följa med någonstans. Byt aldrig till en riktig domän.
    ///
    /// ⚠️ INGA DISNEYNAMN. Klubbnamnet Ankeborg är fritt, men figurnamnen är varumärken och
    /// filmen ska ligga publikt på YouTube. Rollistan nedan är neutrala påhittade namn och
    /// matchar rollistan i klubbguide-2026/MANUS.md — håll dem i synk, en person som byter namn
    /// mellan två klipp avslöjar att bilderna är monterade.
    ///
    /// Anrop (dev, ingen inloggning krävs) — kör ALLTID dryRun först:
    ///   /umbraco/surface/AnkeborgSeed/Status?confirm=ANKEBORG
    ///   /umbraco/surface/AnkeborgSeed/Seed?confirm=ANKEBORG&amp;dryRun=true
    ///   /umbraco/surface/AnkeborgSeed/Seed?confirm=ANKEBORG&amp;dryRun=false
    ///   /umbraco/surface/AnkeborgSeed/SeedActivity?confirm=ANKEBORG&amp;dryRun=false
    ///   /umbraco/surface/AnkeborgSeed/SeedProgram?confirm=ANKEBORG&amp;dryRun=false
    ///   /umbraco/surface/AnkeborgSeed/Purge?confirm=ANKEBORG&amp;reallyDelete=true
    ///
    /// ⚠️ VARJE STEG MÅSTE VARA IDEMPOTENT. Stegen körs om så fort en del av dem behöver rättas,
    /// och en del utan dubblettspärr skriver då en gång till. Märkesserierna blev åtta i stället
    /// för fyra av precis det, och panelen visade en guldfodring som såg dubbelt uppfylld ut.
    ///
    /// ⚠️ ANMÄLNINGAR SAVE():AS MEN PUBLICERAS ALDRIG — en publicerad anmälningshubb är en
    /// ogrindad publik sida med namn, klubb och skytteklass.
    ///
    /// Filen är avsedd att raderas när filmen är inspelad.
    /// </summary>
    public class AnkeborgSeedController : SurfaceController
    {
        private const string Confirm = "ANKEBORG";
        private const string ClubNodeName = "Ankeborg Pistolklubb";
        private const string ClubDisplayName = "Ankeborg Pistolklubb";
        private const string MailDomain = "ankeborg.invalid";
        private const string SeriesNodeName = "Ankeborgsserien";

        /// <summary>Sätts på allt seedern skapar så att <see cref="Purge"/> kan hitta igen det.</summary>
        private const string SeedTag = "Ankeborg-demodata (seed)";

        /// <summary>Externt förbundsnummer på klubbnoden. 999 finns inte hos SPSF — med flit.</summary>
        private const int FictionalExternalClubId = 999;

        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly FirearmService _firearmService;
        private readonly ForeningsintygRequestService _intygRequests;
        private readonly FirearmBookingService _bookings;
        private readonly BoardRoleService _boardRoles;
        private readonly BoardMeetingService _boardMeetings;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IConfiguration _configuration;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;
        private readonly ILogger<AnkeborgSeedController> _logger;

        public AnkeborgSeedController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IContentService contentService,
            ClubMembershipService clubMembershipService,
            FirearmService firearmService,
            ForeningsintygRequestService intygRequests,
            FirearmBookingService bookings,
            BoardRoleService boardRoles,
            BoardMeetingService boardMeetings,
            IMemberManager memberManager,
            IConfiguration configuration,
            Microsoft.AspNetCore.Hosting.IWebHostEnvironment env,
            ILogger<AnkeborgSeedController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _clubMembershipService = clubMembershipService;
            _firearmService = firearmService;
            _intygRequests = intygRequests;
            _bookings = bookings;
            _boardRoles = boardRoles;
            _boardMeetings = boardMeetings;
            _memberManager = memberManager;
            _databaseFactory = databaseFactory;
            _configuration = configuration;
            _env = env;
            _logger = logger;
        }

        // ── Rollista ─────────────────────────────────────────────────────────────────
        // De sju första är filmens namngivna personer (MANUS.md "Rollista"). Resten fyller
        // medlemslistan så att klubben ser levande ut i bild — en tom klubb säljer ingenting,
        // en klubb med 400 medlemmar ser inte ut som tittarens klubb.

        private sealed record Person(
            string First,
            string Last,
            string Role,          // fri etikett, bara för seederns egen redovisning
            string? ShooterClass, // precisionShooterClass
            bool Pending = false);

        private static readonly List<Person> Roster = new()
        {
            new("Sigrid",       "Almkvist",    "klubbadmin (filmens 'vi')", "2"),
            new("Nils",         "Tegelberg",   "väntande ansökan",          null, Pending: true),
            new("Britt-Marie",  "Ekvall",      "skjutledare",               "1"),
            new("Hasse",        "Lindwall",    "aktivitet + föreningsintyg","2"),
            new("Yvonne",       "Sjöstrand",   "guldserieskytt",            "1"),
            new("Gunnar",       "Falkenmark",  "ordförande",                "3"),
            new("Elin",         "Hagberg",     "medlemmen på Min Sida",     "2"),

            new("Torbjörn",  "Rydell",     "medlem", "3"),
            new("Margareta", "Wiklund",    "medlem", "2"),
            new("Kenneth",   "Blomgren",   "medlem", "1"),
            new("Anneli",    "Sundström",  "medlem", "2"),
            new("Rolf",      "Hjelmberg",  "medlem", "3"),
            new("Ingela",    "Norrby",     "medlem", "1"),
            new("Sven-Erik", "Dahlgren",   "medlem", "2"),
            new("Birgitta",  "Lundahl",    "medlem", "3"),
            new("Mats",      "Örnberg",    "medlem", "1"),
            new("Karin",     "Fridell",    "medlem", "2"),
            new("Lennart",   "Sjökvist",   "medlem", "3"),
            new("Ulla",      "Bergquist",  "medlem", "2"),
            new("Håkan",     "Melander",   "medlem", "1"),
            new("Siv",       "Åkerlund",   "medlem", "2"),
            new("Bertil",    "Ranstorp",   "medlem", "3"),
            new("Monica",    "Hedlund",    "medlem", "1"),
            new("Jan-Olof",  "Tornberg",   "medlem", "2"),
            new("Elisabet",  "Widmark",    "medlem", "3"),
            new("Per-Åke",   "Strandberg", "medlem", "1"),
            new("Gunilla",   "Rosander",   "medlem", "2"),
            new("Åke",       "Lindgren",   "medlem", "3"),
            new("Vivianne",  "Sandell",    "medlem", "2"),
            new("Bo",        "Kjellberg",  "medlem", "1"),
            new("Marianne",  "Ödman",      "medlem", "2"),
            new("Stig",      "Hammarlund", "medlem", "3"),
            new("Berit",     "Falk",       "medlem", "1"),
            new("Ove",       "Tranberg",   "medlem", "2"),
            new("Kerstin",   "Wallin",     "medlem", "3"),
            new("Göran",     "Ekström",    "medlem", "1"),
            new("Astrid",    "Molander",   "medlem", "2"),
            new("Nils-Erik", "Byström",    "medlem", "3"),
            new("Lena",      "Hallberg",   "medlem", "2"),
            new("Arne",      "Sjöberg",    "medlem", "1"),
            new("Inger",     "Palmgren",   "medlem", "2"),
            new("Kjell",     "Roos",       "medlem", "3"),
            new("Solveig",   "Enander",    "medlem", "1"),
            new("Tommy",     "Lindqvist",  "medlem", "2"),
            new("Barbro",    "Nyström",    "medlem", "3"),
        };

        // ── Status ───────────────────────────────────────────────────────────────────

        public IActionResult Status(string confirm = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = true, exists = false, message = "Ankeborg finns inte i dev." });

            var memberIds = MembersOfClub(club.Id);
            using var db = _databaseFactory.CreateDatabase();

            return Json(new
            {
                success = true,
                exists = true,
                clubNodeId = club.Id,
                clubName = club.GetValue<string>("clubName"),
                published = club.Published,
                parentId = club.ParentId,
                members = memberIds.Count,
                pending = memberIds.Count(id => (_memberService.GetById(id)?.IsApproved ?? true) == false),
                clubMemberships = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM ClubMembership WHERE ClubId=@0", club.Id),
                trainingScores = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM TrainingScores WHERE MemberId IN (SELECT MemberId FROM ClubMembership WHERE ClubId=@0)", club.Id),
                markenSeries = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM MarkenSeries WHERE ClubId=@0", club.Id),
                intygRequests = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM ForeningsintygRequest WHERE ClubId=@0", club.Id),
                clubWeapons = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM Firearm WHERE ScopeKind='Club' AND ScopeId=@0", club.Id),
                filmShots = "Klubb, medlemmar, aktivitet, märken, intyg och klubbvapen. "
                          + "Händelser, tävlingar, serier, styrelsemöte och bokning seedas INTE "
                          + "av den här filen — se README i klubbguide-2026."
            });
        }

        // ── Steg 1: klubb + medlemmar ────────────────────────────────────────────────

        public IActionResult Seed(string confirm = "", bool dryRun = true, string region = "Halland")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var parentId = ResolveClubsPageId(region, out var parentError);
            if (parentId == null)
                return Json(new { success = false, message = parentError });

            var existingClub = FindClub();
            var plannedMembers = new List<object>();
            foreach (var p in Roster)
            {
                var email = Email(p);
                var exists = _memberService.GetByEmail(email) != null;
                plannedMembers.Add(new
                {
                    namn = $"{p.First} {p.Last}",
                    email,
                    roll = p.Role,
                    klass = p.ShooterClass,
                    status = p.Pending ? "Väntande" : "Aktiv",
                    atgard = exists ? "finns redan — återanvänds" : "skapas"
                });
            }

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    klubb = existingClub == null
                        ? $"SKAPAS: '{ClubNodeName}' under nod {parentId} ({region})"
                        : $"FINNS: nod {existingClub.Id} — återanvänds, inget skrivs över",
                    medlemmar = plannedMembers.Count,
                    grupper = new[]
                    {
                        "ClubAdmin_<klubbid> -> Sigrid Almkvist",
                        "Skjutledare_<klubbid> -> Britt-Marie Ekvall",
                        "Foreningsintygsansvarig_<klubbid> -> Gunnar Falkenmark",
                        "PendingApproval -> Nils Tegelberg"
                    },
                    plan = plannedMembers
                });
            }

            // ── Klubbnoden ───────────────────────────────────────────────────────────
            var club = existingClub;
            var clubCreated = false;
            if (club == null)
            {
                club = _contentService.Create(ClubNodeName, parentId.Value, "club");
                club.SetValue("clubName", ClubDisplayName);
                club.SetValue("clubId", FictionalExternalClubId);
                club.SetValue("regionalFederation", region);
                club.SetValue("isActive", true);
                club.SetValue("city", "Ankeborg");
                club.SetValue("address", "Skjutbanevägen 7");
                club.SetValue("postalCode", "310 42");
                club.SetValue("contactPerson", "Sigrid Almkvist");
                club.SetValue("contactEmail", $"styrelsen@{MailDomain}");
                club.SetValue("contactPhone", "0340-12 34 56");
                club.SetValue("orgNumber", "802000-0000");
                club.SetValue("swishNumber", "123 456 78 90");
                club.SetValue("description",
                    "Demoklubb för pistol.nu. Alla personer och uppgifter är påhittade.");
                club.SetValue("aboutClub",
                    "<p><strong>Ankeborg Pistolklubb</strong> är en demoklubb som används för att visa "
                  + "hur pistol.nu ser ut för en klubb. Alla medlemmar, resultat och händelser är "
                  + "påhittade och används i pistol.nu:s informationsmaterial.</p>");

                if (!_contentService.Save(club).Success)
                    return Json(new { success = false, message = "Kunde inte spara klubbnoden." });

                _contentService.Publish(club, new[] { "*" }, -1);
                clubCreated = true;
            }

            var clubId = club.Id;

            // ── Medlemmar ────────────────────────────────────────────────────────────
            int created = 0, reused = 0, membershipsAdded = 0;
            var failures = new List<string>();
            var roleAssignments = new List<string>();

            foreach (var p in Roster)
            {
                try
                {
                    var email = Email(p);
                    var name = $"{p.First} {p.Last}";
                    var member = _memberService.GetByEmail(email);

                    if (member == null)
                    {
                        member = _memberService.CreateMember(email, email, name, "hpskMember");
                        member.SetValue("firstName", p.First);
                        member.SetValue("lastName", p.Last);
                        member.SetValue("primaryClubId", clubId);
                        member.SetValue("memberSince", DateTime.Now.AddYears(-RandomYears(p)));
                        if (p.ShooterClass != null)
                            member.SetValue("precisionShooterClass", p.ShooterClass);

                        // ⚠️ Den väntande medlemmen ska INTE vara godkänd — det är hela poängen
                        // med scen 5 ("dyker upp som väntande, ni godkänner med ett klick").
                        member.IsApproved = !p.Pending;
                        _memberService.Save(member);

                        _memberService.AssignRoles(new[] { member.Id },
                            p.Pending ? new[] { "PendingApproval" } : new[] { "Users" });
                        created++;
                    }
                    else
                    {
                        reused++;
                    }

                    // Klubbmedlemskapet är sanningskällan för "medlem sedan" och för aktivitet.
                    if (_clubMembershipService.Get(member.Id, clubId) == null)
                    {
                        _clubMembershipService.Save(new ClubMembership
                        {
                            MemberId = member.Id,
                            ClubId = clubId,
                            MembershipStatus = p.Pending ? "Väntande" : "Aktiv",
                            MemberSince = DateTime.Now.AddYears(-RandomYears(p)),
                            MemberNotes = SeedTag
                        });
                        membershipsAdded++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{p.First} {p.Last}: {ex.Message}");
                }
            }

            // ── Roller ───────────────────────────────────────────────────────────────
            // Grupperna heter <Roll>_<klubbnodid> (AdminAuthorizationService). Umbraco skapar
            // inte en medlemsgrupp av sig själv — den måste finnas som nod först.
            AssignClubRole($"ClubAdmin_{clubId}", "Sigrid", "Almkvist", roleAssignments, failures);
            AssignClubRole($"Skjutledare_{clubId}", "Britt-Marie", "Ekvall", roleAssignments, failures);
            AssignClubRole($"Foreningsintygsansvarig_{clubId}", "Gunnar", "Falkenmark", roleAssignments, failures);

            _logger.LogWarning("Ankeborg-seed körd: klubb {ClubId}, {Created} nya medlemmar.", clubId, created);

            return Json(new
            {
                success = failures.Count == 0,
                dryRun = false,
                clubNodeId = clubId,
                clubCreated,
                membersCreated = created,
                membersReused = reused,
                membershipsAdded,
                roles = roleAssignments,
                failures,
                next = $"/umbraco/surface/AnkeborgSeed/SeedActivity?confirm={Confirm}&dryRun=true"
            });
        }

        // ── Steg 2: aktivitet, märken, föreningsintyg ────────────────────────────────

        public IActionResult SeedActivity(string confirm = "", bool dryRun = true)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = false, message = "Kör Seed först — Ankeborg finns inte." });

            var hasse = FindMember("Hasse", "Lindwall");
            var yvonne = FindMember("Yvonne", "Sjöstrand");
            var elin = FindMember("Elin", "Hagberg");
            var gunnar = FindMember("Gunnar", "Falkenmark");
            if (hasse == null || yvonne == null || elin == null || gunnar == null)
                return Json(new { success = false, message = "Saknar filmens namngivna medlemmar — kör Seed först." });

            var year = DateTime.Now.Year;

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    trainingScores = "24 träningsserier på Hasse Lindwall + 18 på Elin Hagberg (12 mån bakåt)",
                    marken = "3 godkända guldserier + 1 oavgjord på Yvonne Sjöstrand",
                    intyg = "1 OBESVARAD föreningsintygsförfrågan från Hasse Lindwall "
                          + "(brickan i rälsen ska synas i bild 9)",
                    scen = "Underlag till scen 6, 7 och 9 samt bild 8, 10 och 13."
                });
            }

            using var db = _databaseFactory.CreateDatabase();
            int scores = 0, marken = 0, intyg = 0;
            var failures = new List<string>();

            // Träningsserier — underlaget både Min Sida (bild 13) och Aktivitet (bild 8) läser.
            scores += SeedTrainingScores(db, hasse.Id, 24);
            scores += SeedTrainingScores(db, elin.Id, 18);

            // Guldserier till märkespanelen (bild 10). Tre godkända + en som väntar på validering,
            // så att panelen visar BÅDE en färdig guldfodring och något kvar att göra.
            //
            // ⚠️ DUBBLETTSPÄRR. Utan den lade en andra körning fyra serier TILL — märkespanelen
            // visade åtta guldserier och en guldfodring som såg dubbelt uppfylld ut. Varje seedsteg
            // måste vara idempotent: steget körs om så fort något annat i det behöver rättas.
            var existingMarken = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM MarkenSeries WHERE ClubId=@0 AND Notes=@1", club.Id, SeedTag);
            var seriesDates = new[] { -140, -96, -55, -12 };
            if (existingMarken == 0)
            for (var i = 0; i < seriesDates.Length; i++)
            {
                var pending = i == seriesDates.Length - 1;
                var date = DateTime.Now.AddDays(seriesDates[i]);
                db.Execute(@"
INSERT INTO MarkenSeries
 (MemberId, ClubId, BadgeFamily, SeriesType, Year, SeriesDate, WeaponGroup, ClaimedLevel,
  Shots, Total, Threshold, Qualifies, Status, ValidatedByMemberId, ValidatedDate,
  Notes, EnteredByMemberId, CreatedAt, UpdatedAt, CountsTowardGuldfodring)
VALUES
 (@0, @1, 'Pistolskyttemarket', 'Guldserie', @2, @3, 'C', 'Guld',
  @4, @5, 46, 1, @6, @7, @8, @9, @0, @10, @10, 1)",
                    yvonne.Id, club.Id, year, date,
                    "10,9,10,9,10", 48,
                    pending ? "Inskickad" : "Godkand",
                    pending ? (object?)null : gunnar.Id,
                    pending ? (object?)null : date.AddDays(2),
                    SeedTag, DateTime.Now);
                marken++;
            }

            // ── Klubbvapen (bild 12 och 17, och hela scen 9b) ───────────────────────
            // ⚠️ Går via FirearmService, inte rå SQL: raden har ett krypterat valv som måste
            // skapas i rätt ordning (se kommentaren i FirearmService.Create). En INSERT förbi
            // tjänsten ger en vapenrad vars uppgifter aldrig kan läsas.
            var clubWeapons = 0;
            var existingClubWeapons = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM Firearm WHERE ScopeKind='Club' AND ScopeId=@0", club.Id);
            if (existingClubWeapons == 0)
            {
                // Nummer 7 är med flit med — det är vapnet filmens nybörjare bokar.
                // Flera vapen delar namn med FLIT: ett riktigt klubbskap har fyra likadana
                // matchpistoler, och det ar precis darfor NUMRET identifierar exemplaret och
                // namnet inte gor det. Aliaset ar ocksa kort nog att rymmas pa etiketten utan
                // ellips — .alias i VaultLabels.cshtml trunkerar (overflow+text-overflow).
                var models = new[]
                {
                    (nr: 1, alias: "Match .22"),
                    (nr: 2, alias: "Match .22"),
                    (nr: 5, alias: "Sportpistol 9 mm"),
                    (nr: 7, alias: "Match .22"),
                    (nr: 8, alias: "Sportpistol 9 mm"),
                };
                foreach (var m in models)
                {
                    var (id, err) = _firearmService.Create(
                        FirearmScope.Club(club.Id),
                        new FirearmWriteRequest
                        {
                            Alias = m.alias,
                            WeaponClass = m.nr is 5 or 8 ? "B" : "C",
                            AcquisitionStatus = FirearmAcquisitionStatus.Innehas,
                            ClubWeaponNumber = m.nr,
                            IsLoanable = true,
                            Status = FirearmStatus.Tillgangligt,
                            SortOrder = m.nr,
                        });
                    if (err != null) failures.Add($"Klubbvapen {m.nr}: {err}");
                    else clubWeapons++;
                }
            }

            // ── Föreningsintyg — EN obesvarad förfrågan ─────────────────────────────
            // Brickan i rälsen sätts av renderRequests() vid sidladdning, så den syns i bild
            // utan att någon klickat på fliken först.
            //
            // ⚠️ Förfrågan KRÄVER ett vapen som tillhör medlemmen (FK till Firearm, plus
            // tjänstens egen ägarkontroll). Ett "Nytt vapen"-intyg gäller ett vapen medlemmen
            // ännu inte äger — därför AcquisitionStatus 'Planerat'. Första försöket sköt in
            // FirearmId 0 och sprack på främmande nyckel.
            var alreadyOpen = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ForeningsintygRequest WHERE ClubId=@0 AND Status='Ny'", club.Id);
            if (alreadyOpen == 0)
            {
                var (firearmId, firearmError) = _firearmService.Create(
                    FirearmScope.Member(hasse.Id),
                    new FirearmWriteRequest
                    {
                        Alias = "Match .22",
                        WeaponClass = "C",
                        AcquisitionStatus = FirearmAcquisitionStatus.Planerat,
                        Federations = new List<string> { ForeningsintygDocument.ForbundSpsf },
                    });

                if (firearmError != null)
                {
                    failures.Add($"Vapen till föreningsintyget: {firearmError}");
                }
                else
                {
                    var (reqId, reqError) = _intygRequests.Create(
                        hasse.Id, club.Id,
                        ForeningsintygRequestKind.NyttVapen,
                        firearmId,
                        ForeningsintygDocument.ForbundSpsf,
                        "C – Precision",
                        "Ansöker om licens för min första egna pistol. Tack på förhand!",
                        // ⚠️ Medlemmen bekräftar antalet vapen vid ansökan — utan flaggan
                        // vägrar tjänsten förfrågan. I filmen är bekräftelsen redan gjord.
                        antalVapenBekraftat: true);
                    if (reqError != null) failures.Add($"Föreningsintygsförfrågan: {reqError}");
                    else if (reqId > 0) intyg++;
                }
            }

            _logger.LogWarning("Ankeborg-aktivitet seedad: {Scores} serier, {Marken} marken, {Intyg} intyg.",
                scores, marken, intyg);

            return Json(new
            {
                success = failures.Count == 0,
                dryRun = false,
                trainingScoresCreated = scores,
                markenSeriesCreated = marken,
                clubWeaponsCreated = clubWeapons,
                intygRequestsCreated = intyg,
                failures
            });
        }

        // ── Steg 3: händelser, serier, tävlingar, styrelse, bokning ──────────────────

        /// <summary>
        /// Bild 4–6, 11 och 15. Kör efter <see cref="Seed"/> och <see cref="SeedActivity"/>.
        ///
        /// ⚠️ Varje del har en egen dubblettspärr och rapporterar sitt eget antal. Steget körs om
        /// så fort en av delarna behöver rättas, och då får de övriga inte skriva en gång till —
        /// märkesserierna lärde oss det genom att bli åtta i stället för fyra.
        /// </summary>
        public IActionResult SeedProgram(string confirm = "", bool dryRun = true)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = false, message = "Kör Seed först — Ankeborg finns inte." });

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    handelser = "4 st: klubbträning, nybörjarkurs (anmälan + lånevapen), "
                              + "städdag (obligatorisk, i DÅTID med upprop) och årsmöte",
                    serie = "Ankeborgsserien 2026 med 2 omgångar, båda med anmälningar",
                    tavlingar = "3 st: 2 serieomgångar + 1 klubbmästerskap (Endast klubb, framtida)",
                    styrelse = "5 förtroendevalda + ett styrelsemöte om en vecka",
                    bokning = "Elin Hagberg bokar klubbvapen nr 7 till nästa träning",
                    scen = "Underlag till scen 4, 6, 8 och 9b samt bild 4, 5, 6, 11 och 15."
                });
            }

            var failures = new List<string>();
            int events = 0, signups = 0, attendance = 0, seriesCreated = 0,
                comps = 0, regs = 0, roles = 0, meetings = 0, bookings = 0;

            var today = DateTime.Now.Date;

            // ── Händelser ────────────────────────────────────────────────────────────
            var existingEvents = _contentService.GetPagedChildren(club.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                .ToDictionary(c => c.Name ?? "", c => c, StringComparer.OrdinalIgnoreCase);

            // ⚠️ Städdagen ligger i DÅTID med ett taget upprop. Aktivitetssammanställningen
            // (bild 8) läser närvaro, och ett framtida evenemang har ingen att läsa.
            var eventPlan = new[]
            {
                new EventSeed("Klubbträning onsdag", "Träning", today.AddDays(5).AddHours(18),
                              "Ordinarie klubbträning på 25-metersbanan.", false, 0, 0m, false, false),
                new EventSeed("Nybörjarkurs steg 1", "Annat", today.AddDays(12).AddHours(9),
                              "Första passet för nya skyttar. Vi går igenom säkerhet, grepp och "
                              + "sikte. Klubbvapen finns att låna.", true, 12, 300m, true, false),
                new EventSeed("Städdag på banan", "Städning", today.AddDays(-14).AddHours(9),
                              "Vårstädning av banan och klubbstugan.", true, 0, 0m, false, true),
                new EventSeed("Årsmöte 2027", "Möte", today.AddDays(40).AddHours(19),
                              "Ordinarie årsmöte i klubbstugan.", true, 0, 0m, false, true),
            };

            var eventNodes = new Dictionary<string, IContent>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in eventPlan)
            {
                try
                {
                    if (existingEvents.TryGetValue(e.Name, out var found))
                    {
                        eventNodes[e.Name] = found;
                        continue;
                    }

                    var node = _contentService.Create(e.Name, club.Id, "clubSimpleEvent");
                    node.SetValue("eventName", e.Name);
                    node.SetValue("eventType", e.Type);
                    node.SetValue("eventDate", e.Date);
                    node.SetValue("description", e.Description);
                    node.SetValue("venue", "Ankeborgs skjutbana");
                    node.SetValue("contactPerson", "Sigrid Almkvist");
                    node.SetValue("contactEmail", $"styrelsen@{MailDomain}");
                    node.SetValue("isActive", true);
                    node.SetValue("registrationRequired", e.Registration);
                    if (e.MaxParticipants > 0) node.SetValue("maxParticipants", e.MaxParticipants);
                    if (e.Fee > 0) node.SetValue("feeAmount", e.Fee);
                    node.SetValue("lanevapenOffered", e.LoanWeapons);
                    node.SetValue("isMandatory", e.Mandatory);
                    if (e.Registration && e.Date > today)
                        node.SetValue("registrationDeadline", e.Date.AddDays(-3).Date);

                    if (!_contentService.Save(node).Success)
                    {
                        failures.Add($"Händelse '{e.Name}': kunde inte sparas.");
                        continue;
                    }
                    _contentService.Publish(node, new[] { "*" }, -1);
                    eventNodes[e.Name] = node;
                    events++;
                }
                catch (Exception ex) { failures.Add($"Händelse '{e.Name}': {ex.Message}"); }
            }

            using (var db = _databaseFactory.CreateDatabase())
            {
                // Anmälningar till nybörjarkursen + upprop på städdagen.
                signups += SeedEventParticipants(db, eventNodes, "Nybörjarkurs steg 1", 7, false, failures).Item1;
                var stad = SeedEventParticipants(db, eventNodes, "Städdag på banan", 14, true, failures);
                signups += stad.Item1;
                attendance += stad.Item2;
            }

            // ── Serie + tävlingar ────────────────────────────────────────────────────
            var hubId = ResolveCompetitionsHubId(out var hubError);
            if (hubId == null)
            {
                failures.Add(hubError);
            }
            else
            {
                var series = _contentService.GetPagedChildren(hubId.Value, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionSeries"
                                      && string.Equals(c.Name, SeriesNodeName, StringComparison.OrdinalIgnoreCase));
                if (series == null)
                {
                    series = _contentService.Create(SeriesNodeName, hubId.Value, "competitionSeries");
                    series.SetValue("seriesName", SeriesNodeName);
                    series.SetValue("clubId", club.Id);
                    series.SetValue("regionalFederation", "Halland");
                    series.SetValue("seriesShortDescription", "Klubbens egen precisionsserie, fyra omgångar under året.");
                    series.SetValue("seriesDescription",
                        "<p>Ankeborgsserien skjuts som precision i vapengrupp A, B och C. "
                      + "Sammanlagd ställning räknas på de tre bästa omgångarna.</p>");
                    series.SetValue("seriesStartDate", new DateTime(today.Year, 3, 1));
                    series.SetValue("seriesEndDate", new DateTime(today.Year, 10, 31));
                    series.SetValue("isActive", true);
                    series.SetValue("showInMenu", false);
                    if (_contentService.Save(series).Success)
                    {
                        _contentService.Publish(series, new[] { "*" }, -1);
                        seriesCreated++;
                    }
                    else failures.Add("Serien kunde inte sparas.");
                }

                var classIds = new[] { "A1", "A2", "A3", "B1", "B2", "B3", "C1", "C2", "C3" };

                var compPlan = new[]
                {
                    new CompSeed("Ankeborgsserien omgång 1", series!.Id, today.AddDays(-96), false, 18),
                    new CompSeed("Ankeborgsserien omgång 2", series!.Id, today.AddDays(-38), false, 16),
                    new CompSeed("Klubbmästerskap precision " + today.Year, hubId.Value, today.AddDays(25), true, 12),
                };

                foreach (var c in compPlan)
                {
                    try
                    {
                        var existing = _contentService.GetPagedChildren(c.ParentId, 0, int.MaxValue, out _)
                            .FirstOrDefault(x => x.ContentType.Alias == "competition"
                                              && string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                        IContent comp;
                        if (existing != null) { comp = existing; }
                        else
                        {
                            comp = _contentService.Create(c.Name, c.ParentId, "competition");
                            comp.SetValue("competitionName", c.Name);
                            comp.SetValue("competitionType", "Precision");
                            comp.SetValue("clubId", club.Id);
                            comp.SetValue("regionalFederation", "Halland");
                            comp.SetValue("venue", "Ankeborgs skjutbana");
                            comp.SetValue("description", $"<p>{c.Name} — precision, 7 serier.</p>");
                            comp.SetValue("competitionDate", c.Date.AddHours(10));
                            comp.SetValue("registrationOpenDate", c.Date.AddDays(-45));
                            comp.SetValue("registrationCloseDate", c.Date.AddDays(-2));
                            comp.SetValue("numberOfSeriesOrStations", 7);
                            comp.SetValue("numberOfFinalSeries", 0);
                            comp.SetValue("maxParticipants", 60);
                            comp.SetValue("registrationFee", 80m);
                            comp.SetValue("juniorRegistrationFee", 0m);
                            comp.SetValue("isActive", true);
                            comp.SetValue("isClubOnly", c.ClubOnly);
                            comp.SetValue("allowTeams", false);
                            comp.SetValue("allowStafett", false);
                            comp.SetValue("showLiveResults", true);
                            comp.SetValue("competitionDirector", "Sigrid Almkvist");
                            comp.SetValue("contactEmail", $"tavling@{MailDomain}");
                            // ⚠️ JSON-array, aldrig CSV — se CLAUDE.md om shootingClassIds.
                            comp.SetValue("shootingClassIds", ShootingClassIdsValue.Normalize(classIds));

                            if (!_contentService.Save(comp).Success)
                            {
                                failures.Add($"Tävling '{c.Name}': kunde inte sparas.");
                                continue;
                            }
                            var pub = _contentService.Publish(comp, new[] { "*" }, -1);
                            if (!pub.Success)
                            {
                                failures.Add($"Tävling '{c.Name}': sparad men inte publicerad — "
                                           + string.Join(", ", pub.EventMessages?.GetAll().Select(m => m.Message)
                                                               ?? Array.Empty<string>()));
                                continue;
                            }
                            comps++;
                        }

                        regs += SeedRegistrations(comp, club.Id, c.Registrations, c.Date, failures);
                    }
                    catch (Exception ex) { failures.Add($"Tävling '{c.Name}': {ex.Message}"); }
                }
            }

            // ── Styrelse ─────────────────────────────────────────────────────────────
            var actor = FindMember("Sigrid", "Almkvist")?.Id ?? 0;
            var boardPlan = new (string First, string Last, string RoleKey)[]
            {
                ("Gunnar",     "Falkenmark", "Ordforande"),
                ("Sigrid",     "Almkvist",   "Sekreterare"),
                ("Torbjörn",   "Rydell",     "Kassor"),
                ("Margareta",  "Wiklund",    "Ledamot"),
                ("Kenneth",    "Blomgren",   "Ledamot"),
            };

            using (var db = _databaseFactory.CreateDatabase())
            {
                var existingRoles = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM BoardRoles WHERE OwnerType=0 AND OwnerId=@0 AND IsActive=1", club.Id);
                if (existingRoles == 0)
                {
                    foreach (var b in boardPlan)
                    {
                        try
                        {
                            var m = FindMember(b.First, b.Last);
                            if (m == null) { failures.Add($"Styrelse: hittade inte {b.First} {b.Last}."); continue; }
                            _boardRoles.AssignBoardRole(0, club.Id, m.Id, b.RoleKey, null, true, actor,
                                new DateTime(today.Year, 3, 15), new DateTime(today.Year + 2, 3, 15), 2);
                            roles++;
                        }
                        catch (Exception ex) { failures.Add($"Styrelse {b.First} {b.Last}: {ex.Message}"); }
                    }
                }

                var existingMeetings = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM BoardMeetings WHERE OwnerType=0 AND OwnerId=@0 AND IsActive=1", club.Id);
                if (existingMeetings == 0 && actor > 0)
                {
                    try
                    {
                        _boardMeetings.CreateMeeting(0, club.Id, "Styrelsemote", "Styrelsemöte",
                            today.AddDays(7).AddHours(18).AddMinutes(30), "Klubbstugan", actor);
                        meetings++;
                    }
                    catch (Exception ex) { failures.Add($"Styrelsemöte: {ex.Message}"); }
                }
            }

            // ── Bokning av klubbvapen nr 7 ───────────────────────────────────────────
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var already = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM FirearmBooking WHERE ClubId=@0", club.Id);
                if (already == 0)
                {
                    var elin = FindMember("Elin", "Hagberg");
                    var nr7 = db.ExecuteScalar<int?>(
                        "SELECT TOP 1 Id FROM Firearm WHERE ScopeKind='Club' AND ScopeId=@0 AND ClubWeaponNumber=7",
                        club.Id);
                    if (elin == null || nr7 == null || nr7 <= 0)
                    {
                        failures.Add("Bokning: hittade inte Elin Hagberg eller klubbvapen nr 7.");
                    }
                    else
                    {
                        // ⚠️ I DAG, inte nästa träning. Valvet listar KVÄLLENS utlämning, så en
                        // bokning fem dagar fram ger skärmen "Inga lån i dag" — en bild som ser
                        // ut som att funktionen är tom i stället för som att den fungerar.
                        var from = today.AddHours(18);
                        var (id, err) = _bookings.Create(new FirearmBookingRequest
                        {
                            MemberId = elin.Id,
                            ClubId = club.Id,
                            FirearmId = nr7,
                            OccasionKind = FirearmOccasionKind.Fritt,
                            From = from,
                            To = from.AddHours(3),
                        });
                        if (err != null) failures.Add($"Bokning: {err}");
                        else if (id > 0) bookings++;
                    }
                }
            }
            catch (Exception ex) { failures.Add($"Bokning: {ex.Message}"); }

            _logger.LogWarning("Ankeborg-program seedat: {Events} händelser, {Comps} tävlingar, "
                             + "{Regs} anmälningar, {Roles} förtroendevalda, {Bookings} bokningar.",
                               events, comps, regs, roles, bookings);

            return Json(new
            {
                success = failures.Count == 0,
                dryRun = false,
                eventsCreated = events,
                eventSignups = signups,
                eventAttendance = attendance,
                seriesCreated,
                competitionsCreated = comps,
                registrationsCreated = regs,
                boardRolesCreated = roles,
                boardMeetingsCreated = meetings,
                bookingsCreated = bookings,
                failures
            });
        }

        private sealed record EventSeed(string Name, string Type, DateTime Date, string Description,
                                        bool Registration, int MaxParticipants, decimal Fee,
                                        bool LoanWeapons, bool Mandatory);

        private sealed record CompSeed(string Name, int ParentId, DateTime Date, bool ClubOnly, int Registrations);

        /// <summary>
        /// Anmälningar och (för ett passerat evenemang) upprop.
        ///
        /// ⚠️ Uppropet lämnar EN medlem utan status. "Ej registrerad" är ett eget tillstånd och
        /// inte frånvaro — panelen räknar och visar det separat, och filmen ska visa den skillnaden.
        /// </summary>
        private (int, int) SeedEventParticipants(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            Dictionary<string, IContent> nodes, string eventName, int count, bool withAttendance,
            List<string> failures)
        {
            if (!nodes.TryGetValue(eventName, out var node)) return (0, 0);

            var existing = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ClubEventParticipant WHERE EventId=@0", node.Id);
            if (existing > 0) return (0, 0);

            var recorder = FindMember("Sigrid", "Almkvist")?.Id ?? 0;
            var picked = Roster.Where(p => !p.Pending).Skip(3).Take(count).ToList();
            int signed = 0, marked = 0;

            for (var i = 0; i < picked.Count; i++)
            {
                var p = picked[i];
                var m = FindMember(p.First, p.Last);
                if (m == null) continue;

                string? status = null;
                string? note = null;
                if (withAttendance && i < picked.Count - 1)
                {
                    if (i == 4) { status = "GiltigFranvaro"; note = "Anmäld frånvaro, jobbar helg."; }
                    else if (i == 9) { status = "Franvarande"; }
                    else { status = "Narvarande"; }
                }

                try
                {
                    db.Execute(@"
INSERT INTO ClubEventParticipant
 (EventId, MemberId, MemberName, SignedUpAt, SignedUpByMemberId, AttendanceStatus, AttendanceNote,
  RecordedByMemberId, RecordedAt, CreatedDate, UpdatedDate)
VALUES (@0, @1, @2, @3, @1, @4, @5, @6, @7, @8, @8)",
                        node.Id, m.Id, $"{p.First} {p.Last}",
                        DateTime.Now.AddDays(-20 + i),
                        status, note,
                        status == null ? (object?)null : recorder,
                        status == null ? (object?)null : DateTime.Now.AddDays(-14),
                        DateTime.Now);
                    signed++;
                    if (status != null) marked++;
                }
                catch (Exception ex) { failures.Add($"Deltagare {p.First} {p.Last}: {ex.Message}"); }
            }
            return (signed, marked);
        }

        /// <summary>
        /// ⚠️ Anmälningarna Save():as men publiceras ALDRIG — exakt som den publika vägen.
        /// En publicerad anmälningshubb är en ogrindad publik sida med namn, klubb och klass.
        /// </summary>
        private int SeedRegistrations(IContent comp, int clubId, int count, DateTime compDate,
                                      List<string> failures)
        {
            var regHub = _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (regHub == null)
            {
                regHub = _contentService.Create("Anmälningar", comp.Id, "competitionRegistrationsHub");
                _contentService.Save(regHub);
            }

            var existing = _contentService.GetPagedChildren(regHub.Id, 0, int.MaxValue, out _)
                .Count(c => c.ContentType.Alias == "competitionRegistration");
            if (existing > 0) return 0;

            var classes = new[] { "C1", "C2", "C3", "A1", "A2", "B2", "C2", "C3", "A3", "B1" };
            var picked = Roster.Where(p => !p.Pending).Take(count).ToList();
            var created = 0;

            for (var i = 0; i < picked.Count; i++)
            {
                var p = picked[i];
                var m = FindMember(p.First, p.Last);
                if (m == null) continue;
                var name = $"{p.First} {p.Last}";
                try
                {
                    var entries = new List<ShootingClassEntry>
                    {
                        new ShootingClassEntry
                        {
                            Class = classes[i % classes.Length],
                            StartPreference = "Inget",
                            TeamNumber = null
                        }
                    };
                    var reg = _contentService.Create(
                        $"{name} - {compDate:yyyy-MM-dd}", regHub.Id, "competitionRegistration");
                    reg.SetValue("competitionId", comp.Id);
                    reg.SetValue("memberId", m.Id);
                    reg.SetValue("memberName", name);
                    reg.SetValue("clubId", clubId);
                    reg.SetValue("shootingClasses",
                        CompetitionRegistrationDocument.SerializeShootingClasses(entries));
                    reg.SetValue("registrationDate", compDate.AddDays(-20));
                    reg.SetValue("registeredBy", SeedTag);
                    reg.SetValue("isActive", true);
                    if (reg.HasProperty("isSubCompetition")) reg.SetValue("isSubCompetition", false);
                    _contentService.Save(reg);
                    created++;
                }
                catch (Exception ex) { failures.Add($"Anmälan {name}: {ex.Message}"); }
            }
            return created;
        }


        /// <summary>
        /// Bygger en serieuppsattning i det format kolumnen faktiskt bar.
        ///
        /// ⚠️⚠️ SeriesScores AR EN JSON-ARRAY av serieobjekt
        /// (<c>[{"seriesNumber":1,"shots":["X","10",...],"total":47,"xCount":2}]</c>),
        /// INTE en kommaseparerad strang. Kolumnen ar nvarchar, sa en CSV sparas utan
        /// protest — och varje serie tolkas da som NOLL poang. Foljden syns forst pa
        /// Min Sida, dar utvecklingskurvan och snittet ligger platt pa noll medan
        /// antalet pass ar ratt. Det ar alltsa ett tyst fel: datat ser ut att finnas.
        ///
        /// Poangen trendar svagt uppat over tid sa kurvan har nagot att visa — men
        /// med en sagtandsvariation, eftersom en spikrak linje ser tillverkad ut.
        /// </summary>
        private static (string Json, int Total, int XCount) BuildSeriesJson(int memberId, int index)
        {
            // index 0 ar senaste passet; hogre index ligger langre bak i tiden.
            var trend = 44.0 + Math.Max(0, 10 - index) * 0.25;      // svag forbattring
            var wobble = ((memberId + index * 7) % 5) * 0.4 - 0.8;  // sagtand
            var target = Math.Clamp(trend + wobble, 41.0, 49.0);

            var series = new List<string>();
            var total = 0;
            var xs = 0;

            for (var n = 1; n <= 5; n++)
            {
                var goal = (int)Math.Round(target) + ((n + index) % 3 - 1); // +-1 mellan serier
                goal = Math.Clamp(goal, 40, 50);

                // Fem skott som summerar till goal, med X for tior som traffat innerringen.
                var shots = new List<string>();
                var left = goal;
                for (var shot = 5; shot >= 1; shot--)
                {
                    var avg = (int)Math.Round(left / (double)shot);
                    var v = Math.Clamp(avg, 6, 10);
                    if (shot == 1) v = Math.Clamp(left, 6, 10);
                    left -= v;
                    if (v == 10 && (n + shot + index) % 3 == 0) { shots.Add("\"X\""); xs++; }
                    else shots.Add($"\"{v}\"");
                }

                var serieTotal = goal - left;   // left ar 0 utom i extremfall
                total += serieTotal;
                series.Add($"{{\"seriesNumber\":{n},\"shots\":[{string.Join(",", shots)}]," +
                           $"\"total\":{serieTotal},\"xCount\":0}}");
            }

            return ("[" + string.Join(",", series) + "]", total, xs);
        }

        private int? ResolveCompetitionsHubId(out string error)
        {
            error = "";
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) { error = "Ingen rotnod hittades."; return null; }
            var hub = Descendants(root).FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            if (hub == null) { error = "Hittade ingen competitionsHub."; return null; }
            return hub.Id;
        }

        // ── Steg 4: inloggning för bildhämtningen ────────────────────────────────────

        /// <summary>
        /// Sätter ett känt dev-lösenord på filmens namngivna medlemmar, så
        /// <c>capture.js</c> kan fotografera MEDLEMMENS vyer (Min sida, Mitt schema) och inte
        /// bara administratörens.
        ///
        /// ⚠️ DEV ONLY, som resten av filen — och kontona är oåtkomliga även om datan skulle
        /// följa med någonstans: alla adresser ligger på <c>@ankeborg.invalid</c>, en domän
        /// som per RFC 2606 aldrig kan ta emot post, så ingen lösenordsåterställning kan nå dem.
        ///
        /// ⚠️ Går via GeneratePasswordResetToken + ResetPassword, samma väg som
        /// <c>MemberAdminController</c> använder. Medlemmarna skapades utan lösenord, så
        /// ChangePasswordAsync (som kräver det gamla) är inte användbar.
        /// </summary>
        public async Task<IActionResult> SeedLogin(string confirm = "", bool dryRun = true,
                                                   string password = "Ankeborg123!")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var who = new[]
            {
                ("Elin", "Hagberg"), ("Hasse", "Lindwall"), ("Sigrid", "Almkvist"),
            };

            if (dryRun)
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    satter = who.Select(w => Email(new Person(w.Item1, w.Item2, "", null))),
                    losenord = password
                });

            var done = new List<string>();
            var failures = new List<string>();

            foreach (var (first, last) in who)
            {
                try
                {
                    var m = FindMember(first, last);
                    if (m == null) { failures.Add($"{first} {last}: hittades inte."); continue; }

                    var identity = await _memberManager.FindByEmailAsync(m.Email);
                    if (identity == null) { failures.Add($"{first} {last}: ingen identitet."); continue; }

                    var token = await _memberManager.GeneratePasswordResetTokenAsync(identity);
                    var res = await _memberManager.ResetPasswordAsync(identity, token, password);
                    if (res.Succeeded) done.Add(m.Email);
                    else failures.Add($"{first} {last}: " +
                                      string.Join("; ", res.Errors.Select(e => e.Description)));
                }
                catch (Exception ex) { failures.Add($"{first} {last}: {ex.Message}"); }
            }

            return Json(new { success = failures.Count == 0, dryRun = false, done, failures });
        }

        // ── Steg 5: skjutbana, incheckning och träningsmatch ─────────────────────────

        /// <summary>
        /// Underlaget till scenen om vad som RÄKNAS som aktivitet: klubbens egen bana,
        /// incheckningar på den, och en träningsmatch.
        ///
        /// ⚠️ Alla tre delarna har egen dubblettspärr, som resten av seedern.
        /// </summary>
        public IActionResult SeedRange(string confirm = "", bool dryRun = true)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = false, message = "Kör Seed först — Ankeborg finns inte." });

            if (dryRun)
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    bana = "Ankeborgs skjutbana, länkad till klubben, med inställningen påslagen",
                    incheckningar = "18 incheckningar spridda över året på filmens medlemmar",
                    match = "1 avslutad träningsmatch + 2 av Hasses pass märkta som matchpass"
                });

            var failures = new List<string>();
            int rangeCreated = 0, links = 0, checkIns = 0, matches = 0, tagged = 0, participants = 0;

            using var db = _databaseFactory.CreateDatabase();

            // ── Skjutbanan ───────────────────────────────────────────────────────────
            var rangeId = db.ExecuteScalar<int?>(
                "SELECT TOP 1 Id FROM ShootingRange WHERE HuvudmanClubId=@0", club.Id);
            if (rangeId is null or 0)
            {
                db.Execute(@"
INSERT INTO ShootingRange
 (Name, Latitude, Longitude, Address, Postcode, City, Municipality, County,
  LocationSensitivity, HuvudmanType, HuvudmanClubId, HuvudmanName, SkjutbanechefName,
  Description, Status, Source, CreatedByMemberId, CreatedAt, UpdatedAt, DefaultShotCount)
VALUES (N'Ankeborgs skjutbana', 57.1050, 12.2510, N'Skjutbanevägen 7', N'310 42',
        N'Ankeborg', N'Ankeborg', N'Halland', N'Exact', N'Club', @0,
        N'Ankeborg Pistolklubb', N'Britt-Marie Ekvall',
        N'Klubbens egen bana med 12 platser på 25 meter och 10 platser på 50 meter.',
        N'Active', N'Manual', @1, @2, @2, 40)", club.Id, FindMember("Sigrid", "Almkvist")?.Id ?? 0, DateTime.Now);
                rangeId = db.ExecuteScalar<int?>(
                    "SELECT TOP 1 Id FROM ShootingRange WHERE HuvudmanClubId=@0", club.Id);
                rangeCreated = 1;
            }

            if (rangeId is null or 0)
                return Json(new { success = false, message = "Kunde inte skapa skjutbanan." });

            var linked = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ClubRangeLink WHERE ClubId=@0 AND RangeId=@1", club.Id, rangeId);
            if (linked == 0)
            {
                db.Execute(@"
INSERT INTO ClubRangeLink (RangeId, ClubId, RelationType, AddedByMemberId, AddedAt)
VALUES (@0, @1, N'Huvudman', @2, @3)",
                    rangeId, club.Id, FindMember("Sigrid", "Almkvist")?.Id ?? 0, DateTime.Now);
                links = 1;
            }

            // ⚠️ SetValue på en saknad doctype-egenskap är en TYST no-op — då hade switchen
            // sett påslagen ut i seedern och av i gränssnittet. Kontrollera och säg ifrån.
            if (!club.HasProperty("activityFromRangeCheckIn"))
            {
                failures.Add("Klubben saknar egenskapen 'activityFromRangeCheckIn' — "
                           + "incheckningar kan inte räknas som aktivitet.");
            }
            else if (!club.GetValue<bool>("activityFromRangeCheckIn"))
            {
                club.SetValue("activityFromRangeCheckIn", true);
                _contentService.Save(club);
                _contentService.Publish(club, new[] { "*" }, -1);
            }

            // ── Incheckningar ────────────────────────────────────────────────────────
            var already = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM RangeActivitySession WHERE ClubId=@0", club.Id);
            if (already == 0)
            {
                // ⚠️ Spridda över året och på olika DAGAR än träningsloggen — annars viker
                // MarkRedundantCheckIns ihop dem som "samma tillfälle" och de syns inte i
                // sammanställningen, vilket är precis det scenen ska visa.
                var who = new[] { ("Hasse", "Lindwall"), ("Elin", "Hagberg"), ("Yvonne", "Sjöstrand"),
                                  ("Anneli", "Sundström"), ("Torbjörn", "Rydell"), ("Karin", "Fridell") };
                for (var i = 0; i < 18; i++)
                {
                    var (f, l) = who[i % who.Length];
                    var m = FindMember(f, l);
                    if (m == null) continue;
                    var d = DateTime.Now.Date.AddDays(-(i * 11 + 6));
                    try
                    {
                        db.Execute(@"
INSERT INTO RangeActivitySession
 (RangeId, MemberId, ClubId, Date, StartTime, EndTime, ShotCount, ShotCountSource,
  ShooterCount, EnteredByMemberId, CreatedAt)
VALUES (@0, @1, @2, @3, @4, @5, @6, N'Member', 1, @1, @7)",
                            rangeId, m.Id, club.Id, d,
                            d.AddHours(18), d.AddHours(20), 40 + (i % 3) * 10, DateTime.Now);
                        checkIns++;
                    }
                    catch (Exception ex) { failures.Add($"Incheckning {f} {l}: {ex.Message}"); }
                }
            }

            // ── Träningsmatch ────────────────────────────────────────────────────────
            var matchId = db.ExecuteScalar<int?>(
                "SELECT TOP 1 Id FROM TrainingMatches WHERE ClubId=@0", club.Id);
            if (matchId is null or 0)
            {
                var creator = FindMember("Elin", "Hagberg")?.Id ?? 0;
                db.Execute(@"
INSERT INTO TrainingMatches
 (MatchCode, MatchName, CreatedByMemberId, WeaponClass, CreatedDate, Status, CompletedDate,
  StartDate, IsOpen, HasHandicap, MaxSeriesCount, IsTeamMatch, MaxShootersPerTeam,
  LastActivityDate, Discipline, ClubId)
VALUES (N'ANK427', N'Onsdagsmatchen', @0, N'C', @1, N'Completed', @2, @2, 1, 1, 5, 0, NULL,
        @2, N'Precision', @3)",
                    creator, DateTime.Now.AddDays(-30), DateTime.Now.AddDays(-28), club.Id);
                matchId = db.ExecuteScalar<int?>(
                    "SELECT TOP 1 Id FROM TrainingMatches WHERE ClubId=@0", club.Id);
                matches = 1;
            }

            // ── Deltagare och poäng i matchen ────────────────────────────────────────
            // ⚠️ UTAN DETTA GÅR TAVLAN INTE ATT FOTOGRAFERA. En match utan deltagare
            // renderar en tom skärm, och den enda befintliga skärmdumpen (i mappen
            // Training Match) visar RIKTIGA medlemmars namn och ansikten — den kan inte
            // ligga i en film som ska ligga publikt på YouTube.
            if (matchId is > 0)
            {
                var hasParts = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId=@0", matchId);
                if (hasParts == 0)
                {
                    var lineup = new[]
                    {
                        ("Elin", "Hagberg", 0.0), ("Hasse", "Lindwall", 1.75),
                        ("Yvonne", "Sjöstrand", 3.5), ("Torbjörn", "Rydell", 0.0),
                        ("Karin", "Fridell", 2.25), ("Anneli", "Sundström", 4.0),
                    };
                    var order = 0;
                    foreach (var (f, l, hcp) in lineup)
                    {
                        var m = FindMember(f, l);
                        if (m == null) continue;
                        try
                        {
                            db.Execute(@"
INSERT INTO TrainingMatchParticipants
 (TrainingMatchId, MemberId, JoinedDate, DisplayOrder, FrozenHandicapPerSeries, FrozenIsProvisional)
VALUES (@0, @1, @2, @3, @4, 0)",
                                matchId, m.Id, DateTime.Now.AddDays(-30), order++, (decimal)hcp);

                            // En resultatrad per deltagare, knuten till matchen. Samma JSON-form
                            // som träningsloggen — se BuildSeriesJson.
                            var (json, total, x) = BuildSeriesJson(m.Id, order);
                            db.Execute(@"
INSERT INTO TrainingScores
 (MemberId, TrainingDate, WeaponClass, SeriesScores, TotalScore, XCount, Notes,
  CreatedAt, UpdatedAt, IsCompetition, Discipline, TrainingMatchId)
VALUES (@0, @1, 'C', @2, @3, @4, @5, @6, @6, 0, 'Precision', @7)",
                                m.Id, DateTime.Now.AddDays(-30), json, total, x, SeedTag,
                                DateTime.Now, matchId);
                            participants++;
                        }
                        catch (Exception ex) { failures.Add($"Matchdeltagare {f} {l}: {ex.Message}"); }
                    }
                }
            }

            // ⚠️ En traningsmatch RAKNAS som aktivitet genom att traningsradens
            // TrainingMatchId ar satt — MemberActivitySummary provar det FORE IsCompetition.
            // Utan de har tva raderna star "0 traningsmatcher" i panelen medan filmen
            // pastar att de raknas.
            if (matchId is > 0)
            {
                var hasse = FindMember("Hasse", "Lindwall");
                if (hasse != null)
                {
                    tagged = db.Execute(@"
UPDATE TOP (2) TrainingScores SET TrainingMatchId=@0
WHERE MemberId=@1 AND Notes=@2 AND TrainingMatchId IS NULL",
                        matchId, hasse.Id, SeedTag);
                }
            }

            return Json(new
            {
                success = failures.Count == 0,
                dryRun = false,
                rangeId,
                rangeCreated,
                clubRangeLinks = links,
                checkInsCreated = checkIns,
                matchesCreated = matches,
                matchParticipants = participants,
                trainingRowsTaggedAsMatch = tagged,
                failures
            });
        }

        // ── Städning ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tar bort seedens data. Medlemmar raderas bara om de har en ClubMembership märkt med
        /// <see cref="SeedTag"/> — en medlem som fanns i dev före seeden rörs aldrig.
        /// </summary>
        public IActionResult Purge(string confirm = "", bool reallyDelete = false, bool deleteClubNode = false)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = true, message = "Inget att städa — Ankeborg finns inte." });

            using var db = _databaseFactory.CreateDatabase();
            var seededMemberIds = db.Fetch<int>(
                "SELECT MemberId FROM ClubMembership WHERE ClubId=@0 AND MemberNotes=@1", club.Id, SeedTag);

            if (!reallyDelete)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    clubNodeId = club.Id,
                    wouldDeleteMembers = seededMemberIds.Count,
                    wouldDeleteClubNode = deleteClubNode,
                    hint = "Lagg till &reallyDelete=true for att kora."
                });
            }

            db.Execute("DELETE FROM MarkenSeries WHERE ClubId=@0 AND Notes=@1", club.Id, SeedTag);
            db.Execute("DELETE FROM ForeningsintygRequest WHERE ClubId=@0", club.Id);
            if (seededMemberIds.Count > 0)
            {
                // ⚠️ IN (@0) med en lista tar ~2100 parametrar — se memory/sql-in-list-parameter-cap.
                // Rostern är 45 personer, så listan är alltid långt under taket.
                db.Execute("DELETE FROM TrainingScores WHERE MemberId IN (@0)", seededMemberIds);
            }
            db.Execute("DELETE FROM ClubMembership WHERE ClubId=@0 AND MemberNotes=@1", club.Id, SeedTag);

            var deleted = 0;
            foreach (var id in seededMemberIds)
            {
                var m = _memberService.GetById(id);
                if (m == null) continue;
                if (!m.Email.EndsWith("@" + MailDomain, StringComparison.OrdinalIgnoreCase)) continue;
                _memberService.Delete(m);
                deleted++;
            }

            if (deleteClubNode)
                _contentService.Delete(club);

            return Json(new
            {
                success = true,
                membersDeleted = deleted,
                clubNodeDeleted = deleteClubNode
            });
        }

        // ── Hjälpare ─────────────────────────────────────────────────────────────────

        private IActionResult? Guard(string confirm)
        {
            if (!string.Equals(confirm, Confirm, StringComparison.Ordinal))
                return Json(new { success = false, message = $"Saknar ?confirm={Confirm}." });

            if (!_env.IsDevelopment())
                return Json(new { success = false, message = "Seedern kör bara i Development." });

            var dsn = _configuration.GetConnectionString("umbracoDbDSN") ?? "";
            var looksLocal = dsn.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                          || dsn.Contains("(localdb)", StringComparison.OrdinalIgnoreCase)
                          || dsn.Contains(".\\SQLEXPRESS", StringComparison.OrdinalIgnoreCase);
            if (!looksLocal)
                return Json(new
                {
                    success = false,
                    message = "Anslutningssträngen ser inte lokal ut — vägrar skriva. "
                            + "Starta appen med --launch-profile \"Umbraco.Web.UI\"."
                });

            return null;
        }

        private static string Email(Person p) =>
            $"{Slug(p.First)}.{Slug(p.Last)}@{MailDomain}";

        private static string Slug(string s) => s.ToLowerInvariant()
            .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
            .Replace("é", "e").Replace("-", "").Replace(" ", "");

        /// <summary>Deterministiskt "antal år som medlem" så att listan inte ser maskinell ut.</summary>
        private static int RandomYears(Person p) => 1 + (Math.Abs(p.Last.GetHashCode()) % 18);

        private IContent? FindClub()
        {
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) return null;
            return Descendants(root).FirstOrDefault(c =>
                c.ContentType.Alias == "club" &&
                string.Equals(c.Name, ClubNodeName, StringComparison.OrdinalIgnoreCase));
        }

        private int? ResolveClubsPageId(string region, out string error)
        {
            error = "";
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) { error = "Ingen rotnod hittades."; return null; }

            var regionNode = Descendants(root).FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                (c.Name ?? "").Contains(region, StringComparison.OrdinalIgnoreCase));
            if (regionNode == null) { error = $"Hittade ingen regionalPage för '{region}'."; return null; }

            var clubsPage = _contentService.GetPagedChildren(regionNode.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
            if (clubsPage == null) { error = $"Hittade ingen clubsPage under {regionNode.Name}."; return null; }

            return clubsPage.Id;
        }

        private IEnumerable<IContent> Descendants(IContent parent)
        {
            var queue = new Queue<IContent>();
            queue.Enqueue(parent);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in _contentService.GetPagedChildren(current.Id, 0, int.MaxValue, out _))
                {
                    yield return child;
                    queue.Enqueue(child);
                }
            }
        }

        private IMember? FindMember(string first, string last) =>
            _memberService.GetByEmail($"{Slug(first)}.{Slug(last)}@{MailDomain}");

        private List<int> MembersOfClub(int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>("SELECT MemberId FROM ClubMembership WHERE ClubId=@0", clubId);
        }

        private void AssignClubRole(string group, string first, string last,
                                    List<string> assigned, List<string> failures)
        {
            try
            {
                var member = FindMember(first, last);
                if (member == null) { failures.Add($"Roll {group}: hittade inte {first} {last}."); return; }

                // Medlemsgruppen måste finnas som nod innan AssignRoles biter.
                // GetAllRoles() ger IMemberGroup i den här Umbraco-versionen, inte strängar.
                if (_memberService.GetAllRoles().All(r => !string.Equals(r.Name, group, StringComparison.OrdinalIgnoreCase)))
                    _memberService.AddRole(group);

                _memberService.AssignRoles(new[] { member.Id }, new[] { group });
                assigned.Add($"{group} -> {first} {last} (#{member.Id})");
            }
            catch (Exception ex)
            {
                failures.Add($"Roll {group}: {ex.Message}");
            }
        }

        private int SeedTrainingScores(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
                                       int memberId, int count)
        {
            var existing = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM TrainingScores WHERE MemberId=@0 AND Notes=@1", memberId, SeedTag);
            if (existing > 0) return 0;

            var created = 0;
            for (var i = 0; i < count; i++)
            {
                var date = DateTime.Now.AddDays(-(i * 14 + 3));
                var (json, total, x) = BuildSeriesJson(memberId, i);
                db.Execute(@"
INSERT INTO TrainingScores
 (MemberId, TrainingDate, WeaponClass, SeriesScores, TotalScore, XCount, Notes,
  CreatedAt, UpdatedAt, IsCompetition, Discipline)
VALUES (@0, @1, 'C', @2, @3, @4, @5, @6, @6, 0, 'Precision')",
                    memberId, date, json, total, x, SeedTag, DateTime.Now);
                created++;
            }
            return created;
        }
    }
}
