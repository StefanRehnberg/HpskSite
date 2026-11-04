using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
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
    /// Utanför mästerskapet, F0: fryser de redan publicerade fältlistorna EN gång (under /admin-page → Underhåll).
    /// Efter att kolumnen <c>CompetitionResultList.PublishedSnapshot</c> kommit på plats saknar varje redan
    /// publicerad lista sin frusna bild, och läses då live som förut. Det här steget skriver bilden ur dagens
    /// uträkning — och bara för en officiell lista som saknar den. Rör varken artefakten, publiceringsflaggan
    /// eller tiden. Bara sajtadministratör. Går att köra om.
    /// </summary>
    public class PublishedResultsFreezeController : SurfaceController
    {
        private readonly AdminAuthorizationService _auth;
        private readonly FaltskytteResultArtifactService _artifact;
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public PublishedResultsFreezeController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, FaltskytteResultArtifactService artifact)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _artifact = artifact;
            _databaseFactory = databaseFactory;
        }

        private const string NotAdmin = "Bara sajtadministratör.";

        private List<int> OfficialFaltIds()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>(
                "SELECT Id FROM dbo.Competition WHERE FaltskytteResultsOfficial = 1 ORDER BY Id");
        }

        /// <summary>Visar läget utan att skriva: per officiell fälttävling om bilden finns eller skulle skapas.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> Preview()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            var rows = new List<object>();
            foreach (var id in OfficialFaltIds())
                rows.Add(new { id, state = await _artifact.FreezeIfMissingAsync(id, apply: false) });
            return Json(new { success = true, rows });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> Run()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            var rows = new List<object>();
            foreach (var id in OfficialFaltIds())
                rows.Add(new { id, state = await _artifact.FreezeIfMissingAsync(id, apply: true) });
            return Json(new { success = true, rows });
        }
    }
}
