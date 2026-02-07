using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
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
    /// Controller for regional administration functionality.
    /// Handles region-related operations and will support regional admin access in the future.
    /// </summary>
    public class RegionalAdminController : SurfaceController
    {
        private readonly AdminAuthorizationService _authService;
        private readonly ILogger<RegionalAdminController> _logger;

        public RegionalAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService authService,
            ILogger<RegionalAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authService = authService;
            _logger = logger;
        }

        /// <summary>
        /// Get all regions (kretsar) for filter dropdowns.
        /// Returns ALL regions from the Federations enum, sorted in Swedish alphabetical order.
        /// </summary>
        [HttpGet]
        public IActionResult GetAllRegions()
        {
            try
            {
                var swedishComparer = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);

                // Get all regions from the enum
                var regions = Enum.GetValues(typeof(Federations.RegionalFederations))
                    .Cast<Federations.RegionalFederations>()
                    .Select(f => new
                    {
                        id = f.ToString(),
                        name = f.GetDescription()
                    })
                    .OrderBy(r => r.name, swedishComparer)
                    .ToList();

                return Json(new { success = true, regions = regions });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading regions");
                return Json(new { success = false, message = "Error loading regions: " + ex.Message });
            }
        }
    }
}
