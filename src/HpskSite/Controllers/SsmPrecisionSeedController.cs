using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
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

namespace HpskSite.Controllers
{
    /// <summary>
    /// SLÄNG-SEEDER för devtest med riktig tävlingsdata: läser
    /// <c>SSMData/SSM_Precision_2026_grundomgang_startlista.csv</c> (152 rader, 94 skyttar),
    /// skapar tävlingen "SSM 2026" (Precision, 7 kvalserier + 3 finalserier,
    /// Landsdelsmästerskap, standardmedaljsgrundande), skapar de skyttar som saknas som
    /// hpskMember med e-post {förnamn}.{efternamn}@invalid.invalid, och anmäler alla.
    ///
    /// ⚠️ DEV ONLY. Vägrar köra om inte ASPNETCORE_ENVIRONMENT=Development OCH
    /// anslutningssträngen pekar på en lokal server. Se
    /// memory/dotnet-run-no-launch-profile-hits-prod-db.
    ///
    /// ⚠️ Anmälningarna och anmälningshubben Save():as men publiceras ALDRIG — exakt som
    /// den publika vägen (CompetitionController.RegisterForCompetition). En publicerad hubb
    /// är en ogrindad publik sida med namn/klubb/klass; se
    /// memory/competition-registrations-unpublished.
    ///
    /// Anrop (dev, ingen inloggning krävs):
    ///   /umbraco/surface/SsmPrecisionSeed/Seed?confirm=SSM2026&amp;dryRun=true
    ///   /umbraco/surface/SsmPrecisionSeed/Seed?confirm=SSM2026&amp;dryRun=false
    ///   /umbraco/surface/SsmPrecisionSeed/Status?confirm=SSM2026
    ///
    /// Filen är avsedd att raderas när testdatan är på plats.
    /// </summary>
    public class SsmPrecisionSeedController : SurfaceController
    {
        private const string Confirm = "SSM2026";
        private const string CompetitionNodeName = "SSM 2026";
        private const string SeedTag = "SSM-testdata (seed)";

        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly CompetitionTeamService _teamService;
        private readonly PaymentService _paymentService;
        private readonly ParticipantStatusService _participantStatusService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IConfiguration _configuration;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;
        private readonly ILogger<SsmPrecisionSeedController> _logger;

        public SsmPrecisionSeedController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IContentService contentService,
            ClubService clubService,
            ClubMembershipService clubMembershipService,
            CompetitionTeamService teamService,
            PaymentService paymentService,
            ParticipantStatusService participantStatusService,
            IConfiguration configuration,
            Microsoft.AspNetCore.Hosting.IWebHostEnvironment env,
            ILogger<SsmPrecisionSeedController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _clubService = clubService;
            _clubMembershipService = clubMembershipService;
            _teamService = teamService;
            _paymentService = paymentService;
            _participantStatusService = participantStatusService;
            _databaseFactory = databaseFactory;
            _configuration = configuration;
            _env = env;
            _logger = logger;
        }

