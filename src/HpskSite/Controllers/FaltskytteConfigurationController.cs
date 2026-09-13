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
using Umbraco.Extensions;

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
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly ILogger<FaltskytteConfigurationController> _logger;

        public FaltskytteConfigurationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FaltskytteConfigurationService configService,
            IMemberService memberService,
            ClubService clubService,
            ILogger<FaltskytteConfigurationController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _configService = configService;
            _memberService = memberService;
            _clubService = clubService;
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
                var viewerCanApprove = await _configService.CanApproveAsync(viewerId);
                return Json(new { success = true, configurations = views, viewerCanApprove });
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

        // ── Approval workflow ───────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestApproval([FromBody] RequestApprovalRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.RequestApprovalAsync(
                    request.ConfigId, viewerId.Value, request.RequestedApproverMemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting approval for config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Returns every active Banläggare cert holder (name + club) for the request-approval picker.</summary>
        [HttpGet]
        public async Task<IActionResult> GetBanlaggareCandidates()
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var candidates = await _configService.GetBanlaggareCandidatesAsync();
                return Json(new { success = true, candidates });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Banläggare candidates");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve([FromBody] ApprovalActionRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.ApproveAsync(request.ConfigId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unapprove([FromBody] ApprovalActionRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.UnapproveAsync(request.ConfigId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unapproving config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Member search (for the collaborator picker) ─────────────────

        /// <summary>
        /// Member search restricted to any logged-in user. Returns up to 20 matches.
        /// Distinct from TrainingGroupController's SearchMembers (which is admin-gated)
        /// because any user can need to add a collaborator to their own configuration.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                    return Json(new { success = true, members = new List<object>() });

                var all = _memberService.GetAll(0, int.MaxValue, out _);
                var matches = all
                    .Where(m => m.IsApproved
                        && ((m.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                            || (m.Email ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(20)
                    .ToList();

                var members = matches.Select(m =>
                {
                    string? clubName = null;
                    var pcid = m.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(pcid) && int.TryParse(pcid, out int clubId))
                        clubName = _clubService.GetClubNameById(clubId);

                    var first = m.GetValue<string>("firstName");
                    var last = m.GetValue<string>("lastName");
                    var displayName = string.IsNullOrWhiteSpace($"{first} {last}".Trim()) ? m.Name : $"{first} {last}".Trim();

                    return new
                    {
                        memberId = m.Id,
                        memberName = displayName,
                        clubName
                    };
                }).ToList();

                return Json(new { success = true, members });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching members for collaborator picker");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Station images ───────────────────────────────────────────────
        // The configurator's photo buttons used to post to
        // Faltskytte/UploadTargetGroupImage, which authorizes on a competition
        // id. A standalone configuration has no competition, so every upload
        // came back success=false and the picture silently never appeared.
        // These two endpoints cover the configurator's other two hosts:
        // a saved configuration (edit rights decide) and the competition
        // wizard, where nothing is saved yet and the image is parked in the
        // uploading member's own draft folder.

        /// <summary>Stores a station/figure photo for a standalone configuration.</summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadStationImage(IFormFile file, int configurationId, string weaponClass, int stationNumber, int groupNumber)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(configurationId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra denna konfiguration." });

                return SaveStationImage(file, Path.Combine("faltkonfig", configurationId.ToString()),
                    weaponClass, stationNumber, groupNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading station image for configuration {Id}", configurationId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Stores a station/figure photo while the competition wizard is still
        /// running, i.e. before there is a competition or configuration to hang
        /// it on. Scoped to the uploading member so two users drafting at the
        /// same time cannot overwrite each other.
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadDraftStationImage(IFormFile file, string weaponClass, int stationNumber, int groupNumber)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                return SaveStationImage(file, Path.Combine("faltkonfig", "utkast", viewerId.Value.ToString()),
                    weaponClass, stationNumber, groupNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading draft station image");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Shared validation + write for the two upload endpoints above.
        /// <paramref name="relativeDir"/> is the folder under wwwroot/images.
        /// Callers must have checked authorization first.
        /// </summary>
        private IActionResult SaveStationImage(IFormFile file, string relativeDir, string weaponClass, int stationNumber, int groupNumber)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Ingen fil vald." });

            if (file.Length > 5 * 1024 * 1024)
                return Json(new { success = false, message = "Filen är för stor (max 5 MB)." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                return Json(new { success = false, message = "Endast JPG, PNG eller WebP." });

            var wc = SanitizeForFileName(weaponClass);
            var fileName = $"st{stationNumber}_{wc}_tg{groupNumber}{ext}";

            var fullDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", relativeDir);
            Directory.CreateDirectory(fullDir);

            using (var stream = new FileStream(Path.Combine(fullDir, fileName), FileMode.Create))
            {
                file.CopyTo(stream);
            }

            var imageUrl = "/images/" + relativeDir.Replace('\\', '/') + "/" + fileName;
            _logger.LogInformation("Uploaded station image: {Url}", imageUrl);
            return Json(new { success = true, imageUrl });
        }

        /// <summary>Keeps a client-supplied weapon class from escaping the image folder.</summary>
        private static string SanitizeForFileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "x";
            var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
            return cleaned.Length == 0 ? "x" : cleaned;
        }
    }
}
