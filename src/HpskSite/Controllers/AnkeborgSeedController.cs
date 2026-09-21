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
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
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
        private readonly MarkenLedgerService _markenLedger;
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
            MarkenLedgerService markenLedger,
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
            _markenLedger = markenLedger;
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
            bool Pending = false,
            // ⚠️ KÖN OCH ÅLDERSGRUPP ÄR INTE DEKORATION. Mästerskapsklasserna i vapengrupp C är
            // Dam, Vet Y, Vet Ä och Junior, och seedens tävlingar delar ut medaljer i dem. Utan de
            // här två fälten fördelades skyttarna i tur och ordning, och första bilduttaget hade
            // "Bertil Ranstorp — brons i C Dam" mitt i beställningslistan. Mottagarna av det
            // utskicket är pistolskyttar; ett sådant fel läses som att hela sammanställningen är
            // påhittad. Se PickFor().
            string Sex = "M",     // "K" | "M"
            string Band = "S");   // "J" junior · "S" senior · "VY" yngre veteran · "VA" äldre veteran

        private static readonly List<Person> Roster = new()
        {
            new("Sigrid",       "Almkvist",    "klubbadmin (filmens 'vi')", "2", Sex: "K"),
            new("Nils",         "Tegelberg",   "väntande ansökan",          null, Pending: true),
            new("Britt-Marie",  "Ekvall",      "skjutledare",               "1", Sex: "K", Band: "VY"),
            new("Hasse",        "Lindwall",    "aktivitet + föreningsintyg","2", Band: "VY"),
            new("Yvonne",       "Sjöstrand",   "guldserieskytt",            "1", Sex: "K"),
            new("Gunnar",       "Falkenmark",  "ordförande",                "3", Band: "VA"),
            new("Elin",         "Hagberg",     "medlemmen på Min Sida",     "2", Sex: "K"),

            new("Torbjörn",  "Rydell",     "medlem", "3", Band: "VY"),
            new("Margareta", "Wiklund",    "medlem", "2", Sex: "K", Band: "VY"),
            new("Kenneth",   "Blomgren",   "medlem", "1", Band: "VY"),
            new("Anneli",    "Sundström",  "medlem", "2", Sex: "K"),
            new("Rolf",      "Hjelmberg",  "medlem", "3", Band: "VA"),
            new("Ingela",    "Norrby",     "medlem", "1", Sex: "K"),
            new("Sven-Erik", "Dahlgren",   "medlem", "2", Band: "VY"),
            new("Birgitta",  "Lundahl",    "medlem", "3", Sex: "K", Band: "VY"),
            new("Mats",      "Örnberg",    "medlem", "1"),
            new("Karin",     "Fridell",    "medlem", "2", Sex: "K"),
            new("Lennart",   "Sjökvist",   "medlem", "3", Band: "VA"),
            new("Ulla",      "Bergquist",  "medlem", "2", Sex: "K", Band: "VA"),
            new("Håkan",     "Melander",   "medlem", "1", Band: "VY"),
            new("Siv",       "Åkerlund",   "medlem", "2", Sex: "K", Band: "VA"),
            new("Bertil",    "Ranstorp",   "medlem", "3", Band: "VA"),
            new("Monica",    "Hedlund",    "medlem", "1", Sex: "K", Band: "VY"),
            new("Jan-Olof",  "Tornberg",   "medlem", "2", Band: "VY"),
            new("Elisabet",  "Widmark",    "medlem", "3", Sex: "K", Band: "VY"),
            new("Per-Åke",   "Strandberg", "medlem", "1", Band: "VA"),
            new("Gunilla",   "Rosander",   "medlem", "2", Sex: "K", Band: "VY"),
            new("Åke",       "Lindgren",   "medlem", "3", Band: "VA"),
            new("Vivianne",  "Sandell",    "medlem", "2", Sex: "K", Band: "VA"),
            new("Bo",        "Kjellberg",  "medlem", "1", Band: "VA"),
            new("Marianne",  "Ödman",      "medlem", "2", Sex: "K", Band: "VA"),
            new("Stig",      "Hammarlund", "medlem", "3", Band: "VA"),
            new("Berit",     "Falk",       "medlem", "1", Sex: "K", Band: "VA"),
            new("Ove",       "Tranberg",   "medlem", "2", Band: "VA"),
            new("Kerstin",   "Wallin",     "medlem", "3", Sex: "K", Band: "VY"),
            new("Göran",     "Ekström",    "medlem", "1", Band: "VA"),
            new("Astrid",    "Molander",   "medlem", "2", Sex: "K", Band: "VA"),
            new("Nils-Erik", "Byström",    "medlem", "3", Band: "VA"),
            new("Lena",      "Hallberg",   "medlem", "2", Sex: "K"),
            new("Arne",      "Sjöberg",    "medlem", "1", Band: "VY"),
            new("Inger",     "Palmgren",   "medlem", "2", Sex: "K", Band: "VY"),
            new("Kjell",     "Roos",       "medlem", "3", Band: "VA"),
            new("Solveig",   "Enander",    "medlem", "1", Sex: "K", Band: "VA"),
            new("Tommy",     "Lindqvist",  "medlem", "2"),
            new("Barbro",    "Nyström",    "medlem", "3", Sex: "K", Band: "VY"),

            // Juniorer. Tillagda 2026-09-21 för att klubbmästerskapens juniorklass ska kunna
            // bemannas med namn som läses som juniorer — rostern i övrigt är vuxna. Tillagda
            // SIST med flit: de sju första är filmens namngivna roller (MANUS.md) och allt som
            // plockar "de N första" ur rostern ska ge samma personer som förut.
            new("Wilma",     "Åkerberg",   "junior", "3", Sex: "K", Band: "J"),
            new("Elias",     "Norling",    "junior", "3", Band: "J"),
            new("Moa",       "Hjelm",      "junior", "2", Sex: "K", Band: "J"),
            new("Viktor",    "Sandin",     "junior", "3", Band: "J"),

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
                    // Prisraderna, inte fritexten. Seed-data som skriver det gamla faltet hade
                    // gjort varje ny demoklubb till en ny migrering att kora.
                    if (e.Fee > 0)
                        node.SetValue(HpskSite.Models.EventPrices.Property,
                            HpskSite.Models.EventPrices.Serialize(
                                new[] { new HpskSite.Models.EventPrice("avgift", "Avgift", e.Fee) }));
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

        // ── Klubbmästerskap med resultat ─────────────────────────────────────────────

        /// <summary>
        /// En mästerskapsklass i ett seedat klubbmästerskap: vilken skytteklass skyttarna står i
        /// och hur många av dem. Antalet styr MEDALJERNA — se <see cref="ChampionshipMedalCount"/>:
        /// fem deltagare i mästerskapsklassen ger guld/silver/brons, fyra ger guld+silver, tre ger
        /// enbart guld. Juniorklassen går åt andra hållet och ger medalj till alla upp till tre.
        /// </summary>
        private sealed record MasterskapClass(string ShootingClass, int Shooters, double TopAverage);

        /// <summary>
        /// Vilka ur rostern som får stå i en skytteklass.
        ///
        /// <para>⚠️ Detta är sanningskravet i bilden. "C1 Dam" måste bemannas med kvinnor och
        /// "C Jun" med juniorer, annars står det en Bertil i damklassen — och mottagarna av
        /// utskicket är pistolskyttar som ser det direkt. Kravet är STRIKT: hittas för få
        /// kandidater loggas det som ett fel i stället för att falla tillbaka på någon annan, för
        /// en tyst fallback är precis hur felet uppstod första gången.</para>
        /// </summary>
        private static bool PickFor(Person p, string shootingClass)
        {
            var cls = shootingClass.Replace("_", " ");

            if (cls.Contains("Jun", StringComparison.OrdinalIgnoreCase)) return p.Band == "J";
            if (cls.Contains("Dam", StringComparison.OrdinalIgnoreCase)) return p.Sex == "K" && p.Band != "J";
            if (cls.Contains("Vet Y", StringComparison.OrdinalIgnoreCase)) return p.Band == "VY";
            if (cls.Contains("Vet Ä", StringComparison.OrdinalIgnoreCase)
             || cls.Contains("Vet A", StringComparison.OrdinalIgnoreCase)) return p.Band == "VA";

            // Öppen klass: vem som helst utom juniorerna, som har sin egen klass i planen.
            return p.Band != "J";
        }

        /// <summary>Ett seedat klubbmästerskap.</summary>
        private sealed record MasterskapSeed(
            string Name,
            string CompetitionType,
            int Series,
            int DaysAgo,
            MasterskapClass[] Classes);

        /// <summary>
        /// Klubbmästerskapen som ger Ankeborg en trovärdig medaljhög att visa upp — underlaget
        /// till marknadsföringsbilderna av <b>Mästerskapsmedaljer</b> och <b>Märken</b>.
        ///
        /// <para><b>⚠️ ANTALET SKYTTAR PER MÄSTERSKAPSKLASS ÄR INTE DEKORATION.</b> Medaljerna
        /// reduceras under fem deltagare (SHB C.3.4.1), så en klass med tre skyttar ger ett enda
        /// guld. Vill man se en full uppsättning medaljer måste klassen ha minst fem. Ändrar du
        /// siffrorna nedan ändrar du alltså medaljantalet — inte bara listans längd.</para>
        ///
        /// <para><b>⚠️ Vapengrupp C delas i sina fem mästerskapsklasser</b> (öppen, Dam, Vet Y,
        /// Vet Ä, Junior) eftersom <c>medalsPerWeaponGroup</c> lämnas OSATT, vilket är förvalet.
        /// Se <see cref="MedalGrouping"/> — seedern sätter medvetet inte egenskapen: den skapas
        /// för hand i backoffice, och <c>SetValue</c> på en saknad egenskap är en tyst no-op.</para>
        ///
        /// <para><b>⚠️ Skicklighetsklasserna 1–3 är INTE egna mästerskapsklasser.</b> C1, C2 och
        /// C3 är alla "C öppen" och tävlar om SAMMA tre medaljer. Därför blandas de med flit inom
        /// samma rad här — det är kategorin, inte klassen, som räknas.</para>
        /// </summary>
        private static readonly MasterskapSeed[] MasterskapPlan =
        {
            // Precision, vapengrupp A — 7 serier. En kategori ("A"), åtta skyttar → tre medaljer.
            new("KM Precision A", "Precision", 7, 214, new MasterskapClass[]
            {
                new("A1", 3, 47.5), new("A2", 3, 46.0), new("A3", 2, 44.5),
            }),

            // Precision, vapengrupp B — en kategori ("B"), sex skyttar → tre medaljer.
            new("KM Precision B", "Precision", 7, 200, new MasterskapClass[]
            {
                new("B1", 2, 47.0), new("B2", 2, 45.5), new("B3", 2, 44.0),
            }),

            // Precision, vapengrupp C — klubbens stora gren. FEM mästerskapsklasser, och alla
            // utom junior har minst fem skyttar → 3+3+3+3+3 = 15 medaljer ur en enda tävling.
            new("KM Precision C", "Precision", 7, 186, new MasterskapClass[]
            {
                new("C1", 2, 48.0), new("C2", 2, 46.5), new("C3", 2, 45.0),   // C öppen: 6
                new("C1 Dam", 2, 47.0), new("C2 Dam", 2, 45.5), new("C3 Dam", 1, 44.0), // C Dam: 5
                new("C Vet Y", 5, 46.5),
                new("C Vet Ä", 5, 45.5),
                new("C Jun", 3, 44.5),
            }),

            // Duell, vapengrupp C — 6 serier. Två kategorier med fem skyttar → sex medaljer.
            new("KM Duell C", "Duell", 6, 158, new MasterskapClass[]
            {
                new("C1", 2, 46.0), new("C2", 2, 45.0), new("C3", 1, 43.5),   // C öppen: 5
                new("C1 Dam", 2, 45.5), new("C2 Dam", 3, 44.0),               // C Dam: 5
            }),

            // Milsnabb, vapengrupp A — 12 serier (10 s / 8 s / 6 s). Sex skyttar → tre medaljer.
            new("KM Milsnabb A", "Milsnabb", 12, 130, new MasterskapClass[]
            {
                new("A1", 2, 46.0), new("A2", 2, 44.5), new("A3", 2, 43.0),
            }),

            // Milsnabb, vapengrupp C — C öppen (6) + C Vet Ä (5) → sex medaljer.
            new("KM Milsnabb C", "Milsnabb", 12, 116, new MasterskapClass[]
            {
                new("C1", 2, 46.5), new("C2", 2, 45.0), new("C3", 2, 43.5),
                new("C Vet Ä", 5, 44.0),
            }),
        };

        /// <summary>
        /// Skapar sex klubbmästerskap på Ankeborg med anmälningar OCH färdiga serieresultat, så
        /// att medaljerna faktiskt går att räkna fram.
        ///
        /// <para><b>⚠️ RESULTATARTEFAKTEN SKRIVS INTE HÄRIFRÅN.</b> <c>resultData</c> har exakt en
        /// skrivväg — <c>CompetitionResultsController</c> — och den är privat med flit (se
        /// memory/result-artifact-single-write-path). Seedern skriver alltså bara serieraderna,
        /// precis som en sekreterare som matat in resultaten, och lämnar
        /// <c>CompetitionResults/CreateResultsList</c> åt anroparen. Svaret räknar upp
        /// tävlings-id:na just för det. En egen artefaktskrivning här vore en andra skrivväg, och
        /// det är precis den buggklassen minnet beskriver.</para>
        ///
        /// <para>Idempotent: en tävling som redan finns återanvänds, och serieraderna skrivs med
        /// MERGE på (tävling, medlem, klass, serie) — samma nyckel som resultatinmatningen.</para>
        /// </summary>
        public IActionResult SeedMasterskap(string confirm = "", bool dryRun = true)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = false, message = "Kör Seed först — Ankeborg finns inte." });

            var hubId = ResolveCompetitionsHubId(out var hubError);
            if (hubId == null)
                return Json(new { success = false, message = hubError });

            // Skyttarna hämtas ur rostern i ordning och förbrukas — samma person ska inte stå i
            // två mästerskapsklasser i samma tävling (F.2.2), och helst inte vinna allt heller.
            var pool = Roster.Where(p => !p.Pending).ToList();
            var needed = MasterskapPlan.Sum(m => m.Classes.Sum(c => c.Shooters));

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    tavlingar = MasterskapPlan.Select(m => new
                    {
                        m.Name,
                        typ = m.CompetitionType,
                        serier = m.Series,
                        datum = DateTime.Now.Date.AddDays(-m.DaysAgo).ToString("yyyy-MM-dd"),
                        skyttar = m.Classes.Sum(c => c.Shooters),
                        klasser = m.Classes.Select(c => $"{c.ShootingClass} ({c.Shooters})"),
                        medaljer = MedalPreview(m),
                    }),
                    rosterSize = pool.Count,
                    shootersNeededPerCompetition = MasterskapPlan.Max(m => m.Classes.Sum(c => c.Shooters)),
                    totalStarts = needed,
                    nextStep = "Kör om med dryRun=false, och POSTa sedan "
                             + "CompetitionResults/CreateResultsList för varje tävlings-id i svaret."
                });
            }

            var failures = new List<string>();
            var created = new List<object>();
            var compIds = new List<int>();
            var today = DateTime.Now.Date;

            using var db = _databaseFactory.CreateDatabase();

            foreach (var plan in MasterskapPlan)
            {
                try
                {
                    var date = today.AddDays(-plan.DaysAgo);
                    var classIds = plan.Classes.Select(c => c.ShootingClass).Distinct().ToArray();

                    var comp = _contentService.GetPagedChildren(hubId.Value, 0, int.MaxValue, out _)
                        .FirstOrDefault(x => x.ContentType.Alias == "competition"
                                          && string.Equals(x.Name, plan.Name, StringComparison.OrdinalIgnoreCase));

                    if (comp == null)
                    {
                        comp = _contentService.Create(plan.Name, hubId.Value, "competition");
                        comp.SetValue("competitionName", plan.Name);
                        comp.SetValue("competitionType", plan.CompetitionType);
                        comp.SetValue("clubId", club.Id);
                        comp.SetValue("regionalFederation", "Halland");
                        comp.SetValue("venue", "Ankeborgs skjutbana");
                        comp.SetValue("description",
                            $"<p>{plan.Name} — klubbmästerskap, {plan.Series} serier.</p>");
                        comp.SetValue("competitionDate", date.AddHours(9));
                        comp.SetValue("registrationOpenDate", date.AddDays(-40));
                        comp.SetValue("registrationCloseDate", date.AddDays(-3));
                        comp.SetValue("numberOfSeriesOrStations", plan.Series);
                        comp.SetValue("numberOfFinalSeries", 0);
                        comp.SetValue("maxParticipants", 40);
                        comp.SetValue("registrationFee", 60m);
                        comp.SetValue("juniorRegistrationFee", 0m);
                        comp.SetValue("isActive", true);
                        comp.SetValue("isClubOnly", true);
                        comp.SetValue("allowTeams", false);
                        comp.SetValue("allowStafett", false);
                        comp.SetValue("showLiveResults", true);
                        comp.SetValue("competitionDirector", "Sigrid Almkvist");
                        comp.SetValue("contactEmail", $"tavling@{MailDomain}");
                        // ⚠️ Det är DEN HÄR raden som gör tävlingen till ett mästerskap. Utan den
                        // räknas inga mästerskapsmedaljer alls och medaljpanelen står tom — och
                        // tävlingen syns då som en vanlig klubbtävling, vilket ser ut som ett fel
                        // i panelen snarare än som ett saknat fält här.
                        comp.SetValue("competitionScope", CompetitionScopeHelper.Klubbmasterskap);
                        comp.SetValue("shootingClassIds", ShootingClassIdsValue.Normalize(classIds));

                        if (!_contentService.Save(comp).Success)
                        {
                            failures.Add($"{plan.Name}: kunde inte sparas.");
                            continue;
                        }
                        var pub = _contentService.Publish(comp, new[] { "*" }, -1);
                        if (!pub.Success)
                        {
                            failures.Add($"{plan.Name}: sparad men inte publicerad — "
                                       + string.Join(", ", pub.EventMessages?.GetAll().Select(m => m.Message)
                                                           ?? Array.Empty<string>()));
                            continue;
                        }
                    }

                    var (regs, rows, medals) = SeedMasterskapResults(db, comp, club.Id, plan, pool, date, failures);
                    compIds.Add(comp.Id);
                    created.Add(new
                    {
                        id = comp.Id,
                        name = plan.Name,
                        date = date.ToString("yyyy-MM-dd"),
                        registrations = regs,
                        seriesRows = rows,
                        expectedMedals = medals
                    });
                }
                catch (Exception ex) { failures.Add($"{plan.Name}: {ex.Message}"); }
            }

            return Json(new
            {
                success = failures.Count == 0,
                competitions = created,
                competitionIds = compIds,
                expectedMedalsTotal = MasterskapPlan.Sum(MedalPreview),
                nextStep = "POSTa CompetitionResults/CreateResultsList "
                         + "{ competitionId, keepExistingMerges: true } för varje id ovan — "
                         + "det är 'Uppdatera' på Resultat-fliken, och den enda vägen till resultData.",
                failures
            });
        }

        /// <summary>Hur många mästerskapsmedaljer planen ger, enligt samma regel som ytorna.</summary>
        private static int MedalPreview(MasterskapSeed plan) =>
            plan.Classes
                .GroupBy(c => ChampionshipCategory.For(c.ShootingClass, splitGroupC: true))
                .Sum(g => ChampionshipMedalCount.For(
                        g.Sum(c => c.Shooters),
                        g.Key.Contains("Jun", StringComparison.OrdinalIgnoreCase)).Medals);

        /// <summary>
        /// Anmälningar + serieresultat för ett seedat mästerskap.
        ///
        /// ⚠️ <b>Klassen skrivs i NAMN-form på resultatraden</b> (<c>ShootingClasses.ToCanonicalName</c>).
        /// Id- och namnformen är samma sträng för C1/A2/B3 men skiljer sig för exakt de klasser den
        /// här planen bygger på — C Vet Ä, C1 Dam, C Jun. Fel form hade alltså sett helt rätt ut i
        /// vapengrupp A och B och tyst delat upp veteranerna. Se
        /// memory/shooting-class-id-vs-name-canonical.
        /// </summary>
        private (int Registrations, int Rows, int Medals) SeedMasterskapResults(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            IContent comp, int clubId, MasterskapSeed plan, List<Person> pool,
            DateTime date, List<string> failures)
        {
            var table = CompetitionResultTables.For(plan.CompetitionType);

            // ⚠️ Tabellnamnet interpoleras in i SQL:en. Det är ofarligt HÄR eftersom det kommer ur
            // CompetitionResultTables och aldrig ur ett anrop — men skriv aldrig om det till att ta
            // emot ett namn utifrån.
            var mergeSql = $@"
                MERGE INTO [{table}] AS target
                USING (SELECT @0 AS CompetitionId, @1 AS MemberId, @2 AS ShootingClass, @3 AS SeriesNumber) AS source
                ON target.CompetitionId = source.CompetitionId
                   AND target.MemberId = source.MemberId
                   AND target.ShootingClass = source.ShootingClass
                   AND target.SeriesNumber = source.SeriesNumber
                WHEN MATCHED THEN
                    UPDATE SET Shots = @4, TeamNumber = @5, Position = @6,
                               EnteredBy = @7, LastModified = @8
                WHEN NOT MATCHED THEN
                    INSERT (CompetitionId, SeriesNumber, MemberId, TeamNumber, Position,
                            ShootingClass, Shots, EnteredBy, EnteredAt, LastModified)
                    VALUES (@0, @3, @1, @5, @6, @2, @4, @7, @8, @8);";

            // ⚠️ TÄVLINGEN RENSAS FÖRST, och det är inte samma sak som att skriva över.
            // Klassen ingår i resultatradens nyckel, så en MERGE lägger till en NY rad när en
            // skytt byter klass mellan två körningar i stället för att flytta hen — och skytten
            // står då kvar i sin gamla mästerskapsklass också. Det upptäcktes när rostern fick
            // kön och åldersgrupp: utan rensningen hade Bertil Ranstorp blivit kvar i C Dam vid
            // sidan av sin nya klass. Tävlingarna ägs helt av seedern, så rensningen är trygg
            // HÄR och bara här.
            db.Execute($"DELETE FROM [{table}] WHERE CompetitionId = @0", comp.Id);

            var regHub = _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (regHub == null)
            {
                regHub = _contentService.Create("Anmälningar", comp.Id, "competitionRegistrationsHub");
                _contentService.Save(regHub);
            }
            foreach (var old in _contentService.GetPagedChildren(regHub.Id, 0, int.MaxValue, out _)
                         .Where(c => c.ContentType.Alias == "competitionRegistration"
                                  && string.Equals(c.GetValue<string>("registeredBy"), SeedTag, StringComparison.Ordinal))
                         .ToList())
            {
                _contentService.Delete(old);
            }
            var alreadyRegistered = _contentService.GetPagedChildren(regHub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .Select(c => c.GetValue<int>("memberId"))
                .ToHashSet();

            var now = DateTime.Now;
            int regs = 0, rows = 0;
            var offset = Math.Abs(comp.Id) % Math.Max(1, pool.Count);   // olika startskyttar per tävling
            var take = 0;
            var team = 1;
            var pos = 0;

            // ⚠️ Varje skytts slutsumma måste vara unik i HELA tävlingen, inte bara i klassen:
            // mästerskapsklassen "C öppen" spänner över C1, C2 och C3, och en delad summa där ger
            // en oavgjord medaljplats lika säkert som inom en klass.
            var usedTotals = new HashSet<int>();

            // Ingen skytt får dubbleras inom tävlingen — en skytt startar inte i två klasser i
            // samma vapengrupp (F.2.2), och en dubblett hade dessutom gett samma person två
            // medaljer i samma mästerskapsklass.
            var usedInComp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cls in plan.Classes)
            {
                var canonical = ShootingClasses.ToCanonicalName(cls.ShootingClass);

                // Kandidaterna för klassen, roterade per tävling så att inte samma personer
                // vinner allt i alla sex mästerskapen.
                var eligible = pool.Where(x => PickFor(x, canonical)).ToList();
                if (eligible.Count == 0)
                {
                    failures.Add($"{plan.Name}: inga kandidater för klassen {canonical} — "
                               + "kontrollera Sex/Band i rostern.");
                    continue;
                }
                var rot = offset % eligible.Count;
                var ordered = eligible.Skip(rot).Concat(eligible.Take(rot)).ToList();

                var placed = 0;
                foreach (var person in ordered)
                {
                    if (placed >= cls.Shooters) break;
                    var key = $"{person.First} {person.Last}";
                    if (!usedInComp.Add(key)) continue;

                    var i = placed;
                    placed++;
                    take++;

                    var member = FindMember(person.First, person.Last);
                    if (member == null)
                    {
                        failures.Add($"{plan.Name}: hittade inte {person.First} {person.Last}.");
                        continue;
                    }

                    pos++;
                    if (pos > 8) { pos = 1; team++; }

                    // ── Anmälan ──────────────────────────────────────────────────────
                    if (!alreadyRegistered.Contains(member.Id))
                    {
                        try
                        {
                            var name = $"{person.First} {person.Last}";
                            var reg = _contentService.Create(
                                $"{name} - {date:yyyy-MM-dd}", regHub.Id, "competitionRegistration");
                            reg.SetValue("competitionId", comp.Id);
                            reg.SetValue("memberId", member.Id);
                            reg.SetValue("memberName", name);
                            reg.SetValue("clubId", clubId);
                            reg.SetValue("shootingClasses",
                                CompetitionRegistrationDocument.SerializeShootingClasses(
                                    new List<ShootingClassEntry>
                                    {
                                        new ShootingClassEntry
                                        {
                                            Class = canonical,
                                            StartPreference = "Inget",
                                            TeamNumber = team
                                        }
                                    }));
                            reg.SetValue("registrationDate", date.AddDays(-14));
                            reg.SetValue("registeredBy", SeedTag);
                            reg.SetValue("isActive", true);
                            if (reg.HasProperty("isSubCompetition")) reg.SetValue("isSubCompetition", false);
                            // ⚠️ Save, aldrig Publish — en publicerad anmälningshubb är en ogrindad
                            // publik sida med namn, klubb och skytteklass.
                            _contentService.Save(reg);
                            alreadyRegistered.Add(member.Id);
                            regs++;
                        }
                        catch (Exception ex) { failures.Add($"Anmälan {person.First} {person.Last}: {ex.Message}"); }
                    }

                    // ── Serieresultat ────────────────────────────────────────────────
                    // Snittet sjunker per placering i klassen så listan får en ordning som håller
                    // ihop. Summan knuffas därefter nedåt tills den är ledig — ett steg om en
                    // poäng, alltså långt mindre än de ~6 poäng som skiljer två placeringar, så
                    // den avsedda ordningen överlever.
                    var average = cls.TopAverage - i * 0.9;
                    var total = (int)Math.Round(average * plan.Series);
                    while (!usedTotals.Add(total)) total--;

                    try
                    {
                        var allSeries = BuildSeriesForShooter(total, plan.Series, member.Id);
                        for (var s = 1; s <= plan.Series; s++)
                        {
                            db.Execute(mergeSql, comp.Id, member.Id, canonical, s,
                                Newtonsoft.Json.JsonConvert.SerializeObject(allSeries[s - 1]),
                                team, pos, 0, now);
                            rows++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"Resultat {person.First} {person.Last} ({canonical}): {ex.Message}");
                    }
                }

                // ⚠️ För få skyttar i en mästerskapsklass REDUCERAR medaljerna tyst (SHB C.3.4.1),
                // så en klass som inte gick att bemanna syns bara som att listan blev kortare än
                // planerad. Larma i stället.
                if (placed < cls.Shooters)
                    failures.Add($"{plan.Name}: bara {placed} av {cls.Shooters} skyttar kunde "
                               + $"placeras i {canonical} — medaljantalet blir lägre än planerat.");
            }

            return (regs, rows, MedalPreview(plan));
        }

        /// <summary>
        /// Skyttens alla serier, byggda så att SLUTSUMMAN blir exakt <paramref name="totalTarget"/>.
        ///
        /// <para><b>⚠️ SUMMAN MÅSTE VARA UNIK INOM TÄVLINGEN — annars blir medaljer OAVGJORDA.</b>
        /// Två skyttar med samma slutsumma och samma antal innertior kan inte skiljas åt utan
        /// särskjutning, och medaljen står då utan mottagare i beställningslistan. Det gick inte
        /// att slumpa bort: första försöket lämnade två oavgjorda platser i KM Duell C, och ett
        /// försök att sprida variationen mer flyttade bara problemet till KM Precision B (tre
        /// platser). Därför tilldelas summan i stället uppifrån av anroparen, som håller en
        /// mängd över redan använda summor. Slumpen väljer formen, inte utfallet.</para>
        ///
        /// <para>⚠️ Skotten är STRÄNGAR och "X" är en innertia som räknas som tio. Kolumnen rymmer
        /// 50 tecken, alltså får en serie aldrig serialiseras bredare än
        /// <c>["X","10","9","9","8"]</c>.</para>
        /// </summary>
        private static string[][] BuildSeriesForShooter(int totalTarget, int seriesCount, int memberId)
        {
            // Jämn fördelning över serierna, plus en variation som summerar till NOLL så att
            // slutsumman inte glider. En spikrak serieföljd ser tillverkad ut; en som inte summerar
            // rätt gör hela poängen med unika summor om intet.
            var per = new int[seriesCount];
            var baseScore = totalTarget / seriesCount;
            var remainder = totalTarget % seriesCount;

            var wobble = new int[seriesCount];
            var wobbleSum = 0;
            for (var i = 0; i < seriesCount; i++)
            {
                wobble[i] = ((memberId * 3 + i * 7) % 5) - 2;
                wobbleSum += wobble[i];
            }
            wobble[seriesCount - 1] -= wobbleSum;

            for (var i = 0; i < seriesCount; i++)
                per[i] = Math.Clamp(baseScore + (i < remainder ? 1 : 0) + wobble[i], 20, 50);

            // Klampningen kan ha ätit eller lagt till poäng — lägg tillbaka differensen på de
            // serier som har utrymme, annars stämmer inte den unika summan längre.
            var drift = totalTarget - per.Sum();
            for (var pass = 0; pass < 3 && drift != 0; pass++)
            {
                for (var i = 0; i < seriesCount && drift != 0; i++)
                {
                    var step = Math.Sign(drift);
                    var next = per[i] + step;
                    if (next < 20 || next > 50) continue;
                    per[i] = next;
                    drift -= step;
                }
            }

            var all = new string[seriesCount][];
            for (var i = 0; i < seriesCount; i++)
                all[i] = BuildShots(per[i], memberId, i + 1);
            return all;
        }

        /// <summary>Fem skott som summerar exakt till <paramref name="seriesTotal"/>.</summary>
        private static string[] BuildShots(int seriesTotal, int memberId, int series)
        {
            var shots = new string[5];
            var left = Math.Clamp(seriesTotal, 0, 50);

            for (var i = 0; i < 5; i++)
            {
                var remaining = 4 - i;
                // Aldrig mer än vad resten kan bära, och aldrig mindre än vad den måste bära.
                var max = Math.Clamp(left, 0, 10);
                var min = Math.Clamp(left - remaining * 10, 0, 10);
                var value = Math.Clamp((int)Math.Round((max + min) / 2.0), min, max);
                if (i == 4) value = Math.Clamp(left, 0, 10);

                // En tia skrivs ibland som innertia. Den avgör inget här (summorna är unika), men
                // en resultatlista helt utan X ser fel ut för en skytt.
                shots[i] = value == 10 && (memberId + series + i) % 3 == 0 ? "X" : value.ToString();
                left -= value;
            }

            return shots;
        }

        // ── Märken ───────────────────────────────────────────────────────────────────

        /// <summary>Ett märke att dela ut i år: familj, valör och vem som tog det.</summary>
        private sealed record MarkeSeed(string Family, string Level, string First, string Last,
                                        string? UniqueNumber = null);

        /// <summary>
        /// En medlems guldfodringshistorik: hur många år i rad t.o.m. i år som är uppfyllda.
        /// <para>⚠️ ANTALET ÅR AVGÖR OM DET BLIR ETT MÄRKE ATT BESTÄLLA. Ett årtalsmärke delas ut
        /// var tredje uppfyllt år (<c>Marken.YearsPerArtalsmarkeStep</c>), så tre år ger ett nytt
        /// steg medan fyra år inte gör det — den senare står i utdelningslistan som "inget märke".
        /// Båda utfallen finns med i planen med flit: de ser lika ut på skärmen i allt utom den
        /// detaljen, och det är just den skillnaden ytan finns för att visa.</para>
        /// </summary>
        private sealed record FodringSeed(string First, string Last, string Family, int Years);

        private static readonly MarkeSeed[] MarkenPlan =
        {
            // Pistolskyttemärket — grundvalörerna. Ett guld MED nummer och ett UTAN: numret
            // graveras av förbundet och fylls i när märket kommit, så båda lägena är normala.
            new(Marken.FamilyPistolskytte, Marken.LevelBrons,  "Nils-Erik", "Byström"),
            new(Marken.FamilyPistolskytte, Marken.LevelBrons,  "Solveig",   "Enander"),
            new(Marken.FamilyPistolskytte, Marken.LevelSilver, "Lena",      "Hallberg"),
            new(Marken.FamilyPistolskytte, Marken.LevelSilver, "Ove",       "Tranberg"),
            new(Marken.FamilyPistolskytte, Marken.LevelGuld,   "Elin",      "Hagberg", "24-1187"),
            new(Marken.FamilyPistolskytte, Marken.LevelGuld,   "Kenneth",   "Blomgren"),

            new(MarkenFamilies.Precision, Marken.LevelBrons,  "Karin",     "Fridell"),
            new(MarkenFamilies.Precision, Marken.LevelSilver, "Yvonne",    "Sjöstrand"),
            new(MarkenFamilies.Precision, Marken.LevelGuld,   "Gunnar",    "Falkenmark"),

            new(MarkenFamilies.Milsnabb, Marken.LevelBrons,  "Håkan",     "Melander"),
            new(MarkenFamilies.Milsnabb, Marken.LevelSilver, "Margareta", "Wiklund"),

            new(MarkenFamilies.Falt, Marken.LevelBrons,  "Bo",        "Kjellberg"),
            new(MarkenFamilies.Falt, Marken.LevelSilver, "Torbjörn",  "Rydell"),
            new(MarkenFamilies.Falt, Marken.LevelGuld,   "Britt-Marie", "Ekvall"),

            new(MarkenFamilies.Elit, Marken.LevelBrons,  "Sigrid",    "Almkvist"),
        };

        private static readonly FodringSeed[] FodringPlan =
        {
            // 3 och 6 år korsar ett steg i år → ett årtalsmärke att beställa.
            new("Gunnar",      "Falkenmark", Marken.FamilyPistolskytte, 6),
            new("Sigrid",      "Almkvist",   Marken.FamilyPistolskytte, 3),
            new("Britt-Marie", "Ekvall",     Marken.FamilyPistolskytte, 3),
            // 4 och 5 år korsar inget steg → "inget märke", men läses ändå upp på årsmötet.
            new("Elin",        "Hagberg",    Marken.FamilyPistolskytte, 4),
            new("Hasse",       "Lindwall",   Marken.FamilyPistolskytte, 5),
        };

        /// <summary>
        /// Ger Ankeborg en märkesskörd för innevarande år: grundvalörer i fem familjer plus
        /// guldfodringar med olika lång historik.
        ///
        /// <para>⚠️ Allt läggs som <c>Verified</c> och <c>Source = Admin</c>. Det är avsiktligt:
        /// en egenrapporterad, ogranskad post FLAGGAS i beställningslistan ("ej granskad"), och
        /// demodatat ska visa den normala bilden — inte en lista full av varningar. Vill du se
        /// varningen, ändra status på en enskild post.</para>
        ///
        /// <para>Idempotent: ett märke som redan finns för (medlem, familj, valör) hoppas över,
        /// och guldfodringarna går genom <c>UpsertQualificationAsync</c>, som nycklar på
        /// (medlem, familj, år).</para>
        /// </summary>
        public async Task<IActionResult> SeedMarken(string confirm = "", bool dryRun = true)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var club = FindClub();
            if (club == null)
                return Json(new { success = false, message = "Kör Seed först — Ankeborg finns inte." });

            var year = DateTime.Now.Year;

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    year,
                    marken = MarkenPlan.Select(m => new
                    {
                        familj = Marken.FamilyDisplayName(m.Family) is { Length: > 0 } d && d != m.Family
                            ? d : MarkenFamilies.Get(m.Family)?.DisplayName ?? m.Family,
                        m.Level,
                        skytt = $"{m.First} {m.Last}",
                        guldnr = m.UniqueNumber ?? "(fylls i när märket kommit)"
                    }),
                    guldfodringar = FodringPlan.Select(f => new
                    {
                        skytt = $"{f.First} {f.Last}",
                        uppfylldaAr = f.Years,
                        arsmarkeIAr = Marken.ArtalsmarkeStepIndex(f.Years) > Marken.ArtalsmarkeStepIndex(f.Years - 1)
                            ? Marken.ArtalsmarkeName(f.Years) : "(inget nytt steg)"
                    })
                });
            }

            var failures = new List<string>();
            int badges = 0, quals = 0, skipped = 0;
            var signer = FindMember("Gunnar", "Falkenmark")?.Id ?? 0;

            foreach (var m in MarkenPlan)
            {
                try
                {
                    var member = FindMember(m.First, m.Last);
                    if (member == null) { failures.Add($"Märke: hittade inte {m.First} {m.Last}."); continue; }

                    var existing = await _markenLedger.GetBadgesForMemberAsync(member.Id, m.Family);
                    if (existing.Any(b => string.Equals(b.Level, m.Level, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped++;
                        continue;
                    }

                    await _markenLedger.InsertBadgeAsync(new MemberBadge
                    {
                        MemberId = member.Id,
                        BadgeFamily = m.Family,
                        Level = m.Level,
                        LevelOrdinal = Marken.LevelOrdinal(m.Level),
                        AchievedYear = year,
                        AchievedDate = new DateTime(year, 5, 18),
                        SignedOffByMemberId = signer,
                        SignedOffDate = new DateTime(year, 5, 20),
                        UniqueNumber = m.UniqueNumber,
                        Source = Marken.SourceAdmin,
                        Status = Marken.StatusVerified,
                        Notes = SeedTag,
                        EnteredByMemberId = signer
                    });
                    badges++;
                }
                catch (Exception ex) { failures.Add($"Märke {m.First} {m.Last} ({m.Level}): {ex.Message}"); }
            }

            foreach (var f in FodringPlan)
            {
                try
                {
                    var member = FindMember(f.First, f.Last);
                    if (member == null) { failures.Add($"Guldfodring: hittade inte {f.First} {f.Last}."); continue; }

                    // Åren läggs bakåt från i år, så att räkningen t.o.m. i år blir exakt f.Years
                    // och räkningen t.o.m. förra året blir f.Years − 1. Det är den JÄMFÖRELSEN
                    // som avgör om ett årtalsmärke erövrades i år.
                    for (var i = 0; i < f.Years; i++)
                    {
                        var y = year - i;
                        await _markenLedger.UpsertQualificationAsync(new MemberBadgeQualification
                        {
                            MemberId = member.Id,
                            BadgeFamily = f.Family,
                            Year = y,
                            Part1Met = true,
                            Part1Source = Marken.PartSourceTrainingScore,
                            Part1Date = new DateTime(y, 4, 12),
                            Part2Met = true,
                            Part2Source = Marken.PartSourceCompetition,
                            Part2Date = new DateTime(y, 6, 8),
                            SignedOffByMemberId = signer,
                            SignedOffDate = new DateTime(y, 6, 10),
                            Status = Marken.StatusVerified,
                            Notes = SeedTag,
                            EnteredByMemberId = signer
                        });
                        quals++;
                    }
                }
                catch (Exception ex) { failures.Add($"Guldfodring {f.First} {f.Last}: {ex.Message}"); }
            }

            return Json(new { success = failures.Count == 0, year, badges, skipped, quals, failures });
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
