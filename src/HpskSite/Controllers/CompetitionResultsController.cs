using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Milsnabb.Services;
using HpskSite.CompetitionTypes.Milsnabb.Models;
using HpskSite.CompetitionTypes.Duell.Services;
using HpskSite.CompetitionTypes.Duell.Models;
using HpskSite.CompetitionTypes.NationellHelmatch.Services;
using HpskSite.CompetitionTypes.NationellHelmatch.Models;
using HpskSite.CompetitionTypes.MagnumPrecision.Services;
using HpskSite.CompetitionTypes.MagnumPrecision.Models;
using Newtonsoft.Json;
using PrecisionResultEntry = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntry;
using ResultEntryRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryRequest;
using ResultEntryResponse = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryResponse;
using DeleteResultRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionDeleteResultRequest;
using ShooterResult = HpskSite.CompetitionTypes.Precision.Models.PrecisionShooterResult;
using ClassGroup = HpskSite.CompetitionTypes.Precision.Models.PrecisionClassGroup;
using FinalResults = HpskSite.CompetitionTypes.Precision.Models.PrecisionFinalResults;
using System.Data.SqlClient;
using Microsoft.Data.SqlClient;
using System.Data;

namespace HpskSite.Controllers
{
    public class CompetitionResultsController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly IAntiforgery _antiforgery;
        private readonly ILogger<CompetitionResultsController> _logger;
        private readonly UmbracoStartListRepository _startListRepository;
        private readonly ClubService _clubService;
        private readonly SeriesCalculationService _seriesCalculationService;
        private readonly AdminAuthorizationService _adminAuthorizationService;
        private readonly EmailService _emailService;
        private readonly ShootOffService _shootOffService;
        private readonly StandardMedalMaterializationService _medalMaterialization;

