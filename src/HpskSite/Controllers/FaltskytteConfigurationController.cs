using HpskSite.CompetitionTypes.Faltskytte.Models;
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
    /// API surface for standalone Fältskytte station configurations.
    /// All endpoints require a logged-in member; view/edit/delete checks
    /// flow through FaltskytteConfigurationService.
    /// </summary>
    public class FaltskytteConfigurationController : SurfaceController
    {
        private readonly FaltskytteConfigurationService _configService;
        private readonly ILogger<FaltskytteConfigurationController> _logger;

        public FaltskytteConfigurationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FaltskytteConfigurationService configService,
            ILogger<FaltskytteConfigurationController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _configService = configService;
            _logger = logger;
        }

        // ── Reads ────────────────────────────────────────────────────────

        /// <summary>Returns every configuration the current user can view. JSON blob omitted.</summary>
        [HttpGet]
        public async Task<IActionResult> ListAccessible()
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var configs = await _configService.ListAccessibleAsync(viewerId);
                var views = new List<FaltskytteConfigurationView>();
                foreach (var cfg in configs)
                {
                    views.Add(await _configService.BuildViewAsync(cfg, viewerId, includeJson: false));
                }
                return Json(new { success = true, configurations = views });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing Fältskytte configurations");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Returns a single configuration, full view including JSON blob. Auth via CanViewAsync.</summary>
        [HttpGet]
        public async Task<IActionResult> Get(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanViewAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att visa denna konfiguration." });

                var view = await _configService.BuildViewAsync(config, viewerId, includeJson: true);
                return Json(new { success = true, configuration = view });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Lightweight station-summary view used by the "Importera station från en annan konfiguration"
        /// picker. Returns the stations array (figures + målgrupps) but skips the full faltCfgData blob.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStationsForImport(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanViewAsync(config, viewerId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                return Json(new
                {
                    success = true,
                    id = config.Id,
                    name = config.Name,
                    jsonBlob = config.JsonBlob
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stations for import from config {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Writes ───────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromBody] CreateFaltskytteConfigurationRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var (success, message, created) = await _configService.CreateAsync(request, viewerId.Value);
                if (!success || created == null) return Json(new { success = false, message });

                return Json(new { success = true, configurationId = created.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Fältskytte configuration");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update([FromBody] UpdateFaltskytteConfigurationRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.Id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra denna konfiguration." });

                var (success, message) = await _configService.UpdateAsync(request);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Fältskytte configuration {Id}", request.Id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanDeleteAsync(config, viewerId))
                    return Json(new { success = false, message = "Endast ägare eller administratör kan ta bort konfigurationen." });

                var (success, message) = await _configService.DeleteAsync(id);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Duplicate(int id, string? newName = null)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var source = await _configService.GetByIdAsync(id);
                if (source == null) return Json(new { success = false, message = "Källkonfigurationen hittades inte." });

                // Must be able to view source in order to copy it.
                if (!await _configService.CanViewAsync(source, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att kopiera denna konfiguration." });

                var (success, message, created) = await _configService.DuplicateAsync(id, viewerId.Value, newName);
                if (!success || created == null) return Json(new { success = false, message });
                return Json(new { success = true, configurationId = created.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error duplicating Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Collaborators ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCollaborator([FromBody] AddCollaboratorRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.ConfigId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra delning." });

                var (success, message) = await _configService.AddCollaboratorAsync(request.ConfigId, request.MemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding collaborator to config {Id}", request.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveCollaborator([FromBody] RemoveCollaboratorRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.ConfigId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra delning." });

                var (success, message) = await _configService.RemoveCollaboratorAsync(request.ConfigId, request.MemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing collaborator from config {Id}", request.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }
    }
}
