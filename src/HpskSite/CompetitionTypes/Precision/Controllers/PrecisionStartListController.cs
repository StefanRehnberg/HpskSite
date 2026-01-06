using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class PrecisionStartListController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly ILogger<PrecisionStartListController> _logger;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IStartListService _startListService;
        private readonly StartListRequestValidator _validator;
        private readonly UmbracoStartListRepository _repository;
        private readonly StartListGenerator _generator;
        private readonly StartListHtmlRenderer _renderer;
        private readonly ClubService _clubService;

        public PrecisionStartListController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            IContentTypeService contentTypeService,
            ILogger<PrecisionStartListController> logger,
            IStartListService startListService,
            StartListRequestValidator validator,
            UmbracoStartListRepository repository,
            StartListGenerator generator,
            StartListHtmlRenderer renderer,
            ClubService clubService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _logger = logger;
            _databaseFactory = databaseFactory;
            _startListService = startListService;
            _validator = validator;
            _repository = repository;
            _generator = generator;
            _renderer = renderer;
            _clubService = clubService;
        }

        [HttpGet]
        public async Task<IActionResult> PreviewStartList([FromQuery] StartListGenerationRequest request, [FromQuery] int? startListId = null)
        {
            try
            {
                if (request.CompetitionId <= 0)
                {
                    return Content("Invalid competition ID", "text/plain");
                }

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Content("Competition not found", "text/plain");
                }

                // If startListId is provided, try to redirect to the actual start list page
                if (startListId.HasValue && startListId.Value > 0)
                {
                    var savedStartList = _contentService.GetById(startListId.Value);
                    if (savedStartList != null)
                    {
                        // Try to get the published URL of the start list
                        if (UmbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
                        {
                            var publishedStartList = umbracoContext.Content?.GetById(savedStartList.Id);
                            if (publishedStartList != null)
                            {
                                var startListUrl = publishedStartList.Url();
                                return Redirect(startListUrl);
                            }
                        }
                        
                        _logger.LogWarning("Could not get published URL for start list {StartListId}, falling back to manual HTML generation", startListId.Value);
                    }
                }

                // Get current user info for highlighting
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var currentMemberName = currentMember?.Name ?? "";
                var currentMemberClub = "";
                
                if (currentMember != null)
                {
                    var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                    if (memberData != null)
                    {
                        var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                        {
                            currentMemberClub = _clubService.GetClubNameById(primaryClubId) ?? "";
                        }
                    }
                }

                string htmlContent;

                // If startListId is provided, try to get saved content first
                if (startListId.HasValue && startListId.Value > 0)
                {
                    var savedStartList = _contentService.GetById(startListId.Value);
                    if (savedStartList != null)
                    {
                        var savedContent = savedStartList.GetValue<string>("startListContent");
                        if (!string.IsNullOrEmpty(savedContent))
                        {
                            // Build redesigned HTML structure using StringBuilder
                            var sb = new StringBuilder();
                            sb.AppendLine("<!DOCTYPE html>");
                            sb.AppendLine("<html>");
                            sb.AppendLine("<head>");
                            sb.AppendLine($"<title>Startlista - {competition.Name}</title>");
                            sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">");
                            sb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.0/font/bootstrap-icons.css\" rel=\"stylesheet\">");
                            sb.AppendLine("<style>");
                            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; background-color: #f8f9fa; }");
                            sb.AppendLine(".start-list-content h1:first-child, .start-list-content h2:first-child, .start-list-content h3:first-child, .start-list-content .competition-title { font-size: 1.1rem !important; font-weight: 600 !important; margin-bottom: 0.5rem !important; color: #333 !important; }");
                            sb.AppendLine(".start-list-content p:nth-child(2), .start-list-content p:nth-child(3) { display: none !important; }");
                            sb.AppendLine(".start-list-content { font-size: 0.9rem; }");
                            sb.AppendLine(".start-list-content table { font-size: 0.85rem; width: 100%; border-collapse: collapse; margin: 20px 0; }");
                            sb.AppendLine(".start-list-content table th, .start-list-content table td { padding: 4px 8px !important; line-height: 1.2 !important; border: 1px solid #ddd !important; text-align: left; }");
                            sb.AppendLine(".start-list-content table th { background-color: #f5f5f5 !important; font-weight: 600 !important; font-size: 0.8rem !important; }");
                            sb.AppendLine(".start-list-content table td { font-size: 0.8rem !important; }");
                            sb.AppendLine(".start-list-content table tbody tr { height: auto !important; min-height: 28px !important; }");
            sb.AppendLine(".current-user { background-color: #d4edda !important; }"); // Green for current user
            sb.AppendLine(".same-club { background-color: #e8f5e8 !important; }"); // Light green for same club
            sb.AppendLine(".start-list-content table tbody tr:nth-child(even) { background-color: transparent !important; }"); // Disable alternating rows
            sb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(odd) { background-color: transparent !important; }"); // Override Bootstrap striped
            sb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(even) { background-color: transparent !important; }"); // Override Bootstrap striped
                            sb.AppendLine(".card { border: 1px solid #dee2e6; border-radius: 0.375rem; box-shadow: 0 0.125rem 0.25rem rgba(0, 0, 0, 0.075); }");
                            sb.AppendLine(".card-header { background-color: #f8f9fa; border-bottom: 1px solid #dee2e6; padding: 0.75rem 1rem; }");
                            sb.AppendLine("</style>");
                            sb.AppendLine("</head>");
                            sb.AppendLine("<body>");
                            sb.AppendLine("<div class=\"container-fluid\">");
                            sb.AppendLine("<div class=\"card\">");
                            sb.AppendLine("<div class=\"card-body\">");
                            sb.AppendLine("<div class=\"start-list-content\">");
                            sb.AppendLine(savedContent);
                            sb.AppendLine("</div>");
                            sb.AppendLine("</div>");
                            sb.AppendLine("</div>");
                            sb.AppendLine("</div>");
                            sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js\"></script>");
                            
                            // Add user highlighting script
                            if (currentMember != null)
                            {
                                var currentMemberForJs = currentMemberName.Replace("'", "\\'");
                                var currentClubForJs = currentMemberClub.Replace("'", "\\'");
                                
                                sb.AppendLine("<script>");
                                sb.AppendLine("document.addEventListener('DOMContentLoaded', function() {");
                                sb.AppendLine($"    const currentUserName = '{currentMemberForJs}';");
                                sb.AppendLine($"    const currentUserClub = '{currentClubForJs}';");
                                sb.AppendLine("    ");
                                sb.AppendLine("    // Apply highlighting to existing table rows");
                                sb.AppendLine("    const tables = document.querySelectorAll('.start-list-content table tbody');");
                                sb.AppendLine("    tables.forEach(tbody => {");
                                sb.AppendLine("        const rows = tbody.querySelectorAll('tr');");
                                sb.AppendLine("        rows.forEach(row => {");
                                sb.AppendLine("            const cells = row.querySelectorAll('td');");
                                sb.AppendLine("            if (cells.length >= 3) {");
                                sb.AppendLine("                const nameCell = cells[1].textContent.trim();");
                                sb.AppendLine("                const clubCell = cells[2].textContent.trim();");
                                sb.AppendLine("                ");
                                sb.AppendLine("                if (currentUserName && nameCell === currentUserName) {");
                                sb.AppendLine("                    row.classList.add('current-user');");
                                sb.AppendLine("                } else if (currentUserClub && clubCell === currentUserClub) {");
                                sb.AppendLine("                    row.classList.add('same-club');");
                                sb.AppendLine("                }");
                                sb.AppendLine("            }");
                                sb.AppendLine("        });");
                                sb.AppendLine("    });");
                                sb.AppendLine("});");
                                sb.AppendLine("</script>");
                            }
                            
                            sb.AppendLine("</body>");
                            sb.AppendLine("</html>");
                            
                            htmlContent = sb.ToString();

                            return Content(htmlContent, "text/html");
                        }
                    }
                }

                // Generate new content if no saved content found
                var registrations = await _repository.GetCompetitionRegistrations(request.CompetitionId);
                var startListData = _generator.GenerateStartListData(registrations, request);

                var generatedContent = await _renderer.GenerateStartListHtml(startListData, competition.Name ?? "");

                // Build redesigned HTML structure using StringBuilder for fallback
                var fallbackSb = new StringBuilder();
                fallbackSb.AppendLine("<!DOCTYPE html>");
                fallbackSb.AppendLine("<html>");
                fallbackSb.AppendLine("<head>");
                fallbackSb.AppendLine($"<title>Startlista - {competition.Name}</title>");
                fallbackSb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css\" rel=\"stylesheet\">");
                fallbackSb.AppendLine("<link href=\"https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.0/font/bootstrap-icons.css\" rel=\"stylesheet\">");
                fallbackSb.AppendLine("<style>");
                fallbackSb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; background-color: #f8f9fa; }");
                fallbackSb.AppendLine(".start-list-content h1:first-child, .start-list-content h2:first-child, .start-list-content h3:first-child, .start-list-content .competition-title { font-size: 1.1rem !important; font-weight: 600 !important; margin-bottom: 0.5rem !important; color: #333 !important; }");
                fallbackSb.AppendLine(".start-list-content p:nth-child(2), .start-list-content p:nth-child(3) { display: none !important; }");
                fallbackSb.AppendLine(".start-list-content { font-size: 0.9rem; }");
                fallbackSb.AppendLine(".start-list-content table { font-size: 0.85rem; width: 100%; border-collapse: collapse; margin: 20px 0; }");
                fallbackSb.AppendLine(".start-list-content table th, .start-list-content table td { padding: 4px 8px !important; line-height: 1.2 !important; border: 1px solid #ddd !important; text-align: left; }");
                fallbackSb.AppendLine(".start-list-content table th { background-color: #f5f5f5 !important; font-weight: 600 !important; font-size: 0.8rem !important; }");
                fallbackSb.AppendLine(".start-list-content table td { font-size: 0.8rem !important; }");
                fallbackSb.AppendLine(".start-list-content table tbody tr { height: auto !important; min-height: 28px !important; }");
                fallbackSb.AppendLine(".current-user { background-color: #d4edda !important; }"); // Green for current user
                fallbackSb.AppendLine(".same-club { background-color: #e8f5e8 !important; }"); // Light green for same club
                fallbackSb.AppendLine(".start-list-content table tbody tr:nth-child(even) { background-color: transparent !important; }"); // Disable alternating rows
                fallbackSb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(odd) { background-color: transparent !important; }"); // Override Bootstrap striped
                fallbackSb.AppendLine(".start-list-content .table-striped tbody tr:nth-child(even) { background-color: transparent !important; }"); // Override Bootstrap striped
                fallbackSb.AppendLine(".card { border: 1px solid #dee2e6; border-radius: 0.375rem; box-shadow: 0 0.125rem 0.25rem rgba(0, 0, 0, 0.075); }");
                fallbackSb.AppendLine(".card-header { background-color: #f8f9fa; border-bottom: 1px solid #dee2e6; padding: 0.75rem 1rem; }");
                fallbackSb.AppendLine("</style>");
                fallbackSb.AppendLine("</head>");
                fallbackSb.AppendLine("<body>");
                fallbackSb.AppendLine("<div class=\"container-fluid\">");
                fallbackSb.AppendLine("<div class=\"card\">");
                fallbackSb.AppendLine("<div class=\"card-body\">");
                fallbackSb.AppendLine("<div class=\"start-list-content\">");
                fallbackSb.AppendLine(generatedContent);
                fallbackSb.AppendLine("</div>");
                fallbackSb.AppendLine("</div>");
                fallbackSb.AppendLine("</div>");
                fallbackSb.AppendLine("</div>");
                fallbackSb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js\"></script>");
                
                // Add user highlighting script for fallback too
                if (currentMember != null)
                {
                    var currentMemberForJs = currentMemberName.Replace("'", "\\'");
                    var currentClubForJs = currentMemberClub.Replace("'", "\\'");
                    
                    fallbackSb.AppendLine("<script>");
                    fallbackSb.AppendLine("document.addEventListener('DOMContentLoaded', function() {");
                    fallbackSb.AppendLine($"    const currentUserName = '{currentMemberForJs}';");
                    fallbackSb.AppendLine($"    const currentUserClub = '{currentClubForJs}';");
                    fallbackSb.AppendLine("    ");
                    fallbackSb.AppendLine("    // Apply highlighting to existing table rows");
                    fallbackSb.AppendLine("    const tables = document.querySelectorAll('.start-list-content table tbody');");
                    fallbackSb.AppendLine("    tables.forEach(tbody => {");
                    fallbackSb.AppendLine("        const rows = tbody.querySelectorAll('tr');");
                    fallbackSb.AppendLine("        rows.forEach(row => {");
                    fallbackSb.AppendLine("            const cells = row.querySelectorAll('td');");
                    fallbackSb.AppendLine("            if (cells.length >= 3) {");
                    fallbackSb.AppendLine("                const nameCell = cells[1].textContent.trim();");
                    fallbackSb.AppendLine("                const clubCell = cells[2].textContent.trim();");
                    fallbackSb.AppendLine("                ");
                    fallbackSb.AppendLine("                if (currentUserName && nameCell === currentUserName) {");
                    fallbackSb.AppendLine("                    row.classList.add('current-user');");
                    fallbackSb.AppendLine("                } else if (currentUserClub && clubCell === currentUserClub) {");
                    fallbackSb.AppendLine("                    row.classList.add('same-club');");
                    fallbackSb.AppendLine("                }");
                    fallbackSb.AppendLine("            }");
                    fallbackSb.AppendLine("        });");
                    fallbackSb.AppendLine("    });");
                    fallbackSb.AppendLine("});");
                    fallbackSb.AppendLine("</script>");
                }
                
                fallbackSb.AppendLine("</body>");
                fallbackSb.AppendLine("</html>");
                
                htmlContent = fallbackSb.ToString();

                return Content(htmlContent, "text/html");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating start list preview");
                return Content($"Error: {ex.Message}", "text/plain");
            }
        }


        [HttpGet]
        public async Task<IActionResult> GetStartLists(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                // Get all start lists for this competition
                var startLists = _repository.GetStartListsForCompetition(competitionId);

                var response = new
                {
                    Success = true,
                    StartLists = startLists.Select(sl => new
                    {
                        Id = sl.Id,
                        GeneratedDate = sl.GetValue<DateTime>("generatedDate"),
                        TeamFormatDisplay = _renderer.GetTeamFormatDisplay(sl.GetValue<string>("teamFormat") ?? ""),
                        TeamCount = _repository.GetTeamCountFromContent(sl),
                        TotalShooters = _repository.GetTotalShootersFromContent(sl),
                        Status = sl.GetValue<bool>("isOfficialStartList") ? "Official" : "",
                        Url = GetStartListDisplayUrl(sl, competitionId)
                    }).ToList()
                };

                return Json(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting start lists for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid hämtning av startlistor." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetOfficialStartList(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                // Get all start lists for this competition (most recent first)
                var allStartLists = _repository.GetStartListsForCompetition(competitionId);

                // FILTER OUT CORRUPTED START LISTS (where HTML generation wasn't awaited)
                var validStartLists = allStartLists.Where(sl =>
                {
                    var content = sl.GetValue<string>("startListContent");
                    return !string.IsNullOrEmpty(content) && !content.Contains("System.Threading.Tasks.Task");
                }).ToList();

                if (!validStartLists.Any())
                {
                    _logger.LogWarning("No valid start lists found for competition {CompetitionId}. Total lists: {Total}, Corrupted: {Corrupted}",
                        competitionId, allStartLists.Count(), allStartLists.Count() - validStartLists.Count);
                    return Json(new { Success = false, Message = "Ingen giltig startlista finns för denna tävling." });
                }

                // Get THE ONE current start list (most recent valid one)
                var currentStartList = validStartLists.OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate")).First();

                // Return UI-friendly format
                var response = new
                {
                    Success = true,
                    StartList = new
                    {
                        Id = currentStartList.Id,
                        GeneratedDate = currentStartList.GetValue<DateTime>("generatedDate"),
                        TeamFormatDisplay = _renderer.GetTeamFormatDisplay(currentStartList.GetValue<string>("teamFormat") ?? ""),
                        TeamCount = _repository.GetTeamCountFromContent(currentStartList),
                        TotalShooters = _repository.GetTotalShootersFromContent(currentStartList),
                        IsOfficial = currentStartList.GetValue<bool>("isOfficialStartList"),
                        Url = GetStartListDisplayUrl(currentStartList, competitionId)
                    }
                };

                return Json(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting official start list for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid hämtning av startlistan." });
            }
        }

        private string GetStartListDisplayUrl(IContent startList, int competitionId)
        {
            // Get the competition's published URL and append /startlista/
            // This provides the canonical URL regardless of where the start list content is actually stored
            if (UmbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
            {
                var publishedCompetition = umbracoContext.Content?.GetById(competitionId);
                if (publishedCompetition != null)
                {
                    var competitionUrl = publishedCompetition.Url();
                    return competitionUrl.TrimEnd('/') + "/startlista/";
                }
            }

            // Fallback to PreviewStartList action if we can't get the competition URL
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = $"/umbraco/surface/PrecisionStartList/PreviewStartList?competitionId={competitionId}&startListId={startList.Id}&t={timestamp}";
            return url;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteStartList([FromBody] DeleteStartListRequest request)
        {
            try
            {
                // Validate user authentication
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad för att ta bort startlistor." });
                }

                // Get the actual member data with integer ID
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata hittades inte." });
                }

                if (request.StartListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startlist-ID." });
                }

                // Get the start list content
                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlista hittades inte." });
                }

                // Check if user has permission to delete this start list
                var competitionId = startList.GetValue<int>("competitionId");
                if (competitionId > 0)
                {
                    var competition = _contentService.GetById(competitionId);
                    // TODO: Implement permission check
                    // if (competition == null || !await PrecisionCanManageCompetition(memberData.Id, competition.Id))
                    // {
                    //     return Json(new { success = false, message = "Du har inte behörighet att ta bort denna startlista." });
                    // }
                }

                // Delete the start list
                var deleteResult = _contentService.Delete(startList);
                if (deleteResult.Success)
                {
                    _logger.LogInformation("Deleted start list {StartListId} by user {UserId}", request.StartListId, memberData.Id);
                    return Json(new { success = true, message = "Startlistan har tagits bort." });
                }
                else
                {
                    var errorCount = deleteResult.EventMessages?.Count ?? 0;
                    _logger.LogError("Failed to delete start list {StartListId}. Error count: {ErrorCount}", 
                                   request.StartListId, errorCount);
                    return Json(new { success = false, message = "Ett fel uppstod vid borttagning av startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting start list {StartListId}", request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishStartList([FromBody] PublishStartListRequest request)
        {
            try
            {
                // Validate user authentication
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad för att hantera startlistor." });
                }

                // Get the actual member data with integer ID
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata hittades inte." });
                }

                if (request.StartListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startlist-ID." });
                }

                // Get the start list content
                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlista hittades inte." });
                }

                // Check if user has permission to manage this start list
                var competitionId = startList.GetValue<int>("competitionId");
                if (competitionId > 0)
                {
                    var competition = _contentService.GetById(competitionId);
                    if (competition == null || !await _validator.CanManageCompetition(memberData.Id, competition.Id))
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att hantera denna startlista." });
                    }

                    // First, unpublish all other start lists for this competition
                    await UnpublishAllStartListsForCompetition(competitionId);
                }

                // Set the start list as published
                startList.SetValue("isOfficialStartList", request.IsPublished);
                
                var saveResult = _contentService.Save(startList);
                if (saveResult.Success)
                {
                    _logger.LogInformation("{Action} start list {StartListId} by user {UserId}", 
                                         request.IsPublished ? "Published" : "Unpublished", request.StartListId, memberData.Id);
                    
                    var actionText = request.IsPublished ? "publicerad" : "avpublicerad";
                    return Json(new { success = true, message = $"Startlistan har {actionText}." });
                }
                else
                {
                    var errorCount = saveResult.EventMessages?.Count ?? 0;
                    _logger.LogError("Failed to {Action} start list {StartListId}. Error count: {ErrorCount}", 
                                   request.IsPublished ? "publish" : "unpublish", request.StartListId, errorCount);
                    return Json(new { success = false, message = "Ett fel uppstod vid statusändring." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error {Action} start list {StartListId}", 
                               request.IsPublished ? "publishing" : "unpublishing", request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        private async Task UnpublishAllStartListsForCompetition(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return;

                var children = _contentService.GetPagedChildren(competitionId, 0, 50, out _);
                var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };

                // NEW ARCHITECTURE: Look for start list as direct child of competition
                var directStartList = children.FirstOrDefault(c => possibleAliases.Contains(c.ContentType.Alias));
                if (directStartList != null)
                {
                    directStartList.SetValue("isOfficialStartList", false);
                    _contentService.Save(directStartList);
                    _logger.LogInformation("Unpublished direct start list {StartListId} for competition {CompetitionId}",
                        directStartList.Id, competitionId);
                    return;
                }

                // BACKWARD COMPATIBILITY: Check under hub during migration period
                var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                if (startListsHub == null) return;

                var startListChildren = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out _);
                var startLists = startListChildren
                    .Where(sl => possibleAliases.Contains(sl.ContentType.Alias))
                    .ToList();

                foreach (var startList in startLists)
                {
                    startList.SetValue("isOfficialStartList", false);
                    _contentService.Save(startList);
                }

                _logger.LogInformation("Unpublished {Count} start lists (legacy hub) for competition {CompetitionId}",
                    startLists.Count, competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unpublishing start lists for competition {CompetitionId}", competitionId);
            }
        }

        #region Start List Editor Endpoints (Phase 2 - 2025-11-24)

        /// <summary>
        /// Get full start list configuration for editing
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStartListForEditing(int startListId)
        {
            try
            {
                if (startListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startliste-ID." });
                }

                var startList = _contentService.GetById(startListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get configuration data
                var configData = startList.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configData))
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                var isOfficial = startList.GetValue<bool>("isOfficialStartList");

                return Json(new
                {
                    success = true,
                    startListId = startListId,
                    configuration = configuration,
                    isOfficial = isOfficial
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting start list {StartListId} for editing", startListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Search for registered shooters not yet in the start list
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchAvailableShooters(int startListId, string query)
        {
            try
            {
                if (startListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startliste-ID." });
                }

                if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                {
                    return Json(new { success = true, shooters = new object[0] });
                }

                // Get the start list to find the competition ID
                var startList = _contentService.GetById(startListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var competitionId = startList.GetValue<int>("competitionId");
                if (competitionId <= 0)
                {
                    return Json(new { success = false, message = "Tävlings-ID saknas i startlistan." });
                }

                // Get configuration to find shooters already in start list
                var configData = startList.GetValue<string>("configurationData");
                var existingMemberIds = new HashSet<int>();

                if (!string.IsNullOrEmpty(configData))
                {
                    var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                    if (configuration?.Teams != null)
                    {
                        foreach (var team in configuration.Teams)
                        {
                            if (team.Shooters != null)
                            {
                                foreach (var shooter in team.Shooters)
                                {
                                    existingMemberIds.Add(shooter.MemberId);
                                }
                            }
                        }
                    }
                }

                // Get all registrations for this competition
                var registrations = await _repository.GetCompetitionRegistrations(competitionId);

                // Filter: not already in start list AND matches search query
                var queryLower = query.ToLowerInvariant();
                var availableShooters = registrations
                    .Where(r => !existingMemberIds.Contains(r.MemberId))
                    .Where(r => r.MemberName.ToLowerInvariant().Contains(queryLower))
                    .Select(r => new
                    {
                        memberId = r.MemberId,
                        name = r.MemberName,
                        club = r.MemberClub ?? "Okänd klubb",
                        shootingClass = r.MemberClass
                    })
                    .Take(20) // Limit results
                    .ToList();

                return Json(new { success = true, shooters = availableShooters });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching available shooters for start list {StartListId}", startListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod vid sökning." });
            }
        }

        /// <summary>
        /// Repair club data in start list (fills in missing clubs from registrations)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RepairClubData([FromBody] RepairClubDataRequest request)
        {
            try
            {
                if (request.StartListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startliste-ID." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var configData = startList.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configData))
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                {
                    return Json(new { success = false, message = "Ingen lagdata hittades." });
                }

                var competitionId = startList.GetValue<int>("competitionId");
                var registrations = await _repository.GetCompetitionRegistrations(competitionId);
                var regDict = registrations.ToDictionary(r => r.MemberId);

                int updatedCount = 0;

                foreach (var team in configuration.Teams)
                {
                    if (team.Shooters != null)
                    {
                        foreach (var shooter in team.Shooters)
                        {
                            // Check if club is missing or unknown
                            if (string.IsNullOrWhiteSpace(shooter.Club) ||
                                shooter.Club == "Okänd klubb" ||
                                shooter.Club == "Unknown Club")
                            {
                                // Try to get club from registration
                                if (regDict.TryGetValue(shooter.MemberId, out var reg) &&
                                    !string.IsNullOrWhiteSpace(reg.MemberClub) &&
                                    reg.MemberClub != "Okänd klubb")
                                {
                                    shooter.Club = reg.MemberClub;
                                    updatedCount++;
                                }
                                else
                                {
                                    // Try member lookup as fallback
                                    var memberClub = _repository.GetMemberClub(shooter.MemberId);
                                    if (!string.IsNullOrWhiteSpace(memberClub) && memberClub != "Okänd klubb")
                                    {
                                        shooter.Club = memberClub;
                                        updatedCount++;
                                    }
                                }
                            }
                        }
                    }
                }

                if (updatedCount > 0)
                {
                    // Save updated configuration
                    var configJson = JsonConvert.SerializeObject(configuration);
                    startList.SetValue("configurationData", configJson);

                    // Regenerate HTML content
                    var competition = _contentService.GetById(competitionId);
                    var competitionName = competition?.Name ?? "Okänd tävling";
                    var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                    startList.SetValue("startListContent", htmlContent);

                    var result = _contentService.Save(startList);
                    if (!result.Success)
                    {
                        return Json(new { success = false, message = "Kunde inte spara uppdateringarna." });
                    }

                    _contentService.Publish(startList, Array.Empty<string>());
                }

                return Json(new {
                    success = true,
                    message = updatedCount > 0
                        ? $"Uppdaterade klubbinfo för {updatedCount} skyttar."
                        : "Ingen klubbinfo behövde uppdateras - alla skyttar har redan klubbinfo."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error repairing club data for start list {StartListId}", request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Update entire start list configuration
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStartList([FromBody] UpdateStartListRequest request)
        {
            try
            {
                if (request.StartListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startliste-ID." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get competition name
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                // Serialize configuration
                var configJson = JsonConvert.SerializeObject(request.Configuration);
                startList.SetValue("configurationData", configJson);

                // Regenerate HTML content
                var htmlContent = await _renderer.GenerateStartListHtml(request.Configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                // Save
                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = "Startlistan har uppdaterats." });
                }
                else
                {
                    _logger.LogError("Failed to save start list {StartListId}", request.StartListId);
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating start list {StartListId}", request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Add a shooter to a specific team in the start list
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddShooterToStartList([FromBody] AddShooterToStartListRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.TeamNumber <= 0 || request.MemberId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get configuration
                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration == null || configuration.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                // Check if shooter already exists
                var existingShooter = configuration.Teams
                    .SelectMany(t => t.Shooters ?? new List<StartListShooter>())
                    .FirstOrDefault(s => s.MemberId == request.MemberId);

                if (existingShooter != null)
                {
                    return Json(new { success = false, message = "Skyttan finns redan i startlistan." });
                }

                // Get member info
                var member = _memberService.GetById(request.MemberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlemmen kunde inte hittas." });
                }

                // Get club name
                var primaryClubId = member.GetValue<int>("primaryClubId");
                var clubName = _clubService.GetClubNameById(primaryClubId) ?? "Okänd klubb";

                // Find team
                var team = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                if (team == null)
                {
                    return Json(new { success = false, message = "Laget kunde inte hittas." });
                }

                // Create shooter
                var newShooter = new StartListShooter
                {
                    Position = (team.Shooters?.Count ?? 0) + 1,
                    Name = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}",
                    Club = clubName,
                    WeaponClass = request.WeaponClass,
                    MemberId = request.MemberId
                };

                // Add shooter
                if (team.Shooters == null) team.Shooters = new List<StartListShooter>();
                team.Shooters.Add(newShooter);
                team.ShooterCount = team.Shooters.Count;

                // Update weapon classes
                if (!team.WeaponClasses.Contains(request.WeaponClass))
                {
                    team.WeaponClasses.Add(request.WeaponClass);
                    team.WeaponClasses = team.WeaponClasses.OrderBy(c => c).ToList();
                }

                // Get competition name
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                // Save configuration
                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                // Regenerate HTML
                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new
                    {
                        success = true,
                        message = "Skyttan har lagts till.",
                        shooter = newShooter
                    });
                }
                else
                {
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding shooter {MemberId} to start list {StartListId}", request.MemberId, request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Remove a shooter from the start list
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveShooterFromStartList([FromBody] RemoveShooterFromStartListRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.MemberId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get competition info (needed for both results check and HTML generation)
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                // Get configuration
                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration == null || configuration.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                // Find and remove shooter
                bool shooterFound = false;
                foreach (var team in configuration.Teams)
                {
                    if (team.Shooters == null) continue;

                    var shooter = team.Shooters.FirstOrDefault(s => s.MemberId == request.MemberId);
                    if (shooter != null)
                    {
                        // CRITICAL: Check if shooter has results in database
                        using (var db = _databaseFactory.CreateDatabase())
                        {
                            var hasResults = db.ExecuteScalar<int>(
                                "SELECT COUNT(*) FROM PrecisionResultEntry WHERE CompetitionId = @0 AND MemberId = @1",
                                competitionId, request.MemberId) > 0;

                            if (hasResults)
                            {
                                return Json(new
                                {
                                    success = false,
                                    message = "Kan inte ta bort skyttan eftersom resultat redan har registrerats för denna skytt."
                                });
                            }
                        }

                        team.Shooters.Remove(shooter);
                        shooterFound = true;

                        // Reposition remaining shooters
                        for (int i = 0; i < team.Shooters.Count; i++)
                        {
                            team.Shooters[i].Position = i + 1;
                        }

                        team.ShooterCount = team.Shooters.Count;

                        // Update weapon classes (only if no other shooter has this class)
                        var weaponClasses = team.Shooters.Select(s => s.WeaponClass).Distinct().OrderBy(c => c).ToList();
                        team.WeaponClasses = weaponClasses;

                        break;
                    }
                }

                if (!shooterFound)
                {
                    return Json(new { success = false, message = "Skyttan kunde inte hittas i startlistan." });
                }

                // Save configuration
                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                // Regenerate HTML
                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = "Skyttan har tagits bort." });
                }
                else
                {
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing shooter {MemberId} from start list {StartListId}", request.MemberId, request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Create a new team with manual start/end times
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateNewTeam([FromBody] CreateNewTeamRequest request)
        {
            try
            {
                if (request.StartListId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt startliste-ID." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get configuration
                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                if (configuration.Teams == null)
                {
                    configuration.Teams = new List<StartListTeam>();
                }

                // Determine new team number
                var nextTeamNumber = configuration.Teams.Any() ? configuration.Teams.Max(t => t.TeamNumber) + 1 : 1;

                // Create new team
                var newTeam = new StartListTeam
                {
                    TeamNumber = nextTeamNumber,
                    StartTime = request.StartTime,
                    EndTime = request.EndTime,
                    WeaponClasses = new List<string>(),
                    ShooterCount = 0,
                    Shooters = new List<StartListShooter>()
                };

                configuration.Teams.Add(newTeam);

                // Get competition name
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                // Save configuration
                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                // Regenerate HTML
                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new
                    {
                        success = true,
                        message = "Nytt lag har skapats.",
                        team = newTeam
                    });
                }
                else
                {
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new team in start list {StartListId}", request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Update start and end times for a specific team
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTeamTimes([FromBody] UpdateTeamTimesRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.TeamNumber <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                // Get configuration
                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration == null || configuration.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                // Find team
                var team = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                if (team == null)
                {
                    return Json(new { success = false, message = "Laget kunde inte hittas." });
                }

                // Update times
                team.StartTime = request.StartTime;
                team.EndTime = request.EndTime;

                // Get competition name
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                // Save configuration
                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                // Regenerate HTML
                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = "Lagets tider har uppdaterats." });
                }
                else
                {
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating team {TeamNumber} times in start list {StartListId}", request.TeamNumber, request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Move a shooter to a different team
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveShooterToTeam([FromBody] MoveShooterRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.MemberId <= 0 || request.TargetTeamNumber <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                // Find the shooter in any team
                StartListShooter? shooter = null;
                StartListTeam? sourceTeam = null;
                foreach (var team in configuration.Teams)
                {
                    shooter = team.Shooters?.FirstOrDefault(s => s.MemberId == request.MemberId);
                    if (shooter != null)
                    {
                        sourceTeam = team;
                        break;
                    }
                }

                if (shooter == null || sourceTeam == null)
                {
                    return Json(new { success = false, message = "Skyttan kunde inte hittas i startlistan." });
                }

                // Find target team
                var targetTeam = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TargetTeamNumber);
                if (targetTeam == null)
                {
                    return Json(new { success = false, message = "Mållaget kunde inte hittas." });
                }

                // Don't move to same team
                if (sourceTeam.TeamNumber == targetTeam.TeamNumber)
                {
                    return Json(new { success = false, message = "Skyttan är redan i detta lag." });
                }

                // Remove from source team
                sourceTeam.Shooters?.Remove(shooter);
                sourceTeam.ShooterCount = sourceTeam.Shooters?.Count ?? 0;

                // Reorder source team positions
                int pos = 1;
                foreach (var s in sourceTeam.Shooters ?? new List<StartListShooter>())
                {
                    s.Position = pos++;
                }

                // Update source team weapon classes
                sourceTeam.WeaponClasses = sourceTeam.Shooters?
                    .Select(s => s.WeaponClass)
                    .Distinct()
                    .ToList() ?? new List<string>();

                // Add to target team
                if (targetTeam.Shooters == null)
                {
                    targetTeam.Shooters = new List<StartListShooter>();
                }
                shooter.Position = targetTeam.Shooters.Count + 1;
                targetTeam.Shooters.Add(shooter);
                targetTeam.ShooterCount = targetTeam.Shooters.Count;

                // Update target team weapon classes
                targetTeam.WeaponClasses = targetTeam.Shooters
                    .Select(s => s.WeaponClass)
                    .Distinct()
                    .ToList();

                // Save and regenerate
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = $"Skyttan har flyttats till Lag {request.TargetTeamNumber}." });
                }
                return Json(new { success = false, message = "Kunde inte spara startlistan." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving shooter {MemberId} to team {TeamNumber}", request.MemberId, request.TargetTeamNumber);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Move multiple shooters to a different team
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMoveShooters([FromBody] BulkMoveShootersRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.MemberIds == null || !request.MemberIds.Any() || request.TargetTeamNumber <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                var targetTeam = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TargetTeamNumber);
                if (targetTeam == null)
                {
                    return Json(new { success = false, message = "Mållaget kunde inte hittas." });
                }

                if (targetTeam.Shooters == null)
                {
                    targetTeam.Shooters = new List<StartListShooter>();
                }

                int movedCount = 0;
                var affectedTeams = new HashSet<int>();

                foreach (var memberId in request.MemberIds)
                {
                    // Find shooter
                    StartListShooter? shooter = null;
                    StartListTeam? sourceTeam = null;
                    foreach (var team in configuration.Teams)
                    {
                        shooter = team.Shooters?.FirstOrDefault(s => s.MemberId == memberId);
                        if (shooter != null)
                        {
                            sourceTeam = team;
                            break;
                        }
                    }

                    if (shooter == null || sourceTeam == null || sourceTeam.TeamNumber == targetTeam.TeamNumber)
                    {
                        continue; // Skip if not found or already in target team
                    }

                    // Move shooter
                    sourceTeam.Shooters?.Remove(shooter);
                    affectedTeams.Add(sourceTeam.TeamNumber);

                    shooter.Position = targetTeam.Shooters.Count + 1;
                    targetTeam.Shooters.Add(shooter);
                    movedCount++;
                }

                // Update affected teams
                foreach (var teamNum in affectedTeams)
                {
                    var team = configuration.Teams.First(t => t.TeamNumber == teamNum);
                    team.ShooterCount = team.Shooters?.Count ?? 0;
                    int pos = 1;
                    foreach (var s in team.Shooters ?? new List<StartListShooter>())
                    {
                        s.Position = pos++;
                    }
                    team.WeaponClasses = team.Shooters?.Select(s => s.WeaponClass).Distinct().ToList() ?? new List<string>();
                }

                // Update target team
                targetTeam.ShooterCount = targetTeam.Shooters.Count;
                targetTeam.WeaponClasses = targetTeam.Shooters.Select(s => s.WeaponClass).Distinct().ToList();

                // Save and regenerate
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = $"{movedCount} skytt(ar) har flyttats till Lag {request.TargetTeamNumber}." });
                }
                return Json(new { success = false, message = "Kunde inte spara startlistan." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error bulk moving shooters to team {TeamNumber}", request.TargetTeamNumber);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Update a shooter's weapon class
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateShooterWeaponClass([FromBody] UpdateShooterWeaponClassRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.MemberId <= 0 || string.IsNullOrWhiteSpace(request.NewWeaponClass))
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                // Find shooter
                StartListShooter? shooter = null;
                StartListTeam? team = null;
                foreach (var t in configuration.Teams)
                {
                    shooter = t.Shooters?.FirstOrDefault(s => s.MemberId == request.MemberId);
                    if (shooter != null)
                    {
                        team = t;
                        break;
                    }
                }

                if (shooter == null || team == null)
                {
                    return Json(new { success = false, message = "Skyttan kunde inte hittas." });
                }

                // Update weapon class
                shooter.WeaponClass = request.NewWeaponClass;

                // Update team weapon classes
                team.WeaponClasses = team.Shooters?.Select(s => s.WeaponClass).Distinct().ToList() ?? new List<string>();

                // Save and regenerate
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = $"Vapenklass har ändrats till {request.NewWeaponClass}." });
                }
                return Json(new { success = false, message = "Kunde inte spara startlistan." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating weapon class for shooter {MemberId}", request.MemberId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        /// <summary>
        /// Delete an empty team from the start list
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTeam([FromBody] DeleteTeamRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.TeamNumber <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                {
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });
                }

                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                {
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });
                }

                var team = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                if (team == null)
                {
                    return Json(new { success = false, message = "Laget kunde inte hittas." });
                }

                // Check if team has shooters
                if (team.Shooters != null && team.Shooters.Any())
                {
                    return Json(new { success = false, message = "Laget har fortfarande skyttar. Flytta eller ta bort dem först." });
                }

                // Remove team
                configuration.Teams.Remove(team);

                // Renumber remaining teams
                int num = 1;
                foreach (var t in configuration.Teams.OrderBy(t => t.TeamNumber))
                {
                    t.TeamNumber = num++;
                }

                // Save and regenerate
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                var configJson = JsonConvert.SerializeObject(configuration);
                startList.SetValue("configurationData", configJson);

                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (result.Success)
                {
                    return Json(new { success = true, message = "Laget har tagits bort." });
                }
                return Json(new { success = false, message = "Kunde inte spara startlistan." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting team {TeamNumber} from start list {StartListId}", request.TeamNumber, request.StartListId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod." });
            }
        }

        #endregion

        #region Finals Start List Management

        /// <summary>
        /// Calculate finals qualifiers based on qualification results
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CalculateFinalsQualifiers(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                // Check if this is a championship
                var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
                if (numberOfFinalSeries <= 0)
                {
                    return Json(new { Success = false, Message = "Denna tävling har ingen final." });
                }

                // Get qualification results
                var qualificationResults = await GetQualificationResults(competitionId);
                if (!qualificationResults.Any())
                {
                    return Json(new { Success = false, Message = "Inga kvalificeringsresultat finns ännu." });
                }

                // Get shooter information
                var shooterInfo = await GetShooterInfoDictionary(competitionId);

                // Calculate qualifiers
                var maxShootersPerTeam = competition.GetValue<int>("numberOfSeriesOrStations");
                var loggerFactory = HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                var finalsLogger = loggerFactory?.CreateLogger<PrecisionFinalsQualificationService>();
                var finalsService = new PrecisionFinalsQualificationService(finalsLogger!);
                var qualificationViewModel = finalsService.CalculateQualifiers(
                    qualificationResults,
                    shooterInfo,
                    maxShootersPerTeam
                );

                qualificationViewModel.CompetitionId = competitionId;
                qualificationViewModel.CompetitionName = competition.Name;

                return Json(new
                {
                    Success = true,
                    Data = qualificationViewModel
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating finals qualifiers for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid beräkning av kvalificerade: " + ex.Message });
            }
        }

        /// <summary>
        /// Generate and save finals start list
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GenerateFinalsStartList([FromBody] GenerateFinalsStartListRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltig förfrågan." });
                }

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                // Get current user
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var generatedBy = request.GeneratedBy ?? currentMember?.Name ?? "Unknown";

                // Calculate qualifiers
                var qualificationResults = await GetQualificationResults(request.CompetitionId);
                var shooterInfo = await GetShooterInfoDictionary(request.CompetitionId);
                
                var loggerFactory = HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                var finalsLogger = loggerFactory?.CreateLogger<PrecisionFinalsQualificationService>();
                var finalsService = new PrecisionFinalsQualificationService(finalsLogger!);
                var qualificationViewModel = finalsService.CalculateQualifiers(
                    qualificationResults,
                    shooterInfo,
                    request.MaxShootersPerTeam
                );

                // NEW ARCHITECTURE: Create/update finals start list as DIRECT child of competition
                var existingFinalsStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");

                IContent finalsStartList;
                if (existingFinalsStartList != null)
                {
                    // UPDATE existing finals start list
                    finalsStartList = existingFinalsStartList;
                    _logger.LogInformation("Updating existing finals start list {StartListId} for competition {CompetitionId}",
                        finalsStartList.Id, request.CompetitionId);
                }
                else
                {
                    // CREATE new finals start list as direct child of competition
                    finalsStartList = _contentService.Create("Finalstartlista", competition.Id, "finalsStartList");
                    _logger.LogInformation("Creating new finals start list for competition {CompetitionId}", request.CompetitionId);
                }
                
                // Set properties
                finalsStartList.SetValue("competitionId", request.CompetitionId);
                finalsStartList.SetValue("generatedDate", DateTime.Now);
                finalsStartList.SetValue("generatedBy", generatedBy);
                finalsStartList.SetValue("isOfficialFinalsStartList", false); // Start as unofficial
                finalsStartList.SetValue("teamFormat", "Championship Finals");
                finalsStartList.SetValue("totalFinalists", qualificationViewModel.TotalQualifiers);
                finalsStartList.SetValue("maxShootersPerTeam", request.MaxShootersPerTeam);

                // Get qualification start list ID
                var qualStartLists = _repository.GetStartListsForCompetition(request.CompetitionId);
                var officialQualStartList = qualStartLists.FirstOrDefault(sl => 
                    sl.GetValue<bool>("isOfficialStartList") && 
                    sl.ContentType.Alias == "precisionStartList");
                
                if (officialQualStartList != null)
                {
                    finalsStartList.SetValue("qualificationStartListId", officialQualStartList.Id);
                }

                // Build configuration data (same format as regular start list)
                var configData = BuildFinalsConfigurationData(qualificationViewModel);
                finalsStartList.SetValue("configurationData", JsonConvert.SerializeObject(configData));

                // Save and publish
                var saveResult = _contentService.Save(finalsStartList);
                if (!saveResult.Success)
                {
                    _logger.LogError("Failed to save finals start list. Messages: {Messages}",
                        string.Join(", ", saveResult.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>()));
                    return Json(new { Success = false, Message = "Kunde inte spara finalstartlistan." });
                }

                var publishResult = _contentService.Publish(finalsStartList, new[] { "*" }, -1);
                if (!publishResult.Success)
                {
                    _logger.LogWarning("Finals start list saved but publish failed. Messages: {Messages}",
                        string.Join(", ", publishResult.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>()));
                }

                _logger.LogInformation("Created finals start list {Id} for competition {CompetitionId}",
                    finalsStartList.Id, request.CompetitionId);

                return Json(new
                {
                    Success = true,
                    Message = "Finalstartlistan har skapats framgångsrikt!",
                    FinalsStartListId = finalsStartList.Id,
                    TotalFinalists = qualificationViewModel.TotalQualifiers,
                    Teams = qualificationViewModel.ProposedTeams.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating finals start list for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid skapande av finalstartlista: " + ex.Message });
            }
        }

        /// <summary>
        /// Generate a new regular start list for a competition
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateStartList([FromForm] StartListGenerationRequest request)
        {
            try
            {
                // Validate user authentication
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad för att generera startlistor." });
                }

                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata hittades inte." });
                }

                // Validate request
                if (request.CompetitionId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltigt tävlings-ID." });
                }

                // Get competition
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen hittades inte." });
                }

                // Check authorization
                if (!await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                {
                    return Json(new { success = false, message = "Du har inte behörighet att generera startlistor för denna tävling." });
                }

                // Get current user name for "Generated By" field
                var generatedBy = !string.IsNullOrEmpty(request.GeneratedBy)
                    ? request.GeneratedBy
                    : currentMember.Name ?? memberData.Name;

                // Fetch competition registrations
                var registrations = await _repository.GetCompetitionRegistrations(request.CompetitionId);
                if (registrations == null || !registrations.Any())
                {
                    return Json(new { success = false, message = "Inga registreringar hittades för denna tävling." });
                }

                // Generate start list data using the generator service
                var startListData = _generator.GenerateStartListData(registrations, request);
                if (startListData == null || startListData.Teams == null || !startListData.Teams.Any())
                {
                    return Json(new { success = false, message = "Kunde inte generera startlista. Kontrollera att registreringar finns." });
                }

                // Generate HTML content using the renderer service
                var htmlContent = await _renderer.GenerateStartListHtml(startListData, competition.Name ?? "");

                // NEW ARCHITECTURE: Create/update start list as DIRECT child of competition (no hub)
                var existingStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                IContent startList;
                if (existingStartList != null)
                {
                    // UPDATE existing start list
                    startList = existingStartList;
                    _logger.LogInformation("Updating existing start list {StartListId} for competition {CompetitionId}",
                        startList.Id, request.CompetitionId);
                }
                else
                {
                    // CREATE new start list as direct child of competition
                    startList = _contentService.Create("Startlista", competition.Id, "precisionStartList");
                    _logger.LogInformation("Creating new start list for competition {CompetitionId}", request.CompetitionId);
                }

                // Set properties
                startList.SetValue("competitionId", request.CompetitionId);
                startList.SetValue("teamFormat", request.TeamFormat);
                startList.SetValue("generatedDate", DateTime.Now);
                startList.SetValue("generatedBy", generatedBy);
                startList.SetValue("notes", request.Notes ?? "");
                startList.SetValue("isOfficialStartList", false); // Start as unofficial
                startList.SetValue("configurationData", JsonConvert.SerializeObject(startListData));
                startList.SetValue("startListContent", htmlContent);

                // Save and publish
                var saveResult = _contentService.Save(startList);
                if (!saveResult.Success)
                {
                    _logger.LogError("Failed to save start list. Messages: {Messages}",
                        string.Join(", ", saveResult.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>()));
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }

                var publishResult = _contentService.Publish(startList, new[] { "*" }, -1);
                if (!publishResult.Success)
                {
                    _logger.LogWarning("Start list saved but publish failed. Messages: {Messages}",
                        string.Join(", ", publishResult.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>()));
                    // Don't fail completely - the start list was saved
                }

                // Build summary for response
                var summary = new StartListSummary
                {
                    TeamCount = startListData.Teams?.Count ?? 0,
                    TotalShooters = startListData.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0,
                    TeamFormat = request.TeamFormat,
                    FirstStartTime = request.FirstStartTime,
                    LastEndTime = CalculateLastEndTime(startListData.Teams, request),
                    Teams = CreateTeamSummaries(startListData.Teams)
                };

                _logger.LogInformation("Successfully generated start list {StartListId} for competition {CompetitionId} by {User}",
                    startList.Id, request.CompetitionId, generatedBy);

                // Return success response
                return Json(new StartListGenerationResponse
                {
                    Success = true,
                    Message = "Startlistan har skapats framgångsrikt!",
                    StartListId = startList.Id,
                    StartListUrl = $"/umbraco/surface/PrecisionStartList/PreviewStartList?competitionId={request.CompetitionId}&startListId={startList.Id}",
                    Summary = summary
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating start list for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod vid generering av startlistan: " + ex.Message });
            }
        }

        /// <summary>
        /// Get finals start list for a competition
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetFinalsStartList(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                var children = _contentService.GetPagedChildren(competition.Id, 0, 50, out _);

                // NEW ARCHITECTURE: Look for finals start list as DIRECT child of competition
                var finalsStartList = children.FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");

                // BACKWARD COMPATIBILITY: Check under hub during migration period
                if (finalsStartList == null)
                {
                    var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                    if (startListsHub != null)
                    {
                        finalsStartList = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out _)
                            .Where(c => c.ContentType.Alias == "finalsStartList")
                            .OrderByDescending(c => c.CreateDate)
                            .FirstOrDefault();
                    }
                }

                if (finalsStartList == null)
                {
                    return Json(new { Success = false, Message = "Ingen finalstartlista hittades.", Exists = false });
                }

                var configData = finalsStartList.GetValue<string>("configurationData");

                if (string.IsNullOrEmpty(configData))
                {
                    return Json(new { Success = false, Message = "Finalstartlistan saknar data.", Exists = false });
                }

                var startListData = JsonConvert.DeserializeObject<StartListConfiguration>(configData);

                return Json(new
                {
                    Success = true,
                    Exists = true,
                    FinalsStartListId = finalsStartList.Id,
                    IsOfficial = finalsStartList.GetValue<bool>("isOfficialFinalsStartList"),
                    GeneratedDate = finalsStartList.GetValue<DateTime>("generatedDate"),
                    TotalFinalists = finalsStartList.GetValue<int>("totalFinalists"),
                    StartList = startListData
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting finals start list for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message, Exists = false });
            }
        }

        #endregion

        #region Finals Helper Methods

        private async Task<List<PrecisionResultEntry>> GetQualificationResults(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            var numberOfFinalSeries = competition?.GetValue<int>("numberOfFinalSeries") ?? 0;
            var numberOfSeries = competition?.GetValue<int>("numberOfSeriesOrStations") ?? 0;
            var qualSeriesCount = numberOfFinalSeries > 0 ? (numberOfSeries - numberOfFinalSeries) : numberOfSeries;

            using (var db = _databaseFactory.CreateDatabase())
            {
                var results = await db.FetchAsync<PrecisionResultEntry>(
                    @"SELECT * FROM PrecisionResultEntry
                      WHERE CompetitionId = @0 AND SeriesNumber <= @1
                      ORDER BY MemberId, SeriesNumber",
                    competitionId, qualSeriesCount);

                return results;
            }
        }

        private async Task<Dictionary<int, (string Name, string Club)>> GetShooterInfoDictionary(int competitionId)
        {
            var dict = new Dictionary<int, (string, string)>();

            // Get shooter info from start list
            var startLists = _repository.GetStartListsForCompetition(competitionId);
            var officialStartList = startLists.FirstOrDefault(sl => 
                sl.GetValue<bool>("isOfficialStartList") && 
                sl.ContentType.Alias == "precisionStartList");

            if (officialStartList == null)
                return dict;

            var configData = officialStartList.GetValue<string>("configurationData");
            if (string.IsNullOrEmpty(configData))
                return dict;

            try
            {
                var startListData = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (startListData?.Teams != null)
                {
                    foreach (var team in startListData.Teams)
                    {
                        if (team.Shooters != null)
                        {
                            foreach (var shooter in team.Shooters)
                            {
                                if (!dict.ContainsKey(shooter.MemberId))
                                {
                                    dict[shooter.MemberId] = (shooter.Name ?? "Unknown", shooter.Club ?? "Unknown");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing start list data for shooter info");
            }

            return dict;
        }

        private object BuildFinalsConfigurationData(PrecisionFinalsQualificationViewModel qualificationData)
        {
            // Build same structure as regular start list for compatibility
            var teams = qualificationData.ProposedTeams.Select(team => new
            {
                TeamNumber = int.Parse(team.TeamName.Replace("Team F", "")),
                StartTime = "", // Finals times are managed separately
                EndTime = "",
                ShooterCount = team.ShooterCount,
                WeaponClasses = team.Positions.Select(p => p.ShootingClass).Distinct().ToList(),
                Shooters = team.Positions.Select(pos => new
                {
                    Position = pos.Position,
                    Name = pos.Name,
                    Club = pos.Club,
                    WeaponClass = pos.ShootingClass,
                    MemberId = pos.MemberId,
                    ChampionshipClass = pos.ChampionshipClass,
                    QualificationScore = pos.QualificationScore,
                    QualificationRank = pos.Rank
                }).ToList()
            }).ToList();

            return new
            {
                Format = "Championship Finals",
                MaxShootersPerTeam = qualificationData.MaxShootersPerTeam,
                Generated = DateTime.Now,
                Teams = teams
            };
        }

        /// <summary>
        /// Calculate the last end time from a list of teams
        /// </summary>
        private string CalculateLastEndTime(List<StartListTeam>? teams, StartListGenerationRequest request)
        {
            if (teams == null || !teams.Any())
            {
                return request.FirstStartTime;
            }

            var lastTeam = teams.Last();
            return lastTeam.EndTime;
        }

        /// <summary>
        /// Create team summaries for the response
        /// </summary>
        private List<StartListTeamSummary> CreateTeamSummaries(List<StartListTeam>? teams)
        {
            if (teams == null)
            {
                return new List<StartListTeamSummary>();
            }

            return teams.Select(t => new StartListTeamSummary
            {
                TeamNumber = t.TeamNumber,
                StartTime = t.StartTime,
                EndTime = t.EndTime,
                ShooterCount = t.ShooterCount,
                WeaponClasses = t.WeaponClasses
            }).ToList();
        }

        #endregion
    }

    public class DeleteStartListRequest
    {
        public int StartListId { get; set; }
    }

    public class PublishStartListRequest
    {
        public int StartListId { get; set; }
        public bool IsPublished { get; set; }
    }

    public class GenerateFinalsStartListRequest
    {
        public int CompetitionId { get; set; }
        public int MaxShootersPerTeam { get; set; } = 20;
        public string? GeneratedBy { get; set; }
    }

    // ============================================================================
    // START LIST EDITOR REQUEST MODELS (Phase 2 - 2025-11-24)
    // ============================================================================

    public class GetStartListForEditingRequest
    {
        public int StartListId { get; set; }
    }

    public class UpdateStartListRequest
    {
        public int StartListId { get; set; }
        public StartListConfiguration Configuration { get; set; } = new StartListConfiguration();
    }

    public class AddShooterToStartListRequest
    {
        public int StartListId { get; set; }
        public int TeamNumber { get; set; }
        public int MemberId { get; set; }
        public string WeaponClass { get; set; } = "";
    }

    public class RemoveShooterFromStartListRequest
    {
        public int StartListId { get; set; }
        public int MemberId { get; set; }
    }

    public class CreateNewTeamRequest
    {
        public int StartListId { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
    }

    public class UpdateTeamTimesRequest
    {
        public int StartListId { get; set; }
        public int TeamNumber { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
    }

    public class MoveShooterRequest
    {
        public int StartListId { get; set; }
        public int MemberId { get; set; }
        public int TargetTeamNumber { get; set; }
    }

    public class BulkMoveShootersRequest
    {
        public int StartListId { get; set; }
        public List<int> MemberIds { get; set; } = new List<int>();
        public int TargetTeamNumber { get; set; }
    }

    public class UpdateShooterWeaponClassRequest
    {
        public int StartListId { get; set; }
        public int MemberId { get; set; }
        public string NewWeaponClass { get; set; } = "";
    }

    public class DeleteTeamRequest
    {
        public int StartListId { get; set; }
        public int TeamNumber { get; set; }
    }

    public class RepairClubDataRequest
    {
        public int StartListId { get; set; }
    }
}
