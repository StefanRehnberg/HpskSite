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
    /// API surface for Fältskytte "Projekt" — lightweight containers that group
    /// standalone configurations. All endpoints require a logged-in member;
    /// view/edit checks flow through FaltskytteProjectService.
    /// (Member search for the member-picker reuses FaltskytteConfiguration/SearchMembers.)
    /// </summary>
    public class FaltskytteProjectController : SurfaceController
    {
        private readonly FaltskytteProjectService _projectService;
        private readonly FaltskytteConfigurationService _configService;
        private readonly ILogger<FaltskytteProjectController> _logger;

        public FaltskytteProjectController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FaltskytteProjectService projectService,
            FaltskytteConfigurationService configService,
            ILogger<FaltskytteProjectController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _projectService = projectService;
            _configService = configService;
            _logger = logger;
        }

        // ── Reads ────────────────────────────────────────────────────────

        /// <summary>Returns every project the current user can see (owned + member-on; all for site admins).</summary>
        [HttpGet]
        public async Task<IActionResult> ListAccessible()
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var projects = await _projectService.ListAccessibleAsync(viewerId.Value);
                var views = new List<FaltskytteProjectView>();
                foreach (var p in projects)
                    views.Add(await _projectService.BuildViewAsync(p, viewerId));
                return Json(new { success = true, projects = views });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing Fältskytte projects");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Writes ───────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromBody] CreateFaltskytteProjectRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var (success, message, created) = await _projectService.CreateAsync(request, viewerId.Value);
                if (!success || created == null) return Json(new { success = false, message });
                return Json(new { success = true, projectId = created.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Fältskytte project");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update([FromBody] UpdateFaltskytteProjectRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.UpdateAsync(request, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Fältskytte project {Id}", request?.Id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.DeleteAsync(id, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Fältskytte project {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Archive([FromBody] ProjectStatusRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.SetStatusAsync(
                    request.ProjectId, FaltskytteProjectService.StatusArchived, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error archiving Fältskytte project {Id}", request?.ProjectId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unarchive([FromBody] ProjectStatusRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.SetStatusAsync(
                    request.ProjectId, FaltskytteProjectService.StatusActive, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unarchiving Fältskytte project {Id}", request?.ProjectId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Members ──────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember([FromBody] ProjectMemberRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.AddMemberAsync(
                    request.ProjectId, request.MemberId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding member to project {Id}", request?.ProjectId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMember([FromBody] ProjectMemberRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _projectService.RemoveMemberAsync(
                    request.ProjectId, request.MemberId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing member from project {Id}", request?.ProjectId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Config assignment ──────────────────────────────────────────────

        /// <summary>Moves a configuration into a project, or clears it (projectId null).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignConfig([FromBody] AssignConfigToProjectRequest request)
        {
            try
            {
                var viewerId = await _projectService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.AssignToProjectAsync(
                    request.ConfigId, request.ProjectId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning config {ConfigId} to project {ProjectId}",
                    request?.ConfigId, request?.ProjectId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }
    }
}
