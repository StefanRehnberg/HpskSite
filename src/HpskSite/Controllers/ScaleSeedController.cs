using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// SKALPLANEN, FAS A (2026-10-12): fyller en egen databaskopia med påhittade klubbar, medlemmar
    /// och tävlingar så att vi kan mäta vad som händer vid 20 000 medlemmar och 400 klubbar.
    ///
    /// <para><b>⚠️⚠️ KÖR BARA MOT <c>Umbraco_Scale</c>.</b> <see cref="Guard"/> kräver Development, att
    /// anslutningssträngen pekar på databasen <c>Umbraco_Scale</c> OCH att <c>DB_NAME()</c> svarar
    /// samma sak. Starta appen med profilen <c>Umbraco.Web.UI (Scale)</c>. Dev-datat är verkliga
    /// personer — inget härifrån får hamna där.</para>
    ///
    /// <para>Kopian är engångs: den raderas när mätningen är klar, så seedern har ingen städning.
    /// Alla adresser slutar på <c>@skala.invalid</c> (RFC 2606, kan aldrig levereras).</para>
    ///
    /// <para>Anrop (inloggad sajtadmin behövs inte — guarden är databasen):</para>
    /// <code>
    /// /umbraco/surface/ScaleSeed/Status?confirm=SKALA
    /// /umbraco/surface/ScaleSeed/SeedClubs?confirm=SKALA&amp;count=400
    /// /umbraco/surface/ScaleSeed/SeedMembers?confirm=SKALA&amp;target=20000&amp;batch=1000   (upprepa)
    /// /umbraco/surface/ScaleSeed/SeedCompetitions?confirm=SKALA&amp;perClub=8&amp;batch=40  (upprepa)
    /// /umbraco/surface/ScaleSeed/MeasureMemberSaves?confirm=SKALA&amp;parallel=20&amp;total=200
    /// </code>
    /// <para>Varje steg är idempotent och fortsätter där det slutade.</para>
    /// </summary>
    public class ScaleSeedController : SurfaceController
    {
        private const string Confirm = "SKALA";
        private const string Database = "Umbraco_Scale";
        private const string ClubPrefix = "Skalklubb ";
        private const string CompPrefix = "Skaltävling ";
        private const string MailDomain = "skala.invalid";
        private const string Tag = "skalseed";

        private static readonly string[] FirstNames =
        {
            "Anna", "Erik", "Maria", "Lars", "Karin", "Anders", "Eva", "Johan", "Lena", "Per",
            "Sara", "Mikael", "Emma", "Nils", "Ingrid", "Olof", "Elin", "Gustav", "Astrid", "Hugo"
        };
        private static readonly string[] LastNames =
        {
            "Lind", "Berg", "Holm", "Ek", "Strand", "Dahl", "Sjö", "Lund", "Hed", "Ås",
            "Nyberg", "Ström", "Vik", "Bäck", "Kvist", "Mård", "Falk", "Ros", "Ahl", "Torn"
        };
        private static readonly string[] Classes = { "C1", "C2", "C3", "A1", "A2", "B1", "B2", "C_Vet_Y", "C1_Dam" };

        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubMembershipService _memberships;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ScaleSeedController> _logger;

        public ScaleSeedController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IContentService contentService,
            ClubMembershipService memberships,
            IConfiguration configuration,
            IWebHostEnvironment env,
            ILogger<ScaleSeedController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _memberships = memberships;
            _databaseFactory = databaseFactory;
            _configuration = configuration;
            _env = env;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Status(string confirm = "")
        {
            if (Guard(confirm) is { } g) return g;
            using var db = _databaseFactory.CreateDatabase();
            return Json(new
            {
                success = true,
                database = db.ExecuteScalar<string>("SELECT DB_NAME()"),
                clubs = db.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoNode WHERE text LIKE @0 AND trashed = 0", ClubPrefix + "%"),
                members = db.ExecuteScalar<int>("SELECT COUNT(*) FROM cmsMember WHERE Email LIKE @0", "%@" + MailDomain),
                membersTotal = db.ExecuteScalar<int>("SELECT COUNT(*) FROM cmsMember"),
                competitions = db.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoNode WHERE text LIKE @0 AND trashed = 0", CompPrefix + "%"),
                contentNodes = db.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoNode WHERE nodeObjectType = 'C66BA18E-EAF3-4CFF-8A22-41B16D66A972'"),
                propertyRows = db.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoPropertyData"),
                largestClubs = db.Fetch<dynamic>(
                    @"SELECT TOP 5 m.ClubId, COUNT(*) AS Members FROM ClubMembership m
                        JOIN umbracoNode n ON n.id = m.ClubId WHERE n.text LIKE @0
                       GROUP BY m.ClubId ORDER BY COUNT(*) DESC", ClubPrefix + "%")
            });
        }

        // ── Klubbar ────────────────────────────────────────────────────────────────

        [HttpGet]
        public IActionResult SeedClubs(string confirm = "", int count = 400)
        {
            if (Guard(confirm) is { } g) return g;

            var regions = RegionClubPages();
            if (regions.Count == 0) return Json(new { success = false, message = "Inga kretsar med en clubsPage hittades." });

            var existing = ExistingNames(ClubPrefix);
            int created = 0;
            for (var i = 1; i <= count; i++)
            {
                var name = $"{ClubPrefix}{i:000}";
                if (existing.Contains(name)) continue;
                var (clubsPageId, regionCode) = regions[(i - 1) % regions.Count];
                var club = _contentService.Create(name, clubsPageId, "club");
                club.SetValue("clubName", name);
                club.SetValue("regionalFederation", regionCode);
                club.SetValue("isActive", true);
                club.SetValue("city", "Skalby");
                club.SetValue("contactEmail", $"klubb{i:000}@{MailDomain}");
                club.SetValue("description", "Påhittad klubb för skalmätningen (Umbraco_Scale).");
                if (_contentService.Save(club).Success)
                {
                    _contentService.Publish(club, new[] { "*" }, -1);
                    created++;
                }
            }
            return Json(new { success = true, created, total = ExistingNames(ClubPrefix).Count });
        }

        // ── Medlemmar ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Skapar medlemmar fram till <paramref name="target"/>, högst <paramref name="batch"/> per anrop.
        /// Klubbstorleken följer en sned fördelning (några stora, många små), som verkligheten — en
        /// jämn fördelning hade aldrig mätt den största klubbens medlemslista.
        /// </summary>
        [HttpGet]
        public IActionResult SeedMembers(string confirm = "", int target = 20000, int batch = 1000)
        {
            if (Guard(confirm) is { } g) return g;

            var clubs = SeedClubIds();
            if (clubs.Count == 0) return Json(new { success = false, message = "Kör SeedClubs först." });

            int start;
            using (var db = _databaseFactory.CreateDatabase())
                start = db.ExecuteScalar<int>("SELECT COUNT(*) FROM cmsMember WHERE Email LIKE @0", "%@" + MailDomain);

            var weights = clubs.Select((_, i) => 1.0 / Math.Pow(i + 1, 0.8)).ToArray();
            var sum = weights.Sum();
            var cumulative = new double[weights.Length];
            double acc = 0;
            for (var i = 0; i < weights.Length; i++) { acc += weights[i] / sum; cumulative[i] = acc; }

            var sw = Stopwatch.StartNew();
            var createdIds = new List<int>();
            var end = Math.Min(target, start + batch);
            for (var n = start + 1; n <= end; n++)
            {
                var email = $"skala{n:00000}@{MailDomain}";
                if (_memberService.GetByEmail(email) != null) continue;

                var rnd = new Random(n);
                var first = FirstNames[rnd.Next(FirstNames.Length)];
                var last = LastNames[rnd.Next(LastNames.Length)] + (n % 7 == 0 ? "gren" : "");
                var clubId = clubs[Pick(cumulative, rnd.NextDouble())];
                var second = rnd.NextDouble() < 0.15 ? clubs[rnd.Next(clubs.Count)] : 0;

                var member = _memberService.CreateMember(email, email, $"{first} {last}", "hpskMember");
                member.SetValue("firstName", first);
                member.SetValue("lastName", last);
                member.SetValue("primaryClubId", clubId.ToString());
                if (second > 0 && second != clubId) member.SetValue("memberClubIds", $"{clubId},{second}");
                member.SetValue("precisionShooterClass", Classes[rnd.Next(Classes.Length)]);
                member.SetValue("lastActiveDate", DateTime.Now.AddDays(-rnd.Next(0, 400)));
                member.IsApproved = true;
                _memberService.Save(member);
                createdIds.Add(member.Id);

                _memberships.EnsureMembership(member.Id, clubId);
                if (second > 0 && second != clubId) _memberships.EnsureMembership(member.Id, second);
            }
            if (createdIds.Count > 0) _memberService.AssignRoles(createdIds.ToArray(), new[] { "Users" });

            _logger.LogWarning("Skalseed: {Count} medlemmar på {Ms} ms ({Start}..{End}).", createdIds.Count, sw.ElapsedMilliseconds, start + 1, end);
            return Json(new
            {
                success = true,
                created = createdIds.Count,
                ms = sw.ElapsedMilliseconds,
                msPerMember = createdIds.Count == 0 ? 0 : sw.ElapsedMilliseconds / createdIds.Count,
                seededMembers = end,
                done = end >= target
            });
        }

        private static int Pick(double[] cumulative, double r)
        {
            for (var i = 0; i < cumulative.Length; i++) if (r <= cumulative[i]) return i;
            return cumulative.Length - 1;
        }

        // ── Tävlingar ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Skapar <paramref name="perClub"/> tävlingar per påhittad klubb, med startlista och
        /// resultatlista som barn och blobbar i realistisk storlek — det är trädet och blobbarna
        /// fas C ska flytta, och det är dem mätningen behöver. Högst <paramref name="batch"/> klubbar
        /// per anrop. Anmälningar och resultatrader skapas inte.
        /// </summary>
        [HttpGet]
        public IActionResult SeedCompetitions(string confirm = "", int perClub = 8, int batch = 40, int year = 2026)
        {
            if (Guard(confirm) is { } g) return g;

            var yearFolder = EnsureYearFolder(year, out var error);
            if (yearFolder == null) return Json(new { success = false, message = error });

            var clubs = SeedClubIds();
            var existing = ExistingNames(CompPrefix);
            var startList = FakeStartListJson();
            var startListHtml = new string('x', 20_000);
            var resultData = FakeResultJson();

            var sw = Stopwatch.StartNew();
            int created = 0, clubsTouched = 0;
            foreach (var clubId in clubs)
            {
                if (clubsTouched >= batch) break;
                var any = false;
                for (var k = 1; k <= perClub; k++)
                {
                    var name = $"{CompPrefix}{clubId}-{k}";
                    if (existing.Contains(name)) continue;
                    any = true;

                    var comp = _contentService.Create(name, yearFolder.Id, "competition");
                    comp.SetValue("competitionName", name);
                    comp.SetValue("competitionType", "Precision");
                    comp.SetValue("clubId", clubId);
                    comp.SetValue("competitionDate", new DateTime(year, 1 + (k * 37 + clubId) % 12, 1 + (clubId + k) % 27));
                    comp.SetValue("numberOfSeriesOrStations", 7);
                    comp.SetValue("shootingClassIds", "[\"C1\",\"C2\",\"C3\",\"A1\",\"B1\"]");
                    if (!_contentService.Save(comp).Success) continue;
                    _contentService.Publish(comp, new[] { "*" }, -1);

                    var sl = _contentService.Create("Startlista", comp.Id, "precisionStartList");
                    sl.SetValue("configurationData", startList);
                    sl.SetValue("startListContent", startListHtml);
                    sl.SetValue("isOfficialStartList", true);
                    if (_contentService.Save(sl).Success) _contentService.Publish(sl, new[] { "*" }, -1);

                    var res = _contentService.Create("Resultat", comp.Id, "competitionResult");
                    res.SetValue("resultData", resultData);
                    res.SetValue("isOfficial", true);
                    if (_contentService.Save(res).Success) _contentService.Publish(res, new[] { "*" }, -1);

                    created++;
                }
                if (any) clubsTouched++;
            }

            _logger.LogWarning("Skalseed: {Count} tävlingar på {Ms} ms.", created, sw.ElapsedMilliseconds);
            return Json(new
            {
                success = true,
                created,
                ms = sw.ElapsedMilliseconds,
                msPerCompetition = created == 0 ? 0 : sw.ElapsedMilliseconds / created,
                total = ExistingNames(CompPrefix).Count,
                done = clubsTouched == 0
            });
        }

        // ── Mätning: medlemssparningar parallellt ────────────────────────────────────

        /// <summary>
        /// Mäter vad en rusning av medlemssparningar kostar — formen <c>MemberActivityService</c> ger
        /// (lastActiveDate var 5:e minut per inloggad medlem). <paramref name="parallel"/> samtidiga
        /// arbetare sparar sammanlagt <paramref name="total"/> påhittade medlemmar.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> MeasureMemberSaves(string confirm = "", int parallel = 20, int total = 200)
        {
            if (Guard(confirm) is { } g) return g;

            List<int> ids;
            using (var db = _databaseFactory.CreateDatabase())
                ids = db.Fetch<int>($"SELECT TOP {Math.Max(1, total)} nodeId FROM cmsMember WHERE Email LIKE @0 ORDER BY nodeId", "%@" + MailDomain);
            if (ids.Count == 0) return Json(new { success = false, message = "Inga påhittade medlemmar." });

            var latencies = new System.Collections.Concurrent.ConcurrentBag<long>();
            var errors = 0;
            var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(ids);
            var sw = Stopwatch.StartNew();

            // ⚠️ Varje arbetare i en tom körkontext — annars delar de Umbracos ambienta scope (CLAUDE.md,
            // IsolatedBackgroundService), och mätningen mäter en låsning vi själva skapat.
            Task[] workers;
            using (ExecutionContext.SuppressFlow())
            {
                workers = Enumerable.Range(0, Math.Max(1, parallel)).Select(_ => Task.Run(() =>
                {
                    while (queue.TryDequeue(out var id))
                    {
                        var t = Stopwatch.StartNew();
                        try
                        {
                            var m = _memberService.GetById(id);
                            if (m == null) continue;
                            m.SetValue("lastActiveDate", DateTime.Now);
                            _memberService.Save(m);
                            latencies.Add(t.ElapsedMilliseconds);
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                })).ToArray();
            }
            await Task.WhenAll(workers);
            sw.Stop();

            var sorted = latencies.OrderBy(x => x).ToArray();
            long P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
            return Json(new
            {
                success = true,
                parallel,
                saves = sorted.Length,
                errors,
                wallMs = sw.ElapsedMilliseconds,
                savesPerSecond = sorted.Length == 0 ? 0 : Math.Round(sorted.Length * 1000.0 / sw.ElapsedMilliseconds, 1),
                p50 = P(0.5),
                p95 = P(0.95),
                max = sorted.Length == 0 ? 0 : sorted[^1]
            });
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────

        private IActionResult? Guard(string confirm)
        {
            if (!string.Equals(confirm, Confirm, StringComparison.Ordinal))
                return Json(new { success = false, message = $"Saknar ?confirm={Confirm}." });
            if (!_env.IsDevelopment())
                return Json(new { success = false, message = "Skalseedern kör bara i Development." });

            var dsn = _configuration.GetConnectionString("umbracoDbDSN") ?? "";
            string catalog;
            try { catalog = new SqlConnectionStringBuilder(dsn).InitialCatalog; }
            catch { catalog = ""; }
            if (!string.Equals(catalog, Database, StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = $"Anslutningen pekar på '{catalog}', inte {Database}. Starta med profilen \"Umbraco.Web.UI (Scale)\"." });

            using var db = _databaseFactory.CreateDatabase();
            var actual = db.ExecuteScalar<string>("SELECT DB_NAME()");
            if (!string.Equals(actual, Database, StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = $"Databasen svarar '{actual}', inte {Database}. Vägrar skriva." });
            return null;
        }

        private List<(int ClubsPageId, string RegionCode)> RegionClubPages()
        {
            var list = new List<(int, string)>();
            var root = _contentService.GetRootContent().FirstOrDefault();
            if (root == null) return list;
            foreach (var region in _contentService.GetPagedChildren(root.Id, 0, int.MaxValue, out _)
                         .Where(c => c.ContentType.Alias == "regionalPage"))
            {
                var clubsPage = _contentService.GetPagedChildren(region.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                var code = region.GetValue<string>("regionCode");
                if (clubsPage != null && !string.IsNullOrWhiteSpace(code)) list.Add((clubsPage.Id, code));
            }
            return list;
        }

        private HashSet<string> ExistingNames(string prefix)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<string>("SELECT text FROM umbracoNode WHERE text LIKE @0 AND trashed = 0", prefix + "%")
                .ToHashSet(StringComparer.Ordinal);
        }

        private List<int> SeedClubIds()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>(
                @"SELECT n.id FROM umbracoNode n
                    JOIN umbracoContent c ON c.nodeId = n.id
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                   WHERE ct.alias = 'club' AND n.text LIKE @0 AND n.trashed = 0
                   ORDER BY n.text", ClubPrefix + "%");
        }

        private IContent? EnsureYearFolder(int year, out string error)
        {
            error = "";
            using var db = _databaseFactory.CreateDatabase();
            var hubId = db.ExecuteScalar<int?>(
                @"SELECT TOP 1 n.id FROM umbracoNode n
                    JOIN umbracoContent c ON c.nodeId = n.id
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                   WHERE ct.alias = 'competitionsHub' AND n.trashed = 0");
            if (hubId == null) { error = "Hittade ingen competitionsHub."; return null; }

            var name = year.ToString();
            var folder = _contentService.GetPagedChildren(hubId.Value, 0, int.MaxValue, out _).FirstOrDefault(c => c.Name == name);
            if (folder != null) return folder;
            folder = _contentService.Create(name, hubId.Value, "contentPage");
            if (!_contentService.Save(folder).Success) { error = "Kunde inte skapa årsmappen."; return null; }
            _contentService.Publish(folder, new[] { "*" }, -1);
            return folder;
        }

        /// <summary>En startlista med 6 skjutlag × 5 skyttar, i den form precisionsredigeraren lagrar.</summary>
        private static string FakeStartListJson()
        {
            var teams = Enumerable.Range(1, 6).Select(t => new
            {
                TeamNumber = t,
                StartTime = $"{8 + t}:00",
                Shooters = Enumerable.Range(1, 5).Select(p => new
                {
                    Position = p, MemberId = 0, Name = $"Påhittad Skytt {t}-{p}", Club = "Skalklubb",
                    WeaponClass = "C1"
                })
            });
            return System.Text.Json.JsonSerializer.Serialize(new { Settings = new { Format = "Mixed" }, Teams = teams });
        }

        /// <summary>En resultatlista i ungefär rätt storlek (~50 KB) för en tävling med 30 skyttar.</summary>
        private static string FakeResultJson()
        {
            var groups = Enumerable.Range(1, 5).Select(g => new
            {
                ClassName = $"C{g}",
                Shooters = Enumerable.Range(1, 6).Select(s => new
                {
                    Rank = s, MemberId = 0, Name = $"Påhittad Skytt {g}-{s}", Club = "Skalklubb",
                    TotalScore = 300 - s, XCount = 5,
                    Series = Enumerable.Range(1, 7).Select(n => new { SeriesNumber = n, Total = 45, Shots = new[] { "10", "9", "9", "8", "X" } })
                })
            });
            var json = System.Text.Json.JsonSerializer.Serialize(new { ClassGroups = groups, UpdatedAt = DateTime.Now, Tag });
            return json + new string(' ', Math.Max(0, 50_000 - json.Length));
        }
    }
}
