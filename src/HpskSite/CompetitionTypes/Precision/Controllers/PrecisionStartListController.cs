using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Globalization;
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
using HpskSite.Services.StartListCoverage;
using HpskSite.Models;

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
        private readonly MemberClubService _memberClubService;
        private readonly PrecisionFinalsQualificationService _finalsQualificationService;
        private readonly PrecisionQualifyingResultsService _qualifyingResultsService;
        private readonly PrecisionFinalsStartListBuilder _finalsBuilder;

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
            ClubService clubService,
            MemberClubService memberClubService,
            PrecisionFinalsQualificationService finalsQualificationService,
            PrecisionQualifyingResultsService qualifyingResultsService,
            PrecisionFinalsStartListBuilder finalsBuilder)
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
            _memberClubService = memberClubService;
            _finalsQualificationService = finalsQualificationService;
            _qualifyingResultsService = qualifyingResultsService;
            _finalsBuilder = finalsBuilder;
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

                            return Content(htmlContent, "text/html; charset=utf-8");
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

                return Content(htmlContent, "text/html; charset=utf-8");
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
                        UniqueShooters = _repository.GetUniqueShootersFromContent(sl),
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
                        UniqueShooters = _repository.GetUniqueShootersFromContent(currentStartList),
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

                // Was it already official? Distinguishes a first publish ("dina tider finns nu") from a
                // re-publish ("tiderna kan ha ändrats"), which are different messages to a shooter.
                var isRepublish = startList.GetValue<bool>("isOfficialStartList");

                // Check if user has permission to manage this start list
                var competitionId = startList.GetValue<int>("competitionId");
                if (competitionId > 0)
                {
                    var competition = _contentService.GetById(competitionId);
                    if (competition == null || !await _validator.CanManageCompetition(memberData.Id, competition.Id))
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att hantera denna startlista." });
                    }

                    // First, unpublish all other start lists for this competition.
                    // NOTE: under the direct-child architecture this also touches THIS node
                    // (it's a direct child of the competition), loading a fresh IContent and
                    // Save()+Publish()ing it. That bumps the version, so the `startList` instance
                    // captured above becomes non-current — saving it then throws
                    // "Cannot save a non-current version". Re-fetch after the call to get the
                    // current version before we mutate + save it.
                    await UnpublishAllStartListsForCompetition(competitionId);

                    startList = _contentService.GetById(request.StartListId);
                    if (startList == null)
                    {
                        return Json(new { success = false, message = "Startlista hittades inte." });
                    }
                }

                // Set the start list as published
                startList.SetValue("isOfficialStartList", request.IsPublished);
                
                var saveResult = _contentService.Save(startList);
                if (saveResult.Success)
                {
                    // Save() only writes the DRAFT version. The public competition page reads the
                    // PUBLISHED version via Model.Children(), so the isOfficialStartList flag must be
                    // pushed to the content cache with Publish() — otherwise "Visa startlista" never
                    // appears (the flag sits unpublished on the draft). Same Save()+Publish() pattern
                    // used by every other mutation in this controller.
                    _contentService.Publish(startList, new[] { "*" }, -1);

                    _logger.LogInformation("{Action} start list {StartListId} by user {UserId}",
                                         request.IsPublished ? "Published" : "Unpublished", request.StartListId, memberData.Id);

                    // Tell the shooters their times are up (or have changed). Opt-in per competition via
                    // autoNotifyParticipants, same gate the results-publish notification uses.
                    //
                    // This matters more than it looks: a member's calendar export (/mitt-schema) is a
                    // one-shot .ics snapshot, so a re-publish that MOVES a start time would otherwise
                    // leave stale alarms on their phone with nothing to tell them. This is that signal.
                    if (request.IsPublished)
                    {
                        NotifyStartListPublished(competitionId, isRepublish);
                    }

                    // The organiser's answer to "stäng självanmälan?" from the publish dialog.
                    // Deliberately AFTER the list itself is published: the list going public is the
                    // thing that must not fail, and the gate is derived (choice AND a published list),
                    // so a failure here leaves registration open rather than closed-by-accident.
                    var gateMessage = ApplyCloseRegistrationChoice(competitionId, request.CloseRegistration);

                    // "har publicerad" var grammatiskt fel och syntes i varje bekräftelse.
                    var actionText = request.IsPublished ? "publicerats" : "avpublicerats";
                    if (gateMessage != null)
                    {
                        // Still success:true — the start list DID publish. Reporting a failure here
                        // would have the organiser press Publicera again to fix an unrelated setting.
                        return Json(new { success = true, message = $"Startlistan har {actionText}. {gateMessage}" });
                    }
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

        /// <summary>
        /// Persists the publish dialog's "stäng självanmälan" checkbox onto the competition.
        ///
        /// Resolved from the service provider rather than the constructor, for the same reason
        /// <see cref="NotifyStartListPublished"/> is: this controller is already large and neither
        /// call is on a path that must have the dependency to do its real job.
        /// </summary>
        /// <returns>A message to append to the success text, or null when there is nothing to say.</returns>
        private string? ApplyCloseRegistrationChoice(int competitionId, bool? closeRegistration)
        {
            if (closeRegistration == null || competitionId <= 0) return null;
            try
            {
                var gate = HttpContext?.RequestServices
                    .GetService(typeof(HpskSite.Services.RegistrationGate.StartListRegistrationGate))
                    as HpskSite.Services.RegistrationGate.StartListRegistrationGate;
                if (gate == null) return null;

                var (ok, message) = gate.PersistChoice(competitionId, closeRegistration);
                return ok ? null : message;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist close-registration choice for competition {CompetitionId}", competitionId);
                return "Inställningen för anmälan kunde inte sparas — kontrollera den i tävlingens inställningar.";
            }
        }

        /// <summary>
        /// Fires the shooter-facing "startlistan är publicerad / tiderna har ändrats" notification.
        ///
        /// Opt-in per competition (autoNotifyParticipants, default off → no behaviour change for existing
        /// competitions) and entirely best-effort: publishing a start list must never fail because a push
        /// couldn't go out. Resolved from the service provider rather than the constructor so this
        /// already-large controller doesn't grow another required dependency.
        /// </summary>
        private void NotifyStartListPublished(int competitionId, bool isRepublish)
        {
            try
            {
                if (competitionId <= 0) return;
                var competition = _contentService.GetById(competitionId);
                if (competition == null || !competition.GetValue<bool>("autoNotifyParticipants")) return;

                var notifier = HttpContext?.RequestServices
                    .GetService(typeof(HpskSite.Services.Messaging.ParticipantNotificationService))
                    as HpskSite.Services.Messaging.ParticipantNotificationService;
                if (notifier == null) return;

                var body = isRepublish
                    ? "Startlistan har uppdaterats — kontrollera din starttid. Du ser dina tider under Mitt schema."
                    : "Startlistan är publicerad. Du ser din starttid och plats under Mitt schema.";

                notifier.Notify(competitionId, "All", null, body, null, 0, "Arrangören");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Start-list publish notification failed for competition {CompetitionId}", competitionId);
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
                    _contentService.Publish(directStartList, new[] { "*" }, -1); // push the flag to the published cache, not just the draft
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
                    _contentService.Publish(startList, new[] { "*" }, -1); // push the flag to the published cache, not just the draft
                }

                _logger.LogInformation("Unpublished {Count} start lists (legacy hub) for competition {CompetitionId}",
                    startLists.Count, competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unpublishing start lists for competition {CompetitionId}", competitionId);
            }
        }

        /// <summary>
        /// Get unique weapon classes registered for a competition
        /// Used by the UI to populate the class start order dropdown
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegisteredWeaponClasses(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Classes = new List<string>() });
                }

                var registrations = await _repository.GetCompetitionRegistrations(competitionId);

                // Extract unique weapon group codes (A, A_Opt, B, C, R, M, L) via the registry
                var classes = registrations
                    .Select(r => ShootingClasses.GetWeaponClassCode(r.MemberClass))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                return Json(new { Success = true, Classes = classes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting registered weapon classes for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Classes = new List<string>() });
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
                // Branch on doctype so the shared editor's "Officiell" banner works for finals
                // too — finalsStartList uses isOfficialFinalsStartList, not isOfficialStartList.
                var isOfficial = startList.ContentType.Alias == "finalsStartList"
                    ? startList.GetValue<bool>("isOfficialFinalsStartList")
                    : startList.GetValue<bool>("isOfficialStartList");

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

                    _contentService.Publish(startList, new[] { "*" }, -1);
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

                // ⚠️ A place on this list is per (MEMBER, CLASS), not per member. The generators
                // already put a multi-class shooter in A2, B2 and C2 on the same competition, so a
                // member-only duplicate check made the manual editor unable to do what the generator
                // does: a shooter who enters a second class late could not be placed at all, and the
                // whole list had to be regenerated (reshuffling everyone else's skjutlag and times)
                // to add one row. Same key as StartListCoverage and StartListCleanup use.
                //
                // Canonical() is required, not tidiness: registrations and most writers store the
                // class ID ("C1"), while ChangeShooterClass writes the display NAME ("C 1"). A
                // literal compare misses the duplicate for every class where the two differ, which
                // would let the same start be added twice.
                var placements = configuration.Teams
                    .SelectMany(t => t.Shooters ?? new List<StartListShooter>())
                    .Where(s => s.MemberId == request.MemberId)
                    .ToList();

                if (string.IsNullOrWhiteSpace(request.WeaponClass))
                {
                    // Without a class we cannot tell a legitimate second start from a duplicate, so
                    // keep the old member-level refusal — but NAME the missing field. Answering
                    // "skyttan finns redan" to a request that simply omitted the class is a statement
                    // about the DATA when the truth is a statement about the REQUEST, and that
                    // misattribution has already cost a debugging round on DeleteResult.
                    if (placements.Count > 0)
                    {
                        return Json(new { success = false, message = "Vapenklass saknas i begäran. Skyttan står redan i startlistan i en annan klass, så klassen måste anges för att avgöra om detta är en ny start." });
                    }
                }
                else
                {
                    var requestedKey = CoverageKeys.Canonical(request.WeaponClass);
                    var sameClass = placements
                        .FirstOrDefault(s => CoverageKeys.Canonical(s.WeaponClass) == requestedKey);

                    if (sameClass != null)
                    {
                        return Json(new { success = false, message = $"Skyttan finns redan i startlistan i klass {sameClass.WeaponClass}." });
                    }
                }

                // Get member info
                var member = _memberService.GetById(request.MemberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlemmen kunde inte hittas." });
                }

                // Get club name.
                // ⚠️ `primaryClubId` is a STRING property. GetValue<int> does not convert it and
                // silently returns 0, so every shooter added through this endpoint was stamped
                // "Okänd klubb" on the start list — visible on the public list and, because the
                // result list reads the start list first, on the results too. Same trap that made
                // every walk-in registration store clubId=0. Go through MemberClubService.
                var primaryClubId = _memberClubService.GetPrimaryClubId(member);
                var clubName = _clubService.GetClubNameById(primaryClubId) ?? "Okänd klubb";

                // Find team
                var team = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                if (team == null)
                {
                    return Json(new { success = false, message = "Laget kunde inte hittas." });
                }

                // ⚠️ KAPACITETSKONTROLL. Den här metoden la skytten sist i laget oavsett
                // hur många skjutplatser laget har — "om det finns plats" kontrollerades
                // inte alls, så en elfte skytt kunde hamna i ett tiotavlors lag och det
                // syntes först på plats. Taket är samma tal generatorn byggde listan med
                // (Settings.MaxShootersPerTeam); saknas inställningarna faller vi tillbaka
                // på modellens standardvärde i stället för att låta kontrollen tystna.
                var maxPerTeam = configuration.Settings?.MaxShootersPerTeam ?? new StartListSettings().MaxShootersPerTeam;
                if (maxPerTeam > 0 && (team.Shooters?.Count ?? 0) >= maxPerTeam)
                {
                    // Föreslå ETT lag med plats i stället för att bara neka — funktionären
                    // står vid disken och behöver nästa steg, inte ett nej.
                    var withRoom = configuration.Teams
                        .Where(t => (t.Shooters?.Count ?? 0) < maxPerTeam)
                        .OrderBy(t => t.TeamNumber)
                        .Select(t => new
                        {
                            t.TeamNumber,
                            Free = maxPerTeam - (t.Shooters?.Count ?? 0)
                        })
                        .ToList();

                    var suggestion = withRoom.Count > 0
                        ? " Lediga platser finns i " + string.Join(", ",
                            withRoom.Take(4).Select(t => $"skjutlag {t.TeamNumber} ({t.Free} st)")) + "."
                        : " Alla skjutlag är fulla — skapa ett nytt skjutlag.";

                    return Json(new
                    {
                        success = false,
                        message = $"Skjutlag {team.TeamNumber} är fullt ({team.Shooters?.Count ?? 0} av {maxPerTeam} platser)." + suggestion,
                        teamFull = true,
                        maxPerTeam,
                        teamsWithRoom = withRoom
                    });
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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

                // ⚠️ Removal is per (MEMBER, CLASS) — the same key as the placement itself, and the
                // mirror of the duplicate check in AddShooterToStartList. This used to take the FIRST
                // row matching the member across all skjutlag and remove that, so on a multi-class
                // shooter it silently removed *an arbitrary* start: asked to clear a leftover C1 row
                // it could just as easily delete a perfectly good A2 place, and reported success.
                // Caught by the verify suite's own cleanup step, which lost the placement it had not
                // touched.
                var requestedClassKey = string.IsNullOrWhiteSpace(request.ShootingClass)
                    ? null
                    : CoverageKeys.Canonical(request.ShootingClass);

                var memberPlacements = configuration.Teams
                    .SelectMany(t => t.Shooters ?? new List<StartListShooter>())
                    .Where(s => s.MemberId == request.MemberId)
                    .ToList();

                // No class given and several placements: refuse and NAME the missing field rather
                // than picking one. Guessing here destroys a start the operator did not ask about,
                // and "Skyttan har tagits bort" would report it as a success.
                if (requestedClassKey == null && memberPlacements.Count > 1)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"Vapenklass saknas i begäran. Skyttan har {memberPlacements.Count} " +
                                  $"placeringar på listan ({string.Join(", ", memberPlacements.Select(s => s.WeaponClass))}), " +
                                  "så klassen måste anges för att rätt start tas bort."
                    });
                }

                // Find and remove shooter
                bool shooterFound = false;
                foreach (var team in configuration.Teams)
                {
                    if (team.Shooters == null) continue;

                    var shooter = team.Shooters.FirstOrDefault(s =>
                        s.MemberId == request.MemberId
                        && (requestedClassKey == null || CoverageKeys.Canonical(s.WeaponClass) == requestedClassKey));
                    if (shooter != null)
                    {
                        // CRITICAL: Check if shooter has results in database.
                        // ⚠️ This asked PrecisionResultEntry unconditionally until 2026-08-25, so on
                        // Duell / Milsnabb / MagnumPrecision / NationellHelmatch — whose rows live in
                        // their own tables — the guard found nothing and protected nothing: the
                        // shooter could be removed and their results left orphaned, silently. The
                        // Precision fallback inside For() is right here; this is a read.
                        using (var db = _databaseFactory.CreateDatabase())
                        {
                            var resultTable = CompetitionResultTables.For(
                                competition?.GetValue<string>("competitionType"));

                            // ⚠️ Scope the guard to the SAME class as the removal. Result rows are
                            // keyed (competition, member, class, series), so a member-wide count let
                            // results in A2 block the removal of a leftover C1 row the shooter is not
                            // even registered for — the orphan that then could not be cleared through
                            // the UI at all.
                            //
                            // The class filter is applied in C# through CoverageKeys.Canonical rather
                            // than in SQL: the class is stored as an ID ("C1") but written as a display
                            // NAME ("C 1") by ChangeShooterClass, and a hand-rolled
                            // UPPER(REPLACE(...)) would be a second normalisation free to drift from
                            // the one every other surface uses. The row set here is one shooter's
                            // series, so there is nothing to gain by filtering in the query.
                            var resultClasses = await db.FetchAsync<string>(
                                $"SELECT ShootingClass FROM [{resultTable}] WHERE CompetitionId = @0 AND MemberId = @1",
                                competitionId, request.MemberId);

                            var resultRows = requestedClassKey == null
                                ? resultClasses.Count
                                : resultClasses.Count(c => CoverageKeys.Canonical(c) == requestedClassKey);

                            if (resultRows > 0)
                            {
                                var which = requestedClassKey == null
                                    ? "denna skytt"
                                    : $"skytten i klass {shooter.WeaponClass}";
                                return Json(new
                                {
                                    success = false,
                                    message = $"Kan inte ta bort skyttan eftersom resultat redan har registrerats för {which}."
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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
        /// Accepts what Flatpickr sends and stores the canonical "yyyy-MM-dd", or "" when the field was
        /// left blank. Anything unparseable becomes "" rather than being stored as junk that would then
        /// break day grouping and calendar export downstream.
        /// </summary>
        private static string NormalizeTeamDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var txt = raw.Trim();
            if (DateTime.TryParseExact(txt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                return exact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (DateTime.TryParse(txt, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var sv))
                return sv.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (DateTime.TryParse(txt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var inv))
                return inv.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return "";
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
                team.Label = request.Label ?? "";
                team.Date = NormalizeTeamDate(request.Date);

                // Sort teams by start time and renumber them
                // Teams should always be ordered from first start to last, with Skjutlag 1 being the first to start.
                // Date FIRST, then time: on a multi-day competition, sorting by clock time alone would put
                // Sunday 09:00 ahead of Saturday 13:00 and then renumber the skjutlag into that wrong order.
                // Dates are "yyyy-MM-dd" so an ordinal string sort is chronological; undated teams (every
                // single-day comp, and legacy lists) sort first as a group, preserving today's behaviour.
                configuration.Teams = configuration.Teams
                    .OrderBy(t => t.Date ?? "", StringComparer.Ordinal)
                    .ThenBy(t => TimeSpan.TryParse(t.StartTime, out var ts) ? ts : TimeSpan.MaxValue)
                    .ToList();

                // Renumber teams based on new sort order
                for (int i = 0; i < configuration.Teams.Count; i++)
                {
                    configuration.Teams[i].TeamNumber = i + 1;
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
                    return Json(new { success = true, message = "Skjutlaget har uppdaterats och skjutlagen har sorterats om." });
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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
        /// Move a shooter up or down by one position within their current team.
        /// Direction is "up" or "down". No-op if already at the boundary.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveShooterPosition([FromBody] MoveShooterPositionRequest request)
        {
            try
            {
                if (request.StartListId <= 0 || request.MemberId <= 0 || string.IsNullOrWhiteSpace(request.Direction))
                    return Json(new { success = false, message = "Ogiltiga parametrar." });

                var direction = request.Direction.Trim().ToLowerInvariant();
                if (direction != "up" && direction != "down")
                    return Json(new { success = false, message = "Ogiltig riktning." });

                var startList = _contentService.GetById(request.StartListId);
                if (startList == null)
                    return Json(new { success = false, message = "Startlistan kunde inte hittas." });

                var configData = startList.GetValue<string>("configurationData");
                var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (configuration?.Teams == null)
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });

                // Locate the shooter and their team.
                StartListTeam? team = null;
                int currentIndex = -1;
                foreach (var t in configuration.Teams)
                {
                    if (t.Shooters == null) continue;
                    var idx = t.Shooters.FindIndex(s => s.MemberId == request.MemberId);
                    if (idx >= 0)
                    {
                        team = t;
                        currentIndex = idx;
                        break;
                    }
                }

                if (team == null || team.Shooters == null || currentIndex < 0)
                    return Json(new { success = false, message = "Skyttan kunde inte hittas i startlistan." });

                var newIndex = direction == "up" ? currentIndex - 1 : currentIndex + 1;
                if (newIndex < 0 || newIndex >= team.Shooters.Count)
                    return Json(new { success = false, message = "Skyttan kan inte flyttas längre." });

                // Swap the two shooters in the list, then renumber the whole team's positions.
                var shooter = team.Shooters[currentIndex];
                team.Shooters.RemoveAt(currentIndex);
                team.Shooters.Insert(newIndex, shooter);
                for (int i = 0; i < team.Shooters.Count; i++)
                    team.Shooters[i].Position = i + 1;

                // Save and regenerate cached HTML — same pattern as MoveShooterToTeam.
                var competitionId = startList.GetValue<int>("competitionId");
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Okänd tävling";

                startList.SetValue("configurationData", JsonConvert.SerializeObject(configuration));
                var htmlContent = await _renderer.GenerateStartListHtml(configuration, competitionName);
                startList.SetValue("startListContent", htmlContent);

                var result = _contentService.Save(startList);
                if (!result.Success)
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                _contentService.Publish(startList, new[] { "*" }, -1);

                return Json(new { success = true, message = $"Skyttan har flyttats {(direction == "up" ? "upp" : "ner")}." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving shooter {MemberId} {Direction} in start list {StartListId}", request.MemberId, request.Direction, request.StartListId);
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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

                // Find shooter in the specific team (a shooter can appear in multiple teams for different classes)
                StartListShooter? shooter = null;
                StartListTeam? team = null;

                if (request.TeamNumber > 0)
                {
                    // Find in specific team
                    team = configuration.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                    if (team != null)
                    {
                        shooter = team.Shooters?.FirstOrDefault(s => s.MemberId == request.MemberId);
                    }
                }
                else
                {
                    // Fallback: find first occurrence (backward compatibility)
                    foreach (var t in configuration.Teams)
                    {
                        shooter = t.Shooters?.FirstOrDefault(s => s.MemberId == request.MemberId);
                        if (shooter != null)
                        {
                            team = t;
                            break;
                        }
                    }
                }

                if (shooter == null || team == null)
                {
                    return Json(new { success = false, message = "Skyttan kunde inte hittas i det angivna laget." });
                }

                // Get old weapon class from shooter or request
                var oldWeaponClass = !string.IsNullOrWhiteSpace(request.OldWeaponClass)
                    ? request.OldWeaponClass
                    : shooter.WeaponClass;

                // Update weapon class in start list
                shooter.WeaponClass = request.NewWeaponClass;

                // Update team weapon classes
                team.WeaponClasses = team.Shooters?.Select(s => s.WeaponClass).Distinct().ToList() ?? new List<string>();

                // Save and regenerate start list
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);

                    // Also update the underlying registration so the change persists when regenerating start lists
                    var registrationUpdated = await UpdateRegistrationWeaponClass(competitionId, request.MemberId, oldWeaponClass, request.NewWeaponClass);

                    var message = $"Vapenklass har ändrats till {request.NewWeaponClass}.";
                    if (registrationUpdated)
                    {
                        message += " Anmälan har också uppdaterats.";
                    }
                    else
                    {
                        message += " OBS: Anmälan kunde inte uppdateras automatiskt.";
                        _logger.LogWarning("Could not update registration weapon class for member {MemberId} in competition {CompetitionId}",
                            request.MemberId, competitionId);
                    }

                    // Migrate any already-entered result rows to the new class. Result rows are keyed by
                    // (CompetitionId, MemberId, ShootingClass, SeriesNumber); without this step the scores
                    // stay tagged with the OLD class and become invisible in result entry (which filters by
                    // the shooter's current class) while still lingering under the old class in the result
                    // list. This mirrors CompetitionResultsController.ChangeShooterClass.
                    int resultRowsUpdated = 0;
                    bool resultMigrationFailed = false;
                    try
                    {
                        var competitionTypeId = competition?.GetValue<string>("competitionType") ?? "Precision";
                        var resultTable = GetResultTableName(competitionTypeId);
                        using var db = _databaseFactory.CreateDatabase();
                        resultRowsUpdated = await db.ExecuteAsync(
                            $"UPDATE [{resultTable}] SET ShootingClass = @0, LastModified = @1 WHERE CompetitionId = @2 AND MemberId = @3 AND ShootingClass = @4",
                            request.NewWeaponClass, DateTime.Now, competitionId, request.MemberId, oldWeaponClass);

                        if (resultRowsUpdated > 0)
                        {
                            _logger.LogInformation("Migrated {Rows} result rows '{OldClass}' -> '{NewClass}' for member {MemberId} in competition {CompetitionId}",
                                resultRowsUpdated, oldWeaponClass, request.NewWeaponClass, request.MemberId, competitionId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Most likely a unique-constraint collision because the member already has result
                        // rows under the new class (a genuine multi-class shooter). Don't fail the class
                        // change itself — surface a note so the admin can reconcile the scores manually.
                        resultMigrationFailed = true;
                        _logger.LogWarning(ex, "Failed to migrate result rows for class change member {MemberId} in competition {CompetitionId}",
                            request.MemberId, competitionId);
                    }

                    if (resultRowsUpdated > 0)
                    {
                        message += $" {resultRowsUpdated} resultatrad(er) flyttades till {request.NewWeaponClass}.";
                    }
                    else if (resultMigrationFailed)
                    {
                        message += " OBS: Befintliga resultat kunde inte flyttas automatiskt – kontrollera resultatinmatningen.";
                    }

                    return Json(new { success = true, message = message, registrationUpdated = registrationUpdated, resultRowsUpdated = resultRowsUpdated });
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
        /// Get the result table name for a competition type. The map lives in
        /// <see cref="CompetitionResultTables"/>; this was the copy carrying "keep the two in sync".
        /// </summary>
        private static string GetResultTableName(string typeId) =>
            CompetitionResultTables.ForSharedResultEndpoint(typeId);

        /// <summary>
        /// Update the weapon class in the member's competition registration
        /// This ensures the change persists when a new start list is generated
        /// </summary>
        private async Task<bool> UpdateRegistrationWeaponClass(int competitionId, int memberId, string oldWeaponClass, string newWeaponClass)
        {
            try
            {
                // Find the competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("Competition {CompetitionId} not found when updating registration", competitionId);
                    return false;
                }

                // Find the registrations hub
                var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
                var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                if (hub == null)
                {
                    _logger.LogWarning("Registrations hub not found for competition {CompetitionId}", competitionId);
                    return false;
                }

                // Find the member's registration
                var registrations = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList();

                var memberRegistration = registrations.FirstOrDefault(r => r.GetValue<int>("memberId") == memberId);
                if (memberRegistration == null)
                {
                    _logger.LogWarning("Registration not found for member {MemberId} in competition {CompetitionId}", memberId, competitionId);
                    return false;
                }

                // Get current shooting classes
                var shootingClassesJson = memberRegistration.GetValue<string>("shootingClasses");
                var shootingClasses = HpskSite.Models.CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                if (!shootingClasses.Any())
                {
                    // Try legacy format
                    var legacyClass = memberRegistration.GetValue<string>("shootingClass");
                    if (!string.IsNullOrWhiteSpace(legacyClass))
                    {
                        // Update legacy format directly
                        if (legacyClass.Equals(oldWeaponClass, StringComparison.OrdinalIgnoreCase))
                        {
                            memberRegistration.SetValue("shootingClass", newWeaponClass);
                            var legacyResult = _contentService.Save(memberRegistration);
                            if (legacyResult.Success)
                            {
                                _contentService.Publish(memberRegistration, new[] { "*" }, -1);
                                _logger.LogInformation("Updated legacy registration weapon class for member {MemberId}: {OldClass} -> {NewClass}",
                                    memberId, oldWeaponClass, newWeaponClass);
                                return true;
                            }
                        }
                    }
                    return false;
                }

                // Find and update the specific class entry.
                // Match by exact class first, then by weapon group code (so "A" matches "A1"/"A2"/"A3"
                // and "A_Opt" matches "A_opt_1"/"A_opt_2"/"A_opt_3" — without crossing groups).
                var oldGroup = ShootingClasses.GetWeaponClassCode(oldWeaponClass);
                var classEntry = shootingClasses.FirstOrDefault(c =>
                    c.Class.Equals(oldWeaponClass, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(oldGroup) && ShootingClasses.GetWeaponClassCode(c.Class) == oldGroup));

                if (classEntry == null)
                {
                    _logger.LogWarning("Class entry {OldClass} not found in registration for member {MemberId}", oldWeaponClass, memberId);
                    return false;
                }

                // Update the class
                classEntry.Class = newWeaponClass;

                // Serialize and save
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(shootingClasses);
                memberRegistration.SetValue("shootingClasses", updatedJson);

                var saveResult = _contentService.Save(memberRegistration);
                if (saveResult.Success)
                {
                    _contentService.Publish(memberRegistration, new[] { "*" }, -1);
                    _logger.LogInformation("Updated registration weapon class for member {MemberId}: {OldClass} -> {NewClass}",
                        memberId, oldWeaponClass, newWeaponClass);
                    return true;
                }

                _logger.LogError("Failed to save registration update for member {MemberId}", memberId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating registration weapon class for member {MemberId} in competition {CompetitionId}",
                    memberId, competitionId);
                return false;
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
                    // Publish to make changes visible on frontend
                    _contentService.Publish(startList, new[] { "*" }, -1);
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

                // Calculate qualifiers (uses DI-registered singleton, no manual logger construction)
                var maxShootersPerTeam = competition.GetValue<int>("numberOfSeriesOrStations");
                var qualificationViewModel = _finalsQualificationService.CalculateQualifiers(
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
        /// Checks if any results exist for this competition.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> HasResults(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            // Same fault as the removal guard above: hardcoding PrecisionResultEntry answered
            // "no results" for every other discipline in the family, so anything gating on this
            // silently stopped warning.
            var resultTable = HpskSite.CompetitionTypes.Common.CompetitionResultTables.For(
                _contentService.GetById(competitionId)?.GetValue<string>("competitionType"));
            var count = await db.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM [{resultTable}] WHERE CompetitionId = @0", competitionId);
            return Json(new { success = true, hasResults = count > 0, resultCount = count });
        }

        /// <summary>
        /// Generate and save the finals start list. Workflow gate: requires a published
        /// qualifying-results snapshot (created via PublishQualifyingResults). The
        /// per-class configuration on the finalsStartList node controls which championship
        /// classes get their own final, merge into the C-family, or are skipped.
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

                // Authorization
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { Success = false, Message = "Du måste vara inloggad." });
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera denna tävling." });

                var generatedBy = request.GeneratedBy ?? currentMember.Name ?? "Unknown";

                // Workflow gate: at least one class must be frozen.
                var snapshot = _qualifyingResultsService.GetSnapshot(request.CompetitionId);
                if (snapshot.ClassSnapshots.Count == 0)
                    return Json(new { Success = false, Message = "Lås minst en klass innan finalsstartlistan kan genereras." });

                // Pull persisted per-class config from the existing finalsStartList node if any,
                // otherwise use the merge-aware defaults.
                var existingFinalsStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");

                var perClassConfig = LoadFinalsConfig(existingFinalsStartList);

                var settings = new FinalsStartListSettings
                {
                    FirstStartTime = string.IsNullOrWhiteSpace(request.FirstStartTime) ? "10:00" : request.FirstStartTime,
                    StartInterval = string.IsNullOrWhiteSpace(request.StartInterval) ? "1:45" : request.StartInterval,
                    MaxShootersPerTeam = request.MaxShootersPerTeam > 0 ? request.MaxShootersPerTeam : 20
                };

                var build = _finalsBuilder.Build(snapshot, perClassConfig, settings);
                if (!build.Ok || build.Configuration == null)
                    return Json(new { Success = false, Message = build.Message });

                var persist = await PersistFinalsStartListAsync(competition, build.Configuration, generatedBy, settings.MaxShootersPerTeam);
                if (!persist.ok)
                    return Json(new { Success = false, Message = persist.message });

                return Json(new
                {
                    Success = true,
                    Message = build.Message,
                    FinalsStartListId = persist.id,
                    TotalFinalists = persist.totalFinalists,
                    Teams = persist.teams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating finals start list for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid skapande av finalstartlista: " + ex.Message });
            }
        }

        /// <summary>
        /// Simplified finals generation for the common case — no freeze, no cut, no per-group config.
        /// Two modes:
        ///   "clone"  — the finals use the SAME order as the qualifying start list (everyone continues
        ///              in their existing skjutlag/position). Reads the official qualifying start list.
        ///   "rerank" — everyone is re-seeded into a single list ordered by their qualifying total
        ///              (highest first, X-count then name as tiebreak). Reads live results.
        /// Both produce a finalsStartList node so results entry has the correct order.
        /// The freeze/cut wizard (GenerateFinalsStartList) remains for championship finals with a cut.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSimpleFinalsStartList([FromBody] GenerateSimpleFinalsRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { Success = false, Message = "Ogiltig förfrågan." });

                var mode = (request.Mode ?? "").Trim().ToLowerInvariant();
                if (mode != "clone" && mode != "rerank")
                    return Json(new { Success = false, Message = "Okänt finalläge." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { Success = false, Message = "Du måste vara inloggad." });
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera denna tävling." });

                if (competition.GetValue<int>("numberOfFinalSeries") <= 0)
                    return Json(new { Success = false, Message = "Denna tävling har inga finalserier konfigurerade." });

                var generatedBy = request.GeneratedBy ?? currentMember.Name ?? "Unknown";
                var maxPerTeam = request.MaxShootersPerTeam > 0 ? request.MaxShootersPerTeam : 20;

                StartListConfiguration config;
                if (mode == "clone")
                {
                    config = BuildCloneFinalsConfig(request.CompetitionId, maxPerTeam);
                    if (config.Teams == null || config.Teams.Count == 0)
                        return Json(new { Success = false, Message = "Ingen kvalstartlista att kopiera. Skapa och publicera kvalstartlistan först." });
                }
                else // rerank
                {
                    var firstStart = string.IsNullOrWhiteSpace(request.FirstStartTime) ? "10:00" : request.FirstStartTime;
                    var interval = string.IsNullOrWhiteSpace(request.StartInterval) ? "1:45" : request.StartInterval;
                    config = await BuildRerankFinalsConfigAsync(request.CompetitionId, maxPerTeam, firstStart, interval);
                    if (config.Teams == null || config.Teams.Count == 0)
                        return Json(new { Success = false, Message = "Inga kvalresultat att placera om. Registrera resultat först." });
                }

                var persist = await PersistFinalsStartListAsync(competition, config, generatedBy, maxPerTeam);
                if (!persist.ok)
                    return Json(new { Success = false, Message = persist.message });

                var msg = mode == "clone"
                    ? $"Finalen använder samma ordning som kvalet — {persist.totalFinalists} skyttar i {persist.teams} skjutlag."
                    : $"Finalen placerad efter kvalresultat — {persist.totalFinalists} skyttar i {persist.teams} skjutlag.";

                return Json(new
                {
                    Success = true,
                    Message = msg,
                    FinalsStartListId = persist.id,
                    TotalFinalists = persist.totalFinalists,
                    Teams = persist.teams
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating simple finals start list for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Shared save/publish tail for all finals-generation paths. Creates or updates the
        /// finalsStartList node, serializes the config, renders cached HTML, saves + publishes.
        /// </summary>
        private async Task<(bool ok, string message, int id, int totalFinalists, int teams)> PersistFinalsStartListAsync(
            IContent competition, StartListConfiguration config, string generatedBy, int maxShootersPerTeam)
        {
            var existingFinalsStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");

            IContent finalsStartList = existingFinalsStartList
                ?? _contentService.Create("Finalstartlista", competition.Id, "finalsStartList");

            var totalFinalists = config.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0;
            finalsStartList.SetValue("competitionId", competition.Id);
            finalsStartList.SetValue("generatedDate", DateTime.Now);
            finalsStartList.SetValue("generatedBy", generatedBy);
            finalsStartList.SetValue("isOfficialFinalsStartList", false);
            finalsStartList.SetValue("teamFormat", config.Settings?.Format ?? "Championship Finals");
            finalsStartList.SetValue("totalFinalists", totalFinalists);
            finalsStartList.SetValue("maxShootersPerTeam", maxShootersPerTeam);

            var qualStartLists = _repository.GetStartListsForCompetition(competition.Id);
            var officialQualStartList = qualStartLists.FirstOrDefault(sl =>
                sl.GetValue<bool>("isOfficialStartList") &&
                sl.ContentType.Alias == "precisionStartList");
            if (officialQualStartList != null)
                finalsStartList.SetValue("qualificationStartListId", officialQualStartList.Id);

            finalsStartList.SetValue("configurationData", JsonConvert.SerializeObject(config));

            // Cached HTML — used by admin preview / print. Renderer auto-detects finals format
            // and emits the Rang/Kvalresultat columns. Isolate failures here so a renderer bug
            // doesn't take the whole generation down — the public view reads configurationData directly.
            try
            {
                var finalsHtml = await _renderer.GenerateStartListHtml(config, competition.Name ?? "");
                finalsStartList.SetValue("startListContent", finalsHtml);
            }
            catch (Exception renderEx)
            {
                _logger.LogWarning(renderEx, "Finals start list HTML render failed for competition {CompetitionId} — continuing without cached HTML", competition.Id);
            }

            // Matches the standard GenerateStartList flow: Save + Publish here. The
            // PublishFinalsStartList endpoint (the card's Publicera button) only flips the
            // isOfficialFinalsStartList flag with a Save — the node is already Published from here.
            var saveResult = _contentService.Save(finalsStartList);
            if (!saveResult.Success)
            {
                _logger.LogError("Failed to save finals start list. Messages: {Messages}",
                    string.Join(", ", saveResult.EventMessages?.GetAll().Select(m => m.Message) ?? Array.Empty<string>()));
                return (false, "Kunde inte spara finalstartlistan.", 0, 0, 0);
            }
            _contentService.Publish(finalsStartList, new[] { "*" }, -1);

            return (true, "", finalsStartList.Id, totalFinalists, config.Teams?.Count ?? 0);
        }

        /// <summary>
        /// "Same order" finals: deep-copy the official qualifying start list's teams/positions,
        /// relabel the format so the finals label ("Final") applies. No Rang/Kval columns render
        /// because these shooters carry no qualification rank/score.
        /// </summary>
        private StartListConfiguration BuildCloneFinalsConfig(int competitionId, int maxPerTeam)
        {
            var empty = new StartListConfiguration { Teams = new List<StartListTeam>() };

            var startLists = _repository.GetStartListsForCompetition(competitionId);
            var qual = startLists.FirstOrDefault(sl =>
                    sl.GetValue<bool>("isOfficialStartList") && sl.ContentType.Alias == "precisionStartList")
                ?? startLists.FirstOrDefault(sl => sl.ContentType.Alias == "precisionStartList");
            if (qual == null) return empty;

            var configData = qual.GetValue<string>("configurationData");
            if (string.IsNullOrWhiteSpace(configData)) return empty;

            var qualConfig = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
            if (qualConfig?.Teams == null || qualConfig.Teams.Count == 0) return empty;

            qualConfig.Settings ??= new StartListSettings();
            qualConfig.Settings.Format = "Final";
            qualConfig.Settings.MaxShootersPerTeam = maxPerTeam;
            qualConfig.Settings.Generated = DateTime.Now;
            return qualConfig;
        }

        /// <summary>
        /// "By result" finals: everyone with qualifying results, ranked by qualifying total, but
        /// SEPARATED BY WEAPON GROUP — all A shooters form the A final, all B the B final, all C
        /// the C final, etc. (A_Opt / AM / AP / AG are their own groups per ShootingClasses).
        /// Within each group: score desc, X-count desc, name; rank restarts per group. Each group
        /// is its own skjutlag (split into more if it exceeds maxPerTeam). Weapon groups follow the
        /// canonical WeaponClass enum order.
        /// </summary>
        private async Task<StartListConfiguration> BuildRerankFinalsConfigAsync(int competitionId, int maxPerTeam, string firstStart, string interval)
        {
            var empty = new StartListConfiguration { Teams = new List<StartListTeam>() };

            var rankings = await _qualifyingResultsService.GetAvailableClassRankingsAsync(competitionId);
            var all = rankings.SelectMany(r => r.QualifiedShooters).ToList();
            if (all.Count == 0) return empty;

            if (maxPerTeam < 1) maxPerTeam = 20;

            // Group by weapon group (A / A_Opt / A_M / A_P / A_G / B / C / R / M / L). Unknown → own
            // trailing bucket keyed "" so nobody is silently dropped.
            static int GroupOrder(string code) =>
                Enum.TryParse<HpskSite.Models.WeaponClass>(code, out var wc) ? (int)wc : 999;

            var groups = all
                .GroupBy(s => HpskSite.Models.ShootingClasses.GetWeaponClassCode(s.ShootingClass))
                .OrderBy(g => GroupOrder(g.Key))
                .ThenBy(g => g.Key);

            var teams = new List<StartListTeam>();
            string currentStart = string.IsNullOrWhiteSpace(firstStart) ? "10:00" : firstStart;
            int teamNumber = 1;

            foreach (var g in groups)
            {
                var ranked = g
                    .OrderByDescending(s => s.QualificationScore)
                    .ThenByDescending(s => s.XCount)
                    .ThenBy(s => s.Name)
                    .ToList();

                var groupLabel = string.IsNullOrEmpty(g.Key) ? "Övriga" : g.Key;
                int rankInGroup = 1;

                for (int i = 0; i < ranked.Count; i += maxPerTeam)
                {
                    var chunk = ranked.Skip(i).Take(maxPerTeam).ToList();
                    var shooters = new List<StartListShooter>();
                    int position = 1;
                    foreach (var qs in chunk)
                    {
                        shooters.Add(new StartListShooter
                        {
                            Position = position++,
                            Name = qs.Name,
                            Club = qs.Club,
                            WeaponClass = qs.ShootingClass,
                            MemberId = qs.MemberId,
                            QualificationRank = rankInGroup++,
                            QualificationScore = qs.QualificationScore,
                            QualificationXCount = qs.XCount,
                            ChampionshipClass = qs.ChampionshipClass
                        });
                    }
                    var endTime = AddTimeInterval(currentStart, interval);
                    teams.Add(new StartListTeam
                    {
                        TeamNumber = teamNumber++,
                        StartTime = currentStart,
                        EndTime = endTime,
                        Label = groupLabel,
                        WeaponClasses = shooters.Select(s => s.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                        ShooterCount = shooters.Count,
                        Shooters = shooters,
                        ChampionshipClasses = groupLabel
                    });
                    currentStart = endTime;
                }
            }

            return new StartListConfiguration
            {
                Settings = new StartListSettings
                {
                    Format = "Championship Finals",
                    MaxShootersPerTeam = maxPerTeam,
                    StartInterval = interval,
                    FirstStartTime = firstStart,
                    Generated = DateTime.Now
                },
                Teams = teams
            };
        }

        private static string AddTimeInterval(string startTime, string interval)
        {
            // Interval is hours:minutes (h:mm) — same convention as StartListGenerator.
            if (!TimeSpan.TryParse(startTime, out var ts)) return startTime;
            var parts = (interval ?? "").Split(':');
            int hours = 0, minutes = 0;
            if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            {
                hours = h;
                minutes = m;
            }
            ts = ts.Add(new TimeSpan(hours, minutes, 0));
            return $"{ts.Hours:D2}:{ts.Minutes:D2}";
        }

        // ====================================================================
        // Per-class qualifying snapshot + finals config endpoints
        // ====================================================================

        /// <summary>
        /// Freeze the current qualifying-round leaderboard for a single championship class.
        /// Admin freezes each class independently as that class's qualifying completes.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FreezeClassResults([FromBody] FreezeClassResultsRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.ChampionshipClass))
                return Json(new { success = false, message = "Ogiltig förfrågan." });

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });
            var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var frozenBy = currentMember.Name ?? memberData.Name ?? "Unknown";
            var (ok, message, classSnap) = await _qualifyingResultsService.FreezeClassResultsAsync(request.CompetitionId, request.ChampionshipClass, frozenBy);

            return Json(new
            {
                success = ok,
                message,
                classSnapshot = ok && classSnap != null
                    ? new
                    {
                        championshipClass = classSnap.ChampionshipClass,
                        frozenAt = classSnap.FrozenAt,
                        frozenBy = classSnap.FrozenBy,
                        shooterCount = classSnap.QualifiedShooters.Count
                    }
                    : null
            });
        }

        /// <summary>Unfreeze a single championship class.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnfreezeClassResults([FromBody] FreezeClassResultsRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.ChampionshipClass))
                return Json(new { success = false, message = "Ogiltig förfrågan." });

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });
            var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var (ok, message) = await _qualifyingResultsService.UnfreezeClassAsync(request.CompetitionId, request.ChampionshipClass);
            return Json(new { success = ok, message });
        }

        /// <summary>
        /// Returns the result-list groups (sub-classes with merge config applied) and
        /// per-group freeze state. Each group is one freezable finals unit.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetQualifyingSnapshot(int competitionId)
        {
            if (competitionId <= 0)
                return Json(new { success = false });

            var snapshot = _qualifyingResultsService.GetSnapshot(competitionId);
            var availableRankings = await _qualifyingResultsService.GetAvailableClassRankingsAsync(competitionId);
            var staleness = await _qualifyingResultsService.ComputeStalenessAsync(competitionId, snapshot);

            // Result list group names come from the merge config applied to the actual
            // sub-classes with results. Snapshot can also contain "orphan" groups (frozen
            // but no longer present in the result list — e.g. admin changed merge config) —
            // include those too so the admin can see/unfreeze them.
            var groupNames = availableRankings.Select(r => r.ChampionshipClass)
                .Concat(snapshot.ClassSnapshots.Keys)
                .Distinct()
                .ToList();

            var perGroup = groupNames
                .Select(group =>
                {
                    var ranking = availableRankings.FirstOrDefault(r => r.ChampionshipClass == group);
                    snapshot.ClassSnapshots.TryGetValue(group, out var frozen);
                    return new
                    {
                        groupName = group,
                        // championshipClass kept for client-side backward compat
                        championshipClass = group,
                        totalShooters = ranking?.TotalShooters ?? 0,
                        hasResults = ranking != null && ranking.TotalShooters > 0,
                        frozen = frozen != null,
                        frozenAt = frozen?.FrozenAt,
                        frozenBy = frozen?.FrozenBy,
                        frozenShooterCount = frozen?.QualifiedShooters.Count ?? 0,
                        stale = frozen != null && staleness.TryGetValue(group, out var s) && s
                    };
                })
                .ToList();

            return Json(new
            {
                success = true,
                hasResultList = availableRankings.Count > 0,
                perClass = perGroup
            });
        }

        /// <summary>
        /// Returns the persisted per-group finals config (admin-set per result-list group —
        /// possibly merged sub-classes — like "C2+Dam", "A1"). JS supplies defaults for
        /// groups that don't yet have a saved entry.
        /// </summary>
        [HttpGet]
        public IActionResult GetFinalsConfig(int competitionId)
        {
            if (competitionId <= 0)
                return Json(new { success = false });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var finalsNode = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");

            var config = LoadFinalsConfig(finalsNode);

            return Json(new { success = true, config });
        }

        /// <summary>
        /// Persist the per-class finals config onto the finalsStartList node. Creates the
        /// node if missing.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveFinalsConfig([FromBody] SaveFinalsConfigRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.Config == null)
                    return Json(new { success = false, message = "Ogiltig förfrågan." });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                var finalsNode = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "finalsStartList");
                if (finalsNode == null)
                {
                    finalsNode = _contentService.Create("Finalstartlista", competition.Id, "finalsStartList");
                    finalsNode.SetValue("competitionId", request.CompetitionId);
                }

                finalsNode.SetValue("perClassConfigData", JsonConvert.SerializeObject(request.Config));

                // Save only — perClassConfigData is admin-only and read via the draft (IContent.GetValue).
                // Publish is expensive (NuCache rebuild + events) and isn't needed here. The next
                // GenerateFinalsStartList call does the Save + Publish to push configurationData public.
                var saveResult = _contentService.Save(finalsNode);
                if (!saveResult.Success)
                    return Json(new { success = false, message = "Kunde inte spara konfigurationen." });

                return Json(new { success = true, message = "Konfiguration sparad." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveFinalsConfig failed for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Dry-run: read the per-class snapshot + supplied config and return per-class
        /// finalist counts and resolved skjutlag numbers. No persistence. Used for live
        /// preview in the admin wizard.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult PreviewFinalsConfig([FromBody] SaveFinalsConfigRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.Config == null)
                return Json(new { success = false, message = "Ogiltig förfrågan." });

            var snapshot = _qualifyingResultsService.GetSnapshot(request.CompetitionId);
            if (snapshot.ClassSnapshots.Count == 0)
                return Json(new { success = true, perClass = new Dictionary<string, object>() });

            var preview = _finalsBuilder.PreviewBuckets(snapshot, request.Config);
            return Json(new
            {
                success = true,
                perClass = preview.PerClass.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new { skjutlag = kv.Value.Skjutlag, finalistCount = kv.Value.FinalistCount, totalInClass = kv.Value.TotalInClass })
            });
        }

        /// <summary>
        /// Toggle isOfficialFinalsStartList on the finals node.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishFinalsStartList([FromBody] PublishFinalsStartListRequest request)
        {
            if (request == null || request.FinalsStartListId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan." });

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });
            var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);

            var node = _contentService.GetById(request.FinalsStartListId);
            if (node == null || node.ContentType.Alias != "finalsStartList")
                return Json(new { success = false, message = "Finalsstartlistan hittades inte." });

            var competitionId = node.GetValue<int>("competitionId");
            if (memberData == null || !await _validator.CanManageCompetition(memberData.Id, competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            // Flip the custom flag, then Save() AND Publish(). Save() alone only updates the
            // DRAFT version; the public competition page reads the PUBLISHED version via
            // Model.Children(), so without Publish() the isOfficialFinalsStartList flag never
            // reaches the content cache and "Visa finalsstartlista" never appears.
            node.SetValue("isOfficialFinalsStartList", request.IsPublished);
            var saveResult = _contentService.Save(node);
            if (!saveResult.Success)
                return Json(new { success = false, message = "Kunde inte spara." });
            _contentService.Publish(node, new[] { "*" }, -1);

            return Json(new
            {
                success = true,
                message = request.IsPublished ? "Finalsstartlistan har publicerats." : "Finalsstartlistan är inte längre publicerad."
            });
        }

        private Dictionary<string, FinalsClassConfig> LoadFinalsConfig(IContent? finalsNode)
        {
            if (finalsNode != null)
            {
                var raw = finalsNode.GetValue<string>("perClassConfigData");
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, FinalsClassConfig>>(raw);
                        if (dict != null) return dict;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not deserialize perClassConfigData on finals node {Id}, returning empty", finalsNode.Id);
                    }
                }
            }
            // Empty dict — JS fills in defaults per group when the group is first interacted with.
            return new Dictionary<string, FinalsClassConfig>();
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
                    TeamFormat = finalsStartList.GetValue<string>("teamFormat"),
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

        #region Direktplacering Start List

        [HttpGet]
        public IActionResult PreviewDirektplaceringStartList(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Content("Tävlingen kunde inte hittas.", "text/plain; charset=utf-8");

                // Find existing start list with saved HTML content
                var existingStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                var savedHtml = existingStartList?.GetValue<string>("startListContent") ?? "";

                if (string.IsNullOrWhiteSpace(savedHtml))
                    return Content("Ingen startlista har genererats ännu. Registrera en skytt för att skapa startlistan.", "text/plain; charset=utf-8");

                // Wrap in full HTML page
                var fullHtml = _renderer.BuildHtmlWrapper(savedHtml, competition.Name ?? "");
                return Content(fullHtml, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error previewing Egenbokning start list for competition {CompetitionId}", competitionId);
                return Content($"Fel: {ex.Message}", "text/plain; charset=utf-8");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateDirektplaceringStartList(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                // Authorization — same three-tier check as the other start-list endpoints.
                // (Previously this endpoint had NO authorization beyond "is logged in".)
                var authMember = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (authMember == null || !await _validator.CanManageCompetition(authMember.Id, competitionId))
                    return Json(new { success = false, message = "Du har inte behörighet att generera startlistor för denna tävling." });

                var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                if (dpConfig == null)
                    return Json(new { success = false, message = "Direktplacering är inte aktiverat för denna tävling." });

                // Build StartListConfiguration from registrations with team assignments
                var startListData = BuildDirektplaceringStartListData(competition, dpConfig);
                if (startListData.Teams == null || !startListData.Teams.Any())
                    return Json(new { success = false, message = "Inga skjutlag konfigurerade." });

                // Generate HTML using existing renderer
                var htmlContent = await _renderer.GenerateStartListHtml(startListData, competition.Name ?? "");

                // Create or update start list document (same pattern as traditional)
                var existingStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                IContent startList;
                if (existingStartList != null)
                {
                    startList = existingStartList;
                    _logger.LogInformation("Updating Direktplacering start list {StartListId} for competition {CompetitionId}",
                        startList.Id, competitionId);
                }
                else
                {
                    startList = _contentService.Create("Startlista", competition.Id, "precisionStartList");
                    _logger.LogInformation("Creating new Direktplacering start list for competition {CompetitionId}", competitionId);
                }

                var memberData = _memberService.GetById(currentMember.Key);
                var generatedBy = currentMember.Name ?? memberData?.Name ?? "System";

                startList.SetValue("competitionId", competitionId);
                startList.SetValue("teamFormat", dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag");
                startList.SetValue("generatedDate", DateTime.Now);
                startList.SetValue("generatedBy", generatedBy);
                startList.SetValue("notes", "Genererad via Direktplacering");
                startList.SetValue("isOfficialStartList", false);
                startList.SetValue("configurationData", JsonConvert.SerializeObject(startListData));
                startList.SetValue("startListContent", htmlContent);

                var saveResult = _contentService.Save(startList);
                if (!saveResult.Success)
                {
                    _logger.LogError("Failed to save Direktplacering start list for competition {CompetitionId}", competitionId);
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });
                }

                _contentService.Publish(startList, new[] { "*" }, -1);

                var totalShooters = startListData.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0;
                return Json(new
                {
                    success = true,
                    message = $"Startlista publicerad med {startListData.Teams?.Count ?? 0} skjutlag och {totalShooters} starter.",
                    startListId = startList.Id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating Direktplacering start list for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        private StartListConfiguration BuildDirektplaceringStartListData(IContent competition, DirektplaceringConfig dpConfig)
        {
            // Get all registrations
            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub" ||
                c.Name.Contains("Anmälningar") ||
                c.Name.Contains("Registration"));

            var registrationDocs = registrationsHub != null
                ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList()
                : new List<IContent>();

            // Build shooters per team from registration data
            var shootersByTeam = new Dictionary<int, List<StartListShooter>>();
            foreach (var team in dpConfig.Teams)
                shootersByTeam[team.TeamNumber] = new List<StartListShooter>();

            // Preserve the order shooters registered (first-come within a team) instead of
            // sorting by name, which would re-shuffle existing positions on every regeneration.
            registrationDocs = registrationDocs.OrderBy(c => c.CreateDate).ThenBy(c => c.Id).ToList();
            var sortSeq = 0;

            foreach (var reg in registrationDocs)
            {
                var classesJson = reg.GetValue<string>("shootingClasses");
                if (string.IsNullOrWhiteSpace(classesJson)) continue;

                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
                var memberName = reg.GetValue<string>("memberName") ?? "Okänd";
                var memberId = reg.GetValue<int>("memberId");

                // Get club name
                var clubName = "Okänd förening";
                var clubId = reg.GetValue<int>("clubId");
                if (clubId > 0)
                {
                    clubName = _clubService.GetClubNameById(clubId) ?? "Okänd förening";
                }
                else
                {
                    var member = _memberService.GetById(memberId);
                    if (member != null)
                    {
                        var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                            clubName = _clubService.GetClubNameById(primaryClubId) ?? "Okänd förening";
                    }
                }

                foreach (var entry in classes)
                {
                    if (!entry.TeamNumber.HasValue) continue;
                    var teamNum = entry.TeamNumber.Value;
                    if (!shootersByTeam.ContainsKey(teamNum))
                        shootersByTeam[teamNum] = new List<StartListShooter>();

                    shootersByTeam[teamNum].Add(new StartListShooter
                    {
                        Position = sortSeq++, // registration order; re-numbered to 1..n below
                        Name = memberName,
                        Club = clubName,
                        WeaponClass = entry.Class,
                        MemberId = memberId
                    });
                }
            }

            // Build teams with sequential positions
            var teams = dpConfig.Teams.Select(team =>
            {
                var shooters = shootersByTeam.TryGetValue(team.TeamNumber, out var s) ? s : new List<StartListShooter>();
                shooters = shooters.OrderBy(sh => sh.Position).ToList();
                var position = 1;
                foreach (var shooter in shooters)
                    shooter.Position = position++;

                return new StartListTeam
                {
                    TeamNumber = team.TeamNumber,
                    StartTime = team.StartTime,
                    EndTime = team.EndTime,
                    ShooterCount = shooters.Count,
                    WeaponClasses = shooters.Select(sh => sh.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                    Shooters = shooters.OrderBy(sh => sh.Position).ToList()
                };
            }).ToList();

            return new StartListConfiguration
            {
                Settings = new StartListSettings
                {
                    Format = dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag",
                    MaxShootersPerTeam = dpConfig.Teams.Max(t => t.Positions),
                    FirstStartTime = dpConfig.Teams.FirstOrDefault()?.StartTime ?? "09:00",
                    Generated = DateTime.Now
                },
                Teams = teams
            };
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

        /// <summary>
        /// Stänga självanmälan på tävlingssidan i samma veva? Null = klienten sa ingenting (äldre
        /// klient, eller en yta som inte frågar), och då lämnas inställningen orörd — den får aldrig
        /// nollställas som sidoeffekt av en publicering. Se <c>StartListRegistrationGate</c> för
        /// varför grinden är ett VAL vid publicering och inget som sker automatiskt.
        /// </summary>
        public bool? CloseRegistration { get; set; }
    }

    public class GenerateFinalsStartListRequest
    {
        public int CompetitionId { get; set; }
        public int MaxShootersPerTeam { get; set; } = 20;
        public string? GeneratedBy { get; set; }
        public string? FirstStartTime { get; set; }
        public string? StartInterval { get; set; }
    }

    public class GenerateSimpleFinalsRequest
    {
        public int CompetitionId { get; set; }
        public string? Mode { get; set; } // "clone" | "rerank"
        public int MaxShootersPerTeam { get; set; } = 20;
        public string? GeneratedBy { get; set; }
        public string? FirstStartTime { get; set; }
        public string? StartInterval { get; set; }
    }

    public class FreezeClassResultsRequest
    {
        public int CompetitionId { get; set; }
        public string ChampionshipClass { get; set; } = "";
    }

    public class SaveFinalsConfigRequest
    {
        public int CompetitionId { get; set; }
        public Dictionary<string, HpskSite.CompetitionTypes.Precision.Models.FinalsClassConfig> Config { get; set; } = new();
    }

    public class PublishFinalsStartListRequest
    {
        public int FinalsStartListId { get; set; }
        public bool IsPublished { get; set; }
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

        /// <summary>
        /// Which of the member's placements to remove. Optional for older callers, but supply it:
        /// a place on the list is per (member, class), so without it a multi-class shooter's
        /// removal is ambiguous.
        /// </summary>
        public string ShootingClass { get; set; } = "";
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
        public string? Label { get; set; }
        /// <summary>"yyyy-MM-dd" for multi-day competitions; empty = the competition's own date.</summary>
        public string? Date { get; set; }
    }

    public class MoveShooterRequest
    {
        public int StartListId { get; set; }
        public int MemberId { get; set; }
        public int TargetTeamNumber { get; set; }
    }

    public class MoveShooterPositionRequest
    {
        public int StartListId { get; set; }
        public int MemberId { get; set; }
        public string Direction { get; set; } = "";   // "up" | "down"
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
        public int TeamNumber { get; set; }  // Which team the shooter is in (shooter can be in multiple teams)
        public string OldWeaponClass { get; set; } = "";  // Current class to be replaced
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
