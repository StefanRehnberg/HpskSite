using HpskSite.Services;
using HpskSite.Services.C3;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// C3-planen S0, skyddsnätet: tre läsande underlag för jämförelsen före och efter att tävlingarna
    /// lämnar trädet. Bara sajtadministratör. Skriver ingenting.
    ///
    /// <list type="bullet">
    ///   <item><c>CompetitionReads</c> — varje fält läst med de gamla uttrycken (<see cref="LegacyCompetitionReadSnapshot"/>).</item>
    ///   <item><c>AuthorizationReport</c> — vilka som passerar varje grind på en tävling (<see cref="CompetitionAuthorizationReport"/>).</item>
    ///   <item><c>UrlCorpus</c> — varje adress sajten serverar för tävlingarna (<see cref="CompetitionUrlCorpusSource"/>).</item>
    /// </list>
    /// Skripten i <c>hpsk-verify/c3/</c> hämtar dem och sparar svaren; jämförelsen sker där.
    /// </summary>
    public class C3BaselineController : SurfaceController
    {
        private readonly AdminAuthorizationService _auth;
        private readonly LegacyCompetitionReadSnapshot _reads;
        private readonly CompetitionAuthorizationReport _report;
        private readonly CompetitionUrlCorpusSource _corpus;
        private readonly CompetitionStoreComparison _storeComparison;

        public C3BaselineController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, LegacyCompetitionReadSnapshot reads, CompetitionAuthorizationReport report,
            CompetitionUrlCorpusSource corpus, CompetitionStoreComparison storeComparison)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _reads = reads;
            _report = report;
            _corpus = corpus;
            _storeComparison = storeComparison;
        }

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Include,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss",
            // Egenskaper i camelCase, men ordlistornas nycklar (fältens alias, grindarnas namn) orörda.
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false },
            },
        };

        private ContentResult JsonText(object value) =>
            Content(JsonConvert.SerializeObject(value, Formatting.None, JsonSettings), "application/json; charset=utf-8");

        private IActionResult Refused() => StatusCode(403, "Bara sajtadministratör.");

        /// <summary>
        /// C3-planen S5d: vilka tävlingscachar finns i den här instansen just nu (meny, adressindex,
        /// adminlistor), och vilken versionsrad den senast såg. Läser bara. <paramref name="check"/> = kör
        /// versionskontrollen först (som menyn och listorna gör).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CompetitionCacheProbe(bool check = false)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            if (check)
                (HttpContext.RequestServices.GetService(typeof(HpskSite.Services.Competitions.CompetitionCacheVersion))
                    as HpskSite.Services.Competitions.CompetitionCacheVersion)?.EnsureFresh();
            var caches = (Umbraco.Cms.Core.Cache.AppCaches)HttpContext.RequestServices.GetService(typeof(Umbraco.Cms.Core.Cache.AppCaches))!;
            return JsonText(new
            {
                menu = caches.RuntimeCache.Get(HpskSite.Services.CompetitionNavigationCaches.MenuKey) != null,
                urlIndex = caches.RuntimeCache.Get(HpskSite.Services.CompetitionNavigationCaches.UrlIndexKey) != null,
                competitionLists = caches.RuntimeCache.SearchByKey("admin_competitions_list_").Count(),
                seriesLists = caches.RuntimeCache.SearchByKey("admin_series_list").Count(),
            });
        }

        [HttpGet]
        public async Task<IActionResult> CompetitionReads(int? id = null)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            var nodes = _reads.Build(id);
            return JsonText(new { capturedAt = DateTime.Now, count = nodes.Count, nodes });
        }

        [HttpGet]
        public async Task<IActionResult> AuthorizationReport(int competitionId)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            return JsonText(await _report.BuildAsync(competitionId));
        }

        [HttpGet]
        public async Task<IActionResult> AuthorizationCompositeSources()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            return JsonText(CompetitionAuthorizationReport.CompositeSources);
        }

        [HttpGet]
        public async Task<IActionResult> UrlCorpus(int visitorTop = 400)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            var entries = _corpus.Build(visitorTop);
            return JsonText(new { capturedAt = DateTime.Now, count = entries.Count, entries });
        }

        // ── S1: posten och lagringarna ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>CompetitionInfo</c> ur nodlagringen mot de gamla läsningarna, fält för fält. Svaret är
        /// avvikelserna, grupperade per (fält, läsform), med upp till fem exempel var.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CompetitionInfoVsLegacy()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            var deviations = _storeComparison.CompareWithLegacy();
            var groups = deviations
                .GroupBy(d => d.Alias + " " + d.Form)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { key = g.Key, count = g.Count(), examples = g.Take(5) })
                .ToList();
            return JsonText(new { capturedAt = DateTime.Now, total = deviations.Count, groups });
        }

        /// <summary>Kopierar de publicerade tävlingarna och serierna till tabellerna. BARA på databaskopian.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyCompetitionsToTable()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            try
            {
                var (competitions, series) = _storeComparison.CopyNodesToTable();
                return JsonText(new { success = true, competitions, series });
            }
            catch (InvalidOperationException ex)
            {
                return JsonText(new { success = false, message = ex.Message });
            }
        }

        /// <summary>Prövar uppdateringen mot båda lagringarna på en tävling och återställer. BARA på databaskopian.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CompetitionStoreUpdateProbe(int id,
            [FromServices] HpskSite.Services.Competitions.SwitchingCompetitionStore switching,
            [FromServices] HpskSite.Services.SiteSettings.SiteSettingService settings)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            try
            {
                var checks = _storeComparison.UpdateProbe(id, switching, settings);
                return JsonText(new { success = true, checks });
            }
            catch (InvalidOperationException ex)
            {
                return JsonText(new { success = false, message = ex.Message });
            }
        }

        /// <summary>Samma frågor mot nodlagringen och tabellagringen. BARA på databaskopian. Tom lista = lika.</summary>
        [HttpGet]
        public async Task<IActionResult> CompetitionStoreCompare()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Refused();
            try
            {
                var diffs = _storeComparison.CompareNodeVsSql();
                return JsonText(new { success = true, count = diffs.Count, diffs });
            }
            catch (InvalidOperationException ex)
            {
                return JsonText(new { success = false, message = ex.Message });
            }
        }
    }
}
