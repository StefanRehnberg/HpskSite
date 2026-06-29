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
        private readonly StandardMedalMaterializationService _medalMaterialization;

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
            AdminAuthorizationService adminAuthorizationService,
            StandardMedalMaterializationService medalMaterialization)
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
            _medalMaterialization = medalMaterialization;
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

                // If finish time provided and no sprint time yet, calculate sprint = finish - start
                if (sprintTimeSeconds == null && !string.IsNullOrWhiteSpace(request.FinishTimeInput))
                {
                    var finishSeconds = _scoringService.ParseSprintTime(request.FinishTimeInput);
                    if (finishSeconds == null)
                        return Json(new SpringskytteResultResponse { Success = false, Message = "Ogiltigt måltidsformat. Använd HH:MM:SS." });

                    // Look up start time: first from DB result entry, then from start list content nodes
                    string? shooterStartTime = null;

                    using (var lookupDb = _umbracoDatabaseFactory.CreateDatabase())
                    {
                        var shooterEntry = await lookupDb.FirstOrDefaultAsync<SpringskytteResultEntry>(
                            "WHERE CompetitionId = @0 AND MemberId = @1 AND WeaponClass = @2",
                            request.CompetitionId, request.MemberId, request.WeaponClass);

                        if (shooterEntry != null && !string.IsNullOrWhiteSpace(shooterEntry.StartTime))
                            shooterStartTime = shooterEntry.StartTime;
                    } // lookupDb disposed here before main db connection is opened

                    if (string.IsNullOrWhiteSpace(shooterStartTime))
                    {
                        // Fall back to start list content nodes
                        var comp = _contentService.GetById(request.CompetitionId);
                        if (comp != null)
                        {
                            var slNodes = _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                                .Where(c => c.ContentType.Alias == "precisionStartList")
                                .ToList();

                            foreach (var node in slNodes)
                            {
                                var cfgJson = node.GetValue<string>("configurationData");
                                if (string.IsNullOrEmpty(cfgJson)) continue;
                                var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                                if (config?.Starters == null) continue;
                                var starter = config.Starters.FirstOrDefault(s =>
                                    s.MemberId == request.MemberId && s.WeaponClass == request.WeaponClass);
                                if (starter != null && !string.IsNullOrEmpty(starter.StartTime))
                                {
                                    shooterStartTime = starter.StartTime;
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(shooterStartTime))
                        return Json(new SpringskytteResultResponse { Success = false, Message = "Starttid saknas — generera startlista först." });

                    var startSeconds = _scoringService.ParseSprintTime(shooterStartTime);
                    if (startSeconds == null)
                        return Json(new SpringskytteResultResponse { Success = false, Message = "Kunde inte tolka starttid från startlistan." });

                    sprintTimeSeconds = finishSeconds.Value - startSeconds.Value;
                    if (sprintTimeSeconds < 0)
                        return Json(new SpringskytteResultResponse { Success = false, Message = "Måltid är före starttid — kontrollera tiderna." });
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
                using var transaction = db.GetTransaction();

                // Atomic MERGE: eliminates race condition when multiple range masters save simultaneously
                var effectiveSprintTime = request.Status != null ? (decimal?)null : sprintTimeSeconds;
                var effectiveShootingScore = request.Status != null ? (int?)null : shootingScore;
                var effectiveTotalTime = request.Status != null ? (decimal?)null : totalTime;

                var mergeSql = @"
                    MERGE INTO [SpringskytteResultEntry] AS target
                    USING (SELECT @0 AS CompetitionId, @1 AS MemberId, @2 AS WeaponClass) AS source
                    ON target.CompetitionId = source.CompetitionId
                       AND target.MemberId = source.MemberId
                       AND target.WeaponClass = source.WeaponClass
                    WHEN MATCHED THEN
                        UPDATE SET AgeGenderClass = @3, SprintTimeSeconds = @4, Shots = @5,
                                   ShootingScore = @6, PenaltyMultiplier = @7, TotalTimeSeconds = @8,
                                   Status = @9, EnteredBy = @10, LastModified = @11
                    WHEN NOT MATCHED THEN
                        INSERT (CompetitionId, MemberId, WeaponClass, AgeGenderClass, StartOrder,
                                SprintTimeSeconds, Shots, ShootingScore, PenaltyMultiplier, TotalTimeSeconds,
                                Status, EnteredBy, EnteredAt, LastModified)
                        VALUES (@0, @1, @2, @3, 0, @4, @5, @6, @7, @8, @9, @10, @11, @11)
                    OUTPUT INSERTED.Id;";

                var savedResultId = await db.ExecuteScalarAsync<int>(mergeSql,
                    request.CompetitionId,           // @0
                    request.MemberId,                // @1
                    request.WeaponClass,             // @2
                    request.AgeGenderClass,          // @3
                    effectiveSprintTime,             // @4
                    shotsJson,                       // @5
                    effectiveShootingScore,          // @6
                    penaltyMultiplier,               // @7
                    effectiveTotalTime,              // @8
                    request.Status,                  // @9
                    enteredBy,                       // @10
                    now                              // @11
                );

                transaction.Complete();

                _logger.LogInformation("Saved Springskytte result Id={ResultId} for MemberId={MemberId}, CompetitionId={CompetitionId}, WeaponClass={WeaponClass}",
                    savedResultId, request.MemberId, request.CompetitionId, request.WeaponClass);

                // === VERIFICATION READ-BACK ===
                // Re-read the stored row from DB to verify data integrity
                var verification = await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                    "WHERE Id = @0", savedResultId);

                if (verification == null)
                {
                    _logger.LogError("INTEGRITY FAILURE: Could not read back saved result Id={ResultId}", savedResultId);
                    return Json(new SpringskytteResultResponse
                    {
                        Success = false,
                        Message = "DATAFEL: Resultat sparades men kunde inte verifieras. Kontrollera och spara igen."
                    });
                }

                // Verify shots match what was sent
                var storedShots = SpringskytteScoringService.DeserializeShots(verification.Shots);
                var sentShots = SpringskytteScoringService.DeserializeShots(shotsJson);
                bool shotsMatch = JsonConvert.SerializeObject(storedShots) == JsonConvert.SerializeObject(sentShots);

                if (!shotsMatch)
                {
                    _logger.LogError("INTEGRITY FAILURE: Shots mismatch for Id={ResultId}. Sent={Sent}, Stored={Stored}",
                        savedResultId, shotsJson, verification.Shots);
                    return Json(new SpringskytteResultResponse
                    {
                        Success = false,
                        Message = "DATAFEL: Skottdata stämmer inte överens med det som sparades. Spara igen.",
                        VerificationShots = storedShots
                    });
                }

                bool timeMatch = verification.SprintTimeSeconds == (request.Status != null ? null : sprintTimeSeconds)
                    && verification.TotalTimeSeconds == (request.Status != null ? null : totalTime);

                if (!timeMatch)
                {
                    _logger.LogError("INTEGRITY FAILURE: Time mismatch for Id={ResultId}. Expected sprint={Sprint}/total={Total}, Got sprint={StoredSprint}/total={StoredTotal}",
                        savedResultId, sprintTimeSeconds, totalTime, verification.SprintTimeSeconds, verification.TotalTimeSeconds);
                    return Json(new SpringskytteResultResponse
                    {
                        Success = false,
                        Message = "DATAFEL: Tidsdata stämmer inte överens med det som sparades. Spara igen."
                    });
                }

                return Json(new SpringskytteResultResponse
                {
                    Success = true,
                    Message = "Resultat sparat.",
                    ResultId = savedResultId,
                    ShootingScore = verification.ShootingScore ?? 0,
                    SprintTimeSeconds = verification.SprintTimeSeconds,
                    TotalTimeSeconds = verification.TotalTimeSeconds,
                    TotalTimeDisplay = FormatTime(verification.TotalTimeSeconds),
                    PenaltyMultiplier = verification.PenaltyMultiplier,
                    VerificationShots = storedShots
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
        public async Task<IActionResult> GetSpringskytteResults(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var entries = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY TotalTimeSeconds", competitionId);

                if (!entries.Any())
                    return Json(new { success = true, results = new List<object>(), classGroups = new List<object>() });

                // Deltävling filter: keep only the shooters who opted into the sub-competition
                // at registration time. Medals are calculated below over this filtered subset.
                if (subCompetitionOnly)
                {
                    var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                    var subCompMemberIds = new HashSet<int>(
                        registrations.Where(r => r.IsSubCompetition).Select(r => r.MemberId));
                    entries = entries.Where(e => subCompMemberIds.Contains(e.MemberId)).ToList();
                    if (!entries.Any())
                        return Json(new { success = true, results = new List<object>(), classGroups = new List<object>() });
                }

                // Load start order from content nodes (authoritative source)
                var startOrderLookup = new Dictionary<string, int>();
                var competition = _contentService.GetById(competitionId);
                if (competition != null)
                {
                    var slNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .Where(c => c.ContentType.Alias == "precisionStartList")
                        .ToList();
                    foreach (var node in slNodes)
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                        if (config?.Starters == null) continue;
                        foreach (var starter in config.Starters)
                            startOrderLookup[$"{starter.MemberId}|{starter.WeaponClass}"] = starter.StartOrder;
                    }
                }

                // Build shooter results with names
                var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
                var memberDict = LoadMemberInfo(memberIds);

                var shooterResults = entries.Select(e =>
                {
                    var (name, club) = memberDict.TryGetValue(e.MemberId, out var info)
                        ? info
                        : ($"Skytt {e.MemberId}", "Okänd klubb");
                    var result = _scoringService.BuildShooterResult(e, name, club);
                    // Apply start order from content nodes if DB value is missing
                    if (result.StartOrder == 0 && startOrderLookup.TryGetValue($"{e.MemberId}|{e.WeaponClass}", out var slOrder))
                        result.StartOrder = slOrder;
                    return result;
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
                                s.StartOrder,
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

                // Surface the medal-award flag so the view can hide the Std column when
                // the competition doesn't award standard medals (or is club-only per BR-PS.1.3).
                var isAwardingStandardMedals = (competition?.GetValue<bool>("isAwardingStandardMedals") ?? false)
                    && !(competition?.GetValue<bool>("isClubOnly") ?? false);

                return Json(new { success = true, classGroups, isAwardingStandardMedals });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CalculateSpringskytteSubFinalResults([FromBody] int competitionId)
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

                // Filter to the Deltävling subset before medal/sort/group work.
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                var subCompMemberIds = new HashSet<int>(
                    registrations.Where(r => r.IsSubCompetition).Select(r => r.MemberId));
                entries = entries.Where(e => subCompMemberIds.Contains(e.MemberId)).ToList();
                if (!entries.Any())
                    return Json(new { success = false, message = "Inga deltävlingsresultat hittades." });

                var memberIds = entries.Select(e => e.MemberId).Distinct().ToList();
                var memberDict = LoadMemberInfo(memberIds);

                var shooterResults = entries.Select(e =>
                {
                    var (name, club) = memberDict.TryGetValue(e.MemberId, out var info)
                        ? info
                        : ($"Skytt {e.MemberId}", "Okänd klubb");
                    return _scoringService.BuildShooterResult(e, name, club);
                }).ToList();

                var competition = _contentService.GetById(competitionId);

                // Calculate medals on the Deltävling subset (1/9 silver, 1/3 bronze within
                // the subset). Gated on the competition's isAwardingStandardMedals flag AND
                // !isClubOnly per BR-PS.1.3 — club competitions never award standard medals.
                var isAwardingMedals = competition?.GetValue<bool>("isAwardingStandardMedals") ?? false;
                var isClubOnlyForMedals = competition?.GetValue<bool>("isClubOnly") ?? false;
                if (isAwardingMedals && !isClubOnlyForMedals)
                {
                    var subMedalService = new SpringskytteMedalService();
                    subMedalService.CalculateStandardMedals(shooterResults);
                }
                if (competition != null)
                {
                    var resultPage = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                    if (resultPage == null)
                    {
                        resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                        resultPage.SetValue("resultType", "Final Results");
                    }
                    if (!resultPage.HasProperty("subCompetitionIsOfficial"))
                        return Json(new { success = false, message = "Egenskapen 'subCompetitionIsOfficial' saknas på dokumenttypen competitionResult. Lägg till den i Umbraco backoffice (True/False)." });
                    resultPage.SetValue("subCompetitionIsOfficial", true);
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    _contentService.Save(resultPage);
                    _contentService.Publish(resultPage, new[] { "*" });
                    _logger.LogInformation("Published Springskytte sub-competition results for CompetitionId={CompetitionId}, {Count} shooters",
                        competitionId, shooterResults.Count);
                }

                return Json(new
                {
                    success = true,
                    message = $"Deltävlingens resultat beräknade för {shooterResults.Count} skyttar.",
                    shooterCount = shooterResults.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating Springskytte sub-competition results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod vid beräkning av deltävlingens slutresultat." });
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

                // Calculate medals — gated on isAwardingStandardMedals AND !isClubOnly
                // (BR-PS.1.3: club competitions don't award standard medals).
                var compForMedals = _contentService.GetById(competitionId);
                var mainAwardingMedals = compForMedals?.GetValue<bool>("isAwardingStandardMedals") ?? false;
                var mainIsClubOnly = compForMedals?.GetValue<bool>("isClubOnly") ?? false;
                if (mainAwardingMedals && !mainIsClubOnly)
                {
                    var medalService = new SpringskytteMedalService();
                    medalService.CalculateStandardMedals(shooterResults);
                }

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
                            ClassName = $"Vapengrupp {sorted.First().WeaponClass} - {SpringskytteClasses.FormatWithAgeSpan(sorted.First().AgeGenderClass)}",
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

                // Store results on competitionResult child node (same pattern as Precision)
                var competition = _contentService.GetById(competitionId);
                if (competition != null)
                {
                    var resultPage = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                    var isNewNode = false;
                    if (resultPage == null)
                    {
                        resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                        isNewNode = true;
                    }

                    resultPage.SetValue("resultData", JsonConvert.SerializeObject(finalResults));
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    resultPage.SetValue("resultType", "Final Results");
                    resultPage.SetValue("isOfficial", true);

                    _contentService.Save(resultPage);
                    _contentService.Publish(resultPage, new[] { "*" });

                    _logger.LogInformation("Published Springskytte final results for CompetitionId={CompetitionId}, {Count} shooters",
                        competitionId, shooterResults.Count);

                    // Materialize won Standard medals into the ledger. Springskytte has no
                    // Riksmästarklass, but its medals still count toward the pooled Guldmedalj.
                    try
                    {
                        var competitionDate = competition.GetValue<DateTime?>("competitionDate");
                        var year = competitionDate?.Year ?? DateTime.Now.Year;
                        var competitionName = competition.GetValue<string>("competitionName");
                        if (string.IsNullOrWhiteSpace(competitionName)) competitionName = competition.Name;

                        var medals = shooterResults
                            .Where(s => s.StandardMedal == "S" || s.StandardMedal == "B")
                            .Select(s => new OnSiteMedal(s.MemberId, $"{s.WeaponClass}-{s.AgeGenderClass}", s.StandardMedal!));

                        await _medalMaterialization.UpsertOnSiteMedalsAsync(
                            competitionId, "Springskytte", year, competitionName, competitionDate, medals);
                    }
                    catch (Exception medalEx)
                    {
                        _logger.LogError(medalEx, "Failed to materialize Springskytte standard medals for CompetitionId={CompetitionId}", competitionId);
                    }
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

                // Load start list data from content nodes (authoritative source for startOrder/startTime)
                var startListLookup = new Dictionary<string, (int StartOrder, string StartTime)>();
                var competition = _contentService.GetById(competitionId);
                if (competition != null)
                {
                    var startListNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .Where(c => c.ContentType.Alias == "precisionStartList")
                        .ToList();

                    foreach (var node in startListNodes)
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                        if (config?.Starters == null) continue;
                        foreach (var starter in config.Starters)
                        {
                            var slKey = $"{starter.MemberId}|{starter.WeaponClass}";
                            startListLookup[slKey] = (starter.StartOrder, starter.StartTime);
                        }
                    }
                }

                var shooters = registrations.Select(r =>
                {
                    var key = $"{r.MemberId}|{ExtractWeaponClass(r.MemberClass)}";
                    var hasRes = resultLookup.TryGetValue(key, out var res);
                    startListLookup.TryGetValue(key, out var slData);
                    return new
                    {
                        r.MemberId,
                        r.MemberName,
                        r.MemberClub,
                        weaponClass = ExtractWeaponClass(r.MemberClass),
                        ageGenderClass = ExtractAgeGenderClass(r.MemberClass),
                        registeredClass = r.MemberClass,
                        startOrder = slData.StartOrder > 0 ? slData.StartOrder : (hasRes ? res.StartOrder : 0),
                        startTime = !string.IsNullOrEmpty(slData.StartTime) ? slData.StartTime : (hasRes ? res.StartTime : null as string),
                        hasResult = hasRes && (res.SprintTimeSeconds != null || res.Status != null || (res.Shots != null && res.Shots != "[]")),
                        existingResult = hasRes
                            ? new
                            {
                                res.SprintTimeSeconds,
                                res.ShootingScore,
                                res.TotalTimeSeconds,
                                totalTimeDisplay = FormatTime(res.TotalTimeSeconds),
                                res.Status,
                                res.PenaltyMultiplier,
                                shots = SpringskytteScoringService.DeserializeShots(res.Shots)
                            }
                            : null
                    };
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

        [HttpGet]
        public async Task<IActionResult> HasResults(int competitionId)
        {
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM SpringskytteResultEntry WHERE CompetitionId = @0", competitionId);
            return Json(new { success = true, hasResults = count > 0, resultCount = count });
        }

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

                // Filter registrations by CoveredClasses if specified
                var coveredClasses = request.CoveredClasses?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();
                if (coveredClasses.Any())
                {
                    var coveredSet = new HashSet<string>(coveredClasses, StringComparer.OrdinalIgnoreCase);
                    registrations = registrations
                        .Where(r => coveredSet.Contains(r.MemberClass?.Trim() ?? ""))
                        .ToList();

                    if (!registrations.Any())
                        return Json(new { success = false, message = "Inga anmälda skyttar matchar de valda klasserna." });
                }

                // Parse time parameters
                var firstStart = TimeSpan.Parse(request.FirstStartTime);
                var interval = TimeSpan.Parse("00:" + request.DefaultInterval);
                var breakDuration = TimeSpan.Parse("00:" + request.BreakDuration);
                int breakAfter = request.BreakAfterEvery > 0 ? request.BreakAfterEvery : 10;

                // Build start list entries with list-local numbering (1, 2, 3...)
                // Cross-list per-weapon-class numbering is done separately via RenumberSpringskytteStartLists
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

                var competition = _contentService.GetById(request.CompetitionId);

                // Store start list as JSON on competition content node
                var listName = !string.IsNullOrWhiteSpace(request.ListName) ? request.ListName : "Startlista";
                var config = new SpringskytteStartListConfig
                {
                    FirstStartTime = request.FirstStartTime,
                    DefaultInterval = request.DefaultInterval,
                    BreakAfterEvery = breakAfter,
                    BreakDuration = request.BreakDuration,
                    ListName = listName,
                    CoveredClasses = coveredClasses,
                    Starters = starters
                };

                if (competition != null)
                {
                    Umbraco.Cms.Core.Models.IContent? startListContent = null;
                    bool isNewNode = false;

                    if (request.ExistingNodeId.HasValue && request.ExistingNodeId.Value > 0)
                    {
                        // Update existing node
                        startListContent = _contentService.GetById(request.ExistingNodeId.Value);
                        if (startListContent == null || startListContent.ParentId != competition.Id)
                            return Json(new { success = false, message = "Startlistan hittades inte." });
                    }

                    if (startListContent == null)
                    {
                        // Create new node
                        var contentType = _contentTypeService.Get("precisionStartList");
                        if (contentType != null)
                        {
                            startListContent = _contentService.Create(listName, competition, contentType.Alias);
                            isNewNode = true;
                        }
                    }

                    if (startListContent != null)
                    {
                        startListContent.Name = listName;
                        startListContent.SetValue("configurationData", JsonConvert.SerializeObject(config));
                        startListContent.SetValue("teamFormat", "Springskytte");
                        startListContent.SetValue("generatedDate", DateTime.Now);
                        startListContent.SetValue("startListContent", BuildStartListHtml(starters));
                        _contentService.Save(startListContent);
                        _contentService.Publish(startListContent, new[] { "*" });
                    }

                    // Update result entries with start order/time for this list's starters only
                    // (non-critical: entries may not exist yet if no results have been entered)
                    try
                    {
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
                    catch (Exception dbEx)
                    {
                        _logger.LogWarning(dbEx, "Failed to update StartOrder/StartTime in SpringskytteResultEntry for CompetitionId={CompetitionId} (non-critical)", request.CompetitionId);
                    }

                    _logger.LogInformation("Generated Springskytte start list '{ListName}' for CompetitionId={CompetitionId}, {Count} starters, NodeId={NodeId}",
                        listName, request.CompetitionId, starters.Count, startListContent?.Id);

                    return Json(new
                    {
                        success = true,
                        message = $"Startlista \"{listName}\" genererad med {starters.Count} startande.",
                        nodeId = startListContent?.Id,
                        starters
                    });
                }

                return Json(new { success = false, message = "Tävling hittades inte." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating Springskytte start list");
                return Json(new { success = false, message = "Ett fel uppstod vid generering av startlista." });
            }
        }

        [HttpGet]
        public IActionResult GetSpringskytteStartLists(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var startListNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList")
                    .ToList();

                if (!startListNodes.Any())
                    return Json(new { success = true, lists = new List<object>() });

                var lists = startListNodes.Select(node =>
                {
                    var configJson = node.GetValue<string>("configurationData");
                    var config = !string.IsNullOrEmpty(configJson)
                        ? JsonConvert.DeserializeObject<SpringskytteStartListConfig>(configJson)
                        : null;

                    return new
                    {
                        nodeId = node.Id,
                        listName = !string.IsNullOrEmpty(config?.ListName) ? config.ListName : "Alla klasser",
                        coveredClasses = config?.CoveredClasses ?? new List<string>(),
                        firstStartTime = config?.FirstStartTime ?? "10:00",
                        defaultInterval = config?.DefaultInterval ?? "01:00",
                        breakAfterEvery = config?.BreakAfterEvery ?? 10,
                        breakDuration = config?.BreakDuration ?? "05:00",
                        starters = config?.Starters ?? new List<SpringskytteStartListEntry>(),
                        starterCount = config?.Starters?.Count ?? 0,
                        generatedDate = node.GetValue<DateTime?>("generatedDate")?.ToString("yyyy-MM-dd HH:mm") ?? ""
                    };
                }).ToList();

                return Json(new { success = true, lists });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte start lists");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Backward-compatible endpoint — returns the first start list (for public page, etc.)
        /// </summary>
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSpringskytteStartList([FromBody] SpringskytteDeleteStartListRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.NodeId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var node = _contentService.GetById(request.NodeId);
                if (node == null || node.ContentType.Alias != "precisionStartList")
                    return Json(new { success = false, message = "Startlistan hittades inte." });

                // Verify the node belongs to this competition
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null || node.ParentId != competition.Id)
                    return Json(new { success = false, message = "Startlistan tillhör inte denna tävling." });

                // Read config to find affected starters
                var configJson = node.GetValue<string>("configurationData");
                var config = !string.IsNullOrEmpty(configJson)
                    ? JsonConvert.DeserializeObject<SpringskytteStartListConfig>(configJson)
                    : null;

                // Clear StartOrder/StartTime for affected result entries (non-critical)
                if (config?.Starters?.Any() == true)
                {
                    try
                    {
                        using var db = _umbracoDatabaseFactory.CreateDatabase();
                        foreach (var starter in config.Starters)
                        {
                            await db.ExecuteAsync(
                                @"UPDATE SpringskytteResultEntry
                                  SET StartOrder = 0, StartTime = NULL, LastModified = @0
                                  WHERE CompetitionId = @1 AND MemberId = @2 AND WeaponClass = @3",
                                DateTime.Now, request.CompetitionId, starter.MemberId, starter.WeaponClass);
                        }
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogWarning(dbEx, "Failed to clear StartOrder/StartTime in SpringskytteResultEntry for CompetitionId={CompetitionId} (non-critical)", request.CompetitionId);
                    }
                }

                // Delete the node
                _contentService.Unpublish(node);
                _contentService.Delete(node);

                _logger.LogInformation("Deleted Springskytte start list NodeId={NodeId} for CompetitionId={CompetitionId}",
                    request.NodeId, request.CompetitionId);

                return Json(new { success = true, message = "Startlistan har tagits bort." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Springskytte start list");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteAvailableClasses(int competitionId)
        {
            try
            {
                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                // Get registrations
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);

                // Extract distinct classes with counts
                var classCounts = registrations
                    .GroupBy(r => r.MemberClass?.Trim() ?? "")
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .Select(g => new { classId = g.Key, count = g.Count() })
                    .OrderBy(c => c.classId)
                    .ToList();

                // Load existing start list nodes to see which classes are already assigned
                var competition = _contentService.GetById(competitionId);
                var assignments = new Dictionary<string, (int nodeId, string listName)>();

                if (competition != null)
                {
                    var startListNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .Where(c => c.ContentType.Alias == "precisionStartList")
                        .ToList();

                    foreach (var node in startListNodes)
                    {
                        var configJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(configJson)) continue;
                        var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(configJson);
                        if (config?.CoveredClasses == null) continue;

                        foreach (var cls in config.CoveredClasses)
                        {
                            assignments[cls] = (node.Id, config.ListName ?? "");
                        }
                    }
                }

                var classes = classCounts.Select(c => new
                {
                    c.classId,
                    c.count,
                    weaponClass = ExtractWeaponClass(c.classId),
                    ageGenderClass = ExtractAgeGenderClass(c.classId),
                    assignedToNodeId = assignments.TryGetValue(c.classId, out var a) ? a.nodeId : (int?)null,
                    assignedToListName = assignments.TryGetValue(c.classId, out var b) ? b.listName : null
                }).ToList();

                return Json(new { success = true, classes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available classes for Springskytte");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== START NUMBER MANAGEMENT =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RenumberSpringskytteStartLists([FromBody] int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                // Load all start list nodes
                var nodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList")
                    .ToList();

                if (!nodes.Any())
                    return Json(new { success = false, message = "Inga startlistor hittades." });

                // Collect all starters with their node reference, ordered by list first-start-time then position
                var allStarters = new List<(Umbraco.Cms.Core.Models.IContent node, SpringskytteStartListConfig config, int starterIndex)>();

                var nodeConfigs = new List<(Umbraco.Cms.Core.Models.IContent node, SpringskytteStartListConfig config)>();
                foreach (var node in nodes)
                {
                    var cfgJson = node.GetValue<string>("configurationData");
                    if (string.IsNullOrEmpty(cfgJson)) continue;
                    var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                    if (config?.Starters == null || !config.Starters.Any()) continue;
                    nodeConfigs.Add((node, config));
                }

                // Sort lists by first start time so earlier lists get lower numbers
                nodeConfigs.Sort((a, b) =>
                {
                    TimeSpan.TryParse(a.config.FirstStartTime, out var ta);
                    TimeSpan.TryParse(b.config.FirstStartTime, out var tb);
                    return ta.CompareTo(tb);
                });

                // Assign sequential numbers per weapon class across all lists
                var weaponClassCounter = new Dictionary<string, int>();

                foreach (var (node, config) in nodeConfigs)
                {
                    foreach (var starter in config.Starters)
                    {
                        if (!weaponClassCounter.ContainsKey(starter.WeaponClass))
                            weaponClassCounter[starter.WeaponClass] = 1;
                        starter.StartOrder = weaponClassCounter[starter.WeaponClass]++;
                    }

                    // Save updated config back to node
                    node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                    node.SetValue("startListContent", BuildStartListHtml(config.Starters));
                    _contentService.Save(node);
                    var publishResult = _contentService.Publish(node, new[] { "*" });
                    if (!publishResult.Success)
                    {
                        _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", node.Id, publishResult.Result);
                    }
                }

                // Update DB result entries
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                foreach (var (_, config) in nodeConfigs)
                {
                    foreach (var starter in config.Starters)
                    {
                        await db.ExecuteAsync(
                            @"UPDATE SpringskytteResultEntry
                              SET StartOrder = @0, StartTime = @1, LastModified = @2
                              WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                            starter.StartOrder, starter.StartTime, DateTime.Now,
                            competitionId, starter.MemberId, starter.WeaponClass);
                    }
                }

                var totalStarters = nodeConfigs.Sum(nc => nc.config.Starters.Count);
                _logger.LogInformation("Renumbered Springskytte start lists for CompetitionId={CompetitionId}, {Count} starters across {Lists} lists",
                    competitionId, totalStarters, nodeConfigs.Count);

                return Json(new
                {
                    success = true,
                    message = $"Startnummer tilldelade för {totalStarters} startande i {nodeConfigs.Count} listor."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renumbering Springskytte start lists");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetSpringskytteStartNumbers([FromBody] int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var nodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList")
                    .ToList();

                int totalReset = 0;
                foreach (var node in nodes)
                {
                    var cfgJson = node.GetValue<string>("configurationData");
                    if (string.IsNullOrEmpty(cfgJson)) continue;
                    var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                    if (config?.Starters == null || !config.Starters.Any()) continue;

                    // Reset to list-local numbering (1, 2, 3...)
                    int localOrder = 1;
                    foreach (var starter in config.Starters)
                    {
                        starter.StartOrder = localOrder++;
                    }
                    totalReset += config.Starters.Count;

                    node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                    node.SetValue("startListContent", BuildStartListHtml(config.Starters));
                    _contentService.Save(node);
                    var publishResult = _contentService.Publish(node, new[] { "*" });
                    if (!publishResult.Success)
                    {
                        _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", node.Id, publishResult.Result);
                    }
                }

                // Reset DB result entries
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                await db.ExecuteAsync(
                    @"UPDATE SpringskytteResultEntry SET StartOrder = 0, StartTime = NULL, LastModified = @0
                      WHERE CompetitionId = @1",
                    DateTime.Now, competitionId);

                _logger.LogInformation("Reset Springskytte start numbers for CompetitionId={CompetitionId}, {Count} starters",
                    competitionId, totalReset);

                return Json(new
                {
                    success = true,
                    message = $"Startnummer återställda för {totalReset} startande."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting Springskytte start numbers");
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

            // Region-hosted (clubless) competition: regional admin of its region can manage it.
            var region = competition?.GetValue<string>("regionalFederation") ?? "";
            if (!string.IsNullOrEmpty(region) && await _adminAuthorizationService.IsRegionalAdminForRegion(region)) return true;

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
                sb.AppendLine($"<tr><td>{s.StartOrder}</td><td>{s.StartTime}</td><td>{s.Name}</td><td>{s.Club}</td><td>{s.WeaponClass}</td><td>{SpringskytteClasses.FormatWithAgeSpan(s.AgeGenderClass)}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
            return sb.ToString();
        }
    }
}
