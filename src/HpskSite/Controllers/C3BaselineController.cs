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

        public C3BaselineController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, LegacyCompetitionReadSnapshot reads, CompetitionAuthorizationReport report,
            CompetitionUrlCorpusSource corpus)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _reads = reads;
            _report = report;
            _corpus = corpus;
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
    }
}
