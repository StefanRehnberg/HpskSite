using HpskSite.Services;
using HpskSite.Services.Storage;
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
    /// Skalplanen D3: flytten av befintliga filer till blobblagringen, under /admin-page → Underhåll.
    /// Bara sajtadministratör. Kopierar, raderar aldrig.
    /// </summary>
    public class FileStorageMigrationController : SurfaceController
    {
        private readonly AdminAuthorizationService _auth;
        private readonly FileStorageMigrationService _migration;

        public FileStorageMigrationController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, FileStorageMigrationService migration)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _migration = migration;
        }

        private const string NotAdmin = "Bara sajtadministratör.";

        [HttpGet]
        public async Task<IActionResult> Preview()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            var status = await _migration.PreviewAsync(HttpContext.RequestAborted);
            return Json(new { success = true, status });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            if (!_migration.StoreIsBlob && !_migration.MediaIsBlob)
                return Json(new { success = false, message = "Blobblagringen är inte påslagen — filerna ligger på disken som förut, och det finns ingenting att flytta." });
            // En omgång i taget: ett anrop får inte bli så långt att proxyn ger upp. Ytan kör om tills inget återstår.
            var result = await _migration.RunAsync(TimeSpan.FromSeconds(45), HttpContext.RequestAborted);
            return Json(new { success = result.Failures.Count == 0, result });
        }
    }
}
