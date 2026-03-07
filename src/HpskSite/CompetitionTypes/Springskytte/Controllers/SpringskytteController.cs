using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using Umbraco.Cms.Core.Security;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.CompetitionTypes.Springskytte.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Services;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Springskytte.Controllers
{
    public class SpringskytteController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly ILogger<SpringskytteController> _logger;
        private readonly UmbracoStartListRepository _startListRepository;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _adminAuthorizationService;
        private readonly SpringskytteScoringService _scoringService;

        public SpringskytteController(
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
            ILogger<SpringskytteController> logger,
            UmbracoStartListRepository startListRepository,
            ClubService clubService,
            AdminAuthorizationService adminAuthorizationService)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _contentTypeService = contentTypeService;
            _memberManager = memberManager;
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _logger = logger;
            _startListRepository = startListRepository;
            _clubService = clubService;
            _adminAuthorizationService = adminAuthorizationService;
            _scoringService = new SpringskytteScoringService();
        }

        // ===== RESULT ENTRY =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSpringskytteResult([FromBody] SpringskytteResultRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0)
                    return Json(new SpringskytteResultResponse { Success = false, Message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new SpringskytteResultResponse { Success = false, Message = "Åtkomst nekad." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition != null && competition.GetValue<bool>("isExternal"))
                    return Json(new SpringskytteResultResponse { Success = false, Message = "Extern tävling - resultat kan inte registreras." });

                // Parse sprint time from input if provided
                decimal? sprintTimeSeconds = request.SprintTimeSeconds;
                if (sprintTimeSeconds == null && !string.IsNullOrWhiteSpace(request.SprintTimeInput))
                {
                    sprintTimeSeconds = _scoringService.ParseSprintTime(request.SprintTimeInput);
                    if (sprintTimeSeconds == null)
                        return Json(new SpringskytteResultResponse { Success = false, Message = "Ogiltigt tidsformat. Använd MM:SS eller H:MM:SS." });
                }

                // Serialize shots
                var shotsJson = request.ShotSeries != null
                    ? JsonConvert.SerializeObject(request.ShotSeries)
                    : "[]";

                // Calculate shooting score and total time
                int shootingScore = _scoringService.CalculateShootingScore(shotsJson, request.WeaponClass);
                int penaltyMultiplier = request.PenaltyMultiplier > 0 ? request.PenaltyMultiplier : 1;
                decimal? totalTime = _scoringService.CalculateTotalTime(sprintTimeSeconds, shootingScore, penaltyMultiplier);

                // Get current user as EnteredBy
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int enteredBy = currentMember != null ? int.Parse(currentMember.Id) : 0;

                var now = DateTime.Now;

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Check for existing result (upsert)
                var existing = await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0 AND MemberId = @1 AND WeaponClass = @2",
                    request.CompetitionId, request.MemberId, request.WeaponClass);

                if (existing != null)
                {
                    // Update
                    existing.AgeGenderClass = request.AgeGenderClass;
                    existing.SprintTimeSeconds = request.Status != null ? null : sprintTimeSeconds;
                    existing.Shots = shotsJson;
                    existing.ShootingScore = request.Status != null ? null : (int?)shootingScore;
                    existing.PenaltyMultiplier = penaltyMultiplier;
                    existing.TotalTimeSeconds = request.Status != null ? null : totalTime;
                    existing.Status = request.Status;
                    existing.EnteredBy = enteredBy;
                    existing.LastModified = now;
                    await db.UpdateAsync(existing);

                    _logger.LogInformation("Updated Springskytte result for MemberId={MemberId}, CompetitionId={CompetitionId}, WeaponClass={WeaponClass}",
                        request.MemberId, request.CompetitionId, request.WeaponClass);

                    return Json(new SpringskytteResultResponse
                    {
                        Success = true,
                        Message = "Resultat uppdaterat.",
                        ResultId = existing.Id,
                        ShootingScore = shootingScore,
                        TotalTimeSeconds = totalTime,
                        TotalTimeDisplay = FormatTime(totalTime)
                    });
                }

                // Insert new
                var entry = new SpringskytteResultEntry
                {
                    CompetitionId = request.CompetitionId,
                    MemberId = request.MemberId,
                    WeaponClass = request.WeaponClass,
                    AgeGenderClass = request.AgeGenderClass,
                    StartOrder = 0,
                    SprintTimeSeconds = request.Status != null ? null : sprintTimeSeconds,
                    Shots = shotsJson,
                    ShootingScore = request.Status != null ? null : (int?)shootingScore,
                    PenaltyMultiplier = penaltyMultiplier,
                    TotalTimeSeconds = request.Status != null ? null : totalTime,
                    Status = request.Status,
                    EnteredBy = enteredBy,
                    EnteredAt = now,
                    LastModified = now
                };

                var resultId = await db.InsertAsync(entry);

                _logger.LogInformation("Inserted Springskytte result Id={ResultId} for MemberId={MemberId}, CompetitionId={CompetitionId}",
                    resultId, request.MemberId, request.CompetitionId);

                return Json(new SpringskytteResultResponse
                {
                    Success = true,
                    Message = "Resultat sparat.",
                    ResultId = (int)(long)resultId,
                    ShootingScore = shootingScore,
                    TotalTimeSeconds = totalTime,
                    TotalTimeDisplay = FormatTime(totalTime)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Springskytte result for CompetitionId={CompetitionId}, MemberId={MemberId}",
                    request?.CompetitionId, request?.MemberId);
                return Json(new SpringskytteResultResponse { Success = false, Message = "Ett fel uppstod vid sparning av resultat." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSpringskytteResult([FromBody] SpringskytteDeleteResultRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var rowsDeleted = await db.ExecuteAsync(
                    "DELETE FROM SpringskytteResultEntry WHERE CompetitionId = @0 AND MemberId = @1 AND WeaponClass = @2",
                    request.CompetitionId, request.MemberId, request.WeaponClass);

                _logger.LogInformation("Deleted {Count} Springskytte result(s) for MemberId={MemberId}, CompetitionId={CompetitionId}",
                    rowsDeleted, request.MemberId, request.CompetitionId);

                return Json(new { success = true, message = $"Resultat borttaget.", rowsDeleted });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Springskytte result");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== RESULTS LIST & CALCULATION =====

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteResults(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var entries = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TotalTimeSeconds", competitionId);

                if (!entries.Any())
                    return Json(new { success = true, results = new List<object>(), classGroups = new List<object>() });

                // Build shooter results with names
                var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
                var memberDict = LoadMemberInfo(memberIds);

                var shooterResults = entries.Select(e =>
                {
                    var (name, club) = memberDict.TryGetValue(e.MemberId, out var info)
                        ? info
                        : ($"Skytt {e.MemberId}", "Okänd klubb");
                    return _scoringService.BuildShooterResult(e, name, club);
                }).ToList();

                // Sort using tiebreaker
                var tieBreaker = new SpringskytteTieBreaker();
                shooterResults.Sort(tieBreaker);

                // Group by WeaponClass + AgeGenderClass
                var classGroups = shooterResults
                    .GroupBy(s => $"{s.WeaponClass}|{s.AgeGenderClass}")
                    .Select(g =>
                    {
                        var sorted = g.OrderBy(s => s, tieBreaker).ToList();
                        return new
                        {
                            weaponClass = sorted.First().WeaponClass,
                            ageGenderClass = sorted.First().AgeGenderClass,
                            className = $"Vapengrupp {sorted.First().WeaponClass} - {sorted.First().AgeGenderClass}",
                            shooters = sorted.Select((s, idx) => new
                            {
                                rank = s.Status == null && s.TotalTimeSeconds.HasValue ? idx + 1 : 0,
                                s.MemberId,
                                s.Name,
                                s.Club,
                                s.WeaponClass,
                                s.AgeGenderClass,
                                s.SprintTimeDisplay,
                                s.ShootingScore,
                                s.PenaltyTimeDisplay,
                                s.TotalTimeDisplay,
                                s.TotalTimeSeconds,
                                s.StandardMedal,
                                s.Status,
                                s.ShotSeries
                            })
                        };
                    })
                    .OrderBy(g => g.weaponClass)
                    .ThenBy(g => g.ageGenderClass)
                    .ToList();

                return Json(new { success = true, classGroups });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CalculateSpringskytteFinalResults([FromBody] int competitionId)
        {
            try
            {
                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var entries = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0", competitionId);

                if (!entries.Any())
                    return Json(new { success = false, message = "Inga resultat hittades." });

                var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
                var memberDict = LoadMemberInfo(memberIds);

                var shooterResults = entries.Select(e =>
                {
                    var (name, club) = memberDict.TryGetValue(e.MemberId, out var info)
                        ? info
                        : ($"Skytt {e.MemberId}", "Okänd klubb");
                    return _scoringService.BuildShooterResult(e, name, club);
                }).ToList();

                // Calculate medals
                var medalService = new SpringskytteMedalService();
                medalService.CalculateStandardMedals(shooterResults);

                // Sort using tiebreaker
                var tieBreaker = new SpringskytteTieBreaker();
                shooterResults.Sort(tieBreaker);

                // Build final results grouped by weapon class + age/gender class
                var classGroups = shooterResults
                    .GroupBy(s => $"{s.WeaponClass}|{s.AgeGenderClass}")
                    .Select(g =>
                    {
                        var sorted = g.OrderBy(s => s, tieBreaker).ToList();
                        return new SpringskytteClassGroup
                        {
                            ClassName = $"Vapengrupp {sorted.First().WeaponClass} - {sorted.First().AgeGenderClass}",
                            Shooters = sorted
                        };
                    })
                    .OrderBy(g => g.ClassName)
                    .ToList();

                var finalResults = new SpringskytteFinalResults
                {
                    CompetitionId = competitionId,
                    UpdatedAt = DateTime.Now,
                    IsOfficial = true,
                    ClassGroups = classGroups
                };

                // Store results on competition content node
                var competition = _contentService.GetById(competitionId);
                if (competition != null)
                {
                    competition.SetValue("competitionResult", JsonConvert.SerializeObject(finalResults));
                    _contentService.Publish(competition, Array.Empty<string>());

                    _logger.LogInformation("Published Springskytte final results for CompetitionId={CompetitionId}, {Count} shooters",
                        competitionId, shooterResults.Count);
                }

                return Json(new
                {
                    success = true,
                    message = $"Resultat beräknade för {shooterResults.Count} skyttar i {classGroups.Count} klasser.",
                    classGroups = classGroups.Select(g => new
                    {
                        className = g.ClassName,
                        shooterCount = g.Shooters.Count,
                        medals = g.Shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal))
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating Springskytte final results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod vid beräkning av slutresultat." });
            }
        }

        // ===== SHOOTERS FOR RESULTS ENTRY =====

        [HttpGet]
        public async Task<IActionResult> GetShootersForSpringskytteResults(int competitionId)
        {
            try
            {
                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                // Get registrations
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);

                // Get existing results to show status
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var existingResults = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0", competitionId);

                var resultLookup = existingResults.ToDictionary(
                    r => $"{r.MemberId}|{r.WeaponClass}",
                    r => r);

                var shooters = registrations.Select(r => new
                {
                    r.MemberId,
                    r.MemberName,
                    r.MemberClub,
                    weaponClass = ExtractWeaponClass(r.MemberClass),
                    ageGenderClass = ExtractAgeGenderClass(r.MemberClass),
                    registeredClass = r.MemberClass,
                    hasResult = resultLookup.ContainsKey($"{r.MemberId}|{ExtractWeaponClass(r.MemberClass)}"),
                    existingResult = resultLookup.TryGetValue($"{r.MemberId}|{ExtractWeaponClass(r.MemberClass)}", out var res)
                        ? new
                        {
                            res.SprintTimeSeconds,
                            res.ShootingScore,
                            res.TotalTimeSeconds,
                            totalTimeDisplay = FormatTime(res.TotalTimeSeconds),
                            res.Status
                        }
                        : null
                }).ToList();

                return Json(new
                {
                    success = true,
                    shooters,
                    availableClasses = SpringskytteClasses.All,
                    weaponClasses = SpringskytteClasses.WeaponClasses
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooters for Springskytte results entry");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== START LIST =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSpringskytteStartList([FromBody] SpringskytteStartListRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                // Get registrations
                var registrations = await _startListRepository.GetCompetitionRegistrations(request.CompetitionId);
                if (!registrations.Any())
                    return Json(new { success = false, message = "Inga anmälda skyttar hittades." });

                // Parse time parameters
                var firstStart = TimeSpan.Parse(request.FirstStartTime);
                var interval = TimeSpan.Parse("00:" + request.DefaultInterval);
                var breakDuration = TimeSpan.Parse("00:" + request.BreakDuration);
                int breakAfter = request.BreakAfterEvery > 0 ? request.BreakAfterEvery : 10;

                // Build start list entries, ordered by weapon class then registration order
                var starters = new List<SpringskytteStartListEntry>();
                var currentTime = firstStart;
                int startOrder = 1;
                int sinceLastBreak = 0;

                var orderedRegistrations = registrations
                    .OrderBy(r => ExtractWeaponClass(r.MemberClass))
                    .ThenBy(r => r.Id)
                    .ToList();

                foreach (var reg in orderedRegistrations)
                {
                    // Insert long break if needed
                    if (sinceLastBreak >= breakAfter && sinceLastBreak > 0)
                    {
                        currentTime += breakDuration;
                        sinceLastBreak = 0;
                    }

                    starters.Add(new SpringskytteStartListEntry
                    {
                        StartOrder = startOrder++,
                        StartTime = currentTime.ToString(@"hh\:mm\:ss"),
                        MemberId = reg.MemberId,
                        Name = reg.MemberName,
                        Club = reg.MemberClub,
                        WeaponClass = ExtractWeaponClass(reg.MemberClass),
                        AgeGenderClass = ExtractAgeGenderClass(reg.MemberClass)
                    });

                    currentTime += interval;
                    sinceLastBreak++;
                }

                // Store start list as JSON on competition content node
                var config = new SpringskytteStartListConfig
                {
                    FirstStartTime = request.FirstStartTime,
                    DefaultInterval = request.DefaultInterval,
                    BreakAfterEvery = breakAfter,
                    BreakDuration = request.BreakDuration,
                    Starters = starters
                };

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition != null)
                {
                    // Store as configurationData on precisionStartList child (reuse document type)
                    var startListContent = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                    if (startListContent == null)
                    {
                        var contentType = _contentTypeService.Get("precisionStartList");
                        if (contentType != null)
                        {
                            startListContent = _contentService.Create("Startlista", competition, contentType.Alias);
                        }
                    }

                    if (startListContent != null)
                    {
                        startListContent.SetValue("configurationData", JsonConvert.SerializeObject(config));
                        startListContent.SetValue("teamFormat", "Springskytte");
                        startListContent.SetValue("generatedDate", DateTime.Now);
                        startListContent.SetValue("startListContent", BuildStartListHtml(starters));
                        _contentService.Publish(startListContent, Array.Empty<string>());
                    }

                    // Also update result entries with start order/time
                    using var db = _umbracoDatabaseFactory.CreateDatabase();
                    foreach (var starter in starters)
                    {
                        await db.ExecuteAsync(
                            @"UPDATE SpringskytteResultEntry
                              SET StartOrder = @0, StartTime = @1, LastModified = @2
                              WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                            starter.StartOrder, starter.StartTime, DateTime.Now,
                            request.CompetitionId, starter.MemberId, starter.WeaponClass);
                    }
                }

                _logger.LogInformation("Generated Springskytte start list for CompetitionId={CompetitionId}, {Count} starters",
                    request.CompetitionId, starters.Count);

                return Json(new
                {
                    success = true,
                    message = $"Startlista genererad med {starters.Count} startande.",
                    starters
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating Springskytte start list");
                return Json(new { success = false, message = "Ett fel uppstod vid generering av startlista." });
            }
        }

        [HttpGet]
        public IActionResult GetSpringskytteStartList(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var startListContent = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

                if (startListContent == null)
                    return Json(new { success = true, hasStartList = false, starters = new List<object>() });

                var configJson = startListContent.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(configJson))
                    return Json(new { success = true, hasStartList = false, starters = new List<object>() });

                var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(configJson);

                return Json(new
                {
                    success = true,
                    hasStartList = true,
                    config?.FirstStartTime,
                    config?.DefaultInterval,
                    config?.BreakAfterEvery,
                    config?.BreakDuration,
                    starters = config?.Starters ?? new List<SpringskytteStartListEntry>(),
                    html = startListContent.GetValue<string>("startListContent") ?? ""
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte start list");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== LIVE RESULTS =====

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteLiveResults(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var entries = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0", competitionId);

                var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
                var memberDict = LoadMemberInfo(memberIds);

                var tieBreaker = new SpringskytteTieBreaker();

                var results = entries.Select(e =>
                {
                    var (name, club) = memberDict.TryGetValue(e.MemberId, out var info)
                        ? info
                        : ($"Skytt {e.MemberId}", "Okänd klubb");
                    return _scoringService.BuildShooterResult(e, name, club);
                })
                .OrderBy(r => r, tieBreaker)
                .Select((r, idx) => new
                {
                    rank = r.Status == null && r.TotalTimeSeconds.HasValue ? idx + 1 : 0,
                    r.Name,
                    r.Club,
                    r.WeaponClass,
                    r.AgeGenderClass,
                    r.StartTime,
                    r.SprintTimeDisplay,
                    r.ShootingScore,
                    r.PenaltyTimeDisplay,
                    r.TotalTimeDisplay,
                    r.Status,
                    r.ShotSeries
                })
                .ToList();

                return Json(new { success = true, results, updatedAt = DateTime.Now.ToString("HH:mm:ss") });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte live results");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== HELPER METHODS =====

        private async Task<bool> HasCompetitionAccess(int competitionId)
        {
            bool isSiteAdmin = await _adminAuthorizationService.IsCurrentUserAdminAsync();
            if (isSiteAdmin) return true;

            bool isCompetitionManager = await _adminAuthorizationService.IsCompetitionManager(competitionId);
            if (isCompetitionManager) return true;

            var competition = _contentService.GetById(competitionId);
            var clubId = competition?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0)
            {
                bool isClubAdmin = await _adminAuthorizationService.IsClubAdminForClub(clubId);
                if (isClubAdmin) return true;
            }

            return false;
        }

        private Dictionary<int, (string Name, string Club)> LoadMemberInfo(List<int> memberIds)
        {
            var dict = new Dictionary<int, (string Name, string Club)>();
            foreach (var memberId in memberIds)
            {
                try
                {
                    var member = _memberService.GetById(memberId);
                    if (member != null)
                    {
                        var firstName = member.GetValue<string>("firstName") ?? "";
                        var lastName = member.GetValue<string>("lastName") ?? "";
                        var name = $"{firstName} {lastName}".Trim();
                        if (string.IsNullOrEmpty(name)) name = member.Name ?? $"Skytt {memberId}";

                        var clubName = "Okänd klubb";
                        var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var clubId) && clubId > 0)
                        {
                            clubName = _clubService.GetClubNameById(clubId) ?? "Okänd klubb";
                        }

                        dict[memberId] = (name, clubName);
                    }
                }
                catch (Exception)
                {
                    // Skip failed member lookups
                }
            }
            return dict;
        }

        /// <summary>
        /// Extract weapon class (A or C) from registration class string.
        /// Registration format is "A-D 21" or "C-H 35" or just "A" or "C".
        /// </summary>
        private static string ExtractWeaponClass(string registrationClass)
        {
            if (string.IsNullOrEmpty(registrationClass)) return "C";
            var trimmed = registrationClass.Trim().ToUpper();
            if (trimmed.StartsWith("A")) return "A";
            if (trimmed.StartsWith("C")) return "C";
            return "C";
        }

        /// <summary>
        /// Extract age/gender class from registration class string.
        /// Registration format is "A-D 21" or "C-H 35".
        /// </summary>
        private static string ExtractAgeGenderClass(string registrationClass)
        {
            if (string.IsNullOrEmpty(registrationClass)) return "";
            var trimmed = registrationClass.Trim();
            var dashIndex = trimmed.IndexOf('-');
            if (dashIndex >= 0 && dashIndex < trimmed.Length - 1)
                return trimmed.Substring(dashIndex + 1).Trim();

            // If no dash, try to find D/H followed by space and number
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"[DH]\s*\d+|[DH]\s*jun", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;

            return trimmed;
        }

        private static string FormatTime(decimal? totalSeconds)
        {
            if (totalSeconds == null) return "-";
            var ts = TimeSpan.FromSeconds((double)totalSeconds.Value);
            if (ts.Hours > 0)
                return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private static string BuildStartListHtml(List<SpringskytteStartListEntry> starters)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<table class='table table-sm table-striped'>");
            sb.AppendLine("<thead><tr><th>#</th><th>Starttid</th><th>Namn</th><th>Klubb</th><th>Vapen</th><th>Klass</th></tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var s in starters)
            {
                sb.AppendLine($"<tr><td>{s.StartOrder}</td><td>{s.StartTime}</td><td>{s.Name}</td><td>{s.Club}</td><td>{s.WeaponClass}</td><td>{s.AgeGenderClass}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
            return sb.ToString();
        }
    }
}
