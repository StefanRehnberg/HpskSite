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
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Services;
using Newtonsoft.Json;
using PrecisionResultEntry = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntry;
using ResultEntrySession = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntrySession;
using ResultEntryRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryRequest;
using ResultEntryResponse = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryResponse;
using SessionRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionSessionRequest;
using SessionResponse = HpskSite.CompetitionTypes.Precision.Models.PrecisionSessionResponse;
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
            ClubService clubService)
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

                // Check if position is available for editing
                var sessionConflict = await CheckSessionConflict(request);
                if (sessionConflict != null)
                {
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = sessionConflict
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

                if (resultId == 0)
                {
                    return Json(new ResultEntryResponse
                    {
                        Success = false,
                        Message = "Ett fel uppstod vid sparande av resultatet."
                    });
                }

                // Update or create session
                await UpdateOrCreateSession(request);

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcquireSession([FromBody] SessionRequest request)
        {
            try
            {
                // Check if position is already being edited
                var existingSession = await GetActiveSession(request);

                if (existingSession != null && existingSession.RangeOfficerId != request.RangeOfficerId)
                {
                    return Json(new SessionResponse
                    {
                        Success = false,
                        Message = "Positionen redigeras för närvarande av en annan domare.",
                        IsAvailable = false
                    });
                }

                // Update or create session
                var sessionId = await UpdateOrCreateSession(new ResultEntryRequest
                {
                    CompetitionId = request.CompetitionId,
                    TeamNumber = request.TeamNumber,
                    Position = request.Position,
                    SeriesNumber = request.SeriesNumber,
                    RangeOfficerId = request.RangeOfficerId,
                    Shots = new string[5]
                });

                return Json(new SessionResponse
                {
                    Success = true,
                    Message = "Session etablerad framgångsrikt.",
                    SessionId = sessionId,
                    IsAvailable = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring session for competition {CompetitionId}, team {TeamNumber}, position {Position}",
                    request.CompetitionId, request.TeamNumber, request.Position);

                return Json(new SessionResponse
                {
                    Success = false,
                    Message = "Ett fel uppstod vid etablering av session.",
                    IsAvailable = false
                });
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
                return Json(new { Success = true, Results = results });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting competition results for competition {CompetitionId}", competitionId);
                return Json(new { Success = false, Message = "Error loading results" });
            }
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

                // Query database for this member's results (don't filter by shootingClass - get all their results)
                using var database = _umbracoDatabaseFactory.CreateDatabase();
                var query = @"SELECT * FROM PrecisionResultEntry
                              WHERE CompetitionId = @0 AND MemberId = @1
                              ORDER BY SeriesNumber";

                var results = await database.FetchAsync<PrecisionResultEntry>(query, competitionId, memberId);

                if (!results.Any())
                {
                    return Json(new { success = false, message = "Inga resultat hittades för denna tävling." });
                }

                // Get actual shooting class from results (not from input parameter)
                var actualShootingClass = results.First().ShootingClass;

                // Derive weapon class from shooting class (e.g., "A1" -> "A", "C2" -> "C")
                var weaponClass = !string.IsNullOrEmpty(actualShootingClass) && actualShootingClass.Length > 0
                    ? actualShootingClass.Substring(0, 1)
                    : "?";

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
                    Club = r.MemberClub ?? "Okänd klubb",
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
                            Club = registration.MemberClub ?? shooter.Club ?? "Okänd klubb",
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
                        Club = reg.MemberClub ?? "Okänd klubb",
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
        public async Task<IActionResult> ClearAllSessions()
        {
            try
            {
                _logger.LogInformation("Clearing all active sessions");

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                // Clear all active sessions
                await db.ExecuteAsync("UPDATE ResultEntrySession SET IsActive = 0 WHERE IsActive = 1");

                transaction.Complete();

                _logger.LogInformation("All sessions cleared successfully");

                return Json(new { Success = true, Message = "All sessions cleared successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing sessions");
                return Json(new { Success = false, Message = $"Error clearing sessions: {ex.Message}" });
            }
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

        private async Task<string?> CheckSessionConflict(ResultEntryRequest request)
        {
            var existingSession = await GetActiveSession(new SessionRequest
            {
                CompetitionId = request.CompetitionId,
                TeamNumber = request.TeamNumber,
                Position = request.Position,
                SeriesNumber = request.SeriesNumber,
                RangeOfficerId = request.RangeOfficerId
            });

            if (existingSession != null && existingSession.RangeOfficerId != request.RangeOfficerId)
            {
                // Get range officer name for better error message
                var rangeOfficer = _memberService.GetById(existingSession.RangeOfficerId);
                var officerName = rangeOfficer?.Name ?? "Okänd domare";

                return $"Positionen redigeras för närvarande av {officerName}. Vänligen vänta eller välj en annan position.";
            }

            return null;
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
                _logger.LogInformation("Starting to save result to database for competition {CompetitionId}", request.CompetitionId);

                // Use proper connection disposal
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                try
                {
                    // IDENTITY-BASED LOOKUP: Query by (CompetitionId, MemberId, SeriesNumber)
                    // This allows results to follow the shooter even if their position changes
                    _logger.LogInformation("Checking for existing result with CompetitionId={CompetitionId}, MemberId={MemberId}, SeriesNumber={SeriesNumber}",
                        request.CompetitionId, request.ShooterMemberId, request.SeriesNumber);

                    var existingResult = await db.FirstOrDefaultAsync<PrecisionResultEntry>(
                        "WHERE CompetitionId = @0 AND MemberId = @1 AND SeriesNumber = @2",
                        request.CompetitionId, request.ShooterMemberId, request.SeriesNumber);

                    var shotsJson = JsonConvert.SerializeObject(request.Shots);
                    var now = DateTime.Now;

                    if (existingResult != null)
                    {
                        // Update existing result
                        _logger.LogInformation("Updating existing result ID {ResultId} for MemberId {MemberId}", existingResult.Id, request.ShooterMemberId);

                        existingResult.Shots = shotsJson;
                        existingResult.TeamNumber = request.TeamNumber; // Update informational field
                        existingResult.Position = request.Position;     // Update informational field
                        existingResult.ShootingClass = request.ShooterClass;
                        existingResult.EnteredBy = request.RangeOfficerId;
                        existingResult.LastModified = now;

                        await db.UpdateAsync(existingResult);
                        transaction.Complete();

                        _logger.LogInformation("Successfully updated result for MemberId {MemberId} (Team {Team}, Position {Position})",
                            request.ShooterMemberId, request.TeamNumber, request.Position);

                        return existingResult.Id;
                    }
                    else
                    {
                        // Insert new result
                        _logger.LogInformation("Creating new result for MemberId {MemberId} (Team {Team}, Position {Position})",
                            request.ShooterMemberId, request.TeamNumber, request.Position);

                        var newResult = new PrecisionResultEntry
                        {
                            CompetitionId = request.CompetitionId,
                            SeriesNumber = request.SeriesNumber,
                            MemberId = request.ShooterMemberId,           // IDENTITY FIELD - Primary lookup
                            TeamNumber = request.TeamNumber,              // INFORMATIONAL - Position at time of entry
                            Position = request.Position,                  // INFORMATIONAL - Position at time of entry
                            ShootingClass = request.ShooterClass,
                            Shots = shotsJson,
                            EnteredBy = request.RangeOfficerId,
                            EnteredAt = now,
                            LastModified = now
                        };

                        _logger.LogInformation("Attempting to insert result to database");
                        var resultId = await db.InsertAsync(newResult);
                        _logger.LogInformation("Successfully inserted result with ID: {ResultId} for MemberId {MemberId}", resultId, request.ShooterMemberId);

                        // Commit the transaction
                        transaction.Complete();

                        // Convert decimal to int (SQL Server returns decimal for auto-increment IDs)
                        return Convert.ToInt32(resultId);
                    }
                }
                catch (Exception ex)
                {
                    // Rollback transaction on error
                    _logger.LogError(ex, "Database operation failed, rolling back transaction. Exception: {ExceptionMessage}", ex.Message);
                    throw; // Re-throw to be caught by outer catch
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error saving result. Exception: {ExceptionMessage}. StackTrace: {StackTrace}",
                    ex.Message, ex.StackTrace);
                return 0;
            }
        }

        private async Task<int> UpdateOrCreateSession(ResultEntryRequest request)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                var existingSession = await db.FirstOrDefaultAsync<ResultEntrySession>(
                    "WHERE CompetitionId = @0 AND TeamNumber = @1 AND Position = @2 AND SeriesNumber = @3",
                    request.CompetitionId, request.TeamNumber, request.Position, request.SeriesNumber);

                var now = DateTime.Now;

                if (existingSession != null)
                {
                    existingSession.RangeOfficerId = request.RangeOfficerId;
                    existingSession.LastActivity = now;
                    existingSession.IsActive = true;

                    await db.UpdateAsync(existingSession);
                    transaction.Complete();
                    return existingSession.Id;
                }
                else
                {
                    var newSession = new ResultEntrySession
                    {
                        CompetitionId = request.CompetitionId,
                        Position = request.Position,
                        SeriesNumber = request.SeriesNumber,
                        RangeOfficerId = request.RangeOfficerId,
                        SessionStart = now,
                        LastActivity = now,
                        IsActive = true
                    };

                    var sessionId = await db.InsertAsync(newSession);
                    transaction.Complete();
                    // Convert decimal to int (SQL Server returns decimal for auto-increment IDs)
                    return Convert.ToInt32(sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error updating session");
                return 0;
            }
        }

        private async Task<ResultEntrySession?> GetActiveSession(SessionRequest request)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                return await db.FirstOrDefaultAsync<ResultEntrySession>(
                    "WHERE CompetitionId = @0 AND TeamNumber = @1 AND Position = @2 AND SeriesNumber = @3 AND IsActive = 1",
                    request.CompetitionId, request.TeamNumber, request.Position, request.SeriesNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error getting active session");
                return null;
            }
        }

        private async Task<List<PrecisionResultEntry>> GetCompetitionResultsInternal(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                return await db.FetchAsync<PrecisionResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TeamNumber, Position, SeriesNumber",
                    competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error getting competition results");
                return new List<PrecisionResultEntry>();
            }
        }

        private async Task<bool> DeleteResultFromDatabase(DeleteResultRequest request)
        {
            try
            {
                _logger.LogInformation("Starting to delete result from database for competition {CompetitionId}", request.CompetitionId);

                // IDENTITY-BASED DELETE: Use MemberId directly from request
                var memberId = request.MemberId;

                if (memberId <= 0)
                {
                    _logger.LogWarning("Invalid MemberId {MemberId} in delete request for competition {CompetitionId}",
                        memberId, request.CompetitionId);
                    return false;
                }

                _logger.LogInformation("Deleting result for MemberId {MemberId}, SeriesNumber {SeriesNumber}",
                    memberId, request.SeriesNumber);

                // Use proper connection disposal
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                try
                {
                    // IDENTITY-BASED LOOKUP: Query by (CompetitionId, MemberId, SeriesNumber)
                    _logger.LogInformation("Checking for existing result to delete with CompetitionId={CompetitionId}, MemberId={MemberId}, SeriesNumber={SeriesNumber}",
                        request.CompetitionId, memberId, request.SeriesNumber);

                    var existingResult = await db.FirstOrDefaultAsync<PrecisionResultEntry>(
                        "WHERE CompetitionId = @0 AND MemberId = @1 AND SeriesNumber = @2",
                        request.CompetitionId, memberId, request.SeriesNumber);

                    if (existingResult != null)
                    {
                        // Delete existing result
                        _logger.LogInformation("Deleting result with ID: {ResultId} for MemberId {MemberId}", existingResult.Id, memberId);

                        await db.DeleteAsync(existingResult);

                        // Commit the transaction
                        transaction.Complete();

                        _logger.LogInformation("Successfully deleted result for MemberId {MemberId}, Series {SeriesNumber}", memberId, request.SeriesNumber);
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
                    // Rollback transaction on error
                    _logger.LogError(ex, "Database operation failed, rolling back transaction. Exception: {ExceptionMessage}", ex.Message);
                    throw; // Re-throw to be caught by outer catch
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database error deleting result. Exception: {ExceptionMessage}. StackTrace: {StackTrace}",
                    ex.Message, ex.StackTrace);
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

            // Group results by MemberId and calculate totals from shots
            var shooterTotals = results
                .GroupBy(r => r.MemberId)
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
                    MemberId = g.Key,
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
                    shooter.TotalScore,
                    shooter.TotalXCount,
                    shooter.SeriesCount,
                    shooter.Results
                }).ToList()
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

                // Get all results for this competition
                var results = await GetCompetitionResultsInternal(request.CompetitionId);
                if (!results.Any())
                {
                    return Json(new { Success = false, Message = "Inga resultat hittades för denna tävling." });
                }

                // Calculate final results with rankings
                var finalResults = await CalculateFinalResults(results, competition.Id);

                // Find or create result page as direct child of competition
                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out long total)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                if (resultPage == null)
                {
                    // Create new result page
                    resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                    resultPage.SetValue("resultType", "Final Results");
                    resultPage.SetValue("isOfficial", false); // Start as preliminary
                    _logger.LogInformation("Created new result page for competition {CompetitionId}", request.CompetitionId);
                }
                
                // Keep existing isOfficial status
                var existingIsOfficial = resultPage.GetValue<bool>("isOfficial");
                
                // Update the result page
                resultPage.SetValue("resultData", Newtonsoft.Json.JsonConvert.SerializeObject(finalResults));
                resultPage.SetValue("lastUpdated", DateTime.Now);
                resultPage.SetValue("isOfficial", existingIsOfficial); // Keep existing status
                resultPage.SetValue("resultType", "Final Results");

                // Save and publish
                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" }, -1);

                _logger.LogInformation("Created/updated final results list for competition {CompetitionId}", request.CompetitionId);

                var finalResultsData = finalResults;
                var totalShooters = finalResults.ClassGroups.Sum(g => g.Shooters.Count);
                
                return Json(new { 
                    Success = true, 
                    Message = "Resultatlistan har skapats/uppdaterats framgångsrikt!",
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
        public async Task<IActionResult> GetResultsList(int competitionId)
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

                if (resultPage == null)
                {
                    return Json(new { Success = false, Message = "Ingen resultatsida hittades.", Exists = false });
                }

                var finalResultsList = resultPage;

                var isOfficial = finalResultsList.GetValue<bool>("isOfficial");
                var resultDataJson = finalResultsList.GetValue<string>("resultData");
                
                FinalResults resultData;
                DateTime lastUpdated;

                // OPTION 2: Single page approach
                if (isOfficial)
                {
                    // Official results: Use saved JSON snapshot (frozen final version)
                    _logger.LogInformation("Loading official (frozen) results for competition {CompetitionId}", competitionId);
                    
                    if (string.IsNullOrEmpty(resultDataJson))
                    {
                        return Json(new { Success = false, Message = "Officiella resultat saknar data.", Exists = false });
                    }
                    
                    resultData = JsonConvert.DeserializeObject<FinalResults>(resultDataJson);
                    lastUpdated = finalResultsList.GetValue<DateTime>("lastUpdated");
                }
                else
                {
                    // Preliminary results: Always generate fresh from database
                    _logger.LogInformation("Loading preliminary (live) results from database for competition {CompetitionId}", competitionId);
                    
                    var dbResults = await GetCompetitionResultsInternal(competitionId);
                    
                    if (!dbResults.Any())
                    {
                        return Json(new { Success = false, Message = "Inga resultat finns i databasen ännu.", Exists = false });
                    }
                    
                    // Generate fresh results from database
                    resultData = await CalculateFinalResults(dbResults, competitionId);
                    lastUpdated = DateTime.Now;
                    
                    _logger.LogInformation("Generated fresh preliminary results with {Count} shooters", 
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

            return Json(new
            {
                Success = true,
                Exists = true,
                IsOfficial = isOfficial,
                LastUpdated = lastUpdated,
                Results = resultData,
                ResultPageUrl = resultPageUrl,
                NumberOfSeries = numberOfSeriesOrStations,
                NumberOfFinalSeries = numberOfFinalSeries,
                IsAwardingStandardMedals = isAwardingStandardMedals
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

                // Toggle or set the isOfficial flag
                var newIsOfficial = request.IsOfficial ?? !finalResultsList.GetValue<bool>("isOfficial");

                // If making official, regenerate results from database to ensure latest format
                if (newIsOfficial)
                {
                    var dbResults = await GetCompetitionResultsInternal(request.CompetitionId);
                    if (dbResults.Any())
                    {
                        var freshResults = await CalculateFinalResults(dbResults, request.CompetitionId);
                        var resultDataJson = JsonConvert.SerializeObject(freshResults);
                        finalResultsList.SetValue("resultData", resultDataJson);
                        _logger.LogInformation("Regenerated results JSON with {Count} shooters for competition {CompetitionId}",
                            freshResults.ClassGroups.Sum(g => g.Shooters.Count), request.CompetitionId);
                    }
                }

                finalResultsList.SetValue("isOfficial", newIsOfficial);
                finalResultsList.SetValue("lastUpdated", DateTime.Now);

                // Save and publish
                _contentService.Save(finalResultsList);
                _contentService.Publish(finalResultsList, new[] { "*" }, -1);

                _logger.LogInformation("Toggled isOfficial for competition {CompetitionId} to {IsOfficial}", request.CompetitionId, newIsOfficial);

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


        private async Task<FinalResults> CalculateFinalResults(List<PrecisionResultEntry> results, int competitionId)
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
                shooterLookup[memberId] = (name, club);
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

            // Define class order (C classes first, then B, then A)
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
                { "A3", 37 }, { "A3 Dam", 38 }, { "A3 Jun", 39 }
            };

            // Group by shooting class and order classes
            var comparer = new SeriesCountBackComparer(hasFinalsRound, qualificationSeriesCount, numberOfFinalSeries);
            var classGroups = shooterResults
                .GroupBy(s => s.ShootingClass)
                .OrderBy(g => classOrder.GetValueOrDefault(g.Key, 999)) // Unknown classes go last
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

            // Calculate Standard Medal Awards if enabled
            var isAwardingStandardMedals = competition?.GetValue<bool>("isAwardingStandardMedals") ?? false;
            var competitionScope = competition?.GetValue<string>("competitionScope") ?? "";

            // Don't award medals for club championships (Klubbmästerskap)
            if (isAwardingStandardMedals && competitionScope != "Klubbmästerskap")
            {
                var medalService = new StandardMedalCalculationService();
                var shouldSplitGroupC = medalService.ShouldSplitGroupC(competitionScope);

                var config = new StandardMedalConfig
                {
                    SeriesCount = qualificationSeriesCount,  // Use qualification series only
                    ShouldSplitGroupC = shouldSplitGroupC
                };

                // Apply medals to ALL shooters across all classes
                // (medals are awarded by weapon group, not shooting class)
                var allShooters = classGroups.SelectMany(g => g.Shooters).ToList();
                medalService.CalculateStandardMedals(allShooters, config);

                _logger.LogInformation("Calculated standard medals for {Count} shooters in competition {CompetitionId} (Scope: {Scope}, Split C: {SplitC}, Series: {SeriesCount})",
                    allShooters.Count, competitionId, competitionScope, shouldSplitGroupC, qualificationSeriesCount);
            }

            return new FinalResults
            {
                CompetitionId = competitionId,
                UpdatedAt = DateTime.Now,
                IsOfficial = true,
                ClassGroups = classGroups
            };
        }

    }

    public class CreateResultsListRequest
    {
        public int CompetitionId { get; set; }
    }

    public class ToggleResultsOfficialRequest
    {
        public int CompetitionId { get; set; }
        public bool? IsOfficial { get; set; } // null = toggle, true/false = set explicit value
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