        public CompetitionResultsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IMemberService memberService,
            IContentTypeService contentTypeService,
            IMemberManager memberManager,
            IAntiforgery antiforgery,
            ILogger<CompetitionResultsController> logger,
            UmbracoStartListRepository startListRepository,
            ClubService clubService,
            SeriesCalculationService seriesCalculationService,
            AdminAuthorizationService adminAuthorizationService,
            EmailService emailService,
            ShootOffService shootOffService,
            StandardMedalMaterializationService medalMaterialization)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _contentTypeService = contentTypeService;
            _memberManager = memberManager;
            _umbracoContextAccessor = umbracoContextAccessor;
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _antiforgery = antiforgery;
            _logger = logger;
            _startListRepository = startListRepository;
            _clubService = clubService;
            _seriesCalculationService = seriesCalculationService;
            _adminAuthorizationService = adminAuthorizationService;
            _emailService = emailService;
            _shootOffService = shootOffService;
            _medalMaterialization = medalMaterialization;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveResult([FromBody] ResultEntryRequest request)
        {
            try
            {
                _logger.LogInformation("SaveResult called with request: CompetitionId={CompetitionId}, SeriesNumber={SeriesNumber}, TeamNumber={TeamNumber}, Position={Position}, RangeOfficerId={RangeOfficerId}, Shots={Shots}",
                    request?.CompetitionId, request?.SeriesNumber, request?.TeamNumber, request?.Position, request?.RangeOfficerId,
                    request?.Shots != null ? string.Join(",", request.Shots) : "null");

                if (!ValidateResultRequest(request))
                {
                    _logger.LogWarning("Validation failed for request: CompetitionId={CompetitionId}, SeriesNumber={SeriesNumber}, TeamNumber={TeamNumber}, Position={Position}, RangeOfficerId={RangeOfficerId}",
                        request?.CompetitionId, request?.SeriesNumber, request?.TeamNumber, request?.Position, request?.RangeOfficerId);

                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = "Ogiltig begäran. Kontrollera att alla fält är korrekt ifyllda."
                    });
                }

                // VALIDATION: Check if competition is external
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition != null && competition.GetValue<bool>("isExternal"))
                {
                    _logger.LogWarning("Attempt to save results for external competition: CompetitionId={CompetitionId}", request.CompetitionId);
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = "Detta är en extern tävling. Resultat kan inte registreras i systemet."
                    });
                }

                // Use shooter info from the request (sent by the UI)
                _logger.LogInformation("Using shooter info from request: MemberId={MemberId}, Class={Class} for Team={Team}, Position={Position}", 
                    request.ShooterMemberId, request.ShooterClass, request.TeamNumber, request.Position);

                // Calculate totals from string shots
                var total = 0;
                var xCount = 0;
                foreach (var shot in request.Shots)
                {
                    if (shot.ToUpper() == "X")
                    {
                        total += 10;
                        xCount++;
                    }
                    else if (int.TryParse(shot, out int value) && value >= 0 && value <= 10)
                    {
                        total += value;
                    }
                }

                // Save result to database
                _logger.LogInformation("Attempting to save result to database for shooter {TeamNumber}-{Position}", request.TeamNumber, request.Position);
                var resultId = await SaveResultToDatabase(request);
                _logger.LogInformation("Database save completed with resultId: {ResultId} for shooter {TeamNumber}-{Position}", resultId, request.TeamNumber, request.Position);

                if (resultId <= 0)
                {
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = resultId == -1
                            ? "Resultatet sparades redan av en annan funktionär. Försök igen."
                            : "Ett fel uppstod vid sparande av resultatet."
                    });
                }

                // Invalidate series results cache (if competition is part of a series)
                try
                {
                    _seriesCalculationService.InvalidateCacheForCompetition(request.CompetitionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to invalidate series cache after result save, continuing");
                }

                // Update the live leaderboard in Umbraco content
                _logger.LogInformation("Attempting to update live leaderboard for competition {CompetitionId}", request.CompetitionId);
                try
                {
                    await UpdateLiveLeaderboard(request.CompetitionId);
                    _logger.LogInformation("Successfully updated live leaderboard for competition {CompetitionId}", request.CompetitionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update live leaderboard, but continuing with result save. Error: {ErrorMessage}", ex.Message);
                    // Continue execution - don't fail the entire save operation
                }

                // TODO: Broadcast live update via SignalR
                // await _hubContext.Clients.Group($"Competition_{request.CompetitionId}")
                //     .SendAsync("ResultUpdated", new ResultUpdate { ... });

                return Json(new ResultEntryResponse
                {
                    Success = true,
                    Message = "Resultat sparat framgångsrikt!",
                    ResultId = resultId,
                    Total = total,
                    XCount = xCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving result for competition {CompetitionId}, team {TeamNumber}, position {Position}. Error: {ErrorMessage}",
                    request.CompetitionId, request.TeamNumber, request.Position, ex.Message);

                return Json(new ResultEntryResponse
                {
                    Success = false,
                    Message = $"Ett fel uppstod vid sparande av resultatet: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Report a result for a distributed (self-reporting) competition.
        /// Only club admins, skjutledare, competition managers, and site admins can use this.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReportDistributedResult([FromBody] DistributedResultRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.TargetMemberId <= 0 ||
                    request.SeriesNumber <= 0 || request.Shots == null || request.Shots.Length != 5 ||
                    string.IsNullOrEmpty(request.ShootingClass))
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Ogiltig begäran. Kontrollera att alla fält är ifyllda." });
                }

                // Validate shots
                foreach (var shot in request.Shots)
                {
                    if (string.IsNullOrEmpty(shot)) continue;
                    var upper = shot.ToUpper();
                    if (upper != "X" && (!int.TryParse(shot, out int val) || val < 0 || val > 10))
                    {
                        return Json(new ResultEntryResponse { Success = false, Message = $"Ogiltigt skottvärde: {shot}" });
                    }
                }

                // 1. Competition must exist and have allowSelfReporting enabled
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Tävlingen hittades inte." });
                }

                if (!competition.GetValue<bool>("allowSelfReporting"))
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Denna tävling tillåter inte resultatrapportering." });
                }

                if (competition.GetValue<bool>("isExternal"))
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Externa tävlingar stöder inte resultatrapportering." });
                }

                // 2. Check date range: must be within competitionDate → competitionEndDate
                var compDate = competition.GetValue<DateTime?>("competitionDate");
                var compEndDate = competition.GetValue<DateTime?>("competitionEndDate");
                var now = DateTime.Now;

                if (compDate.HasValue)
                {
                    var effectiveEnd = (compEndDate.HasValue && compEndDate.Value.Year > 1900)
                        ? compEndDate.Value.Date.AddDays(1) // Include the end date (end of day)
                        : compDate.Value.Date.AddDays(1);

                    if (now.Date < compDate.Value.Date || now >= effectiveEnd)
                    {
                        return Json(new ResultEntryResponse
                        {
                            Success = false,
                            Message = "Resultatrapportering är bara möjlig under tävlingsperioden."
                        });
                    }
                }

                // 3. Validate series number
                var maxSeries = competition.GetValue<int>("numberOfSeriesOrStations");
                if (maxSeries <= 0) maxSeries = 6;
                if (request.SeriesNumber > maxSeries)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = $"Serie {request.SeriesNumber} överskrider max antal serier ({maxSeries})." });
                }

                // 4. Authorization: caller must be club admin/skjutledare for target's club, or competition manager, or site admin
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Du måste vara inloggad." });
                }

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Kunde inte hitta din profil." });
                }

                bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(request.CompetitionId);

                // Get target member's club
                var targetMember = _memberService.GetById(request.TargetMemberId);
                if (targetMember == null)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Målmedlemmen hittades inte." });
                }

                bool isAuthorized = isSiteAdmin || isCompetitionManager;

                if (!isAuthorized)
                {
                    // Check if caller is club admin or skjutledare for the target member's club
                    var targetClubIdStr = targetMember.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(targetClubIdStr) && int.TryParse(targetClubIdStr, out int targetClubId) && targetClubId > 0)
                    {
                        bool isClubAdmin = await _adminAuthorizationService.IsClubAdminForClub(targetClubId);
                        bool isSkjutledare = await _adminAuthorizationService.IsSkjutledareForClub(targetClubId);
                        isAuthorized = isClubAdmin || isSkjutledare;
                    }
                }

                if (!isAuthorized)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Du har inte behörighet att rapportera resultat för denna skytt." });
                }

                // 5. Target member must be registered in the specified shooting class
                var registrations = await _startListRepository.GetCompetitionRegistrations(request.CompetitionId);
                var isRegistered = registrations.Any(r =>
                    r.MemberId == request.TargetMemberId &&
                    r.MemberClass == request.ShootingClass &&
                    r.IsActive);

                if (!isRegistered)
                {
                    return Json(new ResultEntryResponse { Success = false, Message = "Skytten är inte anmäld i angiven klass." });
                }

                // 6. Delegate to existing SaveResultToDatabase
                var entryRequest = new ResultEntryRequest
                {
                    CompetitionId = request.CompetitionId,
                    SeriesNumber = request.SeriesNumber,
                    Shots = request.Shots,
                    ShooterMemberId = request.TargetMemberId,
                    ShooterClass = request.ShootingClass,
                    TeamNumber = 0,
                    Position = 0,
                    RangeOfficerId = memberData.Id
                };

                var resultId = await SaveResultToDatabase(entryRequest);
                if (resultId <= 0)
                {
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = resultId == -1
                            ? "Resultatet sparades redan av en annan funktionär. Försök igen."
                            : "Kunde inte spara resultatet."
                    });
                }

                // 7. Invalidate caches + update live leaderboard
                try { _seriesCalculationService.InvalidateCacheForCompetition(request.CompetitionId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to invalidate series cache after distributed result save"); }

                try { await UpdateLiveLeaderboard(request.CompetitionId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to update live leaderboard after distributed result save"); }

                // Calculate totals for response
                var total = 0;
                var xCount = 0;
                foreach (var shot in request.Shots)
                {
                    if (shot.ToUpper() == "X") { total += 10; xCount++; }
                    else if (int.TryParse(shot, out int value) && value >= 0 && value <= 10) { total += value; }
                }

                return Json(new ResultEntryResponse
                {
                    Success = true,
                    Message = "Resultat sparat!",
                    ResultId = resultId,
                    Total = total,
                    XCount = xCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ReportDistributedResult for competition {CompetitionId}, member {MemberId}",
                    request?.CompetitionId, request?.TargetMemberId);
                return Json(new ResultEntryResponse { Success = false, Message = "Ett oväntat fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Get distributed result status — returns members the caller can enter for
        /// and their already-saved series.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDistributedStatus(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Tävlingen hittades inte." });
                }

                if (!competition.GetValue<bool>("allowSelfReporting"))
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Denna tävling tillåter inte resultatrapportering." });
                }

                // Check date range
                var compDate = competition.GetValue<DateTime?>("competitionDate");
                var compEndDate = competition.GetValue<DateTime?>("competitionEndDate");
                var now = DateTime.Now;
                bool isActive = true;

                if (compDate.HasValue)
                {
                    var effectiveEnd = (compEndDate.HasValue && compEndDate.Value.Year > 1900)
                        ? compEndDate.Value.Date.AddDays(1)
                        : compDate.Value.Date.AddDays(1);
                    isActive = now.Date >= compDate.Value.Date && now < effectiveEnd;
                }

                // Authorization
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Du måste vara inloggad." });
                }

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Kunde inte hitta din profil." });
                }

                bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(competitionId);

                // Get caller's managed clubs and skjutledare clubs
                var managedClubIds = await _adminAuthorizationService.GetManagedClubIds();
                var skjutledareClubIds = await _adminAuthorizationService.GetSkjutledareClubIds();
                var allAuthorizedClubIds = new HashSet<int>(managedClubIds);
                foreach (var id in skjutledareClubIds) allAuthorizedClubIds.Add(id);

                if (!isSiteAdmin && !isCompetitionManager && !allAuthorizedClubIds.Any())
                {
                    return Json(new DistributedStatusResponse { Success = false, Message = "Du har inte behörighet." });
                }

                // Build available classes from competition's shootingClassIds
                var availableClasses = new List<AvailableClass>();
                var classIdsRaw = competition.GetValue<string>("shootingClassIds");
                if (!string.IsNullOrEmpty(classIdsRaw))
                {
                    string[] classIdArray;
                    if (classIdsRaw.TrimStart().StartsWith("["))
                    {
                        try { classIdArray = System.Text.Json.JsonSerializer.Deserialize<string[]>(classIdsRaw) ?? Array.Empty<string>(); }
                        catch { classIdArray = Array.Empty<string>(); }
                    }
                    else
                    {
                        classIdArray = classIdsRaw.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                    }

                    foreach (var classId in classIdArray)
                    {
                        var sc = HpskSite.Models.ShootingClasses.GetById(classId);
                        availableClasses.Add(new AvailableClass
                        {
                            Id = classId,
                            Name = sc?.Name ?? classId
                        });
                    }
                }

                // Build authorized clubs — anyone who can access distributed entry
                // can quick-register shooters from any club (open competitions)
                var authorizedClubs = new List<AuthorizedClub>();
                foreach (var club in _clubService.GetAllClubs())
                {
                    authorizedClubs.Add(new AuthorizedClub { Id = club.Id, Name = club.Name });
                }

                var callerPrimaryClubId = int.TryParse(memberData.GetValue<string>("primaryClubId"), out int cpId) ? cpId : 0;

                // Get all registrations
                var maxSeries = competition.GetValue<int>("numberOfSeriesOrStations");
                if (maxSeries <= 0) maxSeries = 6;

                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                if (registrations == null || !registrations.Any())
                {
                    return Json(new DistributedStatusResponse
                    {
                        Success = true,
                        IsActive = isActive,
                        MaxSeries = maxSeries,
                        Members = new List<DistributedMemberStatus>(),
                        AvailableClasses = availableClasses,
                        AuthorizedClubs = authorizedClubs.OrderBy(c => c.Name).ToList(),
                        CallerClubId = callerPrimaryClubId
                    });
                }

                // Filter to members the caller can enter for
                var authorizedRegistrations = registrations
                    .Where(r => r.IsActive)
                    .Where(r =>
                    {
                        if (isSiteAdmin || isCompetitionManager) return true;

                        // Check if target member's club is in caller's authorized clubs
                        var targetMember = _memberService.GetById(r.MemberId);
                        if (targetMember == null) return false;
                        var clubIdStr = targetMember.GetValue<string>("primaryClubId");
                        if (int.TryParse(clubIdStr, out int clubId))
                        {
                            return allAuthorizedClubIds.Contains(clubId);
                        }
                        return false;
                    })
                    .ToList();

                // Get existing results from database (route to correct table)
                var existingResults = new List<PrecisionResultEntry>();
                using (var db = _umbracoDatabaseFactory.CreateDatabase())
                {
                    var compTypeId = GetCompetitionTypeId(competitionId);
                    if (compTypeId == "Milsnabb")
                    {
                        var milsnabbResults = await db.FetchAsync<MilsnabbResultEntry>(
                            "WHERE CompetitionId = @0", competitionId);
                        existingResults = milsnabbResults.Cast<PrecisionResultEntry>().ToList();
                    }
                    else if (compTypeId == "Duell")
                    {
                        var duellResults = await db.FetchAsync<DuellResultEntry>(
                            "WHERE CompetitionId = @0", competitionId);
                        existingResults = duellResults.Cast<PrecisionResultEntry>().ToList();
                    }
                    else if (compTypeId == "NationellHelmatch")
                    {
                        var nhResults = await db.FetchAsync<NationellHelmatchResultEntry>(
                            "WHERE CompetitionId = @0", competitionId);
                        existingResults = nhResults.Cast<PrecisionResultEntry>().ToList();
                    }
                    else if (compTypeId == "MagnumPrecision")
                    {
                        var mpResults = await db.FetchAsync<MagnumPrecisionResultEntry>(
                            "WHERE CompetitionId = @0", competitionId);
                        existingResults = mpResults.Cast<PrecisionResultEntry>().ToList();
                    }
                    else
                    {
                        existingResults = await db.FetchAsync<PrecisionResultEntry>(
                            "WHERE CompetitionId = @0", competitionId);
                    }
                }

                var entererNameMap = BuildEntererNameMap(existingResults.Select(e => e.EnteredBy));

                var members = authorizedRegistrations
                    .Select(r =>
                    {
                        var memberResults = existingResults
                            .Where(e => e.MemberId == r.MemberId && e.ShootingClass == r.MemberClass)
                            .OrderBy(e => e.SeriesNumber)
                            .Select(e =>
                            {
                                string[] shots;
                                try { shots = JsonConvert.DeserializeObject<string[]>(e.Shots) ?? Array.Empty<string>(); }
                                catch { shots = Array.Empty<string>(); }

                                var total = shots.Sum(s => s.ToUpper() == "X" ? 10 : (int.TryParse(s, out int v) ? v : 0));
                                var xCount = shots.Count(s => s.ToUpper() == "X");

                                return new DistributedSeriesStatus
                                {
                                    SeriesNumber = e.SeriesNumber,
                                    Total = total,
                                    XCount = xCount,
                                    Shots = shots,
                                    EnteredByName = entererNameMap.TryGetValue(e.EnteredBy, out var n) ? n : ""
                                };
                            })
                            .ToList();

                        return new DistributedMemberStatus
                        {
                            MemberId = r.MemberId,
                            Name = r.MemberName ?? "",
                            Club = r.MemberClub ?? "",
                            ShootingClass = r.MemberClass,
                            CompletedSeries = memberResults
                        };
                    })
                    .OrderBy(m => m.Club)
                    .ThenBy(m => m.Name)
                    .ToList();

                return Json(new DistributedStatusResponse
                {
                    Success = true,
                    IsActive = isActive,
                    MaxSeries = maxSeries,
                    Members = members,
                    AvailableClasses = availableClasses,
                    AuthorizedClubs = authorizedClubs.OrderBy(c => c.Name).ToList(),
                    CallerClubId = callerPrimaryClubId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetDistributedStatus for competition {CompetitionId}", competitionId);
                return Json(new DistributedStatusResponse { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Quick-register a brand new shooter: creates member account + registers for competition.
        /// For use by club admins / skjutledare / competition managers at the range.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickRegisterShooter([FromBody] QuickRegisterRequest request)
        {
            try
            {
                // Basic validation
                if (request == null || request.CompetitionId <= 0 ||
                    string.IsNullOrWhiteSpace(request.FirstName) ||
                    string.IsNullOrWhiteSpace(request.LastName) ||
                    string.IsNullOrWhiteSpace(request.Email) ||
                    request.ClubId <= 0 ||
                    string.IsNullOrWhiteSpace(request.ShootingClass))
                {
                    return Json(new { success = false, message = "Alla fält måste fyllas i." });
                }

                var email = request.Email.Trim().ToLowerInvariant();
                var firstName = request.FirstName.Trim();
                var lastName = request.LastName.Trim();
                var fullName = $"{firstName} {lastName}";

                // Validate email format
                try { var addr = new System.Net.Mail.MailAddress(email); }
                catch { return Json(new { success = false, message = "Ogiltig e-postadress." }); }

                // 1. Competition validation
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                if (!competition.GetValue<bool>("allowSelfReporting"))
                    return Json(new { success = false, message = "Denna tävling tillåter inte resultatrapportering." });

                if (competition.GetValue<bool>("isExternal"))
                    return Json(new { success = false, message = "Externa tävlingar stöder inte denna funktion." });

                // Check date range
                var compDate = competition.GetValue<DateTime?>("competitionDate");
                var compEndDate = competition.GetValue<DateTime?>("competitionEndDate");
                var now = DateTime.Now;
                if (compDate.HasValue)
                {
                    var effectiveEnd = (compEndDate.HasValue && compEndDate.Value.Year > 1900)
                        ? compEndDate.Value.Date.AddDays(1)
                        : compDate.Value.Date.AddDays(1);
                    if (now.Date < compDate.Value.Date || now >= effectiveEnd)
                        return Json(new { success = false, message = "Registrering är bara möjlig under tävlingsperioden." });
                }

                // 2. Validate shooting class is valid for this competition
                var classIdsRaw = competition.GetValue<string>("shootingClassIds");
                var validClassIds = new List<string>();
                if (!string.IsNullOrEmpty(classIdsRaw))
                {
                    if (classIdsRaw.TrimStart().StartsWith("["))
                    {
                        try { validClassIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(classIdsRaw) ?? new List<string>(); }
                        catch { /* empty */ }
                    }
                    else
                    {
                        validClassIds = classIdsRaw.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }
                }
                if (!validClassIds.Contains(request.ShootingClass))
                    return Json(new { success = false, message = "Ogiltigt val av skytteklass." });

                // 3. Authorization
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var callerData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (callerData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(request.CompetitionId);

                // Auth: caller must have distributed entry access (club admin or skjutledare for ANY of their clubs)
                // This allows open competitions where a skjutledare registers shooters from other clubs
                var managedClubIds = await _adminAuthorizationService.GetManagedClubIds();
                var skjutledareClubIds = await _adminAuthorizationService.GetSkjutledareClubIds();
                bool isClubAdmin = managedClubIds.Contains(request.ClubId);
                bool hasAnyClubRole = managedClubIds.Any() || skjutledareClubIds.Any();

                if (!isSiteAdmin && !isCompetitionManager && !hasAnyClubRole)
                    return Json(new { success = false, message = "Du har inte behörighet att registrera skyttar." });

                // 4. Check email uniqueness
                var existingMember = _memberService.GetByEmail(email);
                if (existingMember != null)
                    return Json(new { success = false, message = "Det finns redan en medlem med denna e-postadress." });

                // 5. Create member (include invitation token in the single save)
                var invitationToken = Guid.NewGuid().ToString("N");
                var tokenExpiry = DateTime.UtcNow.AddDays(7);

                var newMember = _memberService.CreateMember(email, email, fullName, "hpskMember");
                newMember.SetValue("firstName", firstName);
                newMember.SetValue("lastName", lastName);
                newMember.SetValue("primaryClubId", request.ClubId);
                newMember.SetValue("invitationToken", invitationToken);
                newMember.SetValue("invitationTokenExpiry", tokenExpiry.ToString("o"));
                newMember.IsApproved = true;
                _memberService.Save(newMember);
                _memberService.AssignRole(newMember.Id, "Users");

                _logger.LogInformation("QuickRegisterShooter: Created member {MemberId} ({Name}) for club {ClubId}",
                    newMember.Id, fullName, request.ClubId);

                // 6. Register for competition (create registration document)
                IContent registrationsHub;
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                var existingHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                if (existingHub != null)
                {
                    registrationsHub = existingHub;
                }
                else
                {
                    var hubContentType = _contentTypeService.Get("competitionRegistrationsHub")
                                      ?? _contentTypeService.Get("contentPage");
                    var newHub = _contentService.Create("Anmälningar", competition, hubContentType.Alias);
                    if (hubContentType.Alias == "contentPage")
                    {
                        newHub.SetValue("pageTitle", "Anmälningar");
                        newHub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
                    }
                    _contentService.Save(newHub);
                    _contentService.Publish(newHub, new[] { "*" }, -1);
                    registrationsHub = newHub;
                }

                var shootingClassesArray = new[] { new { @class = request.ShootingClass, startPreference = "Inget" } };
                var shootingClassesJson = System.Text.Json.JsonSerializer.Serialize(shootingClassesArray);

                var registrationName = $"{fullName} - {DateTime.Now:yyyy-MM-dd}";
                var registration = _contentService.Create(registrationName, registrationsHub, "competitionRegistration");
                registration.SetValue("competitionId", request.CompetitionId);
                registration.SetValue("memberId", newMember.Id);
                registration.SetValue("memberName", fullName);
                registration.SetValue("isActive", true);
                registration.SetValue("clubId", request.ClubId);
                registration.SetValue("shootingClasses", shootingClassesJson);
                registration.SetValue("registrationDate", DateTime.Now);
                registration.SetValue("registeredBy", $"{callerData.Name} (snabbregistrering)");
                _contentService.Save(registration);
                _contentService.Publish(registration, new[] { "*" });

                _logger.LogInformation("QuickRegisterShooter: Created registration {RegId} for member {MemberId} in competition {CompId}",
                    registration.Id, newMember.Id, request.CompetitionId);

                // 7. Send invitation email (all DB work is done, only SMTP remains)
                var invitationClubName = _clubService.GetClubNameById(request.ClubId) ?? "din klubb";
                try
                {
                    await _emailService.SendMemberInvitationAsync(email, fullName, invitationToken, invitationClubName);
                    _logger.LogInformation("QuickRegisterShooter: Invitation email sent to {Email}", email);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "QuickRegisterShooter: Failed to send invitation email to {Email}", email);
                }

                // 8. If caller is NOT a club admin, notify club admins
                if (!isClubAdmin && !isSiteAdmin)
                {
                    try
                    {
                        var clubAdminGroupName = $"ClubAdmin_{request.ClubId}";

                        // Direct SQL instead of GetAll + N+1 GetAllRoles (which causes SQL timeout)
                        List<string> clubAdminEmails;
                        using (var db = _umbracoDatabaseFactory.CreateDatabase())
                        {
                            clubAdminEmails = await db.FetchAsync<string>(@"
                                SELECT DISTINCT cm.Email
                                FROM cmsMember2MemberGroup m2g
                                INNER JOIN cmsMember cm ON m2g.Member = cm.nodeId
                                INNER JOIN umbracoNode grp ON m2g.MemberGroup = grp.id
                                WHERE grp.text = @0
                                  AND cm.Email IS NOT NULL AND cm.Email <> ''",
                                clubAdminGroupName);
                        }

                        if (clubAdminEmails.Any())
                        {
                            var callerName = $"{callerData.GetValue<string>("firstName")} {callerData.GetValue<string>("lastName")}".Trim();
                            if (string.IsNullOrEmpty(callerName)) callerName = callerData.Name;
                            var clubName = _clubService.GetClubNameById(request.ClubId) ?? "Okänd klubb";
                            await _emailService.SendMemberAddedByNonAdminAsync(clubAdminEmails, fullName, callerName, clubName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "QuickRegisterShooter: Failed to send club admin notification");
                    }
                }

                // 9. Return the new member in distributed status format
                var clubDisplayName = _clubService.GetClubNameById(request.ClubId) ?? "";
                var sc = HpskSite.Models.ShootingClasses.GetById(request.ShootingClass);

                return Json(new
                {
                    success = true,
                    message = $"{fullName} har registrerats och anmälts till tävlingen.",
                    member = new DistributedMemberStatus
                    {
                        MemberId = newMember.Id,
                        Name = fullName,
                        Club = clubDisplayName,
                        ShootingClass = request.ShootingClass,
                        CompletedSeries = new List<DistributedSeriesStatus>()
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in QuickRegisterShooter");
                return Json(new { success = false, message = "Ett oväntat fel uppstod: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetLiveLeaderboard(int competitionId)
        {
            try
            {
                var results = await GetCompetitionResultsInternal(competitionId);
                var leaderboard = await CalculateLeaderboard(results, competitionId);

                return Json(new { Success = true, Leaderboard = leaderboard });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting live leaderboard for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid hämtning av resultat." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompetitionResults(int competitionId)
        {
            try
            {
                var results = await GetCompetitionResultsInternal(competitionId);
                var nameMap = BuildEntererNameMap(results.Select(r => r.EnteredBy));

                var projected = results.Select(r => new
                {
                    r.Id,
                    r.CompetitionId,
                    r.SeriesNumber,
                    r.MemberId,
                    r.TeamNumber,
                    r.Position,
                    r.ShootingClass,
                    r.Shots,
                    r.EnteredBy,
                    EnteredByName = nameMap.TryGetValue(r.EnteredBy, out var n) ? n : "",
                    r.EnteredAt,
                    r.LastModified
                });

                return Json(new { Success = true, Results = projected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting competition results for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Error loading results" });
            }
        }

        /// <summary>
        /// Resolve a set of MemberIds to compact display names ("Stefan R.").
        /// Single in-memory pass over IMemberService — avoids N+1 lookups in callers.
        /// </summary>
        private Dictionary<int, string> BuildEntererNameMap(IEnumerable<int> memberIds)
        {
            var map = new Dictionary<int, string>();
            foreach (var id in memberIds.Where(i => i > 0).Distinct())
            {
                try
                {
                    var m = _memberService.GetById(id);
                    if (m != null)
                    {
                        map[id] = FormatCompactName(m.Name ?? "");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve enterer name for MemberId {MemberId}", id);
                }
            }
            return map;
        }

        /// <summary>
        /// "Stefan Rehnberg" -> "Stefan R."  (single name -> as-is, empty -> empty)
        /// </summary>
        private static string FormatCompactName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "";
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0];
            return parts[0] + " " + parts[^1][0] + ".";
        }

        /// <summary>
        /// Get the current user's results for a specific competition and shooting class
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyCompetitionResult(int competitionId, string shootingClass)
        {
            try
            {
                // Get current member
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad för att se dina resultat." });
                }

                var memberId = member.Id;

                // Get competition info
                var competition = _contentService.GetById(competitionId);
                var competitionName = competition?.Name ?? "Tävling";
                var competitionDateValue = competition?.GetValue("competitionDate");
                DateTime? competitionDate = competitionDateValue != null ? (DateTime?)competitionDateValue : null;

                // Query this member's results for the requested class. A member can be registered
                // in more than one class for the same competition (e.g. A and C); without the class
                // filter their series get merged into one bogus combined result. Fall back to no
                // class filter only when the caller didn't supply one (legacy callers).
                // Route to correct table based on competition type.
                using var database = _umbracoDatabaseFactory.CreateDatabase();
                var resultTable = GetResultTableName(GetCompetitionTypeId(competitionId));
                var hasClass = !string.IsNullOrWhiteSpace(shootingClass);
                var query = hasClass
                    ? $"SELECT * FROM [{resultTable}] WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2 ORDER BY SeriesNumber"
                    : $"SELECT * FROM [{resultTable}] WHERE CompetitionId = @0 AND MemberId = @1 ORDER BY SeriesNumber";

                var results = hasClass
                    ? await database.FetchAsync<PrecisionResultEntry>(query, competitionId, memberId, shootingClass)
                    : await database.FetchAsync<PrecisionResultEntry>(query, competitionId, memberId);

                if (!results.Any())
                {
                    return Json(new { success = false, message = "Inga resultat hittades för denna tävling." });
                }

                // Get actual shooting class from results (not from input parameter)
                var actualShootingClass = results.First().ShootingClass;

                // Derive weapon class via the registry (so A_opt_X correctly maps to "A_Opt").
                var weaponClass = ShootingClasses.GetWeaponClassCode(actualShootingClass);
                if (string.IsNullOrEmpty(weaponClass)) weaponClass = "?";

                // Placement + standard medal from the published result snapshot (if any), so the
                // detail modal can show a "Tävlingsresultat" section like self-reported results do.
                int? placement = null;
                string? standardMedal = null;
                var resultPage = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                var snapshotJson = resultPage?.GetValue<string>("resultData");
                if (!string.IsNullOrEmpty(snapshotJson))
                {
                    try
                    {
                        var snapshot = JsonConvert.DeserializeObject<FinalResults>(snapshotJson);
                        if (snapshot?.ClassGroups != null)
                        {
                            foreach (var grp in snapshot.ClassGroups)
                            {
                                var idx = grp.Shooters.FindIndex(s => s.MemberId.ToString() == memberId
                                    && string.Equals(s.ShootingClass, actualShootingClass, StringComparison.OrdinalIgnoreCase));
                                if (idx >= 0)
                                {
                                    placement = idx + 1;
                                    standardMedal = grp.Shooters[idx].StandardMedal;
                                    break;
                                }
                            }
                        }
                    }
                    catch { /* snapshot shape mismatch — leave place/medal null */ }
                }

                // Build series data
                var series = results.Select(r => {
                    var shots = ParseShots(r.Shots);
                    return new {
                        seriesNumber = r.SeriesNumber,
                        shots = shots,
                        total = CalculateSeriesTotalFromShots(shots),
                        xCount = CountXFromShots(shots)
                    };
                }).ToList();

                var totalScore = series.Sum(s => s.total);
                var totalX = series.Sum(s => s.xCount);

                return Json(new {
                    success = true,
                    competitionName,
                    competitionDate,
                    shootingClass = actualShootingClass,
                    weaponClass,
                    place = placement,
                    standardMedal,
                    series,
                    totalScore,
                    xCount = totalX,
                    seriesCount = series.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user's competition results for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel vid inläsning av resultat." });
            }
        }

        private string[] ParseShots(string shotsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shotsJson))
                    return Array.Empty<string>();

                return Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private int CalculateSeriesTotalFromShots(string[] shots)
        {
            return shots.Sum(shot => shot.ToUpper() == "X" ? 10 : (int.TryParse(shot, out int value) ? value : 0));
        }

        private int CountXFromShots(string[] shots)
        {
            return shots.Count(shot => shot.ToUpper() == "X");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteResult([FromBody] DeleteResultRequest request)
        {
            try
            {
                _logger.LogInformation("DeleteResult called with request: CompetitionId={CompetitionId}, SeriesNumber={SeriesNumber}, TeamNumber={TeamNumber}, Position={Position}",
                    request?.CompetitionId, request?.SeriesNumber, request?.TeamNumber, request?.Position);

                if (!ValidateDeleteRequest(request))
                {
                    _logger.LogWarning("Validation failed for delete request: CompetitionId={CompetitionId}, SeriesNumber={SeriesNumber}, TeamNumber={TeamNumber}, Position={Position}",
                        request?.CompetitionId, request?.SeriesNumber, request?.TeamNumber, request?.Position);

                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = "Ogiltig begäran. Kontrollera att alla fält är korrekt ifyllda."
                    });
                }

                // Delete result from database
                _logger.LogInformation("Attempting to delete result from database for shooter {TeamNumber}-{Position}", request.TeamNumber, request.Position);
                var deleted = await DeleteResultFromDatabase(request);
                _logger.LogInformation("Database delete completed for shooter {TeamNumber}-{Position}: {Deleted}", request.TeamNumber, request.Position, deleted);

                if (!deleted)
                {
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = "Inget resultat hittades att ta bort."
                    });
                }

                // Invalidate series results cache (if competition is part of a series)
                try
                {
                    _seriesCalculationService.InvalidateCacheForCompetition(request.CompetitionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to invalidate series cache after result delete, continuing");
                }

                // Update the live leaderboard in Umbraco content
                _logger.LogInformation("Attempting to update live leaderboard after deletion for competition {CompetitionId}", request.CompetitionId);
                try
                {
                    await UpdateLiveLeaderboard(request.CompetitionId);
                    _logger.LogInformation("Successfully updated live leaderboard after deletion for competition {CompetitionId}", request.CompetitionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update live leaderboard after deletion, but continuing. Error: {ErrorMessage}", ex.Message);
                    // Continue execution - don't fail the entire delete operation
                }

                return Json(new ResultEntryResponse
                {
                    Success = true,
                    Message = "Resultat borttaget framgångsrikt!"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting result for competition {CompetitionId}, team {TeamNumber}, position {Position}. Error: {ErrorMessage}",
                    request.CompetitionId, request.TeamNumber, request.Position, ex.Message);

                return Json(new ResultEntryResponse
                {
                    Success = false,
                    Message = $"Ett fel uppstod vid borttagning av resultatet: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Remove ALL series results for one shooter in one shooting class for a competition.
        /// Works regardless of whether a start list exists (covers self-reporting / hemmabana
        /// competitions where the per-series CLEAR flow is not available). Identity-based:
        /// keyed on (CompetitionId, MemberId, ShootingClass).
        /// Springskytte and Fältskytte have their own controllers and storage and are not handled here.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteShooterFromClass([FromBody] DeleteShooterFromClassRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 ||
                    string.IsNullOrEmpty(request.ShootingClass))
                {
                    return Json(new { success = false, message = "Ogiltig begäran." });
                }

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen hittades inte." });
                }

                bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(request.CompetitionId);

                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _adminAuthorizationService.IsClubAdminForClub(competitionClubId);
                    isSkjutledare = await _adminAuthorizationService.IsSkjutledareForClub(competitionClubId);
                }

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Json(new { success = false, message = "Du har inte behörighet att ta bort resultat." });
                }

                var compTypeId = GetCompetitionTypeId(request.CompetitionId);
                if (compTypeId is "Springskytte" or "Faltskytte" or "MagnumFalt")
                {
                    return Json(new { success = false, message = $"Borttagning av resultat för {compTypeId} hanteras i dess egen vy." });
                }

                var tableName = GetResultTableName(compTypeId);

                // The result list JSON stores the display Name ("C 1"); the DB stores the Id ("C1").
                // Accept either by normalizing through the registry. Fall back to the raw input
                // for unknown values (e.g. legacy/custom rows) so the existing behaviour is preserved.
                var resolvedClass = ShootingClasses.GetById(request.ShootingClass)?.Id
                                 ?? ShootingClasses.GetByName(request.ShootingClass)?.Id
                                 ?? request.ShootingClass;

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var rowsDeleted = await db.ExecuteAsync(
                    $"DELETE FROM [{tableName}] WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2",
                    request.CompetitionId, request.MemberId, resolvedClass);

                _logger.LogInformation(
                    "DeleteShooterFromClass: removed {Rows} rows from {Table} for competition {CompetitionId}, member {MemberId}, class '{RequestedClass}' (resolved to '{ResolvedClass}')",
                    rowsDeleted, tableName, request.CompetitionId, request.MemberId, request.ShootingClass, resolvedClass);

                if (rowsDeleted == 0)
                {
                    return Json(new { success = false, message = "Inga resultat hittades att ta bort." });
                }

                try { _seriesCalculationService.InvalidateCacheForCompetition(request.CompetitionId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to invalidate series cache after DeleteShooterFromClass"); }

                try { await UpdateLiveLeaderboard(request.CompetitionId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to update live leaderboard after DeleteShooterFromClass"); }

                // Refresh the persisted Slutresultat snapshot so officially-marked pages also reflect the deletion.
                try
                {
                    var dbResults = await GetCompetitionResultsInternal(request.CompetitionId);
                    var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                    if (resultPage != null)
                    {
                        var mergeConfigJson = resultPage.GetValue<string>("mergeConfig");
                        List<ClassMergeAction>? storedMerges = null;
                        if (!string.IsNullOrEmpty(mergeConfigJson))
                        {
                            storedMerges = JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfigJson);
                        }

                        var finalResults = await CalculateFinalResults(dbResults, request.CompetitionId, storedMerges);
                        var existingIsOfficial = resultPage.GetValue<bool>("isOfficial");

                        resultPage.SetValue("resultData", JsonConvert.SerializeObject(finalResults));
                        resultPage.SetValue("lastUpdated", DateTime.Now);
                        resultPage.SetValue("isOfficial", existingIsOfficial);

                        _contentService.Save(resultPage);
                        _contentService.Publish(resultPage, new[] { "*" }, -1);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh final results page after DeleteShooterFromClass");
                }

                return Json(new { success = true, message = $"{rowsDeleted} resultatrad(er) borttagna.", rowsDeleted });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DeleteShooterFromClass for competition {CompetitionId}, member {MemberId}, class {Class}",
                    request?.CompetitionId, request?.MemberId, request?.ShootingClass);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetResultsDebug(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Get all tables
                var tables = await db.ExecuteScalarAsync<string>("SELECT STRING_AGG(TABLE_NAME, ', ') FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME LIKE '%Result%'");
                
                // Get count from PrecisionResultEntry table
                var precisionResultEntryCount = 0;

                try { precisionResultEntryCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PrecisionResultEntry WHERE CompetitionId = @0", competitionId); } catch { }

                return Json(new
                {
                    Success = true,
                    TablesContainingResult = tables,
                    PrecisionResultEntryCount = precisionResultEntryCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in debug query");
                return Json(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> TestDatabaseConnection()
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Simple test query
                var result = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PrecisionResultEntry");

                return Json(new
                {
                    Success = true,
                    Message = "Database connection successful",
                    RecordCount = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database connection test failed");
                return Json(new
                {
                    Success = false,
                    Message = $"Database connection failed: {ex.Message}"
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetResultsStats(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                var results = await GetCompetitionResultsInternal(competitionId);
                
                return Json(new
                {
                    Success = true,
                    Count = results.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting results stats for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Error loading results stats" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentUserId()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { Success = false, Message = "User not logged in" });
                }

                var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (currentMemberData == null)
                {
                    return Json(new { Success = false, Message = "User data not found" });
                }

                return Json(new { Success = true, UserId = currentMemberData.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current user ID");
                return Json(new { Success = false, Message = "Error getting user ID" });
            }
        }

        /// <summary>
        /// Get shooters for results entry from registrations, with optional start list ordering.
        /// This allows results entry to work without a start list, while still supporting
        /// start list order for data entry if one exists.
        /// </summary>
        /// <param name="competitionId">Competition ID</param>
        /// <param name="orderBy">Order: "registration" (default) or "startlist"</param>
        [HttpGet]
        public async Task<IActionResult> GetShootersForResultsEntry(int competitionId, string orderBy = "registration")
        {
            try
            {
                _logger.LogInformation("GetShootersForResultsEntry called for competition {CompetitionId}, orderBy={OrderBy}", competitionId, orderBy);

                if (competitionId <= 0)
                {
                    return Json(new ShootersForResultsEntryResponse
                    {
                        Success = false,
                        Message = "Ogiltigt tävlings-ID."
                    });
                }

                // 1. Get all active registrations
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);

                if (registrations == null || !registrations.Any())
                {
                    return Json(new ShootersForResultsEntryResponse
                    {
                        Success = false,
                        Message = "Inga registreringar hittades för denna tävling."
                    });
                }

                _logger.LogInformation("Found {Count} registrations for competition {CompetitionId}", registrations.Count, competitionId);

                // 2. Always check if start list exists (so frontend knows whether to warn)
                var startListData = await GetOfficialStartListConfiguration(competitionId);
                bool hasStartList = startListData?.Teams != null && startListData.Teams.Any();
                _logger.LogInformation("Competition {CompetitionId} hasStartList: {HasStartList}", competitionId, hasStartList);

                // 3. Order shooters based on requested order
                var shooters = new List<ShooterEntryInfo>();

                if (orderBy == "startlist" && hasStartList)
                {
                    // Order registrations by start list (team number, position)
                    shooters = OrderRegistrationsByStartList(registrations, startListData!);
                    _logger.LogInformation("Ordered {Count} shooters by start list", shooters.Count);
                }
                else
                {
                    // Registration order (by class, then name)
                    shooters = ConvertRegistrationsToShooters(registrations);
                    _logger.LogInformation("Ordered {Count} shooters by registration order", shooters.Count);
                }

                return Json(new ShootersForResultsEntryResponse
                {
                    Success = true,
                    HasStartList = hasStartList,
                    OrderBy = orderBy,
                    Shooters = shooters
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooters for results entry for competition {CompetitionId}", competitionId);
                return Json(new ShootersForResultsEntryResponse
                {
                    Success = false,
                    Message = "Ett fel uppstod vid hämtning av skyttar: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Get the start list configuration for a competition
        /// Start list is stored as direct child of competition
        /// </summary>
        private async Task<StartListConfiguration?> GetOfficialStartListConfiguration(int competitionId)
        {
            try
            {
                var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long total);
                var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };

                // Start list is a direct child of competition
                var startListContent = children.FirstOrDefault(c => possibleAliases.Contains(c.ContentType.Alias));

                if (startListContent == null)
                {
                    return null;
                }

                var configurationData = startListContent.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configurationData))
                {
                    return null;
                }

                return JsonConvert.DeserializeObject<StartListConfiguration>(configurationData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting start list configuration for competition {CompetitionId}", competitionId);
                return null;
            }
        }

        /// <summary>
        /// Convert registrations to shooter info list, ordered by class then name
        /// </summary>
        private List<ShooterEntryInfo> ConvertRegistrationsToShooters(List<Models.ViewModels.Competition.CompetitionRegistration> registrations)
        {
            return registrations
                .OrderBy(r => r.MemberClass)
                .ThenBy(r => r.MemberName)
                .Select(r => new ShooterEntryInfo
                {
                    MemberId = r.MemberId,
                    Name = r.MemberName ?? "Okänd",
                    Club = Helpers.ClubNameHelper.Shorten(r.MemberClub ?? "Okänd klubb"),
                    ShootingClass = ShootingClasses.GetById(r.MemberClass)?.Name ?? r.MemberClass ?? "Okänd klass"
                })
                .ToList();
        }

        /// <summary>
        /// Order registrations by start list (team number, position)
        /// </summary>
        private List<ShooterEntryInfo> OrderRegistrationsByStartList(
            List<Models.ViewModels.Competition.CompetitionRegistration> registrations,
            StartListConfiguration startList)
        {
            var shooters = new List<ShooterEntryInfo>();

            // Create lookup by (MemberId, Class) to handle multi-class shooters
            // After multi-class refactoring, same shooter appears multiple times (once per class)
            var registrationLookup = registrations
                .GroupBy(r => (r.MemberId, r.MemberClass))
                .ToDictionary(g => g.Key, g => g.First());

            var addedKeys = new HashSet<(int, string)>();

            // Add shooters in start list order
            foreach (var team in startList.Teams.OrderBy(t => t.TeamNumber))
            {
                foreach (var shooter in team.Shooters.OrderBy(s => s.Position))
                {
                    var key = (shooter.MemberId, shooter.WeaponClass);
                    if (registrationLookup.TryGetValue(key, out var registration))
                    {
                        var classId = registration.MemberClass ?? shooter.WeaponClass;
                        shooters.Add(new ShooterEntryInfo
                        {
                            MemberId = registration.MemberId,
                            Name = registration.MemberName ?? shooter.Name ?? "Okänd",
                            Club = Helpers.ClubNameHelper.Shorten(registration.MemberClub ?? shooter.Club ?? "Okänd klubb"),
                            ShootingClass = ShootingClasses.GetById(classId)?.Name ?? classId ?? "Okänd klass",
                            TeamNumber = team.TeamNumber,
                            Position = shooter.Position,
                            StartTime = team.StartTime
                        });

                        // Track which registrations we've added
                        addedKeys.Add(key);
                    }
                }
            }

            // Add any registrations not in start list (late registrations)
            foreach (var reg in registrations.OrderBy(r => r.MemberClass).ThenBy(r => r.MemberName))
            {
                var key = (reg.MemberId, reg.MemberClass);
                if (!addedKeys.Contains(key))
                {
                    shooters.Add(new ShooterEntryInfo
                    {
                        MemberId = reg.MemberId,
                        Name = reg.MemberName ?? "Okänd",
                        Club = Helpers.ClubNameHelper.Shorten(reg.MemberClub ?? "Okänd klubb"),
                        ShootingClass = ShootingClasses.GetById(reg.MemberClass)?.Name ?? reg.MemberClass ?? "Okänd klass"
                        // No TeamNumber/Position - not in start list
                    });
                    addedKeys.Add(key);
                }
            }

            return shooters;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConvertShotsToStrings()
        {
            try
            {
                _logger.LogInformation("Starting conversion of shots data from integers to strings");

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                // Get all existing results that need conversion
                var results = await db.FetchAsync<PrecisionResultEntry>(
                    "WHERE Shots NOT LIKE '%''%'"); // Find records where Shots doesn't contain quotes (integer format)

                _logger.LogInformation("Found {Count} records to convert", results.Count);

                int convertedCount = 0;

                foreach (var result in results)
                {
                    try
                    {
                        // Parse the existing integer array
                        var shotsArray = JsonConvert.DeserializeObject<int[]>(result.Shots);
                        if (shotsArray == null || shotsArray.Length != 5) continue;

                        // Convert to string array, handling X conversion
                        var stringShots = new string[5];
                        // Calculate X count from the original integer array
                        var (_, xCount) = CalculateTotalsFromShots(shotsArray.Select(s => s.ToString()).ToArray());
                        int xUsed = 0;

                        for (int i = 0; i < 5; i++)
                        {
                            if (shotsArray[i] == 10 && xUsed < xCount)
                            {
                                stringShots[i] = "X";
                                xUsed++;
                            }
                            else
                            {
                                stringShots[i] = shotsArray[i].ToString();
                            }
                        }

                        // Update the record
                        result.Shots = JsonConvert.SerializeObject(stringShots);
                        await db.UpdateAsync(result);
                        convertedCount++;

                        _logger.LogInformation("Converted record {Id}: {OldShots} -> {NewShots}", 
                            result.Id, JsonConvert.SerializeObject(shotsArray), result.Shots);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error converting record {Id}: {Shots}", result.Id, result.Shots);
                    }
                }

                transaction.Complete();

                _logger.LogInformation("Conversion completed. Converted {ConvertedCount} out of {TotalCount} records", 
                    convertedCount, results.Count);

                return Json(new
                {
                    Success = true,
                    Message = $"Successfully converted {convertedCount} records from integer to string format",
                    ConvertedCount = convertedCount,
                    TotalRecords = results.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shots conversion");
                return Json(new
                {
                    Success = false,
                    Message = $"Conversion failed: {ex.Message}"
                });
            }
        }

        private bool ValidateResultRequest(ResultEntryRequest request)
        {
            if (request == null ||
                request.CompetitionId <= 0 ||
                request.SeriesNumber <= 0 ||
                // TeamNumber and Position are informational only (identity-based results use MemberId)
                // Allow 0 for these fields when no start list exists
                request.TeamNumber < 0 ||
                request.Position < 0 ||
                request.RangeOfficerId <= 0 ||
                request.ShooterMemberId <= 0 ||
                string.IsNullOrWhiteSpace(request.ShooterClass) ||
                request.Shots == null ||
                request.Shots.Length != 5)
            {
                return false;
            }

            // Validate each shot value
            var validValues = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "X" };
            return request.Shots.All(shot => validValues.Contains(shot.ToUpper()));
        }

        private bool ValidateDeleteRequest(DeleteResultRequest request)
        {
            return request != null &&
                   request.CompetitionId > 0 &&
                   request.SeriesNumber > 0 &&
                   request.MemberId > 0;  // Identity-based delete - only require MemberId
        }

        private (int total, int xCount) CalculateTotalsFromShots(string[] shots)
        {
            var total = 0;
            var xCount = 0;
            
            foreach (var shot in shots)
            {
                if (shot.ToUpper() == "X")
                {
                    total += 10;
                    xCount++;
                }
                else if (int.TryParse(shot, out int value) && value >= 0 && value <= 10)
                {
                    total += value;
                }
            }
            
            return (total, xCount);
        }

        private async Task<int> SaveResultToDatabase(ResultEntryRequest request)
        {
            try
            {
                _logger.LogInformation("Starting to save result to database for competition {CompetitionId}, MemberId={MemberId}, ShootingClass={ShootingClass}, SeriesNumber={SeriesNumber}",
                    request.CompetitionId, request.ShooterMemberId, request.ShooterClass, request.SeriesNumber);

                var compTypeId = GetCompetitionTypeId(request.CompetitionId);
                var tableName = GetResultTableName(compTypeId);
                var shotsJson = JsonConvert.SerializeObject(request.Shots);
                var now = DateTime.Now;

                // Atomic MERGE: eliminates race condition when multiple range masters save simultaneously
                var mergeSql = $@"
                    MERGE INTO [{tableName}] AS target
                    USING (SELECT @0 AS CompetitionId, @1 AS MemberId, @2 AS ShootingClass, @3 AS SeriesNumber) AS source
                    ON target.CompetitionId = source.CompetitionId
                       AND target.MemberId = source.MemberId
                       AND target.ShootingClass = source.ShootingClass
                       AND target.SeriesNumber = source.SeriesNumber
                    WHEN MATCHED THEN
                        UPDATE SET Shots = @4, TeamNumber = @5, Position = @6,
                                   EnteredBy = @7, LastModified = @8
                    WHEN NOT MATCHED THEN
                        INSERT (CompetitionId, SeriesNumber, MemberId, TeamNumber, Position,
                                ShootingClass, Shots, EnteredBy, EnteredAt, LastModified)
                        VALUES (@0, @3, @1, @5, @6, @2, @4, @7, @8, @8)
                    OUTPUT INSERTED.Id;";

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                try
                {
                    var resultId = await db.ExecuteScalarAsync<int>(mergeSql,
                        request.CompetitionId,      // @0
                        request.ShooterMemberId,    // @1
                        request.ShooterClass,       // @2
                        request.SeriesNumber,       // @3
                        shotsJson,                  // @4
                        request.TeamNumber,         // @5
                        request.Position,           // @6
                        request.RangeOfficerId,     // @7
                        now                         // @8
                    );

                    transaction.Complete();

                    _logger.LogInformation("Successfully saved result ID {ResultId} for MemberId {MemberId} (Team {Team}, Position {Position})",
                        resultId, request.ShooterMemberId, request.TeamNumber, request.Position);

                    return resultId;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Database MERGE failed, rolling back transaction. Exception: {ExceptionMessage}", ex.Message);
                    throw;
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Number == 2627 || sqlEx.Number == 2601)
            {
                _logger.LogWarning(sqlEx, "Unique constraint violation saving result for MemberId {MemberId} — likely concurrent save by another range master",
                    request.ShooterMemberId);
                return -1; // Signal constraint violation to caller
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error saving result. Exception: {ExceptionMessage}. StackTrace: {StackTrace}",
                    ex.Message, ex.StackTrace);
                return 0;
            }
        }

        /// <summary>
        /// Get the competition type identifier (e.g. "Precision", "Milsnabb", "Duell").
        /// </summary>
        private string GetCompetitionTypeId(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            return competition?.GetValue<string>("competitionType") ?? "Precision";
        }

        /// <summary>
        /// Check if a competition uses the Milsnabb result table.
        /// </summary>
        private bool IsMilsnabbCompetition(int competitionId)
        {
            return GetCompetitionTypeId(competitionId).Equals("Milsnabb", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Get the result table name for a competition type.
        /// </summary>
        private static string GetResultTableName(string typeId) => typeId switch
        {
            "Milsnabb" => "MilsnabbResultEntry",
            "Duell" => "DuellResultEntry",
            "NationellHelmatch" => "NationellHelmatchResultEntry",
            "MagnumPrecision" => "MagnumPrecisionResultEntry",
            "Springskytte" => "SpringskytteResultEntry",
            _ => "PrecisionResultEntry"
        };

        private async Task<List<PrecisionResultEntry>> GetCompetitionResultsInternal(int competitionId)
        {
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var compTypeId = GetCompetitionTypeId(competitionId);

            if (compTypeId == "Milsnabb")
            {
                var milsnabbResults = await db.FetchAsync<MilsnabbResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                    competitionId);
                return milsnabbResults.Cast<PrecisionResultEntry>().ToList();
            }

            if (compTypeId == "Duell")
            {
                var duellResults = await db.FetchAsync<DuellResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                    competitionId);
                return duellResults.Cast<PrecisionResultEntry>().ToList();
            }

            if (compTypeId == "NationellHelmatch")
            {
                var nhResults = await db.FetchAsync<NationellHelmatchResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                    competitionId);
                return nhResults.Cast<PrecisionResultEntry>().ToList();
            }

            if (compTypeId == "MagnumPrecision")
            {
                var mpResults = await db.FetchAsync<MagnumPrecisionResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                    competitionId);
                return mpResults.Cast<PrecisionResultEntry>().ToList();
            }

            return await db.FetchAsync<PrecisionResultEntry>(
                "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                competitionId);
        }

        private async Task<bool> DeleteResultFromDatabase(DeleteResultRequest request)
        {
            try
            {
                var memberId = request.MemberId;

                if (memberId <= 0)
                {
                    _logger.LogWarning("Invalid MemberId {MemberId} in delete request for competition {CompetitionId}",
                        memberId, request.CompetitionId);
                    return false;
                }

                _logger.LogInformation("Deleting result for CompetitionId={CompetitionId}, MemberId={MemberId}, ShootingClass={ShootingClass}, SeriesNumber={SeriesNumber}",
                    request.CompetitionId, memberId, request.ShootingClass, request.SeriesNumber);

                var tableName = GetResultTableName(GetCompetitionTypeId(request.CompetitionId));

                // Direct DELETE — no need for SELECT first, idempotent
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var rowsDeleted = await db.ExecuteAsync(
                    $"DELETE FROM [{tableName}] WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2 AND SeriesNumber = @3",
                    request.CompetitionId, memberId, request.ShootingClass, request.SeriesNumber);

                if (rowsDeleted > 0)
                {
                    _logger.LogInformation("Successfully deleted {RowsDeleted} result row(s) for MemberId {MemberId}, Series {SeriesNumber}",
                        rowsDeleted, memberId, request.SeriesNumber);
                    return true;
                }
                else
                {
                    _logger.LogInformation("No result found to delete for MemberId {MemberId}, SeriesNumber {SeriesNumber}",
                        memberId, request.SeriesNumber);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error deleting result. Exception: {ExceptionMessage}", ex.Message);
                return false;
            }
        }

        private async Task<(int MemberId, string ShootingClass)> GetShooterInfoFromStartList(int competitionId, int memberId)
        {
            try
            {
                // Get start list data from Umbraco content
                var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long total);
                
                // Look for start lists hub and then its children
                var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                if (startListsHub != null)
                {
                    var hubChildren = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out long hubTotal);
                    var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };
                    
                    // Find the OFFICIAL start list
                    var startListContent = hubChildren
                        .Where(c => possibleAliases.Contains(c.ContentType.Alias))
                        .FirstOrDefault(c => {
                            try {
                                var isOfficial = c.GetValue<bool>("isOfficialStartList");
                                return isOfficial;
                            } catch {
                                return false;
                            }
                        });
                    
                    if (startListContent != null)
                    {
                        var configurationData = startListContent.GetValue<string>("configurationData");
                        if (!string.IsNullOrEmpty(configurationData))
                        {
                            var config = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(configurationData);
                            
                            // Try both capitalized and lowercase property names
                            var teamsData = config?.Teams ?? config?.teams;
                            if (teamsData != null)
                            {
                                var teams = (IEnumerable<dynamic>)teamsData;
                                foreach (var team in teams)
                                {
                                    var shootersData = team.Shooters ?? team.shooters;
                                    if (shootersData != null)
                                    {
                                        var shooters = (IEnumerable<dynamic>)shootersData;
                                        foreach (var shooter in shooters)
                                        {
                                            var shooterMemberId = (int)(shooter.MemberId ?? shooter.memberId);
                                            if (shooterMemberId == memberId)
                                            {
                                                var weaponClass = (string)(shooter.WeaponClass ?? shooter.weaponClass);
                                                return (memberId, weaponClass);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                return (memberId, "Unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooter info for MemberId {MemberId}", memberId);
                return (memberId, "Unknown");
            }
        }

        private async Task<(string Name, string Club)> GetShooterNameAndClub(int competitionId, int memberId)
        {
            try
            {
                _logger.LogDebug("Looking for shooter name/club for MemberId {MemberId} in competition {CompetitionId}", memberId, competitionId);

                // 1. Try start list first (existing logic)
                var startListResult = TryGetFromStartList(competitionId, memberId);
                if (startListResult.Name != "Unknown")
                {
                    return startListResult;
                }

                // 2. Fallback: Try competition registrations
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                var registration = registrations?.FirstOrDefault(r => r.MemberId == memberId);
                if (registration != null && !string.IsNullOrEmpty(registration.MemberName))
                {
                    _logger.LogDebug("Found shooter in registrations: {Name} from {Club}", registration.MemberName, registration.MemberClub);
                    return (registration.MemberName, registration.MemberClub ?? "Okänd klubb");
                }

                // 3. Fallback: Try member service + club service directly
                var member = _memberService.GetById(memberId);
                if (member != null)
                {
                    var memberName = member.Name ?? "Unknown";
                    var clubId = member.GetValue<int>("primaryClubId");
                    var clubName = clubId > 0
                        ? (_clubService?.GetClubNameById(clubId) ?? "Okänd klubb")
                        : "Okänd klubb";
                    _logger.LogDebug("Found shooter in member service: {Name} from {Club}", memberName, clubName);
                    return (memberName, clubName);
                }

                // 4. Last resort
                _logger.LogWarning("Could not find name/club for MemberId {MemberId}", memberId);
                return ("Unknown", "Unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooter name and club for MemberId {MemberId}", memberId);
                return ("Unknown", "Unknown");
            }
        }

        /// <summary>
        /// Try to get shooter name and club from start list configuration
        /// </summary>
        private (string Name, string Club) TryGetFromStartList(int competitionId, int memberId)
        {
            try
            {
                var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long total);

                var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                if (startListsHub == null)
                {
                    return ("Unknown", "Unknown");
                }

                var hubChildren = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out long hubTotal);
                var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };

                // Find the OFFICIAL start list
                var officialStartList = hubChildren
                    .Where(c => possibleAliases.Contains(c.ContentType.Alias))
                    .FirstOrDefault(c => {
                        try {
                            return c.GetValue<bool>("isOfficialStartList");
                        } catch {
                            return false;
                        }
                    });

                if (officialStartList == null)
                {
                    return ("Unknown", "Unknown");
                }

                var configurationData = officialStartList.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configurationData))
                {
                    return ("Unknown", "Unknown");
                }

                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(configurationData);
                var teamsData = config?.Teams ?? config?.teams;
                if (teamsData == null)
                {
                    return ("Unknown", "Unknown");
                }

                var teams = (IEnumerable<dynamic>)teamsData;
                foreach (var team in teams)
                {
                    var shootersData = team.Shooters ?? team.shooters;
                    if (shootersData != null)
                    {
                        var shooters = (IEnumerable<dynamic>)shootersData;
                        foreach (var shooter in shooters)
                        {
                            var shooterMemberId = (int)(shooter.MemberId ?? shooter.memberId);
                            if (shooterMemberId == memberId)
                            {
                                var name = (string)(shooter.Name ?? shooter.name);
                                var club = (string)(shooter.Club ?? shooter.club);
                                return (name, club);
                            }
                        }
                    }
                }

                return ("Unknown", "Unknown");
            }
            catch
            {
                return ("Unknown", "Unknown");
            }
        }

        private async Task<(int MemberId, string ShootingClass)> GetShooterInfo(int competitionId, int teamNumber, int position)
        {
            try
            {
                // Get start list data from Umbraco content
                _logger.LogInformation("Looking for start list content for competition {CompetitionId}", competitionId);
                var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long total);
                
                _logger.LogInformation("Found {Total} children for competition {CompetitionId}", total, competitionId);
                foreach (var child in children)
                {
                    _logger.LogInformation("Child: {Name} (ID: {Id}, Type: {ContentType})", child.Name, child.Id, child.ContentType.Alias);
                }
                
                // First, try to find start list content directly under competition
                var startListContent = children.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                if (startListContent == null)
                {
                    _logger.LogInformation("No direct start list found, looking in start lists hub");
                    
                    // Look for start lists hub and then its children
                    var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                    if (startListsHub != null)
                    {
                        _logger.LogInformation("Found start lists hub: {Name} (ID: {Id})", startListsHub.Name, startListsHub.Id);
                        
                        var hubChildren = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out long hubTotal);
                        _logger.LogInformation("Found {HubTotal} children in start lists hub", hubTotal);
                        
                        foreach (var hubChild in hubChildren)
                        {
                            _logger.LogInformation("Hub child: {Name} (ID: {Id}, Type: {ContentType})", hubChild.Name, hubChild.Id, hubChild.ContentType.Alias);
                        }
                        
                        // Look for the OFFICIAL precision start list in the hub
                        var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };
                        startListContent = hubChildren
                            .Where(c => possibleAliases.Contains(c.ContentType.Alias))
                            .FirstOrDefault(c => {
                                try {
                                    var isOfficial = c.GetValue<bool>("isOfficialStartList");
                                    _logger.LogInformation("Start list {Name} is official: {IsOfficial}", c.Name, isOfficial);
                                    return isOfficial;
                                } catch (Exception ex) {
                                    _logger.LogWarning(ex, "Error checking isOfficialStartList for start list {Name}", c.Name);
                                    return false;
                                }
                            });
                        
                        if (startListContent != null)
                        {
                            _logger.LogInformation("Found start list in hub: {Name} (Type: {ContentType})", startListContent.Name, startListContent.ContentType.Alias);
                        }
                    }
                    
                    if (startListContent == null)
                    {
                        _logger.LogWarning("No start list found for competition {CompetitionId}", competitionId);
                        // Fallback: create unique ID
                        var fallbackId = (teamNumber * 1000) + position;
                        _logger.LogWarning("Using fallback MemberId: {FallbackId} for team {TeamNumber}, position {Position}", fallbackId, teamNumber, position);
                        return (fallbackId, "Unknown");
                    }
                }
                
                _logger.LogInformation("Found start list content: {ContentName} (ID: {ContentId})", startListContent.Name, startListContent.Id);

                var configurationData = startListContent.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configurationData))
                {
                    _logger.LogWarning("No configuration data found in start list for competition {CompetitionId}", competitionId);
                    // Fallback: create unique ID
                    var fallbackId = (teamNumber * 1000) + position;
                    _logger.LogWarning("Using fallback MemberId: {FallbackId} for team {TeamNumber}, position {Position} (no config data)", fallbackId, teamNumber, position);
                    return (fallbackId, "Unknown");
                }
                
                _logger.LogInformation("Configuration data length: {Length} characters", configurationData.Length);

                // Parse the JSON configuration data
                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(configurationData);
                
                // Try both capitalized and lowercase property names
                var teamsData = config?.Teams ?? config?.teams;
                if (teamsData != null)
                {
                    var teams = (IEnumerable<dynamic>)teamsData;
                    _logger.LogInformation("Found {TeamCount} teams in configuration data", teams.Count());
                    
                    foreach (var team in teams)
                    {
                        int teamNum = (int)(team.TeamNumber ?? team.teamNumber);
                        _logger.LogInformation("Checking team {TeamNumber} (looking for {TargetTeamNumber})", teamNum, teamNumber);
                        
                        if (teamNum == teamNumber)
                        {
                            var shootersData = team.Shooters ?? team.shooters;
                            if (shootersData != null)
                            {
                                var shooters = (IEnumerable<dynamic>)shootersData;
                            _logger.LogInformation("Found matching team {TeamNumber} with {ShooterCount} shooters", teamNumber, shooters.Count());
                            
                            foreach (var shooter in shooters)
                            {
                                    int shooterPos = (int)(shooter.Position ?? shooter.position);
                                _logger.LogInformation("Checking shooter position {ShooterPosition} (looking for {TargetPosition})", shooterPos, position);
                                
                                    var shooterMemberIdData = shooter.MemberId ?? shooter.memberId;
                                    var shooterWeaponClassData = shooter.WeaponClass ?? shooter.weaponClass;
                                    
                                    if (shooterPos == position && shooterMemberIdData != null && shooterWeaponClassData != null)
                                {
                                        int memberId = (int)shooterMemberIdData;
                                        string weaponClass = (string)shooterWeaponClassData;
                                    
                                    _logger.LogInformation("Found shooter MemberId {MemberId} with class {ShootingClass} for team {TeamNumber}, position {Position}",
                                        memberId, weaponClass, teamNumber, position);
                                    
                                    return (memberId, weaponClass);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("No teams found in configuration data or config is null");
                }

                _logger.LogWarning("No shooter found for competition {CompetitionId}, team {TeamNumber}, position {Position}",
                    competitionId, teamNumber, position);
                
                // Fallback: create unique ID
                var fallbackMemberId = (teamNumber * 1000) + position;
                _logger.LogWarning("Using fallback MemberId: {FallbackId} for team {TeamNumber}, position {Position} (shooter not found)", fallbackMemberId, teamNumber, position);
                return (fallbackMemberId, "Unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooter info for competition {CompetitionId}, team {TeamNumber}, position {Position}",
                    competitionId, teamNumber, position);
                // Fallback: still create unique ID even on error
                var fallbackMemberId = (teamNumber * 1000) + position;
                return (fallbackMemberId, "Unknown");
            }
        }

        private async Task UpdateLiveLeaderboard(int competitionId)
        {
            try
            {
                // Get the competition content
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return;

                // Find the Competition Results Hub
                var resultsHub = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResultsHub");

                if (resultsHub == null) return;

                // Find or create the Live Leaderboard
                var liveLeaderboard = _contentService.GetPagedChildren(resultsHub.Id, 0, int.MaxValue, out long totalChildren)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" &&
                                       c.GetValue<string>("resultType") == "Leaderboard");

                if (liveLeaderboard == null)
                {
                    // Create new live leaderboard
                    liveLeaderboard = _contentService.Create("Live Resultat", resultsHub.Id, "competitionResult");
                    liveLeaderboard.SetValue("resultType", "Leaderboard");
                    liveLeaderboard.SetValue("isOfficial", false);
                }

                // Calculate current leaderboard data
                var results = await GetCompetitionResultsInternal(competitionId);
                var leaderboardData = await CalculateLeaderboard(results, competitionId);

                // Update the content
                liveLeaderboard.SetValue("resultData", Newtonsoft.Json.JsonConvert.SerializeObject(leaderboardData));
                liveLeaderboard.SetValue("lastUpdated", DateTime.Now);
                liveLeaderboard.SetValue("isOfficial", false);

                // Save and publish
                _contentService.Save(liveLeaderboard);
                _contentService.Publish(liveLeaderboard, new[] { "*" }, -1); // Publish for all cultures, system user

                _logger.LogInformation("Updated live leaderboard for competition {CompetitionId}", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating live leaderboard for competition {CompetitionId}. Exception: {ExceptionMessage}. StackTrace: {StackTrace}",
                    competitionId, ex.Message, ex.StackTrace);
            }
        }

        private async Task<object> CalculateLeaderboard(List<PrecisionResultEntry> results, int competitionId)
        {
            if (!results.Any())
            {
                return new { Shooters = new List<object>() };
            }

            // Group results by (MemberId, ShootingClass) to separate multi-class shooters
            var shooterTotals = results
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g => {
                    var totalScore = 0;
                    var totalXCount = 0;

                    foreach (var result in g)
                    {
                        var shots = JsonConvert.DeserializeObject<string[]>(result.Shots) ?? new string[0];
                        var (total, xCount) = CalculateTotalsFromShots(shots);
                        totalScore += total;
                        totalXCount += xCount;
                    }

                    return new
                    {
                        MemberId = g.Key.MemberId,
                        ShootingClass = g.Key.ShootingClass,
                        TotalScore = totalScore,
                        TotalXCount = totalXCount,
                        SeriesCount = g.Count(),
                        Results = g.OrderBy(r => r.SeriesNumber).ToList()
                    };
                })
                .OrderByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.TotalXCount)
                .ToList();

            return new
            {
                CompetitionId = competitionId,
                UpdatedAt = DateTime.Now,
                Shooters = shooterTotals.Select((shooter, index) => new
                {
                    Position = index + 1,
                    shooter.MemberId,
                    shooter.ShootingClass,
                    shooter.TotalScore,
                    shooter.TotalXCount,
                    shooter.SeriesCount,
                    shooter.Results
                }).ToList()
            };
        }

        [HttpGet]
        public async Task<IActionResult> AnalyzeClassMerges(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltigt tävlings-ID." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                var compTypeId = GetCompetitionTypeId(competitionId);

                // Excluded types
                if (compTypeId is "MagnumPrecision" or "Springskytte")
                    return Json(new { success = true, suggestions = Array.Empty<object>(), classes = Array.Empty<object>() });

                var results = await GetCompetitionResultsInternal(competitionId);
                if (subCompetitionOnly)
                {
                    var subIds = GetSubCompetitionMemberIds(competitionId);
                    results = results.Where(r => subIds.Contains(r.MemberId)).ToList();
                }

                // Fallback to cached result data if DB is empty
                if (!results.Any())
                {
                    var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                    var existingJson = resultPage?.GetValue<string>("resultData");
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        // Return analysis based on cached results
                        var cached = JsonConvert.DeserializeObject<FinalResults>(existingJson);
                        if (cached?.ClassGroups != null)
                        {
                            var classInfos = cached.ClassGroups.Select(g => new ClassInfo
                            {
                                ClassName = g.ClassName,
                                WeaponGroup = ShootingClasses.GetWeaponClassCode(g.ClassName),
                                ParticipantCount = g.Shooters.Count,
                                BelowThreshold = g.Shooters.Count < 5,
                                MedalImpact = g.Shooters.Count < 5
                                    ? GetMedalImpactText(g.Shooters.Count, g.ClassName.Contains("Jun"))
                                    : ""
                            }).ToList();

                            // Build suggestions from cached data by creating synthetic PrecisionResultEntry list
                            var syntheticResults = new List<PrecisionResultEntry>();
                            foreach (var cg in cached.ClassGroups)
                            {
                                foreach (var shooter in cg.Shooters)
                                {
                                    // Find the class ID from the name
                                    var classId = ShootingClasses.GetByName(cg.ClassName)?.Id
                                        ?? cg.ClassName.Replace(" ", "_");
                                    syntheticResults.Add(new PrecisionResultEntry
                                    {
                                        CompetitionId = competitionId,
                                        MemberId = shooter.MemberId,
                                        ShootingClass = classId,
                                        SeriesNumber = 1
                                    });
                                }
                            }
                            var svc = new ClassMergingService();
                            var analysis = svc.Analyze(syntheticResults, compTypeId);
                            return Json(new { success = true, analysis.Suggestions, analysis.Classes });
                        }
                    }
                    return Json(new { success = true, suggestions = Array.Empty<object>(), classes = Array.Empty<object>() });
                }

                var service = new ClassMergingService();
                var result = service.Analyze(results, compTypeId);
                return Json(new { success = true, result.Suggestions, result.Classes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing class merges for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel vid analys: " + ex.Message });
            }
        }

        private static string GetMedalImpactText(int count, bool isJunior)
        {
            if (isJunior) return "Alltid medaljer till topp 3";
            return count switch
            {
                4 => "Guld + Silver",
                3 => "Enbart Guld",
                _ => "Inga medaljer"
            };
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateResultsList([FromBody] CreateResultsListRequest request)
        {
            try
            {
                if (request?.CompetitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                // Get competition
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                // VALIDATION: Check if competition is external
                if (competition.GetValue<bool>("isExternal"))
                {
                    return Json(new { Success = false, Message = "Detta är en extern tävling. Resultat kan inte skapas i systemet." });
                }

                // Find or create result page as direct child of competition
                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                // Get all results for this competition; if this is a Deltävling rebuild,
                // narrow down to the IsSubCompetition members.
                var results = await GetCompetitionResultsInternal(request.CompetitionId);
                if (request.IsSubCompetition)
                {
                    var subIds = GetSubCompetitionMemberIds(request.CompetitionId);
                    results = results.Where(r => subIds.Contains(r.MemberId)).ToList();
                }

                FinalResults finalResults;

                if (results.Any())
                {
                    // Calculate fresh results from database
                    finalResults = await CalculateFinalResults(results, competition.Id, request.Merges);
                }
                else if (resultPage != null)
                {
                    // No DB rows but cached result page exists — use existing snapshot
                    var existingJson = resultPage.GetValue<string>("resultData");
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        finalResults = JsonConvert.DeserializeObject<FinalResults>(existingJson);
                        _logger.LogInformation(
                            "No database results for competition {CompetitionId}, using existing result snapshot with {Count} class groups",
                            request.CompetitionId, finalResults?.ClassGroups?.Count ?? 0);

                        // Apply merges to cached data by re-grouping. Uses union-find so
                        // multiple sources targeting the same class collapse into one group.
                        if (request.Merges?.Any() == true && finalResults?.ClassGroups != null)
                        {
                            var groupLookup = ClassMergingService.BuildMergeGroupLookup(request.Merges);

                            var allShooters = finalResults.ClassGroups.SelectMany(g => g.Shooters).ToList();
                            finalResults.ClassGroups = allShooters
                                .GroupBy(s => groupLookup.TryGetValue(s.ShootingClass, out var grp) ? grp : s.ShootingClass)
                                .Select(g => new ClassGroup { ClassName = g.Key, Shooters = g.ToList() })
                                .ToList();
                        }
                    }
                    else
                    {
                        return Json(new { Success = false, Message = "Inga resultat hittades för denna tävling." });
                    }
                }
                else
                {
                    return Json(new { Success = false, Message = "Inga resultat hittades för denna tävling." });
                }

                if (resultPage == null)
                {
                    // Create new result page
                    resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                    resultPage.SetValue("resultType", "Final Results");
                    resultPage.SetValue("isOfficial", false); // Start as preliminary
                    _logger.LogInformation("Created new result page for competition {CompetitionId}", request.CompetitionId);
                }

                // Keep existing isOfficial status (main flag — sub-comp uses subCompetitionIsOfficial)
                var existingIsOfficial = resultPage.GetValue<bool>("isOfficial");

                if (request.IsSubCompetition)
                {
                    // Deltävling path: don't overwrite the main resultData / mergeConfig.
                    // Just persist the sub-comp merge config; live recompute when read.
                    if (resultPage.HasProperty("subCompetitionMergeConfig"))
                    {
                        resultPage.SetValue("subCompetitionMergeConfig", request.Merges?.Any() == true
                            ? Newtonsoft.Json.JsonConvert.SerializeObject(request.Merges)
                            : "");
                    }
                    else
                    {
                        _logger.LogWarning("competitionResult node for comp {CompId} missing 'subCompetitionMergeConfig' property — Deltävling merge config not saved.", request.CompetitionId);
                    }
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    resultPage.SetValue("resultType", "Final Results");
                }
                else
                {
                    // Update the result page (main results path)
                    resultPage.SetValue("resultData", Newtonsoft.Json.JsonConvert.SerializeObject(finalResults));
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    resultPage.SetValue("isOfficial", existingIsOfficial); // Keep existing status
                    resultPage.SetValue("resultType", "Final Results");

                    // Persist merge config so GetResultsList can re-apply on preliminary reload
                    resultPage.SetValue("mergeConfig", request.Merges?.Any() == true
                        ? Newtonsoft.Json.JsonConvert.SerializeObject(request.Merges)
                        : "");
                }

                // Save and publish
                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" }, -1);

                _logger.LogInformation("Created/updated final results list for competition {CompetitionId}", request.CompetitionId);

                // If "Uppdatera" regenerates an ALREADY-OFFICIAL main list, reconcile the
                // Standardmedalj ledger from the fresh results. This covers the case where
                // Standardmedaljsgrundande was forgotten at first publish, then enabled later and
                // the admin clicks Uppdatera (not the publish toggle). Idempotent: medals are
                // upserted by identity, and ones no longer earned are removed (except gold-locked).
                if (!request.IsSubCompetition && existingIsOfficial && results.Any())
                {
                    await MaterializeStandardMedalsAsync(competition, finalResults);
                }

                var totalShooters = finalResults.ClassGroups.Sum(g => g.Shooters.Count);

                return Json(new {
                    Success = true,
                    Message = results.Any()
                        ? "Resultatlistan har skapats/uppdaterats framgångsrikt!"
                        : "Resultatlistan har uppdaterats från befintlig data (inga nya resultat i databasen).",
                    ResultsCount = totalShooters,
                    ClassGroupsCount = finalResults.ClassGroups.Count,
                    IsOfficial = existingIsOfficial
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating results list for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid skapande av resultatlista: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetResultsList(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                if (competitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                // Get competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                // Find result page as direct child of competition
                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                var showLiveResults = competition.GetValue<bool>("showLiveResults");

                if (resultPage == null && !showLiveResults)
                {
                    return Json(new { Success = false, Message = "Ingen resultatsida hittades.", Exists = false });
                }

                FinalResults resultData;
                DateTime lastUpdated;
                bool isOfficial = false;

                // Pre-resolve the IsSubCompetition member set once. Used for both the
                // preliminary recompute and the (rare) live path when no result page exists.
                HashSet<int>? subIds = subCompetitionOnly ? GetSubCompetitionMemberIds(competitionId) : null;

                if (resultPage != null)
                {
                    var finalResultsList = resultPage;
                    // Read the relevant Official flag — main vs sub live on different properties
                    // so each section can be published independently.
                    if (subCompetitionOnly)
                    {
                        isOfficial = finalResultsList.HasProperty("subCompetitionIsOfficial")
                            && finalResultsList.GetValue<bool>("subCompetitionIsOfficial");
                    }
                    else
                    {
                        isOfficial = finalResultsList.GetValue<bool>("isOfficial");
                    }

                    // Deltävling is always recomputed live from the filtered DB rows — we don't
                    // store a separate sub-comp snapshot. Main path keeps its frozen-snapshot
                    // behaviour when Officiell so historical snapshots stay stable.
                    if (isOfficial && !subCompetitionOnly)
                    {
                        var resultDataJson = finalResultsList.GetValue<string>("resultData");
                        _logger.LogInformation("Loading official (frozen) results for competition {CompetitionId}", competitionId);

                        if (string.IsNullOrEmpty(resultDataJson))
                        {
                            return Json(new { Success = false, Message = "Officiella resultat saknar data.", Exists = false });
                        }

                        resultData = JsonConvert.DeserializeObject<FinalResults>(resultDataJson);
                        lastUpdated = finalResultsList.GetValue<DateTime>("lastUpdated");

                        // Apply class-name overrides on top of the snapshot so renaming doesn't
                        // require re-publishing the official result list.
                        ApplyClassNameOverrides(resultData, ReadClassNameOverrides(finalResultsList, subCompetitionOnly: false));
                    }
                    else
                    {
                        // Preliminary results OR sub-comp (any state): generate fresh from DB
                        _logger.LogInformation("Loading {Mode} results from database for competition {CompetitionId}",
                            subCompetitionOnly ? "sub-competition" : "preliminary", competitionId);

                        var dbResults = await GetCompetitionResultsInternal(competitionId);
                        if (subCompetitionOnly && subIds != null)
                        {
                            dbResults = dbResults.Where(r => subIds.Contains(r.MemberId)).ToList();
                        }

                        if (!dbResults.Any())
                        {
                            return Json(new { Success = false, Message = "Inga resultat finns i databasen ännu.", Exists = false });
                        }

                        // Read stored merge config from the slot that matches the current mode
                        var mergeConfigJson = subCompetitionOnly
                            ? (finalResultsList.HasProperty("subCompetitionMergeConfig")
                                ? finalResultsList.GetValue<string>("subCompetitionMergeConfig")
                                : null)
                            : finalResultsList.GetValue<string>("mergeConfig");
                        List<ClassMergeAction>? storedMerges = null;
                        if (!string.IsNullOrEmpty(mergeConfigJson))
                        {
                            storedMerges = JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfigJson);
                        }

                        // Generate fresh results from database (with merges if configured)
                        resultData = await CalculateFinalResults(dbResults, competitionId, storedMerges);
                        lastUpdated = DateTime.Now;

                        ApplyClassNameOverrides(resultData, ReadClassNameOverrides(finalResultsList, subCompetitionOnly));

                        _logger.LogInformation("Generated fresh results with {Count} shooters",
                            resultData.ClassGroups.Sum(g => g.Shooters.Count));
                    }
                }
                else
                {
                    // No result page but showLiveResults is enabled — generate live from database
                    _logger.LogInformation("No result page, generating live results from database for competition {CompetitionId}", competitionId);

                    var dbResults = await GetCompetitionResultsInternal(competitionId);
                    if (subCompetitionOnly && subIds != null)
                    {
                        dbResults = dbResults.Where(r => subIds.Contains(r.MemberId)).ToList();
                    }

                    if (!dbResults.Any())
                    {
                        return Json(new { Success = false, Message = "Inga resultat finns i databasen ännu.", Exists = false });
                    }

                    resultData = await CalculateFinalResults(dbResults, competitionId);
                    lastUpdated = DateTime.Now;
                    isOfficial = false;

                    _logger.LogInformation("Generated live results with {Count} shooters (no result page)",
                        resultData.ClassGroups.Sum(g => g.Shooters.Count));
                }

            // Get the result page URL - construct from competition URL
            var resultPageUrl = "";
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
                {
                    var publishedCompetition = umbracoContext.Content?.GetById(competitionId);
                    if (publishedCompetition != null)
                    {
                        var competitionUrl = publishedCompetition.Url();
                        resultPageUrl = competitionUrl.TrimEnd('/') + "/resultat";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get result page URL for competition {CompetitionId}", competitionId);
            }
            
            // Get competition configuration for finals
            var numberOfSeriesOrStations = competition.GetValue<int>("numberOfSeriesOrStations");
            var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
            var isAwardingStandardMedals = competition.GetValue<bool>("isAwardingStandardMedals");
            var competitionTypeId = competition.GetValue<string>("competitionType") ?? "Precision";

            return Json(new
            {
                Success = true,
                Exists = true,
                IsOfficial = isOfficial,
                LastUpdated = lastUpdated,
                Results = resultData,
                ResultPageUrl = resultPageUrl,
                HasResultPage = resultPage != null,
                NumberOfSeries = numberOfSeriesOrStations,
                NumberOfFinalSeries = numberOfFinalSeries,
                IsAwardingStandardMedals = isAwardingStandardMedals,
                CompetitionTypeId = competitionTypeId
            });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting results list for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod vid hämtning av resultatlista: " + ex.Message, Exists = false });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleResultsOfficial([FromBody] ToggleResultsOfficialRequest request)
        {
            try
            {
                if (request?.CompetitionId <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });
                }

                // Get competition
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { Success = false, Message = "Tävlingen hittades inte." });
                }

                // VALIDATION: Check if competition is external
                if (competition.GetValue<bool>("isExternal"))
                {
                    return Json(new { Success = false, Message = "Detta är en extern tävling. Resultat kan inte hanteras i systemet." });
                }

                // Find result page as direct child of competition
                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                if (resultPage == null)
                {
                    return Json(new { Success = false, Message = "Ingen resultatsida hittades." });
                }

                var finalResultsList = resultPage;

                // Read whichever flag is being toggled (main isOfficial or subCompetitionIsOfficial)
                bool currentFlag;
                if (request.IsSubCompetition)
                {
                    if (!finalResultsList.HasProperty("subCompetitionIsOfficial"))
                        return Json(new { Success = false, Message = "Egenskapen 'subCompetitionIsOfficial' saknas på dokumenttypen competitionResult. Lägg till den i Umbraco backoffice (True/False)." });
                    currentFlag = finalResultsList.GetValue<bool>("subCompetitionIsOfficial");
                }
                else
                {
                    currentFlag = finalResultsList.GetValue<bool>("isOfficial");
                }

                // Toggle or set the flag
                var newIsOfficial = request.IsOfficial ?? !currentFlag;

                // Main publish: regenerate the frozen result snapshot. Sub-comp publish doesn't
                // store a snapshot — sub results are always live-recomputed in GetResultsList.
                if (newIsOfficial && !request.IsSubCompetition)
                {
                    var dbResults = await GetCompetitionResultsInternal(request.CompetitionId);
                    if (dbResults.Any())
                    {
                        // Re-apply the stored class-merge config so the published snapshot keeps
                        // the merged groups the admin saw on the preliminary list. Without this the
                        // snapshot is regenerated with every class in its own group.
                        List<ClassMergeAction>? storedMerges = null;
                        var mergeConfigJson = finalResultsList.GetValue<string>("mergeConfig");
                        if (!string.IsNullOrEmpty(mergeConfigJson))
                        {
                            try { storedMerges = JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfigJson); }
                            catch { /* ignore malformed config — fall back to unmerged */ }
                        }

                        var freshResults = await CalculateFinalResults(dbResults, request.CompetitionId, storedMerges);
                        var resultDataJson = JsonConvert.SerializeObject(freshResults);
                        finalResultsList.SetValue("resultData", resultDataJson);
                        _logger.LogInformation("Regenerated results JSON with {Count} shooters for competition {CompetitionId}",
                            freshResults.ClassGroups.Sum(g => g.Shooters.Count), request.CompetitionId);

                        // Materialize the won Standard medals into the Standardmedalj ledger.
                        await MaterializeStandardMedalsAsync(competition, freshResults);
                    }
                }
                else if (!newIsOfficial && !request.IsSubCompetition)
                {
                    // Results pulled back to preliminary — the medals are no longer official.
                    await _medalMaterialization.RemoveOnSiteForCompetitionAsync(request.CompetitionId);
                }

                if (request.IsSubCompetition)
                {
                    finalResultsList.SetValue("subCompetitionIsOfficial", newIsOfficial);
                }
                else
                {
                    finalResultsList.SetValue("isOfficial", newIsOfficial);
                }
                finalResultsList.SetValue("lastUpdated", DateTime.Now);

                // Save and publish
                _contentService.Save(finalResultsList);
                _contentService.Publish(finalResultsList, new[] { "*" }, -1);

                _logger.LogInformation("Toggled {Flag} for competition {CompetitionId} to {IsOfficial}",
                    request.IsSubCompetition ? "subCompetitionIsOfficial" : "isOfficial",
                    request.CompetitionId, newIsOfficial);

                return Json(new
                {
                    Success = true,
                    Message = newIsOfficial ? "Resultatlistan har markerats som officiell!" : "Resultatlistan har återställts till preliminär.",
                    IsOfficial = newIsOfficial
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling isOfficial for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Extract won Standard medals from a freshly computed precision-family result set and
        /// upsert them into the Standardmedalj ledger. Discipline comes from the competition's
        /// competitionType; season year from competitionDate. Never blocks publishing.
        /// </summary>
        private async Task MaterializeStandardMedalsAsync(Umbraco.Cms.Core.Models.IContent competition, FinalResults freshResults)
        {
            try
            {
                var discipline = competition.GetValue<string>("competitionType") ?? StandardMedals.Precision;
                var competitionDate = competition.GetValue<DateTime?>("competitionDate");
                var year = competitionDate?.Year ?? DateTime.Now.Year;
                var competitionName = competition.GetValue<string>("competitionName");
                if (string.IsNullOrWhiteSpace(competitionName)) competitionName = competition.Name;

                var medals = freshResults.ClassGroups
                    .SelectMany(g => g.Shooters)
                    .Where(s => StandardMedals.IsMedal(s.StandardMedal))
                    .Select(s => new OnSiteMedal(s.MemberId, s.ShootingClass, s.StandardMedal!));

                await _medalMaterialization.UpsertOnSiteMedalsAsync(
                    competition.Id, discipline, year, competitionName, competitionDate, medals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to materialize standard medals for competition {CompetitionId}", competition.Id);
            }
        }

        // ── Särskjutning endpoints ──────────────────────────────────────────────
        // Shoot-off scores for tied medal positions in Championship competitions.
        // The detection of which groups are tied (TiedMedalGroups) is part of the
        // existing GetResultsList response; these endpoints are about recording
        // the actual shoot-off shots and removing them.

        [HttpGet]
        public async Task<IActionResult> GetShootOffStatus(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { Success = false, Message = "Ogiltigt tävlings-ID." });

                if (!await CanManageCompetitionResults(competitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera resultat för denna tävling." });

                var results = await GetCompetitionResultsInternal(competitionId);
                if (subCompetitionOnly)
                {
                    var subIds = GetSubCompetitionMemberIds(competitionId);
                    results = results.Where(r => subIds.Contains(r.MemberId)).ToList();
                }

                // Load merge config from the existing result page so the tied-group detection
                // matches what the admin sees on the published list.
                List<ClassMergeAction>? merges = null;
                var resultPage = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPage != null)
                {
                    var mergeProp = subCompetitionOnly ? "subCompetitionMergeConfig" : "mergeConfig";
                    var mergeJson = resultPage.HasProperty(mergeProp) ? resultPage.GetValue<string>(mergeProp) : null;
                    if (!string.IsNullOrEmpty(mergeJson))
                    {
                        try { merges = JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeJson); }
                        catch { /* ignore — leave merges null */ }
                    }
                }

                var finalResults = await CalculateFinalResults(results, competitionId, merges);

                var tiedGroups = finalResults.ClassGroups
                    .Where(cg => cg.TiedMedalGroups != null && cg.TiedMedalGroups.Any())
                    .Select(cg => new
                    {
                        className = cg.ClassName,
                        groups = cg.TiedMedalGroups
                    })
                    .ToList();

                return Json(new { Success = true, ClassGroups = tiedGroups });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetShootOffStatus for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveShootOffEntry([FromBody] ShootOffEntryRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 ||
                    string.IsNullOrWhiteSpace(request.ShootingClass) || request.Round <= 0 ||
                    request.Shots == null || request.Shots.Length != 5)
                {
                    return Json(new { Success = false, Message = "Ogiltig begäran." });
                }

                if (!await CanManageCompetitionResults(request.CompetitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera resultat för denna tävling." });

                // Validate each shot
                foreach (var s in request.Shots)
                {
                    if (!ScoringUtilities.IsValidShotValue(s))
                        return Json(new { Success = false, Message = $"Ogiltigt skottvärde: {s}" });
                }

                var actingMemberId = await GetCurrentMemberIdOrZero();
                var shotsJson = JsonConvert.SerializeObject(request.Shots);

                var (ok, err) = await _shootOffService.SaveEntryAsync(
                    request.CompetitionId, request.MemberId, request.ShootingClass,
                    request.Round, shotsJson, actingMemberId);

                if (!ok) return Json(new { Success = false, Message = err ?? "Kunde inte spara." });

                return Json(new { Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SaveShootOffEntry");
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteShootOffEntry([FromBody] ShootOffDeleteRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 ||
                    string.IsNullOrWhiteSpace(request.ShootingClass) || request.Round <= 0)
                {
                    return Json(new { Success = false, Message = "Ogiltig begäran." });
                }

                if (!await CanManageCompetitionResults(request.CompetitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera resultat för denna tävling." });

                var (ok, err) = await _shootOffService.DeleteEntryAsync(
                    request.CompetitionId, request.MemberId, request.ShootingClass, request.Round);

                return Json(new { Success = ok, Message = err });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DeleteShootOffEntry");
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        private async Task<bool> CanManageCompetitionResults(int competitionId)
        {
            if (await _adminAuthorizationService.IsCurrentUserAdminAsync()) return true;
            if (await _adminAuthorizationService.IsCompetitionManager(competitionId)) return true;
            var competition = _contentService.GetById(competitionId);
            var clubId = competition?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0 && await _adminAuthorizationService.IsClubAdminForClub(clubId)) return true;
            return false;
        }

        // ── Class-name overrides ────────────────────────────────────────────────
        // The auto-generated combined class names (e.g. "C2+Dam+Vet") are not always
        // ideal — the admin may want to call the merged group something else for the
        // public list. Overrides are stored per result page as JSON dict mapping the
        // auto-generated name to the custom one. Overrides apply on top of both the
        // frozen official snapshot and the live preliminary recompute, so renaming
        // doesn't require re-publishing.

        private static string? ReadClassNameOverrides(IContent resultPage, bool subCompetitionOnly)
        {
            var propName = subCompetitionOnly ? "subCompetitionClassNameOverrides" : "classNameOverrides";
            return resultPage.HasProperty(propName) ? resultPage.GetValue<string>(propName) : null;
        }

        private static void ApplyClassNameOverrides(FinalResults? results, string? overridesJson)
        {
            if (results?.ClassGroups == null || string.IsNullOrEmpty(overridesJson)) return;
            Dictionary<string, string>? overrides = null;
            try { overrides = JsonConvert.DeserializeObject<Dictionary<string, string>>(overridesJson); }
            catch { return; }
            if (overrides == null || overrides.Count == 0) return;

            foreach (var group in results.ClassGroups)
            {
                if (overrides.TryGetValue(group.ClassName, out var custom) && !string.IsNullOrWhiteSpace(custom))
                    group.DisplayClassName = custom;
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveClassNameOverride([FromBody] ClassNameOverrideRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.OriginalName))
                    return Json(new { Success = false, Message = "Ogiltig begäran." });

                if (!await CanManageCompetitionResults(request.CompetitionId))
                    return Json(new { Success = false, Message = "Du har inte behörighet att hantera resultat för denna tävling." });

                var resultPage = _contentService.GetPagedChildren(request.CompetitionId, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPage == null)
                    return Json(new { Success = false, Message = "Ingen resultatsida hittades." });

                var propName = request.IsSubCompetition ? "subCompetitionClassNameOverrides" : "classNameOverrides";
                if (!resultPage.HasProperty(propName))
                    return Json(new { Success = false, Message = $"Egenskapen '{propName}' saknas på doctypen competitionResult. Lägg till den (Textarea) i Umbraco-backoffice." });

                var existingJson = resultPage.GetValue<string>(propName);
                Dictionary<string, string> overrides;
                try
                {
                    overrides = string.IsNullOrEmpty(existingJson)
                        ? new Dictionary<string, string>()
                        : (JsonConvert.DeserializeObject<Dictionary<string, string>>(existingJson) ?? new Dictionary<string, string>());
                }
                catch
                {
                    overrides = new Dictionary<string, string>();
                }

                // Empty custom name → revert (delete the override).
                if (string.IsNullOrWhiteSpace(request.CustomName))
                    overrides.Remove(request.OriginalName);
                else
                    overrides[request.OriginalName] = request.CustomName.Trim();

                resultPage.SetValue(propName, JsonConvert.SerializeObject(overrides));
                resultPage.SetValue("lastUpdated", DateTime.Now);

                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" }, -1);

                return Json(new { Success = true, OriginalName = request.OriginalName, CustomName = request.CustomName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SaveClassNameOverride for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { Success = false, Message = "Ett fel uppstod: " + ex.Message });
            }
        }

        private async Task<int> GetCurrentMemberIdOrZero()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null || string.IsNullOrEmpty(currentMember.Email)) return 0;
            var memberData = _memberService.GetByEmail(currentMember.Email);
            return memberData?.Id ?? 0;
        }


        private async Task<FinalResults> CalculateFinalResults(List<PrecisionResultEntry> results, int competitionId, List<ClassMergeAction>? merges = null)
        {
            if (!results.Any())
            {
                return new FinalResults
                {
                    CompetitionId = competitionId,
                    UpdatedAt = DateTime.Now,
                    IsOfficial = true,
                    ClassGroups = new List<ClassGroup>()
                };
            }

            // Get competition configuration for finals
            var competition = _contentService.GetById(competitionId);
            var numberOfSeriesOrStations = competition?.GetValue<int>("numberOfSeriesOrStations") ?? 0;
            var numberOfFinalSeries = competition?.GetValue<int>("numberOfFinalSeries") ?? 0;
            var hasFinalsRound = numberOfFinalSeries > 0;
            var qualificationSeriesCount = hasFinalsRound ? (numberOfSeriesOrStations - numberOfFinalSeries) : numberOfSeriesOrStations;

            // PERFORMANCE FIX: Build shooter lookup dictionary ONCE instead of calling GetShooterNameAndClub for every result
            _logger.LogInformation("Building shooter lookup cache for competition {CompetitionId}", competitionId);
            var uniqueMemberIds = results.Select(r => r.MemberId).Distinct().ToList();
            var shooterLookup = new Dictionary<int, (string Name, string Club)>();

            foreach (var memberId in uniqueMemberIds)
            {
                var (name, club) = await GetShooterNameAndClub(competitionId, memberId);
                shooterLookup[memberId] = (name, Helpers.ClubNameHelper.Shorten(club));
                _logger.LogInformation("Cached shooter info for MemberId {MemberId}: {Name} from {Club}", memberId, name, club);
            }
            _logger.LogInformation("Shooter lookup cache built with {Count} entries", shooterLookup.Count);

            var shooterResults = results
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g =>
                {
                    var memberId = g.Key.MemberId;
                    var shootingClass = g.Key.ShootingClass;
                    var memberResults = g.OrderBy(r => r.SeriesNumber).ToList();

                    // Use cached lookup instead of expensive method call
                    var (name, club) = shooterLookup.TryGetValue(memberId, out var shooterInfo)
                        ? shooterInfo
                        : ("Unknown Shooter", "Unknown Club");

                    return new ShooterResult
                    {
                        MemberId = memberId,
                        Name = name,
                        Club = club,
                        ShootingClass = ShootingClasses.GetById(shootingClass)?.Name ?? shootingClass,
                        Results = memberResults
                    };
                })
                .ToList();

            // Build merge group lookup: maps original class name → combined group name.
            // Uses union-find so multiple merges into the same target (or chains) collapse
            // into one combined group with one shared name.
            var mergeGroupLookup = ClassMergingService.BuildMergeGroupLookup(merges);
            // Helper to resolve which group a shooter belongs to
            string GetGroupName(string shootingClass) =>
                mergeGroupLookup.TryGetValue(shootingClass, out var group) ? group : shootingClass;

            // Define class order (C classes first, then B, then A, then R)
            var classOrder = new Dictionary<string, int>
            {
                { "C1", 1 }, { "C1 Dam", 2 }, { "C1 Jun", 3 },
                { "C2", 4 }, { "C2 Dam", 5 }, { "C2 Jun", 6 },
                { "C3", 7 }, { "C3 Dam", 8 }, { "C3 Jun", 9 },
                { "C Vet Y", 10 }, { "C Vet Y Dam", 11 }, { "C Vet Y Jun", 12 },
                { "C Vet Ä", 13 }, { "C Vet Ä Dam", 14 }, { "C Vet Ä Jun", 15 },
                { "B1", 16 }, { "B1 Dam", 17 }, { "B1 Jun", 18 },
                { "B2", 19 }, { "B2 Dam", 20 }, { "B2 Jun", 21 },
                { "B3", 22 }, { "B3 Dam", 23 }, { "B3 Jun", 24 },
                { "B Vet Y", 25 }, { "B Vet Y Dam", 26 }, { "B Vet Y Jun", 27 },
                { "B Vet Ä", 28 }, { "B Vet Ä Dam", 29 }, { "B Vet Ä Jun", 30 },
                { "A1", 31 }, { "A1 Dam", 32 }, { "A1 Jun", 33 },
                { "A2", 34 }, { "A2 Dam", 35 }, { "A2 Jun", 36 },
                { "A3", 37 }, { "A3 Dam", 38 }, { "A3 Jun", 39 },
                { "A Opt 1", 40 }, { "A Opt 2", 41 }, { "A Opt 3", 42 },
                { "A Opt", 43 }, // legacy / unspecific
                { "R1", 44 }, { "R2", 45 }, { "R3", 46 }
            };

            // Determine competition type for type-specific logic
            var competitionTypeId = competition?.GetValue<string>("competitionType") ?? "Precision";
            var isMilsnabb = competitionTypeId.Equals("Milsnabb", StringComparison.OrdinalIgnoreCase);
            var isDuell = competitionTypeId.Equals("Duell", StringComparison.OrdinalIgnoreCase);
            var isNationellHelmatch = competitionTypeId.Equals("NationellHelmatch", StringComparison.OrdinalIgnoreCase);
            var isMagnumPrecision = competitionTypeId.Equals("MagnumPrecision", StringComparison.OrdinalIgnoreCase);

            // Select tiebreaker based on competition type
            // Duell uses the same tiebreaker as Precision (series count back)
            // Nationell Helmatch uses the same tiebreaker as Milsnabb (count-back by pairs)
            IComparer<ShooterResult> comparer = (isMilsnabb || isNationellHelmatch)
                ? new MilsnabbTieBreaker()
                : new SeriesCountBackComparer(hasFinalsRound, qualificationSeriesCount, numberOfFinalSeries);

            // Group by shooting class (using merge lookup if merges were applied).
            // Sort key is the MIN classOrder among the group's constituent shooters' classes,
            // so merged groups (e.g. "C2+Dam", "A2+3", "C Vet") land next to their lowest member
            // instead of falling through to the unknown-class bucket.
            var classGroups = shooterResults
                .GroupBy(s => GetGroupName(s.ShootingClass))
                .OrderBy(g => g.Min(s => classOrder.GetValueOrDefault(s.ShootingClass, 999)))
                .ThenBy(g => g.Key) // stable secondary sort for any unknown classes
                .Select(classGroup => new ClassGroup
                {
                    ClassName = classGroup.Key,
                    Shooters = classGroup
                        .OrderByDescending(s => s.TotalScore)
                        .ThenByDescending(s => s.TotalXCount)
                        .ThenByDescending(s => s, comparer)
                        .ToList()
                })
                .ToList();

            // Championship Särskjutning override: for medal-tier ties (rank ≤ 3 in any class)
            // in Championship competitions, re-rank the tied slice using shoot-off entries.
            // Other ranks and non-championship competitions fall through to the existing
            // X-count + countback already applied above.
            var competitionScope = competition?.GetValue<string>("competitionScope") ?? "";
            if (CompetitionScopeHelper.IsChampionshipScope(competitionScope))
            {
                var shootOffEntries = await _shootOffService.GetEntriesForCompetitionAsync(competitionId);
                var entriesByMember = shootOffEntries.ToLookup(e => e.MemberId);

                foreach (var classGroup in classGroups)
                {
                    var tiedRaw = ShootOffService.DetectTiedMedalGroups(classGroup.Shooters, classGroup.ClassName);
                    if (tiedRaw.Count == 0) continue;

                    ShootOffService.ApplyShootOffOverride(classGroup.Shooters, tiedRaw, entriesByMember);

                    // Project DTO (decouple admin payload from the live model)
                    foreach (var g in tiedRaw)
                    {
                        var dto = new PrecisionTiedMedalGroup
                        {
                            MedalTier = g.MedalTier,
                            FirstRank = g.FirstRank,
                            LastRank = g.LastRank,
                            TotalScore = g.TotalScore,
                            RoundsCompleted = g.RoundsCompleted,
                            Resolved = g.Resolved
                        };
                        foreach (var s in g.Shooters)
                        {
                            var rounds = entriesByMember[s.MemberId]
                                .Where(e => string.Equals(e.ShootingClass, s.ShootingClass, StringComparison.OrdinalIgnoreCase))
                                .GroupBy(e => e.Round)
                                .OrderBy(grp => grp.Key)
                                .Select(grp =>
                                {
                                    int total = 0, x = 0;
                                    string rawShots = grp.First().Shots; // round = single 5-shot series in this version
                                    try
                                    {
                                        var shotsList = Newtonsoft.Json.JsonConvert
                                            .DeserializeObject<List<string>>(rawShots) ?? new();
                                        total = (int)CompetitionTypes.Common.Utilities.ScoringUtilities.CalculateTotal(shotsList);
                                        x = CompetitionTypes.Common.Utilities.ScoringUtilities.CountInnerTens(shotsList);
                                    }
                                    catch { /* leave 0/0 */ }
                                    return new PrecisionShootOffRoundEntry
                                    {
                                        Round = grp.Key,
                                        Shots = rawShots,
                                        Total = total,
                                        XCount = x
                                    };
                                })
                                .ToList();

                            dto.Shooters.Add(new PrecisionTiedMedalShooter
                            {
                                MemberId = s.MemberId,
                                Name = s.Name,
                                Club = s.Club,
                                ShootingClass = s.ShootingClass,
                                TotalScore = s.TotalScore,
                                XCount = s.TotalXCount,
                                IsResolved = s.ShootOffIsResolved,
                                NextRound = s.ShootOffNextRound,
                                Rounds = rounds
                            });
                        }
                        classGroup.TiedMedalGroups.Add(dto);

                        // Build a public-friendly footnote when the group is resolved.
                        if (g.Resolved && g.Shooters.Count >= 2)
                        {
                            var ordered = g.Shooters
                                .Where(s => s.ShootOffScore.HasValue)
                                .OrderBy(s => g.Shooters.IndexOf(s))
                                .ToList();
                            if (ordered.Count >= 2)
                            {
                                var medalNouns = ShootOffService.MedalNounsForRange(g.FirstRank, g.LastRank);
                                var parts = ordered.Select(s => $"{s.Name} {s.ShootOffScore}");
                                classGroup.ShootOffNotes.Add(
                                    $"Särskjutning avgjorde {medalNouns}: {string.Join(" vs ", parts)}");
                            }
                        }
                    }
                }
            }

            // Calculate Standard Medal Awards if enabled
            var isAwardingStandardMedals = competition?.GetValue<bool>("isAwardingStandardMedals") ?? false;
            var isClubOnly = competition?.GetValue<bool>("isClubOnly") ?? false;

            // Per BR-PS.1.3: Standard medals may not be awarded at Föreningstävlingar (club competitions).
            // The `isClubOnly` flag is the authoritative marker for "this is a club competition" —
            // it covers Klubbmästerskap, regular club competitions, and any other club-internal event.
            if (isAwardingStandardMedals && !isClubOnly)
            {
                var allShooters = classGroups.SelectMany(g => g.Shooters).ToList();
                var shouldSplitGroupC = new StandardMedalCalculationService().ShouldSplitGroupC(competitionScope);

                // Per BR-PS.2.3: Standard medals are determined from the qualification round only,
                // not the finals. PrecisionShooterResult.TotalScore sums *all* Results, so when finals
                // exist we must run the calculation on shooters whose Results are filtered to
                // qualification series (SeriesNumber <= qualificationSeriesCount). Without this,
                // 10-series totals get compared against 7-series fixed-score thresholds and false
                // Silver medals are awarded.
                var medalShooters = hasFinalsRound
                    ? allShooters.Select(s => new ShooterResult
                    {
                        MemberId = s.MemberId,
                        Name = s.Name,
                        Club = s.Club,
                        ShootingClass = s.ShootingClass,
                        Results = s.Results.Where(r => r.SeriesNumber <= qualificationSeriesCount).ToList()
                    }).ToList()
                    : allShooters;

                if (isMilsnabb)
                {
                    // Milsnabb has its own fixed-score thresholds (includes R group)
                    var milsnabbMedalService = new MilsnabbStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    milsnabbMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isDuell)
                {
                    // Duell uses placement-only medals (no fixed-score thresholds)
                    var duellMedalService = new DuellStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    duellMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isNationellHelmatch)
                {
                    // Nationell Helmatch uses placement-only medals (same pattern as Duell)
                    var nhMedalService = new NationellHelmatchStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    nhMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isMagnumPrecision)
                {
                    // Magnum Precision uses percentage + fixed-score medals with M-class thresholds
                    var mpMedalService = new MagnumPrecisionStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    mpMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else
                {
                    // Precision and other types use standard medal service
                    var medalService = new StandardMedalCalculationService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    medalService.CalculateStandardMedals(medalShooters, config);
                }

                // When we used a filtered copy, copy the medals back to the originals.
                if (hasFinalsRound)
                {
                    var medalLookup = medalShooters.ToDictionary(
                        s => (s.MemberId, s.ShootingClass),
                        s => s.StandardMedal);
                    foreach (var shooter in allShooters)
                    {
                        if (medalLookup.TryGetValue((shooter.MemberId, shooter.ShootingClass), out var medal))
                        {
                            shooter.StandardMedal = medal;
                        }
                    }
                }

                _logger.LogInformation("Calculated standard medals for {Count} shooters in competition {CompetitionId} (Type: {Type}, Scope: {Scope}, Series: {SeriesCount})",
                    classGroups.SelectMany(g => g.Shooters).Count(), competitionId, competitionTypeId, competitionScope, qualificationSeriesCount);
            }

            return new FinalResults
            {
                CompetitionId = competitionId,
                UpdatedAt = DateTime.Now,
                IsOfficial = true,
                ClassGroups = classGroups
            };
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeShooterClass([FromBody] ChangeShooterClassRequest request)
        {
            try
            {
                // Validate parameters
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0)
                {
                    return Json(new { success = false, message = "Ogiltiga parametrar." });
                }

                if (string.IsNullOrWhiteSpace(request.OldShootingClass) || string.IsNullOrWhiteSpace(request.NewShootingClass))
                {
                    return Json(new { success = false, message = "Både gammal och ny vapenklass måste anges." });
                }

                if (request.OldShootingClass.Equals(request.NewShootingClass, StringComparison.OrdinalIgnoreCase))
                {
                    return Json(new { success = false, message = "Ingen ändring behövs - samma vapenklass." });
                }

                // Validate new class exists
                var newClass = ShootingClasses.GetByName(request.NewShootingClass);
                if (newClass == null)
                {
                    return Json(new { success = false, message = $"Vapenklassen '{request.NewShootingClass}' finns inte." });
                }

                // Get competition
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen hittades inte." });
                }

                // Authorization: site admin OR competition manager OR club admin OR Skjutledare
                bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(request.CompetitionId);
                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _adminAuthorizationService.IsClubAdminForClub(competitionClubId);
                    isSkjutledare = await _adminAuthorizationService.IsSkjutledareForClub(competitionClubId);
                }

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Json(new { success = false, message = "Åtkomst nekad. Du har inte behörighet att ändra vapenklass." });
                }

                _logger.LogInformation("ChangeShooterClass: CompetitionId={CompetitionId}, MemberId={MemberId}, OldClass={OldClass}, NewClass={NewClass}",
                    request.CompetitionId, request.MemberId, request.OldShootingClass, request.NewShootingClass);

                // 1. Update database result rows (route to correct table)
                var resultTable = GetResultTableName(GetCompetitionTypeId(request.CompetitionId));
                int rowsUpdated = 0;
                using (var db = _umbracoDatabaseFactory.CreateDatabase())
                {
                    rowsUpdated = await db.ExecuteAsync(
                        $"UPDATE [{resultTable}] SET ShootingClass = @0, LastModified = @1 WHERE CompetitionId = @2 AND MemberId = @3 AND ShootingClass = @4",
                        newClass.Name, DateTime.Now, request.CompetitionId, request.MemberId, request.OldShootingClass);
                }

                _logger.LogInformation("Updated {RowsUpdated} result rows from '{OldClass}' to '{NewClass}' for member {MemberId}",
                    rowsUpdated, request.OldShootingClass, newClass.Name, request.MemberId);

                // 2. Update start list
                bool startListUpdated = false;
                try
                {
                    startListUpdated = UpdateStartListShooterClass(request.CompetitionId, request.MemberId, request.OldShootingClass, newClass.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update start list for class change, continuing");
                }

                // 3. Update registration
                bool registrationUpdated = false;
                try
                {
                    registrationUpdated = UpdateRegistrationShooterClass(request.CompetitionId, request.MemberId, request.OldShootingClass, newClass.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update registration for class change, continuing");
                }

                // 4. Recalculate results
                bool resultsRecalculated = false;
                try
                {
                    var results = await GetCompetitionResultsInternal(request.CompetitionId);
                    if (results.Any())
                    {
                        var finalResults = await CalculateFinalResults(results, request.CompetitionId);

                        var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                            .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                        if (resultPage != null)
                        {
                            var existingIsOfficial = resultPage.GetValue<bool>("isOfficial");
                            resultPage.SetValue("resultData", JsonConvert.SerializeObject(finalResults));
                            resultPage.SetValue("lastUpdated", DateTime.Now);
                            resultPage.SetValue("isOfficial", existingIsOfficial);
                            resultPage.SetValue("resultType", "Final Results");

                            _contentService.Save(resultPage);
                            _contentService.Publish(resultPage, new[] { "*" }, -1);
                            resultsRecalculated = true;

                            _logger.LogInformation("Recalculated results after class change for competition {CompetitionId}", request.CompetitionId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to recalculate results after class change, continuing");
                }

                // 5. Invalidate series cache
                try
                {
                    _seriesCalculationService.InvalidateCacheForCompetition(request.CompetitionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to invalidate series cache after class change, continuing");
                }

                var message = $"Vapenklass ändrad från '{request.OldShootingClass}' till '{newClass.Name}'. {rowsUpdated} resultatrad(er) uppdaterade.";
                if (startListUpdated) message += " Startlistan uppdaterad.";
                if (registrationUpdated) message += " Anmälan uppdaterad.";
                if (resultsRecalculated) message += " Resultatlistan omberäknad.";

                return Json(new
                {
                    success = true,
                    message,
                    rowsUpdated,
                    startListUpdated,
                    registrationUpdated,
                    resultsRecalculated
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing shooter class for member {MemberId} in competition {CompetitionId}",
                    request?.MemberId, request?.CompetitionId);
                return Json(new { success = false, message = "Ett oväntat fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Update the shooter's weapon class in the start list configurationData
        /// </summary>
        private bool UpdateStartListShooterClass(int competitionId, int memberId, string oldClass, string newClass)
        {
            var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out long total);
            var possibleAliases = new[] { "precisionStartList", "PrecisionStartList", "precision-start-list" };

            // Find start list - direct child first
            var startListContent = children.FirstOrDefault(c => possibleAliases.Contains(c.ContentType.Alias));

            if (startListContent == null)
            {
                // Look in start lists hub
                var startListsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
                if (startListsHub != null)
                {
                    var hubChildren = _contentService.GetPagedChildren(startListsHub.Id, 0, int.MaxValue, out long hubTotal);
                    startListContent = hubChildren
                        .Where(c => possibleAliases.Contains(c.ContentType.Alias))
                        .FirstOrDefault(c =>
                        {
                            try { return c.GetValue<bool>("isOfficialStartList"); }
                            catch { return false; }
                        });
                }
            }

            if (startListContent == null)
            {
                _logger.LogInformation("No start list found for competition {CompetitionId}, skipping start list update", competitionId);
                return false;
            }

            var configData = startListContent.GetValue<string>("configurationData");
            if (string.IsNullOrEmpty(configData))
            {
                return false;
            }

            var configuration = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
            if (configuration?.Teams == null)
            {
                return false;
            }

            // Find and update all occurrences of this shooter with the old class
            bool found = false;
            foreach (var team in configuration.Teams)
            {
                if (team.Shooters == null) continue;
                foreach (var shooter in team.Shooters.Where(s => s.MemberId == memberId &&
                    (s.WeaponClass ?? "").Equals(oldClass, StringComparison.OrdinalIgnoreCase)))
                {
                    shooter.WeaponClass = newClass;
                    found = true;
                }

                // Update team weapon classes
                team.WeaponClasses = team.Shooters.Select(s => s.WeaponClass).Distinct().ToList();
            }

            if (!found)
            {
                _logger.LogWarning("Shooter {MemberId} with class {OldClass} not found in start list for competition {CompetitionId}",
                    memberId, oldClass, competitionId);
                return false;
            }

            var configJson = JsonConvert.SerializeObject(configuration);
            startListContent.SetValue("configurationData", configJson);

            var result = _contentService.Save(startListContent);
            if (result.Success)
            {
                _contentService.Publish(startListContent, new[] { "*" }, -1);
                _logger.LogInformation("Updated start list weapon class for member {MemberId}: {OldClass} -> {NewClass}", memberId, oldClass, newClass);
                return true;
            }

            return false;
        }

        /// <summary>
        /// <summary>
        /// Returns the MemberIds of registrations with isSubCompetition=true for the given competition.
        /// Used by the Deltävling result paths to filter the source result rows to the subset of
        /// shooters who opted into the sub-competition at registration time.
        /// </summary>
        private HashSet<int> GetSubCompetitionMemberIds(int competitionId)
        {
            var ids = new HashSet<int>();
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return ids;

            var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null) return ids;

            var registrations = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .Where(c => c.GetValue<int>("competitionId") == competitionId)
                .Where(c => c.HasProperty("isSubCompetition") && c.GetValue<bool>("isSubCompetition"));

            foreach (var r in registrations)
            {
                var memberId = r.GetValue<int>("memberId");
                if (memberId > 0) ids.Add(memberId);
            }
            return ids;
        }

        /// <summary>
        /// Update the shooter's weapon class in the competition registration
        /// </summary>
        private bool UpdateRegistrationShooterClass(int competitionId, int memberId, string oldClass, string newClass)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return false;

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
            var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

            if (!shootingClasses.Any())
            {
                // Try legacy format
                var legacyClass = memberRegistration.GetValue<string>("shootingClass");
                if (!string.IsNullOrWhiteSpace(legacyClass) && legacyClass.Equals(oldClass, StringComparison.OrdinalIgnoreCase))
                {
                    memberRegistration.SetValue("shootingClass", newClass);
                    var legacyResult = _contentService.Save(memberRegistration);
                    if (legacyResult.Success)
                    {
                        _contentService.Publish(memberRegistration, new[] { "*" }, -1);
                        return true;
                    }
                }
                return false;
            }

            // Find and update the specific class entry
            var classEntry = shootingClasses.FirstOrDefault(c =>
                c.Class.Equals(oldClass, StringComparison.OrdinalIgnoreCase));

            if (classEntry == null)
            {
                _logger.LogWarning("Class entry '{OldClass}' not found in registration for member {MemberId}", oldClass, memberId);
                return false;
            }

            classEntry.Class = newClass;

            var updatedJson = System.Text.Json.JsonSerializer.Serialize(shootingClasses);
            memberRegistration.SetValue("shootingClasses", updatedJson);

            var saveResult = _contentService.Save(memberRegistration);
            if (saveResult.Success)
            {
                _contentService.Publish(memberRegistration, new[] { "*" }, -1);
                _logger.LogInformation("Updated registration weapon class for member {MemberId}: {OldClass} -> {NewClass}",
                    memberId, oldClass, newClass);
                return true;
            }

            return false;
        }

    }

    public class ChangeShooterClassRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string OldShootingClass { get; set; } = "";
        public string NewShootingClass { get; set; } = "";
    }

    public class DeleteShooterFromClassRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
    }

    public class CreateResultsListRequest
    {
        public int CompetitionId { get; set; }
        public List<HpskSite.Services.ClassMergeAction>? Merges { get; set; }
        /// <summary>When true, persist merges to subCompetitionMergeConfig and treat the
        /// result generation as the Deltävling subset. The main resultData snapshot is
        /// left untouched.</summary>
        public bool IsSubCompetition { get; set; }
    }

    public class ToggleResultsOfficialRequest
    {
        public int CompetitionId { get; set; }
        public bool? IsOfficial { get; set; } // null = toggle, true/false = set explicit value
        /// <summary>When true, flip subCompetitionIsOfficial on the competitionResult node
        /// instead of the main isOfficial flag. Drives the second public Visa resultat button.</summary>
        public bool IsSubCompetition { get; set; }
    }

    public class ClassNameOverrideRequest
    {
        public int CompetitionId { get; set; }
        /// <summary>The auto-generated class name being overridden (e.g. "C2+Dam+Vet").</summary>
        public string OriginalName { get; set; } = "";
        /// <summary>The custom display name. Empty or null reverts to the auto-generated name.</summary>
        public string? CustomName { get; set; }
        public bool IsSubCompetition { get; set; }
    }

    public class ShootOffEntryRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int Round { get; set; }
        public string[] Shots { get; set; } = new string[5];
    }

    public class ShootOffDeleteRequest
    {
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int Round { get; set; }
    }

    /// <summary>
    /// Comparer for implementing count-back tie-breaking rules.
    /// When shooters have the same total score and X count, the shooter with
    /// the highest score in the last series wins. If still tied, check the
    /// second-to-last series, and so on.
    /// 
    /// For competitions with finals:
    /// - Prioritize finals series (count-back through finals first)
    /// - Then count-back through qualification series if still tied
    /// </summary>
    public class SeriesCountBackComparer : IComparer<ShooterResult>
    {
        private readonly bool _hasFinalsRound;
        private readonly int _qualificationSeriesCount;
        private readonly int _numberOfFinalSeries;

        public SeriesCountBackComparer(bool hasFinalsRound = false, int qualificationSeriesCount = 0, int numberOfFinalSeries = 0)
        {
            _hasFinalsRound = hasFinalsRound;
            _qualificationSeriesCount = qualificationSeriesCount;
            _numberOfFinalSeries = numberOfFinalSeries;
        }

        public int Compare(ShooterResult? x, ShooterResult? y)
        {
            if (x == null || y == null)
                return 0;

            // Get the series scores for both shooters (ordered by series number ascending)
            var xAllSeriesScores = x.Results
                .OrderBy(r => r.SeriesNumber)
                .Select(r => CalculateSeriesScore(r.Shots))
                .ToList();

            var yAllSeriesScores = y.Results
                .OrderBy(r => r.SeriesNumber)
                .Select(r => CalculateSeriesScore(r.Shots))
                .ToList();

            if (_hasFinalsRound && xAllSeriesScores.Count >= _qualificationSeriesCount + _numberOfFinalSeries 
                               && yAllSeriesScores.Count >= _qualificationSeriesCount + _numberOfFinalSeries)
            {
                // Competition with finals: prioritize finals series
                var xFinalsScores = xAllSeriesScores.Skip(_qualificationSeriesCount).Take(_numberOfFinalSeries).ToList();
                var yFinalsScores = yAllSeriesScores.Skip(_qualificationSeriesCount).Take(_numberOfFinalSeries).ToList();

                // Count-back through finals series (last to first)
                for (int i = xFinalsScores.Count - 1; i >= 0; i--)
                {
                    var xScore = i < xFinalsScores.Count ? xFinalsScores[i] : 0;
                    var yScore = i < yFinalsScores.Count ? yFinalsScores[i] : 0;

                    if (xScore != yScore)
                    {
                        return xScore.CompareTo(yScore);
                    }
                }

                // If finals are tied, count-back through qualification series (last to first)
                var xQualScores = xAllSeriesScores.Take(_qualificationSeriesCount).ToList();
                var yQualScores = yAllSeriesScores.Take(_qualificationSeriesCount).ToList();

                for (int i = xQualScores.Count - 1; i >= 0; i--)
                {
                    var xScore = i < xQualScores.Count ? xQualScores[i] : 0;
                    var yScore = i < yQualScores.Count ? yQualScores[i] : 0;

                    if (xScore != yScore)
                    {
                        return xScore.CompareTo(yScore);
                    }
                }
            }
            else
            {
                // Regular competition: count-back from last series to first
                for (int i = xAllSeriesScores.Count - 1; i >= 0; i--)
                {
                    var xScore = i < xAllSeriesScores.Count ? xAllSeriesScores[i] : 0;
                    var yScore = i < yAllSeriesScores.Count ? yAllSeriesScores[i] : 0;

                    if (xScore != yScore)
                    {
                        return xScore.CompareTo(yScore);
                    }
                }
            }

            // If all series scores are equal, they are truly tied
            return 0;
        }

        private int CalculateSeriesScore(string shotsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(shotsJson))
                    return 0;

                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(shotsJson);
                if (shots == null || !shots.Any())
                    return 0;

                return shots.Sum(shot =>
                {
                    if (shot == "X")
                        return 10;
                    if (int.TryParse(shot, out int value))
                        return value;
                    return 0;
                });
            }
            catch
            {
                return 0;
            }
        }
    }
}