        // ── CSV-klass → ShootingClasses.Id ───────────────────────────────────────────
        // D1-D3 = damklasserna i vapengrupp C, VY/VÄ = Veteran Yngre/Äldre, JC = Junior C.
        // Id-formen (inte Name-formen) är det som lagras i registreringens shootingClasses —
        // verifierat mot befintliga noder i dev. Se memory/shooting-class-id-vs-name-canonical.
        private static readonly Dictionary<string, string> ClassMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = "C1", ["C2"] = "C2", ["C3"] = "C3",
            ["B1"] = "B1", ["B2"] = "B2", ["B3"] = "B3",
            ["A1"] = "A1", ["A2"] = "A2", ["A3"] = "A3",
            ["D1"] = "C1_Dam", ["D2"] = "C2_Dam", ["D3"] = "C3_Dam",
            ["VY"] = "C_Vet_Y", ["VÄ"] = "C_Vet_A", ["JC"] = "C_Jun"
        };

        /// <summary>
        /// Klubbnamn i CSV:n som inte finns ordagrant i dev-trädet. Uppslaget sker mot
        /// ClubInfo.Name, som är klubbnodens <c>clubName</c>-EGENSKAP och inte nodnamnet —
        /// därför behöver Varberg (nodnamn "Varberg", clubName "Varbergs Pistolklubb") inget
        /// alias, medan dev-omdöpta HPSK och Ronneby/F17 gör det. Ingen klubb skapas av
        /// seedern — en omatchad klubb rapporteras och körningen avbryts.
        /// </summary>
        private static readonly Dictionary<string, string> ClubAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Harplinge Pistolklubb"] = "Haaplinge GoAss",
            ["Ronneby / F17 Pistolskytteklubb"] = "Ronneby F17 Pistolskytteklubb"
        };

        // Värdena precisionShooterClass faktiskt lagras med (verifierat i dev-databasen).
        private static readonly Dictionary<char, string> ShooterClassByLevel = new()
        {
            ['1'] = "Klass 1 - Nybörjare",
            ['2'] = "Klass 2 - Guldmärkesskytt",
            ['3'] = "Klass 3 - Riksmästare"
        };

        private sealed class Shooter
        {
            public string Name = "";
            public string ClubName = "";
            public List<string> Classes = new();
        }

        [HttpGet]
        public IActionResult Status(string confirm = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var comp = FindCompetition();
            if (comp == null) return Json(new { success = true, competition = (object?)null, message = "Tävlingen finns inte." });

            var hub = _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            var regs = hub == null
                ? new List<IContent>()
                : _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration").ToList();

            return Json(new
            {
                success = true,
                competitionId = comp.Id,
                comp.Name,
                competitionType = comp.GetValue<string>("competitionType"),
                competitionScope = comp.GetValue<string>("competitionScope"),
                numberOfSeriesOrStations = comp.GetValue<int>("numberOfSeriesOrStations"),
                numberOfFinalSeries = comp.GetValue<int>("numberOfFinalSeries"),
                isAwardingStandardMedals = comp.GetValue<bool>("isAwardingStandardMedals"),
                shootingClassIds = comp.GetValue<string>("shootingClassIds"),
                registrations = regs.Count,
                classEntries = regs.Sum(r => CompetitionRegistrationDocument
                    .DeserializeShootingClasses(r.GetValue<string>("shootingClasses") ?? "").Count)
            });
        }

        [HttpGet]
        public IActionResult Seed(string confirm = "", bool dryRun = true, string csvPath = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            // ── CSV ──────────────────────────────────────────────────────────────────
            var path = ResolveCsvPath(csvPath);
            if (path == null)
                return Json(new { success = false, message = "Hittade inte CSV-filen. Ange ?csvPath=<absolut sökväg>." });

            List<Shooter> shooters;
            int csvRows;
            try
            {
                shooters = ParseCsv(path, out csvRows, out var badClasses);
                if (badClasses.Count > 0)
                    return Json(new { success = false, message = "Okända klasser i CSV:n: " + string.Join(", ", badClasses) });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte läsa CSV:n: " + ex.Message });
            }

            // ── Klubbar ──────────────────────────────────────────────────────────────
            var allClubs = _clubService.GetAllClubs();
            var clubByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in allClubs) clubByName.TryAdd(c.Name.Trim(), c.Id);

            var clubResolution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unresolvedClubs = new List<string>();
            foreach (var name in shooters.Select(s => s.ClubName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var lookup = ClubAliases.TryGetValue(name, out var alias) ? alias : name;
                if (clubByName.TryGetValue(lookup, out var id)) clubResolution[name] = id;
                else unresolvedClubs.Add(name);
            }
            if (unresolvedClubs.Count > 0)
                return Json(new { success = false, message = "Klubbar saknas i dev-trädet: " + string.Join(" | ", unresolvedClubs) });

            var allClassIds = shooters.SelectMany(s => s.Classes).Distinct().ToList();

            // ── Torrkörning ──────────────────────────────────────────────────────────
            var existingByName = BuildMemberNameIndex();
            var wouldCreate = shooters.Where(s => !existingByName.ContainsKey(s.Name.ToLowerInvariant())).ToList();

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    csvPath = path,
                    csvRows,
                    shooters = shooters.Count,
                    membersExisting = shooters.Count - wouldCreate.Count,
                    membersWouldBeCreated = wouldCreate.Count,
                    competitionExists = FindCompetition() != null,
                    classes = allClassIds.OrderBy(x => x).ToList(),
                    clubs = clubResolution.OrderBy(k => k.Key).Select(k => $"{k.Key} = {k.Value}").ToList(),
                    reusedMembers = shooters.Where(s => existingByName.ContainsKey(s.Name.ToLowerInvariant()))
                        .Select(s => $"{s.Name} -> #{existingByName[s.Name.ToLowerInvariant()]}").ToList(),
                    sampleEmails = shooters.Take(5).Select(s => $"{s.Name} => {BuildEmail(s.Name)}").ToList()
                });
            }

            // ── Tävling ──────────────────────────────────────────────────────────────
            var competition = FindCompetition();
            bool competitionCreated = false;
            var competitionPatched = new List<string>();
            var competitionWarnings = new List<string>();

            if (competition != null)
            {
                // Noden finns redan i dev (id 2205, "SSM 2026" / competitionName
                // "Syd Svenska Mästerskapen"). Stefans val: behåll namnet, sätt bara det som
                // saknas av det tävlingen ska vara. Serieantal och klasslista rörs inte om de
                // redan stämmer — de rapporteras i stället om de INTE gör det.
                var scope = competition.GetValue<string>("competitionScope") ?? "";
                if (!string.Equals(scope, CompetitionTypes.Common.Utilities.CompetitionScopeHelper.Landsdelsmasterskap, StringComparison.Ordinal))
                {
                    competition.SetValue("competitionScope", CompetitionTypes.Common.Utilities.CompetitionScopeHelper.Landsdelsmasterskap);
                    competitionPatched.Add($"competitionScope: '{scope}' -> Landsdelsmästerskap");
                }
                if (!competition.GetValue<bool>("isAwardingStandardMedals"))
                {
                    competition.SetValue("isAwardingStandardMedals", true);
                    competitionPatched.Add("isAwardingStandardMedals -> true");
                }

                var series = competition.GetValue<int>("numberOfSeriesOrStations");
                var finals = competition.GetValue<int>("numberOfFinalSeries");
                if (series - finals != 7 || finals != 3)
                    competitionWarnings.Add($"Serieuppsättningen är {series} totalt / {finals} final ({series - finals} kval) — förväntat 10/3.");

                var onComp = ParseClassIds(competition.GetValue<string>("shootingClassIds"));
                var notOnComp = allClassIds.Where(c => !onComp.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (notOnComp.Count > 0)
                    competitionWarnings.Add("Klasser i CSV:n som tävlingen inte erbjuder: " + string.Join(", ", notOnComp));

                if (competitionPatched.Count > 0)
                {
                    _contentService.Save(competition);
                    var rePublish = _contentService.Publish(competition, new[] { "*" });
                    if (!rePublish.Success)
                        competitionWarnings.Add("Tävlingen sparades men kunde inte publiceras om.");
                }
            }
            else
            {
                var parentId = ResolveCompetitionParentId(2026, out var parentError);
                if (parentId == null) return Json(new { success = false, message = parentError });

                competition = _contentService.Create(CompetitionNodeName, parentId.Value, "competition");
                competition.SetValue("competitionName", CompetitionNodeName);
                competition.SetValue("competitionType", "Precision");
                competition.SetValue("description",
                    "<p>Landsdelsmästerskap i precision (SSM). Testdata från grundomgångens startlista.</p>");
                competition.SetValue("venue", "Vetlanda Pistolklubbs bana");
                competition.SetValue("competitionScope", CompetitionTypes.Common.Utilities.CompetitionScopeHelper.Landsdelsmasterskap);
                competition.SetValue("regionalFederation", "Jonkoping");
                competition.SetValue("competitionDate", new DateTime(2026, 9, 19, 9, 0, 0));
                competition.SetValue("registrationOpenDate", new DateTime(2026, 6, 1, 0, 0, 0));
                competition.SetValue("registrationCloseDate", new DateTime(2026, 9, 17, 23, 59, 0));
                competition.SetValue("maxParticipants", 250);
                competition.SetValue("registrationFee", 150m);
                competition.SetValue("juniorRegistrationFee", 0m);
                // 7 kvalserier + 3 finalserier. Koden räknar kval som
                // numberOfSeriesOrStations - numberOfFinalSeries, så totalen ska vara 10.
                competition.SetValue("numberOfSeriesOrStations", 10);
                competition.SetValue("numberOfFinalSeries", 3);
                competition.SetValue("isAwardingStandardMedals", true);
                competition.SetValue("showLiveResults", true);
                competition.SetValue("isActive", true);
                competition.SetValue("isClubOnly", false);
                competition.SetValue("allowTeams", false);
                competition.SetValue("allowStafett", false);
                competition.SetValue("competitionDirector", "Tävlingsledare SSM");
                competition.SetValue("contactEmail", "ssm2026@invalid.invalid");
                competition.SetValue("shootingClassIds", ShootingClassIdsValue.Normalize(allClassIds.ToArray()));

                var save = _contentService.Save(competition);
                if (!save.Success)
                    return Json(new { success = false, message = "Kunde inte spara tävlingen." });
                var publish = _contentService.Publish(competition, new[] { "*" });
                if (!publish.Success)
                    return Json(new
                    {
                        success = false,
                        message = "Tävlingen sparades men kunde inte publiceras: "
                                + string.Join(", ", publish.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>())
                    });
                competitionCreated = true;
            }

            var competitionId = competition.Id;

            // ── Anmälningshubb (Save, ALDRIG Publish) ────────────────────────────────
            var regHub = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (regHub == null)
            {
                regHub = _contentService.Create("Anmälningar", competitionId, "competitionRegistrationsHub");
                _contentService.Save(regHub);
            }

            var existingRegs = _contentService.GetPagedChildren(regHub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .ToList();
            var regByMemberId = existingRegs
                .GroupBy(r => r.GetValue<int>("memberId"))
                .ToDictionary(g => g.Key, g => g.First());

            // ── Skyttar ──────────────────────────────────────────────────────────────
            int membersCreated = 0, membersReused = 0, regsCreated = 0, regsUpdated = 0, classEntries = 0;
            var clubMembershipsAdded = 0;
            var reusedDetail = new List<string>();
            var failures = new List<string>();

            foreach (var s in shooters)
            {
                try
                {
                    var clubId = clubResolution[s.ClubName];
                    var (first, last) = SplitName(s.Name);
                    var email = BuildEmail(s.Name);

                    IMember? member = null;
                    if (existingByName.TryGetValue(s.Name.ToLowerInvariant(), out var existingId))
                        member = _memberService.GetById(existingId);
                    member ??= _memberService.GetByEmail(email);

                    if (member == null)
                    {
                        member = _memberService.CreateMember(email, email, s.Name, "hpskMember");
                        member.SetValue("firstName", first);
                        member.SetValue("lastName", last);
                        member.SetValue("primaryClubId", clubId);
                        var level = LevelOf(s.Classes);
                        if (level.HasValue) member.SetValue("precisionShooterClass", ShooterClassByLevel[level.Value]);
                        member.IsApproved = true;
                        _memberService.Save(member);
                        _memberService.AssignRoles(new[] { member.Id }, new[] { "Users" });
                        membersCreated++;
                    }
                    else
                    {
                        // Återanvänd befintlig medlem (ingen dubblett). Om CSV-klubben inte är
                        // någon av medlemmens klubbar läggs den till som extra klubb, annars
                        // skulle "Tävlar för" peka på en klubb personen inte tillhör.
                        membersReused++;
                        reusedDetail.Add($"{s.Name} -> #{member.Id}");
                        var primary = ReadInt(member, "primaryClubId");
                        var extra = (member.GetValue<string>("memberClubIDs") ?? "")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => int.TryParse(x, out var v) ? v : 0)
                            .Where(v => v > 0).ToList();
                        if (primary != clubId && !extra.Contains(clubId))
                        {
                            extra.Add(clubId);
                            member.SetValue("memberClubIDs", string.Join(",", extra));
                            _memberService.Save(member);
                        }
                    }

                    if (_clubMembershipService.Get(member.Id, clubId) == null)
                    {
                        _clubMembershipService.Save(new ClubMembership
                        {
                            MemberId = member.Id,
                            ClubId = clubId,
                            MembershipStatus = "Aktiv",
                            MemberNotes = SeedTag
                        });
                        clubMembershipsAdded++;
                    }

                    var entries = s.Classes
                        .Select(c => new ShootingClassEntry { Class = c, StartPreference = "Inget", TeamNumber = null })
                        .ToList();
                    var json = CompetitionRegistrationDocument.SerializeShootingClasses(entries);

                    if (regByMemberId.TryGetValue(member.Id, out var reg))
                    {
                        reg.SetValue("shootingClasses", json);
                        reg.SetValue("clubId", clubId);
                        reg.SetValue("isActive", true);
                        _contentService.Save(reg);
                        regsUpdated++;
                    }
                    else
                    {
                        reg = _contentService.Create($"{s.Name} - 2026-09-19", regHub.Id, "competitionRegistration");
                        reg.SetValue("competitionId", competitionId);
                        reg.SetValue("memberId", member.Id);
                        reg.SetValue("memberName", s.Name);
                        reg.SetValue("clubId", clubId);
                        reg.SetValue("shootingClasses", json);
                        reg.SetValue("registrationDate", new DateTime(2026, 6, 15, 12, 0, 0));
                        reg.SetValue("registeredBy", SeedTag);
                        reg.SetValue("isActive", true);
                        if (reg.HasProperty("isSubCompetition")) reg.SetValue("isSubCompetition", false);
                        // Save utan Publish — så gör den publika anmälningsvägen också.
                        _contentService.Save(reg);
                        regByMemberId[member.Id] = reg;
                        regsCreated++;
                    }

                    classEntries += entries.Count;
                }
                catch (Exception ex)
                {
                    failures.Add($"{s.Name}: {ex.Message}");
                    _logger.LogError(ex, "SSM-seed misslyckades för {Shooter}", s.Name);
                }
            }

            return Json(new
            {
                success = failures.Count == 0,
                dryRun = false,
                csvPath = path,
                csvRows,
                competitionId,
                competitionCreated,
                competitionPatched,
                competitionWarnings,
                competitionNodeName = competition.Name,
                competitionName = competition.GetValue<string>("competitionName"),
                shooters = shooters.Count,
                membersCreated,
                membersReused,
                reusedDetail,
                clubMembershipsAdded,
                registrationsCreated = regsCreated,
                registrationsUpdated = regsUpdated,
                classEntries,
                classes = allClassIds.OrderBy(x => x).ToList(),
                failures,
                note = "Anmälningarna är sparade men INTE publicerade — samma sak som den publika "
                     + "anmälningsvägen gör. Räkna dem via IContentService eller SQL, inte via den "
                     + "publicerade cachen."
            });
        }

        // ── Resultat ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fördelar skyttens TOTALA X-antal över de sju serierna. CSV:n ger X per SKYTT, inte
        /// per serie, så fördelningen måste hittas på — men inte hur som helst: en serie kan
        /// rymma högst <c>floor(S/10)</c> tior, och X är en tia.
        ///
        /// Round-robin över serierna sorterade efter resultat (bäst först), ett X i taget. En
        /// 50-serie får därför sitt X före en 43-serie, utan att alla X klumpas i samma serie
        /// — vilket "ta de bästa serierna först" hade gjort.
        /// </summary>
        private static int[] DistributeXOverSeries(int[] series, int totalX)
        {
            var used = new int[series.Length];
            var order = Enumerable.Range(0, series.Length)
                .OrderByDescending(i => series[i]).ThenBy(i => i).ToArray();

            var left = totalX;
            while (left > 0)
            {
                var placed = 0;
                foreach (var i in order)
                {
                    if (left == 0) break;
                    var cap = Math.Min(5, series[i] / 10);
                    if (used[i] < cap) { used[i]++; left--; placed++; }
                }
                if (placed == 0)
                    throw new InvalidOperationException(
                        $"{left} X kunde inte placeras i serierna [{string.Join(",", series)}]");
            }
            return used;
        }

        /// <summary>
        /// Fem skott som summerar till <paramref name="total"/>, varav
        /// <paramref name="xCount"/> är X (10 p).
        ///
        /// CSV:n har bara serietotaler, så skotten måste antas. Regeln är Stefans: förutsätt
        /// högre poäng framför lägre. Det översätts till att fördela de återstående poängen så
        /// JÄMNT som möjligt, vilket maximerar det lägsta skottet — 45 med ett X blir
        /// X,9,9,9,8 och inte X,10,10,10,5. Mätt över SSM-datan hamnar spridningen inom en
        /// serie på 0–3 poäng, alltså inga orimliga lågskott.
        /// </summary>
        private static string[] ShotsForSeries(int total, int xCount)
        {
            var restShots = 5 - xCount;
            var restPoints = total - 10 * xCount;

            if (restPoints < 0)
                throw new InvalidOperationException($"{xCount} X ryms inte i serien {total}.");
            if (restShots == 0)
            {
                if (restPoints != 0)
                    throw new InvalidOperationException($"5 X kräver serien 50, fick {total}.");
                return Enumerable.Repeat("X", 5).ToArray();
            }

            var baseVal = restPoints / restShots;
            var rem = restPoints % restShots;
            if (baseVal + (rem > 0 ? 1 : 0) > 10)
                throw new InvalidOperationException($"Serien {total} med {xCount} X kräver skott över 10.");

            var vals = new List<int>();
            for (var i = 0; i < rem; i++) vals.Add(baseVal + 1);
            for (var i = 0; i < restShots - rem; i++) vals.Add(baseVal);
            vals.Sort((a, b) => b.CompareTo(a));

            return Enumerable.Repeat("X", xCount)
                .Concat(vals.Select(v => v.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
        }

        private static int ShotPoints(string shot) =>
            string.Equals(shot, "X", StringComparison.OrdinalIgnoreCase)
                ? 10
                : (int.TryParse(shot, out var v) ? v : 0);

        private sealed class ResultRow
        {
            public string Name = "";
            public string CsvClass = "";
            public string ClassId = "";
            public int[] Series = Array.Empty<int>();
            public int Total;
            public int XCount;
            public bool IsDns;
        }

        /// <summary>
        /// Läser resultaten ur individ-CSV:n och skriver dem som PrecisionResultEntry —
        /// sju serier per (skytt, klass), fem antagna skott per serie.
        ///
        /// ⚠️ <c>ShootingClass</c> skrivs i NAME-formen ("C Vet Y"), inte Id-formen
        /// ("C_Vet_Y"). Det är den kanoniska lagringsformen på en resultatrad och den
        /// resultatinmatningen själv använder — se [[shooting-class-id-vs-name-canonical]].
        /// Anmälningarna lagrar Id-formen; de två får inte blandas.
        ///
        /// Samma MERGE-nyckel som <c>CompetitionResultsController.SaveResultToDatabase</c>
        /// (tävling, medlem, klass, serie), så en omkörning uppdaterar i stället för att
        /// dubblera. Skjutlagen rörs INTE — TeamNumber/Position fylls ur den BEFINTLIGA
        /// startlistan där skytten står i just den klassen, annars 0.
        ///
        /// DNS-rader (7 st) får ingen resultatrad; de sätts som "Ej start" via
        /// ParticipantStatusService, per (skytt, klass).
        ///
        /// Efter skrivningen läses ALLT tillbaka ur databasen och jämförs mot CSV:n —
        /// serietotal för serietotal och X-antal per skytt.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedResults(string confirm = "", bool dryRun = true, string csvPath = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null)
                return Json(new { success = false, message = $"Hittade ingen tävling som heter \"{CompetitionNodeName}\". Kör Seed först." });
            var competitionId = competition.Id;

            var path = ResolveCsvPath(csvPath);
            if (path == null)
                return Json(new { success = false, message = "Hittade inte individ-CSV:n." });

            // ── CSV ──────────────────────────────────────────────────────────────────
            var rows = new List<ResultRow>();
            var parseProblems = new List<string>();
            foreach (var line in System.IO.File.ReadAllLines(path, Encoding.Latin1).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split('\t');
                if (c.Length < 15) continue;

                var csvClass = c[2].Trim();
                var name = c[3].Trim();
                if (name.Length == 0) continue;
                if (!ClassMap.TryGetValue(csvClass, out var classId))
                {
                    parseProblems.Add($"{name}: okänd klass \"{csvClass}\"");
                    continue;
                }

                var summaRaw = c[13].Trim();
                if (string.Equals(summaRaw, "DNS", StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new ResultRow { Name = name, CsvClass = csvClass, ClassId = classId, IsDns = true });
                    continue;
                }

                var series = new int[7];
                var ok = true;
                for (var i = 0; i < 7; i++)
                {
                    if (!int.TryParse(c[6 + i].Trim(), out series[i])) { ok = false; break; }
                }
                if (!ok)
                {
                    parseProblems.Add($"{name} ({csvClass}): serie går inte att tolka men Summa = {summaRaw}");
                    continue;
                }

                var total = int.TryParse(summaRaw, out var t) ? t : -1;
                var xCount = int.TryParse(c[14].Trim(), out var x) ? x : 0;

                if (total < 0) { parseProblems.Add($"{name} ({csvClass}): Summa \"{summaRaw}\""); continue; }
                if (series.Sum() != total)
                    parseProblems.Add($"{name} ({csvClass}): serierna ger {series.Sum()}, Summa säger {total}");

                rows.Add(new ResultRow
                {
                    Name = name, CsvClass = csvClass, ClassId = classId,
                    Series = series, Total = total, XCount = xCount
                });
            }

            if (parseProblems.Count > 0)
                return Json(new { success = false, message = "CSV:n går inte att tolka.", parseProblems });

            // ── Skyttar och deras plats i den BEFINTLIGA startlistan ─────────────────
            var hub = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            var memberIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (hub != null)
            {
                foreach (var reg in _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                             .Where(c => c.ContentType.Alias == "competitionRegistration"))
                {
                    var nm = (reg.GetValue<string>("memberName") ?? "").Trim();
                    if (nm.Length > 0) memberIdByName[nm] = reg.GetValue<int>("memberId");
                }
            }

            // (medlem|kanonisk klass) -> (skjutlag, plats) ur startlistan. Läses BARA.
            var placement = new Dictionary<string, (int Team, int Pos)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sl in _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                         .Where(c => c.ContentType.Alias == "precisionStartList"))
            {
                try
                {
                    var cfg = JsonConvert.DeserializeObject<CompetitionTypes.Precision.Models.StartListConfiguration>(
                        sl.GetValue<string>("configurationData") ?? "");
                    foreach (var team in cfg?.Teams ?? new List<CompetitionTypes.Precision.Models.StartListTeam>())
                        foreach (var sh in team.Shooters ?? new List<CompetitionTypes.Precision.Models.StartListShooter>())
                        {
                            var key = $"{sh.MemberId}|{ShootingClasses.NormalizeKey(sh.WeaponClass)}";
                            placement[key] = (team.TeamNumber, sh.Position);
                        }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kunde inte läsa startlistan {Id} för skjutlagsplacering", sl.Id);
                }
            }

            // ── Planera skotten (och låt nedbrytningen falla FÖRE någon skrivning) ───
            var planned = new List<(ResultRow Row, int MemberId, string CanonicalClass, string[][] Shots, int Team, int Pos)>();
            var blocked = new List<string>();
            var dnsRows = new List<ResultRow>();

            foreach (var row in rows)
            {
                if (!memberIdByName.TryGetValue(row.Name, out var memberId))
                {
                    blocked.Add($"{row.Name} ({row.CsvClass}): är inte anmäld i tävlingen");
                    continue;
                }

                var canonical = ShootingClasses.ToCanonicalName(row.ClassId);

                if (row.IsDns) { dnsRows.Add(row); continue; }

                try
                {
                    var xs = DistributeXOverSeries(row.Series, row.XCount);
                    var shots = new string[7][];
                    for (var i = 0; i < 7; i++)
                    {
                        shots[i] = ShotsForSeries(row.Series[i], xs[i]);
                        var sum = shots[i].Sum(ShotPoints);
                        if (sum != row.Series[i])
                            throw new InvalidOperationException(
                                $"S{i + 1}: skotten ger {sum}, CSV säger {row.Series[i]}");
                    }
                    var xTotal = shots.Sum(s => s.Count(v => v == "X"));
                    if (xTotal != row.XCount)
                        throw new InvalidOperationException($"{xTotal} X placerade, CSV säger {row.XCount}");

                    placement.TryGetValue($"{memberId}|{ShootingClasses.NormalizeKey(canonical)}", out var pl);
                    planned.Add((row, memberId, canonical, shots, pl.Team, pl.Pos));
                }
                catch (Exception ex)
                {
                    blocked.Add($"{row.Name} ({row.CsvClass}): {ex.Message}");
                }
            }

            if (dryRun)
            {
                return Json(new
                {
                    success = blocked.Count == 0,
                    dryRun = true,
                    csvPath = path,
                    competitionId,
                    csvRows = rows.Count,
                    withResults = planned.Count,
                    dns = dnsRows.Count,
                    dnsShooters = dnsRows.Select(r => $"{r.Name} ({r.CsvClass})").ToList(),
                    seriesRowsToWrite = planned.Count * 7,
                    placementsFound = planned.Count(p => p.Team > 0),
                    placementsMissing = planned.Where(p => p.Team == 0)
                        .Select(p => $"{p.Row.Name} ({p.CanonicalClass})").ToList(),
                    blocked,
                    sample = planned.Take(3).Select(p => new
                    {
                        p.Row.Name,
                        klass = p.CanonicalClass,
                        skjutlag = p.Team,
                        plats = p.Pos,
                        summa = p.Row.Total,
                        x = p.Row.XCount,
                        serier = p.Shots.Select((s, i) =>
                            $"S{i + 1} = {p.Row.Series[i]}: [{string.Join(",", s)}]").ToList()
                    }).ToList()
                });
            }

            if (blocked.Count > 0)
                return Json(new { success = false, message = "Avbryter — nedbrytningen faller för några rader.", blocked });

            // ── Skriv ────────────────────────────────────────────────────────────────
            using var db = _databaseFactory.CreateDatabase();
            var now = DateTime.Now;
            int written = 0, dnsSet = 0;
            var failures = new List<string>();

            const string mergeSql = @"
                MERGE INTO [PrecisionResultEntry] AS target
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

            foreach (var p in planned)
            {
                try
                {
                    for (var i = 0; i < 7; i++)
                        await db.ExecuteAsync(mergeSql,
                            competitionId, p.MemberId, p.CanonicalClass, i + 1,
                            JsonConvert.SerializeObject(p.Shots[i]),
                            p.Team, p.Pos, 0, now);
                    written += 7;
                }
                catch (Exception ex)
                {
                    failures.Add($"{p.Row.Name} ({p.CanonicalClass}): {ex.Message}");
                    _logger.LogError(ex, "Resultatimport misslyckades för {Shooter}", p.Row.Name);
                }
            }

            foreach (var row in dnsRows)
            {
                if (!memberIdByName.TryGetValue(row.Name, out var memberId)) continue;
                var (ok, msg) = await _participantStatusService.SetStatusAsync(
                    competitionId, memberId, ShootingClasses.ToCanonicalName(row.ClassId),
                    CompetitionParticipantStatus.Dns, fromSeriesNumber: null,
                    note: SeedTag, actingMemberId: 0);
                if (ok) dnsSet++;
                else failures.Add($"{row.Name} ({row.CsvClass}) DNS: {msg}");
            }

            // ── Verifiera mot CSV:n genom att LÄSA TILLBAKA ur databasen ─────────────
            var verified = 0;
            var mismatches = new List<string>();
            foreach (var p in planned)
            {
                var stored = await db.FetchAsync<CompetitionTypes.Precision.Models.PrecisionResultEntry>(
                    "SELECT * FROM PrecisionResultEntry WHERE CompetitionId = @0 AND MemberId = @1 "
                    + "AND ShootingClass = @2 ORDER BY SeriesNumber",
                    competitionId, p.MemberId, p.CanonicalClass);

                if (stored.Count != 7)
                {
                    mismatches.Add($"{p.Row.Name} ({p.CanonicalClass}): {stored.Count} serier i databasen, väntade 7");
                    continue;
                }

                var rowOk = true;
                var totalPoints = 0;
                var totalX = 0;
                for (var i = 0; i < 7; i++)
                {
                    var shots = JsonConvert.DeserializeObject<string[]>(stored[i].Shots) ?? Array.Empty<string>();
                    if (shots.Length != 5)
                    {
                        mismatches.Add($"{p.Row.Name} ({p.CanonicalClass}) S{i + 1}: {shots.Length} skott i databasen");
                        rowOk = false; continue;
                    }
                    var sum = shots.Sum(ShotPoints);
                    if (sum != p.Row.Series[i])
                    {
                        mismatches.Add($"{p.Row.Name} ({p.CanonicalClass}) S{i + 1}: databasen ger {sum}, CSV säger {p.Row.Series[i]}");
                        rowOk = false;
                    }
                    totalPoints += sum;
                    totalX += shots.Count(v => string.Equals(v, "X", StringComparison.OrdinalIgnoreCase));
                }
                if (totalPoints != p.Row.Total)
                {
                    mismatches.Add($"{p.Row.Name} ({p.CanonicalClass}): summa {totalPoints} i databasen, CSV säger {p.Row.Total}");
                    rowOk = false;
                }
                if (totalX != p.Row.XCount)
                {
                    mismatches.Add($"{p.Row.Name} ({p.CanonicalClass}): {totalX} X i databasen, CSV säger {p.Row.XCount}");
                    rowOk = false;
                }
                if (rowOk) verified++;
            }

            return Json(new
            {
                success = failures.Count == 0 && mismatches.Count == 0,
                dryRun = false,
                competitionId,
                seriesRowsWritten = written,
                shootersWithResults = planned.Count,
                dnsSet,
                verifiedAgainstCsv = verified,
                mismatchCount = mismatches.Count,
                mismatches = mismatches.Take(25).ToList(),
                failures,
                note = "Skjutlagen är orörda. TeamNumber/Position på resultatraderna är LÄSTA ur "
                     + "den befintliga startlistan, inte ur CSV:n."
            });
        }

        // ── Finalresultat ────────────────────────────────────────────────────────────

        /// <summary>
        /// Läser finalresultaten ur <c>SSM_Precision_2026_final.csv</c> (semikolon,
        /// Windows-1252) och skriver dem som finalserier på PrecisionResultEntry.
        ///
        /// Serienumren är kvalserierna + 1 och uppåt — hos SSM 2026 blir det 8, 9, 10, vilket
        /// matchar filens M8/M9/M10. Antalet härleds ur tävlingen, aldrig ur filen, och
        /// importen vägrar om de inte går ihop.
        ///
        /// ⚠️ Filens X-kolumn (<c>Final_X</c>) gäller HELA finalen, inte en serie, precis som
        /// i grundomgången — fördelningen antas därför på samma sätt (round-robin över de tre
        /// serierna, bäst först, tak <c>floor(S/10)</c>).
        ///
        /// <paramref name="groups"/> filtrerar på filens Grupp-kolumn. Standard är C-familjen
        /// (C, CVY, CVÄ, Dam, Jun) eftersom A och B tas separat; <c>all</c> tar alla.
        /// TeamNumber/Position läses ur FINALstartlistan — det är skyttens plats i finalen,
        /// inte i grundomgången.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedFinalResults(
            string confirm = "", bool dryRun = true, string groups = "C,CVY,CVÄ,Dam,Jun", string csvPath = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null)
                return Json(new { success = false, message = $"Hittade ingen tävling som heter \"{CompetitionNodeName}\"." });
            var competitionId = competition.Id;

            // Vilka serienummer ÄR finalserierna? Härled ur tävlingen.
            var totalSeries = competition.GetValue<int>("numberOfSeriesOrStations");
            var finalSeries = competition.GetValue<int>("numberOfFinalSeries");
            if (finalSeries != 3)
                return Json(new
                {
                    success = false,
                    message = $"Filen har tre finalserier (M8/M9/M10) men tävlingen är konfigurerad "
                            + $"med {finalSeries}. Rätta numberOfFinalSeries först."
                });
            var firstFinalSeries = totalSeries - finalSeries + 1;   // 8

            var path = ResolveFinalCsvPath(csvPath);
            if (path == null)
                return Json(new { success = false, message = "Hittade inte SSM_Precision_2026_final.csv." });

            var wanted = groups.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null
                : groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ── CSV ──────────────────────────────────────────────────────────────────
            var rows = new List<(string Group, string Name, string ClassId, int[] Series, int Total, int XCount)>();
            var skippedGroups = new List<string>();
            var parseProblems = new List<string>();

            foreach (var line in System.IO.File.ReadAllLines(path, Encoding.Latin1).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split(';');
                if (c.Length < 14) continue;

                var group = c[1].Trim();
                var csvClass = c[3].Trim();
                var name = c[4].Trim();
                if (name.Length == 0) continue;

                if (wanted != null && !wanted.Contains(group))
                {
                    if (!skippedGroups.Contains(group)) skippedGroups.Add(group);
                    continue;
                }
                if (!ClassMap.TryGetValue(csvClass, out var classId))
                {
                    parseProblems.Add($"{name}: okänd klass \"{csvClass}\"");
                    continue;
                }

                var series = new int[3];
                var ok = true;
                for (var i = 0; i < 3; i++)
                    if (!int.TryParse(c[9 + i].Trim(), out series[i])) { ok = false; break; }
                if (!ok) { parseProblems.Add($"{name}: finalserie går inte att tolka"); continue; }

                var total = int.TryParse(c[12].Trim(), out var t) ? t : -1;
                var x = int.TryParse(c[13].Trim(), out var xv) ? xv : 0;
                if (total < 0) { parseProblems.Add($"{name}: Final_summa \"{c[12]}\""); continue; }
                if (series.Sum() != total)
                    parseProblems.Add($"{name}: M8+M9+M10 = {series.Sum()} men Final_summa = {total}");

                rows.Add((group, name, classId, series, total, x));
            }

            if (parseProblems.Count > 0)
                return Json(new { success = false, message = "CSV:n går inte att tolka.", parseProblems });

            // ── Skyttar och deras plats i FINALstartlistan ───────────────────────────
            var hub = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            var memberIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (hub != null)
                foreach (var reg in _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                             .Where(c => c.ContentType.Alias == "competitionRegistration"))
                {
                    var nm = (reg.GetValue<string>("memberName") ?? "").Trim();
                    if (nm.Length > 0) memberIdByName[nm] = reg.GetValue<int>("memberId");
                }

            var placement = new Dictionary<string, (int Team, int Pos)>(StringComparer.OrdinalIgnoreCase);
            foreach (var fsl in _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                         .Where(c => c.ContentType.Alias == "finalsStartList"))
            {
                try
                {
                    var cfg = JsonConvert.DeserializeObject<CompetitionTypes.Precision.Models.StartListConfiguration>(
                        fsl.GetValue<string>("configurationData") ?? "");
                    foreach (var team in cfg?.Teams ?? new List<CompetitionTypes.Precision.Models.StartListTeam>())
                        foreach (var sh in team.Shooters ?? new List<CompetitionTypes.Precision.Models.StartListShooter>())
                            placement[$"{sh.MemberId}|{ShootingClasses.NormalizeKey(sh.WeaponClass)}"] =
                                (team.TeamNumber, sh.Position);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kunde inte läsa finalstartlistan {Id}", fsl.Id);
                }
            }

            // ── Planera ──────────────────────────────────────────────────────────────
            var planned = new List<(string Group, string Name, int MemberId, string Canonical, string[][] Shots, int[] Series, int Total, int XCount, int Team, int Pos)>();
            var blocked = new List<string>();

            foreach (var r in rows)
            {
                if (!memberIdByName.TryGetValue(r.Name, out var memberId))
                {
                    blocked.Add($"{r.Name} ({r.Group}): är inte anmäld i tävlingen");
                    continue;
                }
                var canonical = ShootingClasses.ToCanonicalName(r.ClassId);
                try
                {
                    var xs = DistributeXOverSeries(r.Series, r.XCount);
                    var shots = new string[3][];
                    for (var i = 0; i < 3; i++)
                    {
                        shots[i] = ShotsForSeries(r.Series[i], xs[i]);
                        var sum = shots[i].Sum(ShotPoints);
                        if (sum != r.Series[i])
                            throw new InvalidOperationException($"M{firstFinalSeries + i}: skotten ger {sum}, CSV säger {r.Series[i]}");
                    }
                    var xTotal = shots.Sum(s => s.Count(v => v == "X"));
                    if (xTotal != r.XCount)
                        throw new InvalidOperationException($"{xTotal} X placerade, CSV säger {r.XCount}");

                    placement.TryGetValue($"{memberId}|{ShootingClasses.NormalizeKey(canonical)}", out var pl);
                    planned.Add((r.Group, r.Name, memberId, canonical, shots, r.Series, r.Total, r.XCount, pl.Team, pl.Pos));
                }
                catch (Exception ex)
                {
                    blocked.Add($"{r.Name} ({r.Group}): {ex.Message}");
                }
            }

            if (dryRun)
                return Json(new
                {
                    success = blocked.Count == 0,
                    dryRun = true,
                    csvPath = path,
                    competitionId,
                    finalSeriesNumbers = Enumerable.Range(firstFinalSeries, 3).ToList(),
                    groupsImported = planned.Select(p => p.Group).Distinct().ToList(),
                    groupsSkipped = skippedGroups,
                    finalists = planned.Count,
                    seriesRowsToWrite = planned.Count * 3,
                    placementsFound = planned.Count(p => p.Team > 0),
                    placementsMissing = planned.Where(p => p.Team == 0).Select(p => $"{p.Name} ({p.Canonical})").ToList(),
                    blocked,
                    sample = planned.Take(3).Select(p => new
                    {
                        p.Name, klass = p.Canonical, finalsumma = p.Total, x = p.XCount,
                        serier = p.Shots.Select((s, i) => $"M{firstFinalSeries + i} = {p.Series[i]}: [{string.Join(",", s)}]").ToList()
                    }).ToList()
                });

            if (blocked.Count > 0)
                return Json(new { success = false, message = "Avbryter — nedbrytningen faller för några rader.", blocked });

            // ── Skriv ────────────────────────────────────────────────────────────────
            using var db = _databaseFactory.CreateDatabase();
            var now = DateTime.Now;
            int written = 0;
            var failures = new List<string>();

            const string mergeSql = @"
                MERGE INTO [PrecisionResultEntry] AS target
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

            foreach (var p in planned)
            {
                try
                {
                    for (var i = 0; i < 3; i++)
                        await db.ExecuteAsync(mergeSql,
                            competitionId, p.MemberId, p.Canonical, firstFinalSeries + i,
                            JsonConvert.SerializeObject(p.Shots[i]), p.Team, p.Pos, 0, now);
                    written += 3;
                }
                catch (Exception ex)
                {
                    failures.Add($"{p.Name} ({p.Canonical}): {ex.Message}");
                    _logger.LogError(ex, "Finalimport misslyckades för {Shooter}", p.Name);
                }
            }

            // ── Verifiera mot CSV:n genom att läsa tillbaka ur databasen ─────────────
            var verified = 0;
            var mismatches = new List<string>();
            foreach (var p in planned)
            {
                var stored = await db.FetchAsync<CompetitionTypes.Precision.Models.PrecisionResultEntry>(
                    "SELECT * FROM PrecisionResultEntry WHERE CompetitionId = @0 AND MemberId = @1 "
                    + "AND ShootingClass = @2 AND SeriesNumber >= @3 ORDER BY SeriesNumber",
                    competitionId, p.MemberId, p.Canonical, firstFinalSeries);

                if (stored.Count != 3)
                {
                    mismatches.Add($"{p.Name} ({p.Canonical}): {stored.Count} finalserier i databasen, väntade 3");
                    continue;
                }

                var rowOk = true;
                int tot = 0, xs = 0;
                for (var i = 0; i < 3; i++)
                {
                    var shots = JsonConvert.DeserializeObject<string[]>(stored[i].Shots) ?? Array.Empty<string>();
                    if (shots.Length != 5)
                    {
                        mismatches.Add($"{p.Name} M{firstFinalSeries + i}: {shots.Length} skott"); rowOk = false; continue;
                    }
                    var sum = shots.Sum(ShotPoints);
                    if (sum != p.Series[i])
                    {
                        mismatches.Add($"{p.Name} M{firstFinalSeries + i}: databasen {sum}, CSV {p.Series[i]}"); rowOk = false;
                    }
                    tot += sum;
                    xs += shots.Count(v => string.Equals(v, "X", StringComparison.OrdinalIgnoreCase));
                }
                if (tot != p.Total) { mismatches.Add($"{p.Name}: finalsumma {tot} i databasen, CSV {p.Total}"); rowOk = false; }
                if (xs != p.XCount) { mismatches.Add($"{p.Name}: {xs} X i databasen, CSV {p.XCount}"); rowOk = false; }
                if (rowOk) verified++;
            }

            return Json(new
            {
                success = failures.Count == 0 && mismatches.Count == 0,
                dryRun = false,
                competitionId,
                finalSeriesNumbers = Enumerable.Range(firstFinalSeries, 3).ToList(),
                groupsImported = planned.Select(p => p.Group).Distinct().ToList(),
                groupsSkipped = skippedGroups,
                finalists = planned.Count,
                seriesRowsWritten = written,
                verifiedAgainstCsv = verified,
                mismatchCount = mismatches.Count,
                mismatches = mismatches.Take(25).ToList(),
                failures
            });
        }

        private string? ResolveFinalCsvPath(string given)
        {
            if (!string.IsNullOrWhiteSpace(given) && System.IO.File.Exists(given)) return given;
            foreach (var dir in new[] { "..\\..\\SSMData", "..\\SSMData", "SSMData" })
            {
                var full = Path.GetFullPath(Path.Combine(_env.ContentRootPath, dir, "SSM_Precision_2026_final.csv"));
                if (System.IO.File.Exists(full)) return full;
            }
            return null;
        }

        // ── Fakturor ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Efterhandsfyller den Pending-faktura varje avgiftsbärande anmälan ska ha.
        ///
        /// ⚠️ Detta är vad seedern MISSADE: den publika vägen
        /// (<c>CompetitionController.RegisterForCompetition</c>) köar
        /// <c>PaymentService.EnsureRegistrationInvoiceAsync</c> ~12 s efter varje NY anmälan, men
        /// seedern skapade bara anmälningsnoden. Följden var att alla 94 anmälningar visade
        /// "Saknar faktura" på Anmälningar-sidan.
        ///
        /// <c>EnsureRegistrationInvoiceAsync</c> är idempotent (återanvänder en befintlig
        /// icke-makulerad faktura) och hoppar över avgiftsfria anmälningar, så den går att köra om.
        /// Avgiften är PER KLASS — <c>RegistrationFeeCalculator</c> loopar klasserna — så en skytt
        /// med tre vapenklasser får 3 × grundavgiften på EN faktura.
        /// </summary>
        /// <param name="reconcile">
        /// Kör <c>ReconcileRegistrationInvoiceAsync</c> i stället för <c>Ensure…</c>. Behövs när
        /// AVGIFTEN har ändrats efter att fakturorna skapades — Ensure är idempotent och
        /// återanvänder den befintliga fakturan utan att röra beloppet, medan Reconcile lappar,
        /// nyskapar eller makulerar enligt delta/top-up-modellen. Det är den appen själv anropar
        /// när en anmälan ändras.
        /// </param>
        [HttpGet]
        public async Task<IActionResult> SeedInvoices(string confirm = "", bool dryRun = true, bool reconcile = false)
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null)
                return Json(new { success = false, message = $"Hittade ingen tävling som heter \"{CompetitionNodeName}\"." });
            var competitionId = competition.Id;

            var hub = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null)
                return Json(new { success = false, message = "Tävlingen har ingen anmälningshubb." });

            var registrations = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .ToList();

            var withInvoice = registrations.Count(r => r.GetValue<int?>("invoiceId") is > 0);

            // Samma uträkning appen gör, så torrkörningen kan säga vad det BORDE bli.
            decimal expectedTotal = 0;
            var byAmount = new Dictionary<decimal, int>();
            foreach (var reg in registrations)
            {
                var classes = CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "")
                    .Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)).ToList();
                var isSub = reg.HasProperty("isSubCompetition") && reg.GetValue<bool>("isSubCompetition");
                var fee = RegistrationFeeCalculator.Calculate(
                    competition, classes.Count > 0 ? classes : new List<string> { "" }, isSub);
                expectedTotal += fee;
                byAmount[fee] = byAmount.GetValueOrDefault(fee) + 1;
            }

            if (dryRun)
            {
                return Json(new
                {
                    success = true,
                    dryRun = true,
                    competitionId,
                    registrations = registrations.Count,
                    alreadyHaveInvoice = withInvoice,
                    missingInvoice = registrations.Count - withInvoice,
                    registrationFee = competition.GetValue<string>("registrationFee"),
                    juniorRegistrationFee = competition.GetValue<string>("juniorRegistrationFee"),
                    expectedTotal,
                    expectedByAmount = byAmount.OrderBy(k => k.Key)
                        .Select(k => $"{k.Value} anmälningar × {k.Key} kr").ToList()
                });
            }

            int created = 0, reused = 0, free = 0, reconciled = 0, failed = 0;
            var failures = new List<string>();

            foreach (var reg in registrations)
            {
                var before = reg.GetValue<int?>("invoiceId") ?? 0;
                try
                {
                    if (reconcile)
                    {
                        if (await _paymentService.ReconcileRegistrationInvoiceAsync(competitionId, reg.Id)) reconciled++;
                        continue;
                    }

                    var invoice = await _paymentService.EnsureRegistrationInvoiceAsync(competitionId, reg.Id);
                    if (invoice == null) { free++; continue; }
                    if (before > 0 && before == invoice.Id) reused++; else created++;
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{reg.GetValue<string>("memberName")}: {ex.Message}");
                    _logger.LogError(ex, "Fakturabackfill misslyckades för anmälan {RegId}", reg.Id);
                }
            }

            return Json(new
            {
                success = failed == 0,
                dryRun = false,
                mode = reconcile ? "reconcile" : "ensure",
                competitionId,
                registrations = registrations.Count,
                invoicesCreated = created,
                invoicesReused = reused,
                freeNoInvoice = free,
                reconciled,
                failed,
                failures,
                expectedTotal
            });
        }

        // ── Lag ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lagklassernas stavning i SSM_lag.csv → den kanoniska lagklassen i
        /// <see cref="TeamClassHelper"/>. CSV:n skriver ut "C Veteran"/"C Junior" där koden har
        /// "C Vet"/"C Jun" — namnet MÅSTE vara det kanoniska, för TeamClass är en sträng som
        /// GetTeamSize, GetCompatibleIndividualClasses och lagresultaten alla slår upp på.
        /// </summary>
        private static readonly Dictionary<string, string> TeamClassFromCsv = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C Öppen"] = "C Öppen",
            ["B"] = "B",
            ["A"] = "A",
            ["C Veteran"] = "C Vet",
            ["C Junior"] = "C Jun",
            ["C Dam"] = "C Dam"
        };

        private sealed class TeamRow
        {
            public string Name = "";
            public string ClubName = "";
            public string CsvClass = "";
            public string TeamClass = "";
            public List<string> MemberNames = new();
        }

        /// <summary>
        /// Läser SSM_lag.csv (semikolon, Windows-1252) och skapar lagen via
        /// <see cref="CompetitionTeamService.CreateTeamAsync"/> — alltså exakt den väg
        /// lagmodalen går: samma validering, samma CompetitionTeam-rad, samma
        /// competitionTeamRegistration-dokument och samma eager-faktura.
        ///
        /// Ett lag som appen VÄGRAR skapa rapporteras med sitt eget felmeddelande och hoppas
        /// över. Seedern skriver ALDRIG förbi valideringen med rå SQL — då hade en riktig
        /// regelkonflikt blivit osynlig.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SeedTeams(string confirm = "", bool dryRun = true, string csvPath = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null)
                return Json(new { success = false, message = $"Hittade ingen tävling som heter \"{CompetitionNodeName}\". Kör Seed först." });
            var competitionId = competition.Id;

            if (!competition.GetValue<bool>("allowTeams"))
                return Json(new { success = false, message = "Tävlingen har inte laganmälan påslagen (allowTeams)." });

            var path = ResolveTeamCsvPath(csvPath);
            if (path == null)
                return Json(new { success = false, message = "Hittade inte SSM_lag.csv. Ange ?csvPath=<absolut sökväg>." });

            var rows = ParseTeamCsv(path, out var unknownClasses);
            if (unknownClasses.Count > 0)
                return Json(new
                {
                    success = false,
                    message = "Okända lagklasser i CSV:n: " + string.Join(", ", unknownClasses)
                             + ". Kända: " + string.Join(", ", TeamClassFromCsv.Keys)
                });

            // Klubbar
            var clubByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _clubService.GetAllClubs()) clubByName.TryAdd(c.Name.Trim(), c.Id);

            // Anmälda skyttar i tävlingen: namn -> medlems-id, plus deras klasser.
            var hub = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            var registered = new Dictionary<string, (int MemberId, List<string> Classes)>(StringComparer.OrdinalIgnoreCase);
            if (hub != null)
            {
                foreach (var reg in _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                             .Where(c => c.ContentType.Alias == "competitionRegistration"))
                {
                    var nm = (reg.GetValue<string>("memberName") ?? "").Trim();
                    if (nm.Length == 0) continue;
                    var classes = CompetitionRegistrationDocument
                        .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "")
                        .Select(e => e.Class).ToList();
                    registered[nm] = (reg.GetValue<int>("memberId"), classes);
                }
            }

            var existingTeams = await _teamService.GetTeamsForCompetitionAsync(competitionId);
            var existingKeys = existingTeams
                .Select(t => $"{t.Team.TeamClass}|{t.Team.TeamName}".ToLowerInvariant())
                .ToHashSet();

            var plan = new List<object>();
            var blocked = new List<string>();
            var created = new List<string>();
            var skipped = new List<string>();

            foreach (var row in rows)
            {
                var key = $"{row.TeamClass}|{row.Name}".ToLowerInvariant();
                if (existingKeys.Contains(key))
                {
                    skipped.Add($"{row.Name} ({row.TeamClass}) — finns redan");
                    continue;
                }

                if (!clubByName.TryGetValue(row.ClubName, out var clubId))
                {
                    blocked.Add($"{row.Name} ({row.TeamClass}): klubben \"{row.ClubName}\" finns inte i dev.");
                    continue;
                }

                var (coreMembers, _) = TeamClassHelper.GetTeamSize(row.TeamClass);
                var compatible = TeamClassHelper.GetCompatibleIndividualClasses(row.TeamClass, isSpringskytte: false);
                var defining = TeamClassHelper.GetDefiningIndividualClasses(row.TeamClass, isSpringskytte: false);

                var memberIds = new List<int>();
                var reasons = new List<string>();
                var hasOwnClassMember = false;
                foreach (var nm in row.MemberNames)
                {
                    if (!registered.TryGetValue(nm, out var hit))
                    {
                        reasons.Add($"{nm} är inte anmäld i tävlingen");
                        continue;
                    }
                    memberIds.Add(hit.MemberId);
                    if (!hit.Classes.Any(c => compatible.Contains(c, StringComparer.OrdinalIgnoreCase)))
                        reasons.Add($"{nm} är anmäld i {string.Join("/", hit.Classes)} — {row.TeamClass} kräver någon av {string.Join("/", compatible)}");
                    if (hit.Classes.Any(c => defining.Contains(c, StringComparer.OrdinalIgnoreCase)))
                        hasOwnClassMember = true;
                }

                // Lagtävlingens villkor 1 — samma påstående som CreateTeamAsync gör, men här så
                // att torrkörningen kan säga det INNAN något skrivs.
                if (memberIds.Count > 0 && !hasOwnClassMember)
                    reasons.Add($"ingen av lagets skyttar är anmäld i {row.TeamClass} ({string.Join("/", defining)})");

                if (row.MemberNames.Count != coreMembers)
                    reasons.Add($"laget har {row.MemberNames.Count} medlemmar i CSV:n, {row.TeamClass} kräver exakt {coreMembers}");

                if (reasons.Count > 0)
                {
                    blocked.Add($"{row.Name} ({row.TeamClass}, {row.ClubName}): " + string.Join("; ", reasons));
                    continue;
                }

                if (dryRun)
                {
                    plan.Add(new
                    {
                        team = row.Name,
                        teamClass = row.TeamClass,
                        club = row.ClubName,
                        clubId,
                        members = row.MemberNames.Zip(memberIds, (n, i) => $"{n}#{i}").ToList()
                    });
                    continue;
                }

                // createdBy = lagets förste medlem. I den riktiga vägen är det den inloggade
                // klubbadmin; kolumnen är bara spårbarhet och måste vara ett giltigt medlems-id.
                var (ok, message, teamId) = await _teamService.CreateTeamAsync(
                    competitionId, row.Name, row.TeamClass, clubId,
                    memberIds.ToArray(), spareId: null,
                    createdByMemberId: memberIds[0], isRelay: false);

                if (ok) created.Add($"{row.Name} ({row.TeamClass}) #{teamId}");
                else blocked.Add($"{row.Name} ({row.TeamClass}, {row.ClubName}): {message}");
            }

            return Json(new
            {
                success = blocked.Count == 0,
                dryRun,
                csvPath = path,
                competitionId,
                csvTeams = rows.Count,
                planned = dryRun ? plan.Count : (int?)null,
                plan = dryRun ? plan : null,
                created = dryRun ? null : created,
                createdCount = dryRun ? (int?)null : created.Count,
                skippedExisting = skipped,
                blocked,
                blockedCount = blocked.Count
            });
        }

        /// <summary>
        /// A/B av lånevillkoren för "C Öppen". TRE fall, varav ETT måste lyckas — annars vet vi
        /// bara att allting faller, inte att reglerna gör det de ska.
        ///   A. tre inlånade skyttar, ingen i C1/C2/C3        -> ska FALLA (villkor 1)
        ///   B. inlånad skytt som redan är med i ett C-lag     -> ska FALLA (villkor 2)
        ///   C. inlånad skytt utan annat C-lag, två i C1-C3    -> ska LYCKAS (vidgningen)
        /// Laget i fall C raderas igen så dev-datan inte får ett påhittat lag.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> TestTeamRules(string confirm = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null) return Json(new { success = false, message = "Tävlingen finns inte." });
            var cid = competition.Id;

            var cases = new[]
            {
                (Label: "A. villkor 1 — bara inlånade (Helena/Maria B/Anders W)",
                 Name:  "ZZTEST villkor1", Members: new[] { 8727, 8733, 8723 }, ShouldSucceed: false),
                (Label: "B. villkor 2 — Maria Blosfeld är redan i Lund Dam (C Dam)",
                 Name:  "ZZTEST villkor2", Members: new[] { 8701, 8705, 8699 }, ShouldSucceed: false),
                (Label: "C. tillåtet lån — Helena Sundin (C1 Dam) i inget annat C-lag",
                 Name:  "ZZTEST tillaten", Members: new[] { 8701, 8705, 8727 }, ShouldSucceed: true)
            };

            var results = new List<object>();
            var allAsExpected = true;

            foreach (var c in cases)
            {
                var (ok, message, teamId) = await _teamService.CreateTeamAsync(
                    cid, c.Name, "C Öppen", clubId: 3117, memberIds: c.Members,
                    spareId: null, createdByMemberId: c.Members[0], isRelay: false);

                var asExpected = ok == c.ShouldSucceed;
                if (!asExpected) allAsExpected = false;

                if (ok && teamId.HasValue)
                {
                    var (delOk, delMsg) = await _teamService.DeleteTeamAsync(teamId.Value, null, SeedTag);
                    results.Add(new { c.Label, expected = c.ShouldSucceed ? "lyckas" : "faller", got = ok ? "lyckades" : "föll", asExpected, message, cleanedUp = delOk, cleanupMessage = delOk ? null : delMsg });
                }
                else
                {
                    results.Add(new { c.Label, expected = c.ShouldSucceed ? "lyckas" : "faller", got = ok ? "lyckades" : "föll", asExpected, message, cleanedUp = (bool?)null, cleanupMessage = (string?)null });
                }
            }

            return Json(new { success = allAsExpected, allAsExpected, results });
        }

        /// <summary>
        /// A/B av klassfiltret i lagresultaten. Lägger in en KÄND fixtur för JPK:s tre skyttar,
        /// räknar ut lagresultaten och jämför med det uträknade svaret — och rapporterar
        /// samtidigt vad den OFILTRERADE summan hade blivit, så man ser att filtret gör något.
        ///
        /// Sandra Sandin Lindqvist är den intressanta: hon har rader i BÅDE "C1 Dam" (Name-form,
        /// den form riktig inmatning skriver) och "A1". C Öppen-laget ska bara räkna hennes
        /// C1 Dam-rader, A-laget bara hennes A1-rader. Att lagklassens lista bär Id-formen
        /// "C1_Dam" är precis varför normaliseringen måste finnas.
        ///
        /// Fixturen raderas alltid på vägen ut, även om ett påstående faller.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> TestTeamResults(string confirm = "")
        {
            var guard = Guard(confirm);
            if (guard != null) return guard;

            var competition = FindCompetition();
            if (competition == null) return Json(new { success = false, message = "Tävlingen finns inte." });
            var cid = competition.Id;

            const int tobias = 8801, kalle = 8845, sandra = 8739;
            var fixture = new (int MemberId, string ShootingClass, string Shots, int PerSeries, int XPerSeries)[]
            {
                (tobias, "C3",     "[\"10\",\"10\",\"10\",\"10\",\"10\"]", 50, 0),
                (kalle,  "C2",     "[\"9\",\"9\",\"9\",\"9\",\"9\"]",      45, 0),
                (sandra, "C1 Dam", "[\"8\",\"8\",\"8\",\"8\",\"8\"]",      40, 0),
                (sandra, "A1",     "[\"X\",\"X\",\"X\",\"X\",\"X\"]",      50, 5)
            };
            const int series = 7;

            using var db = _databaseFactory.CreateDatabase();
            var memberIds = fixture.Select(f => f.MemberId).Distinct().ToArray();

            async Task CleanupAsync()
            {
                foreach (var mid in memberIds)
                    await db.ExecuteAsync(
                        "DELETE FROM PrecisionResultEntry WHERE CompetitionId = @0 AND MemberId = @1", cid, mid);
            }

            try
            {
                await CleanupAsync();

                foreach (var f in fixture)
                    for (var s = 1; s <= series; s++)
                        await db.ExecuteAsync(
                            @"INSERT INTO PrecisionResultEntry
                                (CompetitionId, SeriesNumber, MemberId, TeamNumber, Position, ShootingClass, Shots, EnteredBy, EnteredAt, LastModified)
                              VALUES (@0, @1, @2, 0, 0, @3, @4, 0, @5, @5)",
                            cid, s, f.MemberId, f.ShootingClass, f.Shots, DateTime.Now);

                // Vad en OFILTRERAD läsning hade gett för Sandra — det gamla beteendet.
                var sandraAll = await db.FetchAsync<int>(
                    "SELECT COUNT(*) FROM PrecisionResultEntry WHERE CompetitionId = @0 AND MemberId = @1", cid, sandra);

                var groups = await _teamService.CalculateTeamResultsAsync(cid, "Precision", series);

                var checks = new List<object>();
                var allOk = true;

                void Check(string label, object actual, object expected)
                {
                    var ok = string.Equals(actual?.ToString(), expected?.ToString(), StringComparison.Ordinal);
                    if (!ok) allOk = false;
                    checks.Add(new { label, expected = expected?.ToString(), actual = actual?.ToString(), ok });
                }

                var cOppen = groups.FirstOrDefault(g => g.TeamClass == "C Öppen")?
                    .Teams.FirstOrDefault(t => t.TeamName == "JPK");
                var aTeam = groups.FirstOrDefault(g => g.TeamClass == "A")?
                    .Teams.FirstOrDefault(t => t.TeamName == "JPK");

                // C Öppen JPK: Tobias 7×50 + Kalle 7×45 + Sandra 7×40 (C1 Dam), INTE hennes A1.
                Check("C Öppen JPK finns", cOppen != null, true);
                Check("C Öppen JPK totalpoäng", cOppen?.TotalScore, 7 * (50 + 45 + 40));
                Check("C Öppen JPK X", cOppen?.TotalXCount, 0);
                Check("C Öppen JPK komplett", cOppen?.IsComplete, true);
                Check("Sandras bidrag i C Öppen",
                    cOppen?.MemberResults.FirstOrDefault(m => m.MemberId == sandra)?.Score, 7 * 40);

                // A JPK: bara Sandra har resultat, och bara hennes A1-rader.
                Check("A JPK finns", aTeam != null, true);
                Check("Sandras bidrag i A", aTeam?.MemberResults.FirstOrDefault(m => m.MemberId == sandra)?.Score, 7 * 50);
                Check("Sandras X i A", aTeam?.MemberResults.FirstOrDefault(m => m.MemberId == sandra)?.XCount, 7 * 5);
                Check("A JPK ej komplett (två saknar resultat)", aTeam?.IsComplete, false);

                return Json(new
                {
                    success = allOk,
                    allOk,
                    note = $"Sandra har {sandraAll.FirstOrDefault()} resultatrader i tävlingen ({series} i C1 Dam + "
                         + $"{series} i A1). Ofiltrerat plockade Take({series}) ur båda klasserna.",
                    checks
                });
            }
            finally
            {
                await CleanupAsync();
            }
        }

        private string? ResolveTeamCsvPath(string given)
        {
            if (!string.IsNullOrWhiteSpace(given) && System.IO.File.Exists(given)) return given;

            // Filnamnet på disk är SSM_lag.csv (litet l). Windows bryr sig inte, men var
            // uttrycklig så det inte blir en gåta på ett skiftlägeskänsligt filsystem.
            foreach (var fileName in new[] { "SSM_lag.csv", "SSM_Lag.csv" })
                foreach (var dir in new[] { "..\\..\\SSMData", "..\\SSMData", "SSMData" })
                {
                    var full = Path.GetFullPath(Path.Combine(_env.ContentRootPath, dir, fileName));
                    if (System.IO.File.Exists(full)) return full;
                }
            return null;
        }

        /// <summary>
        /// Semikolonseparerad, Windows-1252: Lagnamn;Klubb;Klass;Medlem 1;Medlem 2;Medlem 3.
        /// Tomma medlemsceller är normala — vet-/dam-/juniorlag har bara två.
        /// </summary>
        private static List<TeamRow> ParseTeamCsv(string path, out List<string> unknownClasses)
        {
            unknownClasses = new List<string>();
            var result = new List<TeamRow>();

            foreach (var line in System.IO.File.ReadAllLines(path, Encoding.Latin1).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = line.Split(';');
                if (cols.Length < 4) continue;

                var csvClass = cols[2].Trim();
                if (!TeamClassFromCsv.TryGetValue(csvClass, out var canon))
                {
                    if (!unknownClasses.Contains(csvClass)) unknownClasses.Add(csvClass);
                    continue;
                }

                result.Add(new TeamRow
                {
                    Name = cols[0].Trim(),
                    ClubName = cols[1].Trim(),
                    CsvClass = csvClass,
                    TeamClass = canon,
                    MemberNames = cols.Skip(3).Select(c => c.Trim()).Where(c => c.Length > 0).ToList()
                });
            }
            return result;
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

        private IContent? FindCompetition()
        {
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) return null;
            var hub = Descendants(root).FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            if (hub == null) return null;
            return Descendants(hub).FirstOrDefault(c =>
                c.ContentType.Alias == "competition" &&
                string.Equals(c.Name, CompetitionNodeName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Årsmappen under competitionsHub, precis som tävlingsguiden gör.</summary>
        private int? ResolveCompetitionParentId(int year, out string error)
        {
            error = "";
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) { error = "Ingen rotnod hittades."; return null; }

            var hub = Descendants(root).FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            if (hub == null) { error = "Hittade ingen competitionsHub."; return null; }

            var yearFolder = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.Name == year.ToString());
            if (yearFolder == null)
            {
                yearFolder = _contentService.Create(year.ToString(), hub.Id, "contentPage");
                if (!_contentService.Save(yearFolder).Success) { error = $"Kunde inte skapa årsmappen {year}."; return null; }
                _contentService.Publish(yearFolder, new[] { "*" }, -1);
            }
            return yearFolder.Id;
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

        private Dictionary<string, int> BuildMemberNameIndex()
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _memberService.GetAll(0, int.MaxValue, out _))
            {
                var key = (m.Name ?? "").Trim().ToLowerInvariant();
                if (key.Length > 0) index.TryAdd(key, m.Id);
            }
            return index;
        }

        /// <summary>
        /// Läser <c>shootingClassIds</c> i båda lagringsformerna: JSON-array (konventionen) och
        /// den äldre CSV:n. Se ShootingClassIdsValue — läsare måste tolerera båda.
        /// </summary>
        private static List<string> ParseClassIds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("["))
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed)?.ToList()
                           ?? new List<string>();
                }
                catch { return new List<string>(); }
            }
            return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        private static int ReadInt(IMember member, string alias)
        {
            var raw = member.GetValue(alias)?.ToString();
            return int.TryParse(raw, out var v) ? v : 0;
        }

        /// <summary>Nivåsiffran (1/2/3) ur skyttens klasser. Rena veteran-/juniorklasser har ingen.</summary>
        private static char? LevelOf(IEnumerable<string> classes)
        {
            foreach (var c in classes)
                if (c.Length > 1 && char.IsDigit(c[1])) return c[1];
            return null;
        }

        private static (string First, string Last) SplitName(string full)
        {
            var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return (parts[0], "");
            return (parts[0], string.Join(" ", parts.Skip(1)));
        }

        /// <summary>
        /// {förnamn}.{efternamn}@invalid.invalid. Första ordet är förnamn, resten efternamn
        /// (mellanslag blir bindestreck). Å/Ä/Ö och accenter translittereras till ASCII —
        /// en e-postadress med icke-ASCII i lokaldelen går inte att lita på. Verifierat:
        /// de 94 skyttarna ger 94 unika adresser.
        /// </summary>
        private static string BuildEmail(string fullName)
        {
            var (first, last) = SplitName(fullName);
            return $"{Slug(first)}.{Slug(last)}@invalid.invalid";
        }

        private static string Slug(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s.ToLowerInvariant())
            {
                switch (ch)
                {
                    case 'å': case 'ä': sb.Append('a'); break;
                    case 'ö': case 'ø': sb.Append('o'); break;
                    case 'é': case 'è': case 'ê': sb.Append('e'); break;
                    case 'ü': sb.Append('u'); break;
                    case 'æ': sb.Append("ae"); break;
                    case 'ß': sb.Append("ss"); break;
                    case ' ': sb.Append('-'); break;
                    default:
                        if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
                        else if (ch == '-' || ch == '.') sb.Append(ch);
                        break;
                }
            }
            return sb.ToString().Trim('-', '.');
        }

        private string? ResolveCsvPath(string given)
        {
            if (!string.IsNullOrWhiteSpace(given) && System.IO.File.Exists(given)) return given;

            const string fileName = "SSM_Precision_2026_grundomgang_startlista.csv";
            var candidates = new[]
            {
                Path.Combine(_env.ContentRootPath, "..", "..", "SSMData", fileName),
                Path.Combine(_env.ContentRootPath, "..", "SSMData", fileName),
                Path.Combine(_env.ContentRootPath, "SSMData", fileName)
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (System.IO.File.Exists(full)) return full;
            }
            return null;
        }

        /// <summary>
        /// Tabbseparerad CSV i Windows-1252. En skytt förekommer på flera rader (en per
        /// vapenklass) och blir EN anmälan med flera klassposter — precis som när en skytt
        /// kryssar tre klasser i anmälningsformuläret.
        /// </summary>
        private static List<Shooter> ParseCsv(string path, out int dataRows, out List<string> badClasses)
        {
            var lines = System.IO.File.ReadAllLines(path, Encoding.Latin1);
            var order = new List<string>();
            var map = new Dictionary<string, Shooter>(StringComparer.OrdinalIgnoreCase);
            badClasses = new List<string>();
            dataRows = 0;

            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = line.Split('\t');
                if (cols.Length < 5) continue;

                var rawClass = cols[2].Trim();
                var name = cols[3].Trim();
                var club = cols[4].Trim();
                if (name.Length == 0) continue;
                dataRows++;

                if (!ClassMap.TryGetValue(rawClass, out var classId))
                {
                    if (!badClasses.Contains(rawClass)) badClasses.Add(rawClass);
                    continue;
                }

                if (!map.TryGetValue(name, out var shooter))
                {
                    shooter = new Shooter { Name = name, ClubName = club };
                    map[name] = shooter;
                    order.Add(name);
                }
                if (!shooter.Classes.Contains(classId)) shooter.Classes.Add(classId);
            }

            return order.Select(n => map[n]).ToList();
        }
    }
}
