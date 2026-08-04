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
using HpskSite.Helpers;
using HpskSite.Services;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Springskytte.Controllers
{
    public partial class SpringskytteController : SurfaceController
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
        private readonly CompetitionTeamService _teamService;

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
            StandardMedalMaterializationService medalMaterialization,
            CompetitionTeamService teamService)
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
            _teamService = teamService;
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

                // Scoring-only save (shots, no finish/sprint/status): preserve the sprint the TIMING
                // role set on its own screen so entering shots never wipes the finish time.
                if (sprintTimeSeconds == null
                    && string.IsNullOrWhiteSpace(request.FinishTimeInput)
                    && string.IsNullOrWhiteSpace(request.SprintTimeInput)
                    && request.SprintTimeSeconds == null
                    && request.Status == null)
                {
                    using var existDb = _umbracoDatabaseFactory.CreateDatabase();
                    var existing = await existDb.FirstOrDefaultAsync<SpringskytteResultEntry>(
                        "WHERE CompetitionId = @0 AND MemberId = @1 AND WeaponClass = @2",
                        request.CompetitionId, request.MemberId, request.WeaponClass);
                    if (existing?.SprintTimeSeconds != null)
                        sprintTimeSeconds = existing.SprintTimeSeconds;
                }

                // Serialize shots
                var shotsJson = request.ShotSeries != null
                    ? JsonConvert.SerializeObject(request.ShotSeries)
                    : "[]";

                // Per-station grip (one/two hands). Null = the caller didn't touch it (e.g. the timing
                // role, or the Class A pad) → COALESCE preserves whatever the scoring role stored.
                var stationHandsJson = request.StationHands != null
                    ? JsonConvert.SerializeObject(request.StationHands)
                    : null;

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
                                   Status = @9, EnteredBy = @10, LastModified = @11, ScoreModified = @11,
                                   StationHands = COALESCE(@12, target.StationHands)
                    WHEN NOT MATCHED THEN
                        INSERT (CompetitionId, MemberId, WeaponClass, AgeGenderClass, StartOrder,
                                SprintTimeSeconds, Shots, ShootingScore, PenaltyMultiplier, TotalTimeSeconds,
                                Status, EnteredBy, EnteredAt, LastModified, StationHands, ScoreModified)
                        VALUES (@0, @1, @2, @3, 0, @4, @5, @6, @7, @8, @9, @10, @11, @11, @12, @11)
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
                    now,                             // @11
                    (object?)stationHandsJson ?? DBNull.Value   // @12
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
        /// <param name="publicOnly">
        /// Set by the public /resultat page: drop weapon classes that are not published yet, so an
        /// organiser can publish A while C is still being scored without C leaking. Admin surfaces and
        /// the live board leave it off — they are meant to show preliminary results.
        /// </param>
        public async Task<IActionResult> GetSpringskytteResults(int competitionId, bool subCompetitionOnly = false, bool publicOnly = false)
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

                // Fold manual penalties/reductions into totals BEFORE ranking.
                await ApplyTimeAdjustmentsAsync(shooterResults, competitionId);

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
                                clubShort = ClubNameHelper.Shorten(s.Club),
                                s.WeaponClass,
                                s.AgeGenderClass,
                                s.SprintTimeDisplay,
                                s.ShootingScore,
                                s.PenaltyTimeDisplay,
                                s.PenaltyPoints,
                                s.PenaltyMinutesDisplay,
                                s.ReductionSeconds,
                                s.ReductionDisplay,
                                s.TotalTimeDisplay,
                                s.TotalTimeSeconds,
                                s.StandardMedal,
                                s.Status,
                                s.ShotSeries,
                                // Per-station grip + the "too many two-hand stations" flag (waived for
                                // 65+), so management can spot a shooter who used two hands too often.
                                stationHands = s.StationHands,
                                oneHandCount = s.OneHandStationCount,
                                twoHandCount = s.TwoHandStationCount,
                                handWarning = s.OneHandWarning
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

                // Published/preliminary state of the result list (for the admin status badge).
                // For the Deltävling view the state lives in subCompetitionIsOfficial.
                var resultNode = competition == null ? null : _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                bool resultsExist = resultNode != null;

                // A and C publish independently, so "official" is per weapon class. resultsOfficial stays
                // as "at least one class is public" for the callers that only ask the yes/no question.
                var officialClasses = subCompetitionOnly
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : ReadOfficialWeaponClasses(resultNode, competitionId);
                bool resultsOfficial = subCompetitionOnly
                    ? (resultNode != null && resultNode.HasProperty("subCompetitionIsOfficial") && resultNode.GetValue<bool>("subCompetitionIsOfficial"))
                    : officialClasses.Count > 0;

                if (publicOnly && !subCompetitionOnly)
                    classGroups = classGroups.Where(g => officialClasses.Contains(g.weaponClass ?? "")).ToList();

                return Json(new
                {
                    success = true,
                    classGroups,
                    isAwardingStandardMedals,
                    resultsExist,
                    resultsOfficial,
                    officialWeaponClasses = officialClasses.OrderBy(s => s).ToList(),
                    // All weapon classes that have results, so the admin card can render one card each
                    // even for a class that has no published list yet.
                    weaponClassesWithResults = WeaponClassesWithResults(competitionId),
                    supportsPerClassPublish = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // "Gör preliminär" for the Deltävling — flip subCompetitionIsOfficial off (hide it publicly).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSpringskytteSubResultsPreliminary([FromBody] int competitionId)
        {
            try
            {
                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });
                var competition = _contentService.GetById(competitionId);
                var resultPage = competition == null ? null : _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPage == null || !resultPage.HasProperty("subCompetitionIsOfficial"))
                    return Json(new { success = false, message = "Ingen deltävlingslista att avpublicera." });
                resultPage.SetValue("subCompetitionIsOfficial", false);
                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" });
                return Json(new { success = true, isOfficial = false, message = "Deltävlingslistan är nu preliminär (inte publik)." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Springskytte sub results preliminary for CompetitionId={CompetitionId}", competitionId);
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
                // Fold manual penalties/reductions before medal ranking on the subset.
                await ApplyTimeAdjustmentsAsync(shooterResults, competitionId);

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
                    isOfficial = true,
                    message = $"Deltävlingens resultat beräknade och publicerade för {shooterResults.Count} skyttar.",
                    shooterCount = shooterResults.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating Springskytte sub-competition results for CompetitionId={CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod vid beräkning av deltävlingens slutresultat." });
            }
        }

        // "Uppdatera" — recompute the result snapshot from current data, PRESERVING which weapon classes
        // are published (a brand-new list starts preliminary, i.e. not public).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> CalculateSpringskytteFinalResults([FromBody] SpringskytteResultsActionRequest request)
            => ComputeStoreSpringskytteResultsAsync(request?.CompetitionId ?? 0, false, request?.WeaponClass);

        // "Publicera" — recompute AND publish. With a WeaponClass only that class becomes public; without
        // one, every class that has results does (the old whole-competition behaviour).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> PublishSpringskytteResults([FromBody] SpringskytteResultsActionRequest request)
            => ComputeStoreSpringskytteResultsAsync(request?.CompetitionId ?? 0, true, request?.WeaponClass);

        // "Gör preliminär" — flip a weapon class (or the whole list) back to preliminary. The computed
        // snapshot is kept; only visibility changes.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSpringskytteResultsPreliminary([FromBody] SpringskytteResultsActionRequest request)
        {
            try
            {
                int competitionId = request?.CompetitionId ?? 0;
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(competitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });
                var competition = _contentService.GetById(competitionId);
                var resultPage = competition == null ? null : _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPage == null)
                    return Json(new { success = false, message = "Ingen resultatlista att avpublicera." });

                var wc = (request?.WeaponClass ?? "").Trim();
                var official = ReadOfficialWeaponClasses(resultPage, competitionId);
                if (string.IsNullOrEmpty(wc)) official.Clear();
                else official.Remove(wc);

                if (!WriteOfficialWeaponClasses(resultPage, official))
                    return Json(new { success = false, message = "Resultatlistan är inte beräknad ännu — klicka Uppdatera först." });
                resultPage.SetValue("isOfficial", official.Count > 0);
                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" });

                // The medal ledger reconciles against the whole competition, so re-materialize from
                // whatever is still official — otherwise unpublishing one class would leave its medals
                // in the ledger (or publishing A would delete C's).
                await MaterializeSpringskytteMedalsAsync(competitionId, official);

                return Json(new
                {
                    success = true,
                    isOfficial = official.Count > 0,
                    officialWeaponClasses = official.ToList(),
                    message = string.IsNullOrEmpty(wc)
                        ? "Resultatlistan är nu preliminär (inte publik)."
                        : $"Vapengrupp {wc} är nu preliminär (inte publik)."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Springskytte results preliminary for CompetitionId={CompetitionId}", request?.CompetitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== Per-weapon-class publishing =====
        // A and C are separate lists that finish at different times, so each is calculated and published
        // on its own (Stefan, 2026-08-04): A can be OFFICIELL while C is still preliminär. The published
        // set is stored in `OfficialWeaponClasses` inside the result node's EXISTING `resultData` blob —
        // deliberately not a new doctype property, so there is no operator step to leave unrun before SM.
        // `isOfficial` is kept in sync as "at least one class is public", so every existing consumer
        // (competition page button, Resultat link, live board badge) keeps working unchanged.

        /// <summary>
        /// The weapon classes whose results are public. Falls back to "every class that has results" for a
        /// legacy node published before per-class publishing existed, so nothing that was public goes dark.
        /// </summary>
        private HashSet<string> ReadOfficialWeaponClasses(Umbraco.Cms.Core.Models.IContent? resultPage, int competitionId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resultPage == null) return set;

            var stored = ReadStoredResults(resultPage);
            if (stored?.OfficialWeaponClasses != null && stored.OfficialWeaponClasses.Count > 0)
            {
                foreach (var s in stored.OfficialWeaponClasses)
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
                return set;
            }

            // Nothing per-class stored: a published legacy list means every class is public.
            bool legacyOfficial = resultPage.HasProperty("isOfficial") && resultPage.GetValue<bool>("isOfficial");
            if (legacyOfficial)
                foreach (var wc in WeaponClassesWithResults(competitionId)) set.Add(wc);
            return set;
        }

        /// <summary>The stored result snapshot, or null when the list has never been calculated.</summary>
        private SpringskytteFinalResults? ReadStoredResults(Umbraco.Cms.Core.Models.IContent? resultPage)
        {
            var raw = resultPage?.GetValue<string>("resultData");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JsonConvert.DeserializeObject<SpringskytteFinalResults>(raw); }
            catch { return null; }
        }

        /// <summary>
        /// Rewrites the official-class set inside the stored snapshot without recomputing it — used by
        /// "Gör preliminär", which must change visibility only.
        /// </summary>
        private bool WriteOfficialWeaponClasses(Umbraco.Cms.Core.Models.IContent resultPage, HashSet<string> official)
        {
            var stored = ReadStoredResults(resultPage);
            if (stored == null) return false;
            stored.OfficialWeaponClasses = official.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            stored.IsOfficial = official.Count > 0;
            resultPage.SetValue("resultData", JsonConvert.SerializeObject(stored));
            return true;
        }

        /// <summary>Weapon classes that actually have entered results for this competition.</summary>
        private List<string> WeaponClassesWithResults(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                return db.Fetch<string>(
                    "SELECT DISTINCT WeaponClass FROM SpringskytteResultEntry WHERE CompetitionId = @0 AND WeaponClass IS NOT NULL",
                    competitionId).Where(s => !string.IsNullOrWhiteSpace(s)).OrderBy(s => s).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Springskytte weapon classes for CompetitionId={CompetitionId}", competitionId);
                return new List<string>();
            }
        }

        /// <summary>
        /// Reconciles the standard-medal ledger with the medals of the currently OFFICIAL weapon classes.
        /// Must always pass the full official set: UpsertOnSiteMedalsAsync deletes on-site medals for the
        /// competition that aren't in the batch, so publishing A alone with only A's medals would wipe C's.
        /// </summary>
        private async Task MaterializeSpringskytteMedalsAsync(int competitionId, HashSet<string> officialClasses)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return;
            try
            {
                var awarding = (competition.GetValue<bool>("isAwardingStandardMedals"))
                    && !(competition.GetValue<bool>("isClubOnly"));

                var medals = new List<OnSiteMedal>();
                if (awarding && officialClasses.Count > 0)
                {
                    using var db = _umbracoDatabaseFactory.CreateDatabase();
                    var entries = await db.FetchAsync<SpringskytteResultEntry>("WHERE CompetitionId = @0", competitionId);
                    var memberDict = LoadMemberInfo(entries.Select(e => e.MemberId).Distinct().ToList());
                    var shooters = entries.Select(e =>
                    {
                        var (name, club) = memberDict.TryGetValue(e.MemberId, out var info) ? info : ($"Skytt {e.MemberId}", "Okänd klubb");
                        return _scoringService.BuildShooterResult(e, name, club);
                    }).ToList();
                    await ApplyTimeAdjustmentsAsync(shooters, competitionId);
                    new SpringskytteMedalService().CalculateStandardMedals(shooters);

                    medals = shooters
                        .Where(s => officialClasses.Contains(s.WeaponClass ?? "")
                                    && (s.StandardMedal == "S" || s.StandardMedal == "B"))
                        .Select(s => new OnSiteMedal(s.MemberId, $"{s.WeaponClass}-{s.AgeGenderClass}", s.StandardMedal!))
                        .ToList();
                }

                var competitionDate = competition.GetValue<DateTime?>("competitionDate");
                var competitionName = competition.GetValue<string>("competitionName");
                if (string.IsNullOrWhiteSpace(competitionName)) competitionName = competition.Name;
                await _medalMaterialization.UpsertOnSiteMedalsAsync(
                    competitionId, "Springskytte", competitionDate?.Year ?? DateTime.Now.Year,
                    competitionName, competitionDate, medals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to materialize Springskytte standard medals for CompetitionId={CompetitionId}", competitionId);
            }
        }

        private async Task<IActionResult> ComputeStoreSpringskytteResultsAsync(int competitionId, bool publish, string? weaponClass)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
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

                // Fold manual penalties/reductions into totals BEFORE ranking.
                await ApplyTimeAdjustmentsAsync(shooterResults, competitionId);

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

                // Store results on competitionResult child node (same pattern as Precision)
                var competition = _contentService.GetById(competitionId);
                bool official = false;
                var officialClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (competition != null)
                {
                    var resultPage = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                    if (resultPage == null)
                        resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");

                    // Preserve which classes are already public; publishing adds, never removes.
                    var existingOfficial = ReadOfficialWeaponClasses(resultPage, competitionId);
                    bool wasOfficial = existingOfficial.Count > 0;
                    officialClasses = new HashSet<string>(existingOfficial, StringComparer.OrdinalIgnoreCase);
                    if (publish)
                    {
                        if (!string.IsNullOrWhiteSpace(weaponClass)) officialClasses.Add(weaponClass.Trim());
                        else foreach (var wc in classGroups
                                 .Select(g => g.Shooters.FirstOrDefault()?.WeaponClass)
                                 .Where(wc => !string.IsNullOrWhiteSpace(wc))) officialClasses.Add(wc!);
                    }
                    official = officialClasses.Count > 0;

                    var finalResults = new SpringskytteFinalResults
                    {
                        CompetitionId = competitionId,
                        UpdatedAt = DateTime.Now,
                        IsOfficial = official,
                        ClassGroups = classGroups,
                        OfficialWeaponClasses = officialClasses.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
                    };

                    resultPage.SetValue("resultData", JsonConvert.SerializeObject(finalResults));
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    resultPage.SetValue("resultType", "Final Results");
                    resultPage.SetValue("isOfficial", official);

                    _contentService.Save(resultPage);
                    _contentService.Publish(resultPage, new[] { "*" });
                    _logger.LogInformation("Stored Springskytte results for CompetitionId={CompetitionId}, {Count} shooters, official classes=[{Official}]",
                        competitionId, shooterResults.Count, string.Join(",", officialClasses));

                    // Reconcile the medal ledger with every currently-official class (preliminary classes
                    // never reach the ledger). Always the full official set — see the helper's remarks.
                    await MaterializeSpringskytteMedalsAsync(competitionId, officialClasses);

                    // Phase 2 auto-trigger: notify registered shooters when a class flips to official
                    // (transition only — never re-fires on a recompute of an already-published class).
                    // Opt-in per comp (autoNotifyParticipants, default off); fire-and-forget.
                    var newlyPublished = officialClasses.Except(existingOfficial, StringComparer.OrdinalIgnoreCase).ToList();
                    if (newlyPublished.Count > 0 && competition.GetValue<bool>("autoNotifyParticipants"))
                    {
                        try
                        {
                            var notifier = HttpContext?.RequestServices?
                                .GetService(typeof(HpskSite.Services.Messaging.ParticipantNotificationService))
                                as HpskSite.Services.Messaging.ParticipantNotificationService;
                            var what = string.IsNullOrWhiteSpace(weaponClass)
                                ? "Resultatlistan är nu publicerad."
                                : $"Resultatlistan för vapengrupp {string.Join(", ", newlyPublished.OrderBy(s => s))} är nu publicerad.";
                            notifier?.Notify(competitionId, "All", null, what, "Normal", 0, "");
                        }
                        catch (Exception notifyEx)
                        {
                            _logger.LogWarning(notifyEx, "Auto-notify participants failed for CompetitionId={CompetitionId}", competitionId);
                        }
                    }
                }

                var scope = string.IsNullOrWhiteSpace(weaponClass) ? "" : $" i vapengrupp {weaponClass.Trim()}";
                int scopedShooters = string.IsNullOrWhiteSpace(weaponClass)
                    ? shooterResults.Count
                    : shooterResults.Count(s => string.Equals(s.WeaponClass, weaponClass.Trim(), StringComparison.OrdinalIgnoreCase));
                return Json(new
                {
                    success = true,
                    isOfficial = official,
                    officialWeaponClasses = officialClasses.OrderBy(s => s).ToList(),
                    message = publish
                        ? $"Resultat beräknade och publicerade för {scopedShooters} skyttar{scope}."
                        : $"Resultat uppdaterade (preliminära) för {scopedShooters} skyttar{scope}.",
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
                _logger.LogError(ex, "Error computing Springskytte results for CompetitionId={CompetitionId}", competitionId);
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
                                shots = SpringskytteScoringService.DeserializeShots(res.Shots),
                                stationHands = SpringskytteScoringService.DeserializeHands(res.StationHands)
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
                    ListDate = (request.ListDate ?? "").Trim(),
                    CoveredClasses = coveredClasses,
                    Starters = starters
                };

                // Regenerate rebuilds the config from scratch, so carry over everything about this list's
                // numbering from the existing node — settings, the manual-edit flag and the audit trail.
                // Otherwise a regen would silently reset them to the defaults and shift the sequence.
                SpringskytteStartListConfig? previousConfig = null;
                if (request.ExistingNodeId.HasValue && request.ExistingNodeId.Value > 0)
                {
                    var oldNode = _contentService.GetById(request.ExistingNodeId.Value);
                    var oldJson = oldNode?.GetValue<string>("configurationData");
                    if (!string.IsNullOrEmpty(oldJson))
                    {
                        try
                        {
                            previousConfig = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(oldJson);
                            if (previousConfig != null)
                            {
                                config.StartNumberBase = previousConfig.StartNumberBase;
                                config.ContinueFromPrevious = previousConfig.ContinueFromPrevious;
                                config.ManualNumbering = previousConfig.ManualNumbering;
                                config.NumberingHistory = previousConfig.NumberingHistory ?? new List<SpringskytteNumberingEvent>();
                            }
                        }
                        catch { /* keep defaults */ }
                    }
                }

                // Enforce a unique list name (slug) — the public /startlista/{comp}/{slug} URL
                // needs it, and duplicate names are confusing. Compare against every OTHER list
                // that has starters (i.e. that gets a public URL), excluding the one being regenerated.
                if (competition != null)
                {
                    var newSlug = SlugHelper.Slugify(listName);
                    if (string.IsNullOrEmpty(newSlug))
                        return Json(new { success = false, message = "Ogiltigt listnamn. Använd bokstäver eller siffror." });

                    var collision = _contentService.GetPagedChildren(competition.Id, 0, 1000, out _)
                        .Where(c => c.ContentType.Alias == "precisionStartList")
                        .Where(c => !(request.ExistingNodeId.HasValue && c.Id == request.ExistingNodeId.Value))
                        .Any(c =>
                        {
                            var cj = c.GetValue<string>("configurationData");
                            if (string.IsNullOrEmpty(cj)) return false;
                            SpringskytteStartListConfig? cc = null;
                            try { cc = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cj); } catch { }
                            if (cc?.Starters == null || cc.Starters.Count == 0) return false;
                            var nm = !string.IsNullOrWhiteSpace(cc.ListName) ? cc.ListName : c.Name;
                            return string.Equals(SlugHelper.Slugify(nm), newSlug, StringComparison.OrdinalIgnoreCase);
                        });
                    if (collision)
                        return Json(new { success = false, message = $"Det finns redan en startlista med namnet \"{listName}\" (eller ett namn som ger samma webbadress). Välj ett unikt namn." });
                }

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
                        // Assign start numbers for THIS list only. Generating a list must never rewrite
                        // numbers on another list, and a shooter who was already on this list keeps the
                        // number they had — including one an organiser typed in by hand.
                        var numberingDetail = ApplyNumbersToGeneratedList(
                            competition, startListContent.Id, config, previousConfig);
                        AppendNumberingEvent(config, isNewNode ? "generate" : "regenerate", numberingDetail,
                            await CurrentMemberNameAsync());

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

                    // DELIBERATELY NOT re-numbering the other lists here. This used to call
                    // ApplyRunningSequenceNumberingAsync(competitionId), which walked EVERY individual list
                    // and rewrote each starter's number from that list's stored base/follow-on settings.
                    // Because a manual per-row edit never updated those settings, generating a second list
                    // silently rewrote the first one's hand-typed numbers (SM rehearsal 2026-08-03: a C list
                    // set to 120+ came back at 5+, "continuing" after 4 A-class shooters). Numbers on an
                    // existing list now change ONLY through the explicit, per-list "Numrera om" opt-in.

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

        /// <summary>
        /// Update ONLY a start list's name + date. Deliberately does NOT touch the starters, so it's
        /// safe to rename/redate a published list without reshuffling start numbers/times.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSpringskytteStartListMeta([FromBody] SpringskytteStartListMetaRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.NodeId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var node = _contentService.GetById(request.NodeId);
                if (node == null || node.ParentId != competition.Id || node.ContentType.Alias != "precisionStartList")
                    return Json(new { success = false, message = "Startlistan hittades inte." });

                var listName = (request.ListName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(listName))
                    return Json(new { success = false, message = "Ange ett listnamn." });
                var newSlug = SlugHelper.Slugify(listName);
                if (string.IsNullOrEmpty(newSlug))
                    return Json(new { success = false, message = "Ogiltigt listnamn. Använd bokstäver eller siffror." });

                // Unique slug among the OTHER lists that have starters (i.e. that get a public URL).
                var collision = _contentService.GetPagedChildren(competition.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList" && c.Id != request.NodeId)
                    .Any(c =>
                    {
                        var cj = c.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cj)) return false;
                        SpringskytteStartListConfig? cc = null;
                        try { cc = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cj); } catch { }
                        if (cc?.Starters == null || cc.Starters.Count == 0) return false;
                        var nm = !string.IsNullOrWhiteSpace(cc.ListName) ? cc.ListName : c.Name;
                        return string.Equals(SlugHelper.Slugify(nm), newSlug, StringComparison.OrdinalIgnoreCase);
                    });
                if (collision)
                    return Json(new { success = false, message = $"Det finns redan en startlista med namnet \"{listName}\" (eller ett namn som ger samma webbadress). Välj ett unikt namn." });

                var json = node.GetValue<string>("configurationData");
                SpringskytteStartListConfig? config = null;
                try { config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(json ?? ""); } catch { }
                if (config == null)
                    return Json(new { success = false, message = "Kunde inte läsa startlistans konfiguration." });

                config.ListName = listName;
                config.ListDate = (request.ListDate ?? "").Trim();
                node.Name = listName;
                node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                _contentService.Save(node);
                _contentService.Publish(node, new[] { "*" });

                // Slug is unique among lists-with-starters, so it equals the base slug (no dedup needed).
                return Json(new { success = true, slug = newSlug, listName, listDate = config.ListDate });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Springskytte start list meta");
                return Json(new { success = false, message = "Ett fel uppstod." });
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

                // Compute the same public-page slug the SpringskytteStartListPageController does, so
                // the admin "Visa / skriv ut" button links to the exact /startlista/{comp}/{slug} URL
                // (dedup applied only to lists that have starters, in node order — must match LoadLists).
                var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lists = new List<object>();
                foreach (var node in startListNodes)
                {
                    var configJson = node.GetValue<string>("configurationData");
                    // Stafett lists live in their own management section — never surface them among
                    // the individual (per-shooter) start lists, where they'd render as empty cards.
                    if (IsStafettConfig(configJson)) continue;
                    var config = !string.IsNullOrEmpty(configJson)
                        ? JsonConvert.DeserializeObject<SpringskytteStartListConfig>(configJson)
                        : null;

                    var starterCount = config?.Starters?.Count ?? 0;
                    var listName = !string.IsNullOrEmpty(config?.ListName) ? config!.ListName : "Alla klasser";

                    var slug = "";
                    if (starterCount > 0)
                    {
                        var baseSlug = SlugHelper.Slugify(listName);
                        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "lista-" + node.Id;
                        slug = baseSlug;
                        var n = 2;
                        while (!usedSlugs.Add(slug)) slug = $"{baseSlug}-{n++}";
                    }

                    lists.Add(new
                    {
                        nodeId = node.Id,
                        listName,
                        slug,
                        listDate = config?.ListDate ?? "",
                        coveredClasses = config?.CoveredClasses ?? new List<string>(),
                        firstStartTime = config?.FirstStartTime ?? "10:00",
                        defaultInterval = config?.DefaultInterval ?? "01:00",
                        breakAfterEvery = config?.BreakAfterEvery ?? 10,
                        breakDuration = config?.BreakDuration ?? "05:00",
                        starters = config?.Starters ?? new List<SpringskytteStartListEntry>(),
                        starterCount,
                        generatedDate = node.GetValue<DateTime?>("generatedDate")?.ToString("yyyy-MM-dd HH:mm") ?? "",
                        isOfficial = node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList")
                    });
                }

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

        /// <summary>
        /// Populates the "Numrera om" modal: every individual start list in numbering order, with its
        /// stored base + follow-on settings and starter count, so the modal can render the rows and a
        /// live preview of the resulting number ranges.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSpringskytteRenumberPlan(int competitionId)
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

                var lists = GetOrderedIndividualStartLists(competition).Select(nc => new
                {
                    nodeId = nc.node.Id,
                    listName = !string.IsNullOrWhiteSpace(nc.config.ListName) ? nc.config.ListName : (nc.node.Name ?? "Startlista"),
                    listDate = nc.config.ListDate ?? "",
                    firstStartTime = nc.config.FirstStartTime ?? "",
                    starterCount = nc.config.Starters?.Count ?? 0,
                    startNumberBase = nc.config.StartNumberBase,
                    continueFromPrevious = nc.config.ContinueFromPrevious,
                    // The modal needs to show what the list HAS (not only what its settings claim) and
                    // warn before overwriting hand-typed numbers.
                    manualNumbering = nc.config.ManualNumbering,
                    currentNumbers = DescribeNumbers(nc.config.Starters?.Select(s => s.StartOrder)),
                    // Numbering is per weapon class, so the modal previews one range per class.
                    weaponClasses = (nc.config.Starters ?? new List<SpringskytteStartListEntry>())
                        .GroupBy(s => s.WeaponClass ?? "").OrderBy(g => g.Key)
                        .Select(g => new
                        {
                            weaponClass = g.Key,
                            starterCount = g.Count(),
                            currentNumbers = DescribeNumbers(g.Select(s => s.StartOrder))
                        }).ToList(),
                    history = (nc.config.NumberingHistory ?? new List<SpringskytteNumberingEvent>())
                        .AsEnumerable().Reverse().Take(6)
                        .Select(h => new { at = h.At, by = h.By, action = h.Action, detail = h.Detail }).ToList()
                }).ToList();

                return Json(new { success = true, lists });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building Springskytte renumber plan for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Renumbers ONLY the lists the user ticked in the "Numrera om" modal. This is the one and only
        /// path that may change a start number on an existing list — generation never does (see the note
        /// in GenerateSpringskytteStartList). Unticked lists keep their numbers and are treated as fixed
        /// occupants, so a plan that would duplicate a number is refused instead of applied.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RenumberSpringskytteStartLists([FromBody] SpringskytteRenumberRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var settingByNode = (request.Lists ?? new List<SpringskytteRenumberListSetting>())
                    .Where(l => l.NodeId > 0)
                    .GroupBy(l => l.NodeId).ToDictionary(g => g.Key, g => g.Last());

                var ordered = GetOrderedIndividualStartLists(competition);
                if (ordered.Count == 0)
                    return Json(new { success = false, message = "Inga startlistor hittades." });

                if (!ordered.Any(nc => settingByNode.TryGetValue(nc.node.Id, out var s) && s.Renumber))
                    return Json(new { success = false, message = "Ingen lista var markerad för omnumrering." });

                // Plan first, write second. Walk the lists in numbering order, per WEAPON CLASS (A and C
                // have separate number ledgers): a list the user ticked gets fresh numbers from its base
                // (or follows on from wherever the previous list ends in that class — ticked or not); a
                // list left unticked keeps every number it has and merely occupies them. Nothing is
                // written until the whole plan is known to be collision-free.
                var planned = new Dictionary<int, Dictionary<int, int>>();  // nodeId -> (starter index -> number)
                var frozen = new Dictionary<int, List<(string wc, int num)>>();
                var runningPerClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var (node, config) in ordered)
                {
                    bool selected = settingByNode.TryGetValue(node.Id, out var setting) && setting.Renumber;
                    if (selected)
                    {
                        var assignment = new Dictionary<int, int>();
                        foreach (var classGroup in config.Starters
                                     .Select((s, i) => (s, i))
                                     .GroupBy(x => x.s.WeaponClass ?? "")
                                     .OrderBy(g => g.Key))
                        {
                            runningPerClass.TryGetValue(classGroup.Key, out var runClass);
                            int baseNum = setting!.ContinueFromPrevious
                                ? runClass + 1
                                : Math.Max(1, setting.StartNumberBase);
                            int n = baseNum;
                            foreach (var (_, idx) in classGroup) assignment[idx] = n++;
                            runningPerClass[classGroup.Key] = Math.Max(runClass, n - 1);
                        }
                        planned[node.Id] = assignment;
                    }
                    else
                    {
                        var nums = config.Starters.Select(s => (wc: s.WeaponClass ?? "", num: s.StartOrder)).ToList();
                        frozen[node.Id] = nums;
                        foreach (var g in nums.Where(x => x.num > 0).GroupBy(x => x.wc))
                        {
                            runningPerClass.TryGetValue(g.Key, out var runClass);
                            runningPerClass[g.Key] = Math.Max(runClass, g.Max(x => x.num));
                        }
                    }
                }

                var nameOf = ordered.ToDictionary(nc => nc.node.Id,
                    nc => !string.IsNullOrWhiteSpace(nc.config.ListName) ? nc.config.ListName : (nc.node.Name ?? "Startlista"));
                var configOf = ordered.ToDictionary(nc => nc.node.Id, nc => nc.config);

                // Numbers are unique WITHIN a weapon class, so refuse rather than create a duplicate that
                // would send two shooters in the same class onto the same patch. Across classes the same
                // number is legitimate and must not be reported as a clash.
                var occupied = new Dictionary<(string wc, int num), string>();
                foreach (var (nodeId, nums) in frozen)
                    foreach (var (wc, num) in nums.Where(x => x.num > 0))
                        occupied[(wc.ToUpperInvariant(), num)] = nameOf[nodeId];
                foreach (var (nodeId, assignment) in planned)
                {
                    var starters = configOf[nodeId].Starters;
                    foreach (var (idx, num) in assignment)
                    {
                        var key = ((starters[idx].WeaponClass ?? "").ToUpperInvariant(), num);
                        if (occupied.TryGetValue(key, out var holder))
                            return Json(new
                            {
                                success = false,
                                message = $"Startnummer {num} i vapengrupp {starters[idx].WeaponClass} används redan i listan \"{holder}\". "
                                        + $"Välj ett annat startnummer för \"{nameOf[nodeId]}\", eller markera \"{holder}\" för omnumrering också."
                            });
                        occupied[key] = nameOf[nodeId];
                    }
                }

                var by = await CurrentMemberNameAsync();
                int totalStarters = 0, listCount = 0;
                var touched = new List<SpringskytteStartListConfig>();

                foreach (var (node, config) in ordered)
                {
                    if (!planned.TryGetValue(node.Id, out var assignment)) continue;  // untouched — not even saved

                    var before = config.Starters.Select(s => s.StartOrder).ToList();
                    for (int i = 0; i < config.Starters.Count; i++)
                        config.Starters[i].StartOrder = assignment[i];
                    var nums = config.Starters.Select(s => s.StartOrder).ToList();

                    var setting = settingByNode[node.Id];
                    config.StartNumberBase = nums.Count > 0 ? nums.Min() : Math.Max(1, setting.StartNumberBase);
                    config.ContinueFromPrevious = setting.ContinueFromPrevious;
                    config.ManualNumbering = false;  // the organiser just chose to overwrite by hand-off
                    AppendNumberingEvent(config, "renumber",
                        $"{DescribeNumbers(before)} → {DescribeNumbers(nums)}", by);

                    node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                    node.SetValue("startListContent", BuildStartListHtml(config.Starters));
                    _contentService.Save(node);
                    var publishResult = _contentService.Publish(node, new[] { "*" });
                    if (!publishResult.Success)
                        _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", node.Id, publishResult.Result);

                    totalStarters += config.Starters.Count;
                    listCount++;
                    touched.Add(config);
                }

                using (var db = _umbracoDatabaseFactory.CreateDatabase())
                {
                    foreach (var config in touched)
                        foreach (var starter in config.Starters)
                            await db.ExecuteAsync(
                                @"UPDATE SpringskytteResultEntry SET StartOrder = @0, StartTime = @1, LastModified = @2
                                  WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                                starter.StartOrder, starter.StartTime, DateTime.Now,
                                request.CompetitionId, starter.MemberId, starter.WeaponClass);
                }

                _logger.LogInformation("Renumbered {Lists} of {Total} Springskytte start lists for CompetitionId={CompetitionId} ({Count} starters) by {By}",
                    listCount, ordered.Count, request.CompetitionId, totalStarters, by);

                return Json(new
                {
                    success = true,
                    message = $"Startnummer tilldelade för {totalStarters} startande i {listCount} "
                            + (listCount == 1 ? "lista." : "listor.")
                            + (ordered.Count > listCount ? $" {ordered.Count - listCount} lista/listor lämnades orörda." : "")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renumbering Springskytte start lists");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== Start-number helpers (single source of truth for who may change a number) =====

        /// <summary>Display name of the acting member, for the numbering audit trail ("" if unresolved).</summary>
        private async Task<string> CurrentMemberNameAsync()
        {
            try
            {
                var m = await _memberManager.GetCurrentMemberAsync();
                if (m == null) return "";
                if (!int.TryParse(m.Id, out var mid)) return m.Name ?? "";
                var member = _memberService.GetById(mid);
                var first = member?.GetValue<string>("firstName") ?? "";
                var last = member?.GetValue<string>("lastName") ?? "";
                var full = $"{first} {last}".Trim();
                return !string.IsNullOrWhiteSpace(full) ? full : (member?.Name ?? m.Name ?? "");
            }
            catch { return ""; }
        }

        /// <summary>Appends one entry to a list's numbering audit trail, keeping only the newest ones.</summary>
        private static void AppendNumberingEvent(SpringskytteStartListConfig config, string action, string detail, string by)
        {
            if (string.IsNullOrWhiteSpace(detail)) return;
            config.NumberingHistory ??= new List<SpringskytteNumberingEvent>();
            config.NumberingHistory.Add(new SpringskytteNumberingEvent
            {
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                By = by ?? "",
                Action = action,
                Detail = detail
            });
            int overflow = config.NumberingHistory.Count - SpringskytteStartListConfig.MaxNumberingHistory;
            if (overflow > 0) config.NumberingHistory.RemoveRange(0, overflow);
        }

        /// <summary>"120–122" / "3, 7, 120–122" / "—" — compact range text for the audit trail.</summary>
        private static string DescribeNumbers(IEnumerable<int>? numbers)
        {
            var list = (numbers ?? Enumerable.Empty<int>()).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
            if (list.Count == 0) return "—";
            var parts = new List<string>();
            int start = list[0], prev = list[0];
            foreach (var n in list.Skip(1))
            {
                if (n == prev + 1) { prev = n; continue; }
                parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
                start = prev = n;
            }
            parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Start numbers are scoped to the WEAPON CLASS: A and C each have their own ledger, so A may run
        /// 1–4 while C runs 1–20 (Stefan, 2026-08-04 — the classes are separate competitions in practice
        /// and each has its own set of physical number patches). Every start-number decision therefore
        /// goes through this: the numbers in use for ONE weapon class across the competition's individual
        /// start lists, optionally excluding one node (the list being generated/renumbered). Stafett lists
        /// carry Teams, not Starters, and number separately — they are not part of this pool.
        /// </summary>
        private HashSet<int> CollectUsedStartNumbers(Umbraco.Cms.Core.Models.IContent competition, int excludeNodeId, string weaponClass)
        {
            var used = new HashSet<int>();
            foreach (var (node, cfg) in GetOrderedIndividualStartLists(competition))
            {
                if (excludeNodeId > 0 && node.Id == excludeNodeId) continue;
                foreach (var s in cfg.Starters)
                    if (s.StartOrder > 0 && string.Equals(s.WeaponClass, weaponClass, StringComparison.OrdinalIgnoreCase))
                        used.Add(s.StartOrder);
            }
            return used;
        }

        /// <summary>
        /// Assigns start numbers to a freshly generated/regenerated list WITHOUT touching any other list.
        /// A shooter who was already on this list keeps their exact number (that is what makes a manually
        /// typed number sticky through a regeneration); everyone else takes the next free number in THEIR
        /// OWN weapon class, so numbers are unique per weapon class and never reused within it. A list that
        /// happens to cover several weapon classes gets one independent sequence per class.
        /// Returns a human-readable summary for the audit trail.
        /// </summary>
        private string ApplyNumbersToGeneratedList(
            Umbraco.Cms.Core.Models.IContent competition,
            int nodeId,
            SpringskytteStartListConfig config,
            SpringskytteStartListConfig? previousConfig)
        {
            var before = previousConfig?.Starters?.Select(s => s.StartOrder).ToList() ?? new List<int>();

            var kept = new Dictionary<string, int>();
            if (previousConfig?.Starters != null)
                foreach (var s in previousConfig.Starters)
                    if (s.StartOrder > 0) kept[$"{s.MemberId}|{s.WeaponClass}"] = s.StartOrder;

            int keptCount = 0, fresh = 0;
            var perClassNotes = new List<string>();

            foreach (var classGroup in config.Starters.GroupBy(s => s.WeaponClass ?? "").OrderBy(g => g.Key))
            {
                var used = CollectUsedStartNumbers(competition, nodeId, classGroup.Key);

                foreach (var st in classGroup)
                {
                    if (kept.TryGetValue($"{st.MemberId}|{st.WeaponClass}", out var n) && n > 0 && !used.Contains(n))
                    {
                        st.StartOrder = n;
                        used.Add(n);
                        keptCount++;
                    }
                    else
                    {
                        st.StartOrder = 0;  // gets a fresh number below
                    }
                }

                int next = (used.Count > 0 ? used.Max() : 0) + 1;
                foreach (var st in classGroup.Where(s => s.StartOrder == 0))
                {
                    while (used.Contains(next)) next++;
                    st.StartOrder = next;
                    used.Add(next);
                    next++;
                    fresh++;
                }

                perClassNotes.Add($"{classGroup.Key}: {DescribeNumbers(classGroup.Select(s => s.StartOrder))}");
            }

            // Store the numbers actually applied so the stored settings always describe the list
            // (they used to claim base 1 on a list numbered from 120, which made the renumber
            // modal's preview lie and hid the drift). On a mixed-class list this is the lowest.
            config.StartNumberBase = config.Starters.Count > 0 ? config.Starters.Min(s => s.StartOrder) : 1;

            var note = $"{DescribeNumbers(before)} → {string.Join(" · ", perClassNotes)}";
            if (keptCount > 0) note += $" (behöll {keptCount} befintliga";
            if (keptCount > 0 && fresh > 0) note += $", {fresh} nya)";
            else if (keptCount > 0) note += ")";
            else if (fresh > 0) note += $" ({fresh} nya)";
            return note;
        }

        /// <summary>
        /// Loads every individual (non-stafett) start list with starters, ordered the way the renumber
        /// modal shows them and the way follow-on numbering walks them: by first start time, then name,
        /// then node id (stable). Stafett nodes are skipped naturally — they carry Teams, not Starters.
        /// </summary>
        private List<(Umbraco.Cms.Core.Models.IContent node, SpringskytteStartListConfig config)> GetOrderedIndividualStartLists(Umbraco.Cms.Core.Models.IContent competition)
        {
            var nodeConfigs = new List<(Umbraco.Cms.Core.Models.IContent node, SpringskytteStartListConfig config)>();
            foreach (var node in _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                         .Where(c => c.ContentType.Alias == "precisionStartList"))
            {
                var cfgJson = node.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(cfgJson)) continue;
                if (IsStafettConfig(cfgJson)) continue;  // stafett lists don't take part in this numbering
                SpringskytteStartListConfig? config = null;
                try { config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                if (config?.Starters == null || !config.Starters.Any()) continue;
                nodeConfigs.Add((node, config));
            }

            return nodeConfigs
                .OrderBy(nc => { TimeSpan.TryParse(nc.config.FirstStartTime, out var t); return t; })
                .ThenBy(nc => nc.config.ListName ?? nc.node.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(nc => nc.node.Id)
                .ToList();
        }

        /// <summary>
        /// Assigns start numbers as a per-list RUNNING SEQUENCE (not per weapon class): each list either
        /// starts at its own StartNumberBase or, when ContinueFromPrevious is set, continues from the
        /// previous list's last number. Numbers are therefore globally unique across the competition.
        ///
        /// This rewrites EVERY individual list, so it is reserved for "Återställ startnummer" — the one
        /// action whose stated purpose is a clean 1..N across the whole competition. Generation must never
        /// call it (that was the SM-rehearsal fault) and the "Numrera om" modal plans per ticked list
        /// instead. It clears each list's manual-numbering flag, because after a reset nothing is manual.
        /// </summary>
        private async Task<(int totalStarters, int listCount)> ApplyRunningSequenceNumberingAsync(int competitionId, string by = "")
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return (0, 0);

            var nodeConfigs = GetOrderedIndividualStartLists(competition);
            if (!nodeConfigs.Any()) return (0, 0);

            // One running sequence PER WEAPON CLASS (A restarts at 1 independently of C).
            var running = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, config) in nodeConfigs)
            {
                var before = config.Starters.Select(s => s.StartOrder).ToList();
                foreach (var classGroup in config.Starters.GroupBy(s => s.WeaponClass ?? "").OrderBy(g => g.Key))
                {
                    running.TryGetValue(classGroup.Key, out var runClass);
                    int baseNum = config.ContinueFromPrevious ? runClass + 1 : Math.Max(1, config.StartNumberBase);
                    int n = baseNum;
                    foreach (var starter in classGroup) starter.StartOrder = n++;
                    running[classGroup.Key] = Math.Max(runClass, n - 1);
                }
                config.StartNumberBase = config.Starters.Count > 0 ? config.Starters.Min(s => s.StartOrder) : 1;
                config.ManualNumbering = false;
                AppendNumberingEvent(config, "reset",
                    $"{DescribeNumbers(before)} → {DescribeNumbers(config.Starters.Select(s => s.StartOrder))}", by);

                node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                node.SetValue("startListContent", BuildStartListHtml(config.Starters));
                _contentService.Save(node);
                var publishResult = _contentService.Publish(node, new[] { "*" });
                if (!publishResult.Success)
                    _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", node.Id, publishResult.Result);
            }

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            foreach (var (_, config) in nodeConfigs)
                foreach (var starter in config.Starters)
                    await db.ExecuteAsync(
                        @"UPDATE SpringskytteResultEntry SET StartOrder = @0, StartTime = @1, LastModified = @2
                          WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                        starter.StartOrder, starter.StartTime, DateTime.Now,
                        competitionId, starter.MemberId, starter.WeaponClass);

            _logger.LogInformation("Renumbered Springskytte (running sequence) for CompetitionId={CompetitionId}, {Count} starters across {Lists} lists",
                competitionId, nodeConfigs.Sum(nc => nc.config.Starters.Count), nodeConfigs.Count);
            return (nodeConfigs.Sum(nc => nc.config.Starters.Count), nodeConfigs.Count);
        }

        /// <summary>Detects duplicate start numbers within a weapon class (defence in depth for legacy data).</summary>
        [HttpGet]
        public async Task<IActionResult> GetSpringskytteStartNumberIssues(int competitionId)
        {
            try
            {
                if (!await HasCompetitionAccess(competitionId)) return Json(new { success = false, message = "Åtkomst nekad." });
                // Start numbers are one running sequence PER WEAPON CLASS, unique within that class across
                // every list. So a duplicate is the same number on two shooters in the SAME weapon class;
                // #5 in A and #5 in C are two different shooters wearing two different patch sets.
                var tl = await BuildTimelineAsync(competitionId);
                var duplicates = tl.Rows
                    .Where(r => r.StartOrder > 0)
                    .GroupBy(r => new { r.WeaponClass, r.StartOrder })
                    .Where(g => g.Count() > 1)
                    .Select(g => new
                    {
                        weaponClass = g.Key.WeaponClass,
                        startOrder = g.Key.StartOrder,
                        count = g.Count(),
                        names = g.Select(x => x.Name).ToList()
                    })
                    .OrderBy(x => x.weaponClass).ThenBy(x => x.startOrder)
                    .ToList();
                return Json(new { success = true, duplicates });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Springskytte start number issues for {Comp}", competitionId);
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

                // Reset = a clean single running sequence 1..N: first list starts at 1, every later list
                // follows on. (This replaces the old list-local 1,2,3 reset, which would now collide with
                // the globally-unique numbering model.)
                var ordered = GetOrderedIndividualStartLists(competition);
                for (int idx = 0; idx < ordered.Count; idx++)
                {
                    var (node, config) = ordered[idx];
                    config.StartNumberBase = 1;
                    config.ContinueFromPrevious = idx > 0;
                    node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                    _contentService.Save(node);
                }

                var (totalReset, _) = await ApplyRunningSequenceNumberingAsync(competitionId, await CurrentMemberNameAsync());

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

        /// <summary>
        /// Edit ONE starter's start number and/or start time in place, preserving every other
        /// starter's number and time (unlike Generate/Regenerate which reshuffles the whole list).
        /// Keeps the Starters array in start-time order and mirrors the change to the DB row.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSpringskytteStarter([FromBody] SpringskytteUpdateStarterRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.NodeId <= 0
                    || request.MemberId <= 0 || string.IsNullOrEmpty(request.WeaponClass))
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var node = _contentService.GetById(request.NodeId);
                if (node == null || node.ContentType.Alias != "precisionStartList" || node.ParentId != request.CompetitionId)
                    return Json(new { success = false, message = "Startlistan hittades inte." });

                var cfgJson = node.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(cfgJson))
                    return Json(new { success = false, message = "Startlistan saknar data." });
                var config = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson);
                if (config?.Starters == null || !config.Starters.Any())
                    return Json(new { success = false, message = "Startlistan saknar startande." });

                var starter = config.Starters.FirstOrDefault(s =>
                    s.MemberId == request.MemberId && s.WeaponClass == request.WeaponClass);
                if (starter == null)
                    return Json(new { success = false, message = "Skytten hittades inte i startlistan." });

                var currentSec = ParseStartTimeSeconds(starter.StartTime);
                SpringTimeline? tl = null;  // built lazily, reused by both guards

                // Guard: a start number must be unique within its WEAPON CLASS across every start list in
                // the competition. The same number in another weapon class is legitimate — A and C have
                // separate ledgers and separate number patches.
                if (request.StartOrder.HasValue && request.StartOrder.Value != starter.StartOrder)
                {
                    if (request.StartOrder.Value < 0)
                        return Json(new { success = false, message = "Ogiltigt startnummer." });
                    tl ??= await BuildTimelineAsync(request.CompetitionId);
                    bool numTaken = tl.Rows.Any(r =>
                        !(r.MemberId == request.MemberId && r.WeaponClass == request.WeaponClass)
                        && string.Equals(r.WeaponClass, starter.WeaponClass, StringComparison.OrdinalIgnoreCase)
                        && r.StartOrder == request.StartOrder.Value);
                    if (numTaken)
                        return Json(new { success = false, message = $"Startnummer {request.StartOrder.Value} används redan i vapengrupp {starter.WeaponClass}." });
                }

                // Guard: don't move onto a time already reserved by another (non-DNS) shooter (same class).
                if (!string.IsNullOrWhiteSpace(request.StartTime))
                {
                    if (!TimeSpan.TryParse(request.StartTime.Trim(), out var ts)
                        || ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
                        return Json(new { success = false, message = "Ogiltig starttid. Använd formatet HH:MM eller HH:MM:SS." });
                    int newSec = (int)ts.TotalSeconds;
                    if (newSec != currentSec)
                    {
                        tl ??= await BuildTimelineAsync(request.CompetitionId);
                        if (tl.OccupiedFor(starter.WeaponClass).Contains(newSec))
                            return Json(new { success = false, message = "Starttiden är upptagen av en annan skytt. Välj en ledig lucka." });
                    }
                    starter.StartTime = ts.ToString(@"hh\:mm\:ss");
                }
                if (request.StartOrder.HasValue)
                {
                    if (request.StartOrder.Value < 0)
                        return Json(new { success = false, message = "Ogiltigt startnummer." });
                    int oldOrder = starter.StartOrder;
                    starter.StartOrder = request.StartOrder.Value;

                    if (oldOrder != starter.StartOrder)
                    {
                        // Flag the list as hand-numbered and record it. Automatic numbering must never
                        // silently undo this (SM rehearsal 2026-08-03) — only an explicit per-list tick
                        // in the "Numrera om" modal, or "Återställ startnummer", may overwrite it.
                        config.ManualNumbering = true;
                        config.StartNumberBase = config.Starters.Min(s => s.StartOrder);
                        AppendNumberingEvent(config, "manual",
                            $"#{oldOrder} → #{starter.StartOrder} ({starter.Name})", await CurrentMemberNameAsync());
                    }
                }

                // Keep the array in start-time order so the public list, paus detection and
                // generated HTML stay correct after the edit.
                config.Starters = config.Starters
                    .OrderBy(s => ParseStartTimeSeconds(s.StartTime))
                    .ThenBy(s => s.StartOrder)
                    .ToList();

                // Critical writes first: the saved config is the authoritative source that the
                // public start-list route and GetSpringskytteStartLists read, and the DB row keeps
                // result entry (sprint = finish − start) consistent. Publish is best-effort — those
                // consumers read the saved content, so a publish hiccup must not fail the edit.
                node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                node.SetValue("startListContent", BuildStartListHtml(config.Starters));
                _contentService.Save(node);

                // Mirror to the DB result row if one exists — the result save reads the DB start
                // time first (then config), so they must stay consistent for sprint = finish − start.
                // Best-effort: the saved config is authoritative for the start-list page, so a
                // transient DB hiccup must not fail the edit (it's logged for follow-up).
                try
                {
                    using var db = _umbracoDatabaseFactory.CreateDatabase();
                    await db.ExecuteAsync(
                        @"UPDATE SpringskytteResultEntry SET StartOrder = @0, StartTime = @1, LastModified = @2
                          WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                        starter.StartOrder, starter.StartTime, DateTime.Now,
                        request.CompetitionId, request.MemberId, request.WeaponClass);
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "DB mirror of start-list edit failed (config saved); comp={Comp} member={Member}", request.CompetitionId, request.MemberId);
                }

                try
                {
                    var publishResult = _contentService.Publish(node, new[] { "*" });
                    if (!publishResult.Success)
                        _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", node.Id, publishResult.Result);
                }
                catch (Exception pubEx)
                {
                    // Saved config is authoritative for the routed start-list page; log and continue.
                    _logger.LogWarning(pubEx, "Publish of start list node {NodeId} failed after save; saved config is authoritative", node.Id);
                }

                _logger.LogInformation("Updated Springskytte starter comp={Comp} member={Member} wc={Wc} -> #{Order} @ {Time}",
                    request.CompetitionId, request.MemberId, request.WeaponClass, starter.StartOrder, starter.StartTime);

                return Json(new
                {
                    success = true,
                    message = "Startande uppdaterad.",
                    starters = config.Starters.Select(s => new
                    {
                        startOrder = s.StartOrder,
                        startTime = s.StartTime,
                        memberId = s.MemberId,
                        name = s.Name,
                        club = s.Club,
                        weaponClass = s.WeaponClass,
                        ageGenderClass = s.AgeGenderClass
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Springskytte starter");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Toggle a Springskytte start list preliminary ⇄ official (published). Independent per list.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSpringskytteStartListOfficial([FromBody] SpringskytteSetOfficialRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.NodeId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var node = _contentService.GetById(request.NodeId);
                if (node == null || node.ContentType.Alias != "precisionStartList" || node.ParentId != request.CompetitionId)
                    return Json(new { success = false, message = "Startlistan hittades inte." });
                if (!node.HasProperty("isOfficialStartList"))
                    return Json(new { success = false, message = "Egenskapen 'isOfficialStartList' saknas på dokumenttypen precisionStartList." });

                node.SetValue("isOfficialStartList", request.IsOfficial);
                _contentService.Save(node);
                try { _contentService.Publish(node, new[] { "*" }); }
                catch (Exception pubEx) { _logger.LogWarning(pubEx, "Publish of official toggle failed for {NodeId} (saved value is authoritative)", node.Id); }

                return Json(new
                {
                    success = true,
                    isOfficial = request.IsOfficial,
                    message = request.IsOfficial ? "Startlistan publicerad som officiell." : "Startlistan satt till preliminär."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling Springskytte start list official");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // Time-of-day "HH:mm:ss" → seconds; empty/invalid sorts last.
        private static int ParseStartTimeSeconds(string? t)
        {
            if (string.IsNullOrEmpty(t)) return int.MaxValue;
            return TimeSpan.TryParse(t, out var ts) ? (int)ts.TotalSeconds : int.MaxValue;
        }

        /// <summary>
        /// Live functionary load for the Springskytte "Funktionärer" hub — derived purely from the saves
        /// scorers and timekeepers already make (no heartbeat). Per weapon-class line (A/C, one runs at a
        /// time): how many scorers / timekeepers are active, who they are, their pace (entries in the last
        /// PACE_WINDOW min) and freshness (minutes since their last save), plus backlog (scored vs startade,
        /// måltider vs startade) with a "behöver hjälp" flag. Scorer attribution = EnteredBy + ScoreModified;
        /// timekeeper attribution = TimeEnteredBy + TimeModified. Timestamps are local (DateTime.Now).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSpringskytteFunctionaryLoad(int competitionId)
        {
            try
            {
                if (competitionId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(competitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                const int ACTIVE_WINDOW = 15, PACE_WINDOW = 10;
                var now = DateTime.Now;   // Springskytte result timestamps are stored local (DateTime.Now).

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var rows = await db.FetchAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId = @0", competitionId);

                // Starters per weapon class from the start list(s).
                var startersByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var comp = _contentService.GetById(competitionId);
                if (comp != null)
                {
                    foreach (var node in _contentService.GetPagedChildren(comp.Id, 0, 200, out _)
                                 .Where(c => c.ContentType.Alias == "precisionStartList"))
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        SpringskytteStartListConfig? cfg = null;
                        try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                        foreach (var st in cfg?.Starters ?? new List<SpringskytteStartListEntry>())
                        {
                            var wc = st.WeaponClass ?? "";
                            if (wc.Length == 0) continue;
                            startersByClass[wc] = startersByClass.TryGetValue(wc, out var n) ? n + 1 : 1;
                        }
                    }
                }

                var nameCache = new Dictionary<int, string>();
                string ResolveName(int id)
                {
                    if (id <= 0) return "";
                    if (nameCache.TryGetValue(id, out var c)) return c;
                    string nm;
                    try { nm = _memberService.GetById(id)?.Name ?? ("#" + id); } catch { nm = "#" + id; }
                    nameCache[id] = nm;
                    return nm;
                }

                var weaponClasses = startersByClass.Keys
                    .Concat(rows.Select(r => r.WeaponClass ?? ""))
                    .Where(w => w.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(w => w)
                    .ToList();

                var classes = weaponClasses.Select(wc =>
                {
                    var crows = rows.Where(r => string.Equals(r.WeaponClass, wc, StringComparison.OrdinalIgnoreCase)).ToList();
                    int starters = startersByClass.TryGetValue(wc, out var sc) ? sc : crows.Select(r => r.MemberId).Distinct().Count();
                    int scored = crows.Count(r => r.ScoreModified.HasValue);
                    int maltider = crows.Count(r => r.TimeModified.HasValue);

                    // Scorer roster — rows a scorer actually scored (ScoreModified stamped), grouped by EnteredBy.
                    var scorers = crows.Where(r => r.ScoreModified.HasValue && r.EnteredBy > 0)
                        .GroupBy(r => r.EnteredBy)
                        .Select(g => new
                        {
                            memberId = g.Key,
                            name = ResolveName(g.Key),
                            entriesTotal = g.Count(),
                            entriesRecent = g.Count(r => (now - r.ScoreModified!.Value).TotalMinutes <= PACE_WINDOW),
                            lastSaveMinsAgo = (int)Math.Max(0, (now - g.Max(r => r.ScoreModified!.Value)).TotalMinutes)
                        })
                        .OrderBy(s => s.lastSaveMinsAgo).ToList();

                    // Timekeeper roster — rows with a måltid (TimeModified stamped), grouped by TimeEnteredBy.
                    var timers = crows.Where(r => r.TimeModified.HasValue && (r.TimeEnteredBy ?? 0) > 0)
                        .GroupBy(r => r.TimeEnteredBy!.Value)
                        .Select(g => new
                        {
                            memberId = g.Key,
                            name = ResolveName(g.Key),
                            entriesTotal = g.Count(),
                            entriesRecent = g.Count(r => (now - r.TimeModified!.Value).TotalMinutes <= PACE_WINDOW),
                            lastSaveMinsAgo = (int)Math.Max(0, (now - g.Max(r => r.TimeModified!.Value)).TotalMinutes)
                        })
                        .OrderBy(s => s.lastSaveMinsAgo).ToList();

                    DateTime? lastActivity = crows
                        .SelectMany(r => new[] { r.ScoreModified, r.TimeModified })
                        .Where(d => d.HasValue).Select(d => d!.Value)
                        .DefaultIfEmpty(DateTime.MinValue).Max();
                    bool active = lastActivity.HasValue && lastActivity.Value != DateTime.MinValue
                                  && (now - lastActivity.Value).TotalMinutes <= ACTIVE_WINDOW;

                    int scoreRemaining = Math.Max(0, starters - scored);
                    int timeRemaining = Math.Max(0, starters - maltider);
                    int scorePace = scorers.Sum(s => s.entriesRecent);
                    int timePace = timers.Sum(s => s.entriesRecent);

                    bool scoringNeedsHelp = false; string scoringHelpReason = "";
                    if (active && scoreRemaining > 0)
                    {
                        if (scorers.Count > 0 && scorePace == 0) { scoringNeedsHelp = true; scoringHelpReason = "Inget poäng registrerat på " + PACE_WINDOW + " min"; }
                        else if (scoreRemaining > 20 && scorers.Count <= 1) { scoringNeedsHelp = true; scoringHelpReason = scoreRemaining + " kvar att poängsätta, endast en poängräknare"; }
                    }
                    bool timingNeedsHelp = false; string timingHelpReason = "";
                    if (active && timeRemaining > 0)
                    {
                        if (timers.Count > 0 && timePace == 0) { timingNeedsHelp = true; timingHelpReason = "Ingen sluttid registrerad på " + PACE_WINDOW + " min"; }
                        else if (timeRemaining > 20 && timers.Count <= 1) { timingNeedsHelp = true; timingHelpReason = timeRemaining + " kvar att tidta, endast en tidtagare"; }
                    }

                    return new
                    {
                        weaponClass = wc,
                        starters,
                        scored,
                        maltider,
                        active,
                        scorers,
                        timers,
                        scoringNeedsHelp,
                        scoringHelpReason,
                        timingNeedsHelp,
                        timingHelpReason
                    };
                }).ToList();

                return Json(new { success = true, serverTime = now, classes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building Springskytte functionary load for {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== TIME ADJUSTMENTS (items 6 & 9: manual penalties + reductions) =====

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteAdjustments(int competitionId)
        {
            try
            {
                if (competitionId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(competitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var rows = await db.FetchAsync<SpringskytteTimeAdjustment>(
                    "SELECT * FROM SpringskytteTimeAdjustment WHERE CompetitionId = @0 ORDER BY EnteredAt", competitionId);
                return Json(new
                {
                    success = true,
                    adjustments = rows.Select(a => new
                    {
                        a.Id, a.MemberId, a.WeaponClass, a.AdjustmentType, a.Points, a.Seconds, a.Reason, a.EnteredAt
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Springskytte adjustments");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSpringskytteAdjustment([FromBody] SpringskytteAddAdjustmentRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 || string.IsNullOrEmpty(request.WeaponClass))
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                var type = request.AdjustmentType == "Reduction" ? "Reduction" : "Penalty";
                int? points = null;
                int seconds;
                if (type == "Penalty")
                {
                    points = request.Points ?? 0;
                    if (points <= 0) return Json(new { success = false, message = "Ange antal straffpoäng (minst 1)." });
                    seconds = points.Value * 60;   // 1 penalty point = 1 minute
                }
                else
                {
                    var secs = _scoringService.ParseSprintTime(request.TimeInput);
                    if (secs == null || secs.Value <= 0)
                        return Json(new { success = false, message = "Ange en tid att dra av (MM:SS)." });
                    seconds = -(int)Math.Round(secs.Value);
                }

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int enteredBy = currentMember != null && int.TryParse(currentMember.Id, out var mid) ? mid : 0;

                var adj = new SpringskytteTimeAdjustment
                {
                    CompetitionId = request.CompetitionId,
                    MemberId = request.MemberId,
                    WeaponClass = request.WeaponClass,
                    AdjustmentType = type,
                    Points = points,
                    Seconds = seconds,
                    Reason = (request.Reason ?? "").Trim(),
                    EnteredBy = enteredBy,
                    EnteredAt = DateTime.Now
                };

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                await db.InsertAsync(adj);

                _logger.LogInformation("Added Springskytte {Type} comp={Comp} member={Member} wc={Wc} seconds={Sec}",
                    type, request.CompetitionId, request.MemberId, request.WeaponClass, seconds);

                return Json(new { success = true, message = type == "Penalty" ? "Straff tillagt." : "Tidsavdrag tillagt.", id = adj.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding Springskytte adjustment");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSpringskytteAdjustment([FromBody] SpringskytteDeleteAdjustmentRequest request)
        {
            try
            {
                if (request == null || request.Id <= 0 || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                await db.ExecuteAsync("DELETE FROM SpringskytteTimeAdjustment WHERE Id = @0 AND CompetitionId = @1",
                    request.Id, request.CompetitionId);
                return Json(new { success = true, message = "Borttaget." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Springskytte adjustment");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Field-scoped finish-time save (timing role, item 5): computes sprint = finish − start and
        /// updates ONLY the time fields, preserving the shots/score the scoring role entered. Creates
        /// the result row (from the start-list starter) if none exists yet.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSpringskytteFinishTime([FromBody] SpringskytteFinishTimeRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 || string.IsNullOrEmpty(request.WeaponClass))
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                var status = (request.Status == "DNS" || request.Status == "DNF") ? request.Status : null;

                // Find the start-list starter (start time + age class, needed for compute / insert).
                SpringskytteStartListEntry? starter = null;
                var comp = _contentService.GetById(request.CompetitionId);
                if (comp != null)
                {
                    foreach (var node in _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                                 .Where(c => c.ContentType.Alias == "precisionStartList"))
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        SpringskytteStartListConfig? cfg = null;
                        try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                        var st = cfg?.Starters?.FirstOrDefault(s => s.MemberId == request.MemberId && s.WeaponClass == request.WeaponClass);
                        if (st != null) { starter = st; break; }
                    }
                }

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var existing = await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId=@0 AND MemberId=@1 AND WeaponClass=@2",
                    request.CompetitionId, request.MemberId, request.WeaponClass);

                decimal? sprint = null, total = null;
                if (status == null)
                {
                    if (string.IsNullOrWhiteSpace(request.FinishTimeInput))
                        return Json(new { success = false, message = "Ange måltid (HH:MM:SS)." });
                    var finish = _scoringService.ParseSprintTime(request.FinishTimeInput);
                    if (finish == null) return Json(new { success = false, message = "Ogiltig måltid. Använd HH:MM:SS." });

                    var startStr = !string.IsNullOrWhiteSpace(existing?.StartTime) ? existing!.StartTime : starter?.StartTime;
                    if (string.IsNullOrWhiteSpace(startStr))
                        return Json(new { success = false, message = "Starttid saknas — generera startlista först." });
                    var start = _scoringService.ParseSprintTime(startStr);
                    if (start == null) return Json(new { success = false, message = "Kunde inte tolka starttid." });

                    sprint = finish.Value - start.Value;
                    if (sprint < 0) return Json(new { success = false, message = "Måltid är före starttid — kontrollera tiderna." });

                    var score = existing?.ShootingScore ?? 0;
                    var mult = (existing?.PenaltyMultiplier ?? 1) == 0 ? 1 : (existing?.PenaltyMultiplier ?? 1);
                    total = _scoringService.CalculateTotalTime(sprint, score, mult);
                }

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int enteredBy = currentMember != null && int.TryParse(currentMember.Id, out var mid) ? mid : 0;
                var now = DateTime.Now;

                if (existing != null)
                {
                    // Preserve shots/score/multiplier — update only the time + status. Stamp the
                    // timekeeper attribution (TimeEnteredBy/TimeModified) without touching EnteredBy/
                    // ScoreModified, which belong to the scorer.
                    await db.ExecuteAsync(
                        @"UPDATE SpringskytteResultEntry SET SprintTimeSeconds=@0, TotalTimeSeconds=@1, Status=@2,
                                 LastModified=@3, TimeEnteredBy=@7, TimeModified=@3
                          WHERE CompetitionId=@4 AND MemberId=@5 AND WeaponClass=@6",
                        sprint, total, status, now, request.CompetitionId, request.MemberId, request.WeaponClass, enteredBy);
                }
                else
                {
                    if (starter == null)
                        return Json(new { success = false, message = "Skytten finns inte i startlistan." });
                    try
                    {
                        await db.InsertAsync(new SpringskytteResultEntry
                        {
                            CompetitionId = request.CompetitionId,
                            MemberId = request.MemberId,
                            WeaponClass = request.WeaponClass,
                            AgeGenderClass = starter.AgeGenderClass,
                            StartOrder = starter.StartOrder,
                            StartTime = starter.StartTime,
                            SprintTimeSeconds = sprint,
                            ShootingScore = 0,
                            PenaltyMultiplier = 1,
                            TotalTimeSeconds = total,
                            Shots = "[]",
                            Status = status,
                            EnteredBy = enteredBy,
                            EnteredAt = now,
                            LastModified = now,
                            TimeEnteredBy = enteredBy,
                            TimeModified = now
                        });
                    }
                    catch
                    {
                        // A concurrent writer (another device, or the scoring role) inserted the row
                        // first. Fall back to updating only the time fields — never lose the måltid to
                        // a duplicate-key race. Shots/score set by the other writer are preserved.
                        await db.ExecuteAsync(
                            @"UPDATE SpringskytteResultEntry SET SprintTimeSeconds=@0, TotalTimeSeconds=@1, Status=@2,
                                     LastModified=@3, TimeEnteredBy=@7, TimeModified=@3
                              WHERE CompetitionId=@4 AND MemberId=@5 AND WeaponClass=@6",
                            sprint, total, status, now, request.CompetitionId, request.MemberId, request.WeaponClass, enteredBy);
                    }
                }

                return Json(new { success = true, message = status ?? "Tid sparad.", sprintTimeSeconds = sprint });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Springskytte finish time");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Folds the manual time-adjustment ledger into shooter results: sets PenaltyPoints /
        /// ReductionSeconds for display and bakes the net delta into TotalTimeSeconds so ranking
        /// reflects it. Best-effort: a ledger read failure leaves base results untouched.
        /// </summary>
        private async Task ApplyTimeAdjustmentsAsync(List<SpringskytteShooterResult> results, int competitionId)
        {
            if (results == null || results.Count == 0) return;
            List<SpringskytteTimeAdjustment> adjustments;
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                adjustments = await db.FetchAsync<SpringskytteTimeAdjustment>(
                    "SELECT * FROM SpringskytteTimeAdjustment WHERE CompetitionId = @0", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load time adjustments for comp {Comp}", competitionId);
                return;
            }
            if (adjustments.Count == 0) return;

            var byKey = adjustments
                .GroupBy(a => $"{a.MemberId}|{a.WeaponClass}")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var r in results)
            {
                if (!byKey.TryGetValue($"{r.MemberId}|{r.WeaponClass}", out var list)) continue;
                r.PenaltyPoints = list.Where(a => a.AdjustmentType == "Penalty").Sum(a => a.Points ?? 0);
                r.ReductionSeconds = list.Where(a => a.AdjustmentType == "Reduction").Sum(a => -a.Seconds); // stored negative → positive magnitude
                var net = list.Sum(a => a.Seconds);  // penalties (+) and reductions (−)
                if (r.TotalTimeSeconds.HasValue && r.Status == null)
                    r.TotalTimeSeconds = r.TotalTimeSeconds.Value + net;
            }
        }

        // ===== STARTER SCREEN + FREE-SLOT / DNS MODEL (items 7 & 8) =====

        private static string SecondsToHms(int s)
        {
            if (s < 0) s = 0;
            return TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");
        }

        private class SpringTimelineRow
        {
            public int Sec;
            public int StartOrder;
            public string StartTime = "";
            public int MemberId;
            public string Name = "";
            public string Club = "";
            public string WeaponClass = "";
            public string AgeGenderClass = "";
            public string ListName = "";
            public int NodeId;
            public string? Status;   // null / "DNS" / "DNF"
        }

        private class SpringTimeline
        {
            public List<SpringTimelineRow> Rows = new();
            public List<object> FreeSlots = new();      // { time, nodeId, kind, weaponClass, freedFrom? }
            // Reserved times PER weapon class. Weapon classes are never mixed (separate sequences,
            // often on different days; start times are time-of-day only), so occupancy + free slots
            // are always scoped to a single weapon class.
            public Dictionary<string, HashSet<int>> OccupiedByWc = new();

            public HashSet<int> OccupiedFor(string wc)
                => OccupiedByWc.TryGetValue(wc ?? "", out var set) ? set : new HashSet<int>();
        }

        /// <summary>
        /// Shared timeline for the competition, scoped per weapon class. A slot is free ONLY if it was
        /// never assigned (pause gap / after the last start of THAT weapon class) or belongs to a DNS'd
        /// shooter of that class. Weapon classes are never mixed. Närvaro (check-in) never frees a slot.
        /// </summary>
        private async Task<SpringTimeline> BuildTimelineAsync(int competitionId)
        {
            var tl = new SpringTimeline();
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return tl;

            var statusByKey = new Dictionary<string, string?>();
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var resultRows = await db.FetchAsync<SpringskytteResultEntry>("WHERE CompetitionId = @0", competitionId);
                foreach (var r in resultRows) statusByKey[$"{r.MemberId}|{r.WeaponClass}"] = r.Status;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Timeline: status load failed for {Comp}", competitionId); }

            var gapSlots = new List<(int sec, string wc, object row)>();
            var slNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                .Where(c => c.ContentType.Alias == "precisionStartList").ToList();
            foreach (var node in slNodes)
            {
                var cfgJson = node.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(cfgJson)) continue;
                SpringskytteStartListConfig? cfg = null;
                try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                if (cfg?.Starters == null || cfg.Starters.Count == 0) continue;

                int intervalSec = 60;
                var iv = cfg.DefaultInterval?.Split(':');
                if (iv != null && iv.Length == 2 && int.TryParse(iv[0], out var mm) && int.TryParse(iv[1], out var ss))
                    intervalSec = Math.Max(1, mm * 60 + ss);

                // Record every starter (for Rows).
                foreach (var s in cfg.Starters)
                {
                    var t = ParseStartTimeSeconds(s.StartTime);
                    statusByKey.TryGetValue($"{s.MemberId}|{s.WeaponClass}", out var st);
                    tl.Rows.Add(new SpringTimelineRow
                    {
                        Sec = t, StartOrder = s.StartOrder, StartTime = s.StartTime, MemberId = s.MemberId,
                        Name = s.Name, Club = s.Club, WeaponClass = s.WeaponClass, AgeGenderClass = s.AgeGenderClass,
                        ListName = cfg.ListName ?? "", NodeId = node.Id, Status = st
                    });
                }

                // Compute gaps + trailing slots PER weapon class within the list (never mix classes).
                foreach (var grp in cfg.Starters.GroupBy(s => s.WeaponClass ?? ""))
                {
                    var wcKey = grp.Key;
                    var sorted = grp.OrderBy(s => ParseStartTimeSeconds(s.StartTime)).ToList();
                    int? prev = null;
                    foreach (var s in sorted)
                    {
                        var t = ParseStartTimeSeconds(s.StartTime);
                        if (prev.HasValue && t != int.MaxValue && (t - prev.Value) > intervalSec)
                            for (int slot = prev.Value + intervalSec; slot < t; slot += intervalSec)
                                gapSlots.Add((slot, wcKey, new { time = SecondsToHms(slot), nodeId = node.Id, kind = "paus", weaponClass = wcKey }));
                        if (t != int.MaxValue) prev = t;
                    }
                    if (prev.HasValue)
                        for (int k = 1; k <= 3; k++)
                            gapSlots.Add((prev.Value + k * intervalSec, wcKey, new { time = SecondsToHms(prev.Value + k * intervalSec), nodeId = node.Id, kind = "efter", weaponClass = wcKey }));
                }
            }

            // Reserved times per weapon class = that class's non-DNS starters.
            foreach (var r in tl.Rows)
                if (r.Sec != int.MaxValue && r.Status != "DNS")
                {
                    if (!tl.OccupiedByWc.TryGetValue(r.WeaponClass, out var set)) { set = new HashSet<int>(); tl.OccupiedByWc[r.WeaponClass] = set; }
                    set.Add(r.Sec);
                }

            // Candidate free slots: gaps + DNS'd times, each tagged with its weapon class.
            var allSlots = new List<(int sec, string wc, object row)>(gapSlots);
            foreach (var r in tl.Rows)
                if (r.Status == "DNS" && r.Sec != int.MaxValue)
                    allSlots.Add((r.Sec, r.WeaponClass, new { time = r.StartTime, nodeId = r.NodeId, kind = "dns", weaponClass = r.WeaponClass, freedFrom = r.Name }));

            // Free only if not reserved by a non-DNS shooter of the SAME weapon class; dedup per (wc, sec).
            var seen = new HashSet<string>();
            tl.FreeSlots = allSlots
                .Where(x => !tl.OccupiedFor(x.wc).Contains(x.sec))
                .OrderBy(x => x.wc).ThenBy(x => x.sec)
                .Where(x => seen.Add(x.wc + "|" + x.sec))
                .Select(x => x.row)
                .ToList();
            return tl;
        }

        /// <summary>
        /// Live feed for the start-line screen: all starters (globally time-ordered) with DNS + check-in
        /// status, plus the free slots a late shooter can be moved into. Polled by /startlinje.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSpringskytteStarterState(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                    return Json(new { success = false, message = "Tävling hittades inte." });
                if ((competition.GetValue<string>("competitionType") ?? "") != "Springskytte")
                    return Json(new { success = false, message = "Fel tävlingstyp." });

                // Check-in map — informational only (never frees a slot).
                var checkedIn = new Dictionary<int, bool>();
                var regHub = _contentService.GetPagedChildren(competition.Id, 0, 200, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                if (regHub != null)
                    foreach (var reg in _contentService.GetPagedChildren(regHub.Id, 0, 5000, out _)
                                 .Where(c => c.ContentType.Alias == "competitionRegistration"))
                    {
                        var midRaw = reg.GetValue<string>("memberId");
                        if (!int.TryParse(midRaw, out var mid) || mid <= 0) continue;
                        checkedIn[mid] = reg.HasProperty("isCheckedIn") && reg.GetValue<bool>("isCheckedIn");
                    }

                // Saved results (keyed member|weaponClass) so the timing view can restore an
                // already-entered måltid/löptid on every re-render — never silently blanking a
                // saved finish time (which could otherwise be overwritten by accident).
                var resultLookup = new Dictionary<string, SpringskytteResultEntry>();
                using (var resDb = _umbracoDatabaseFactory.CreateDatabase())
                {
                    var resultRows = await resDb.FetchAsync<SpringskytteResultEntry>(
                        "WHERE CompetitionId = @0", competitionId);
                    foreach (var r in resultRows)
                        resultLookup[$"{r.MemberId}|{r.WeaponClass}"] = r;
                }

                var tl = await BuildTimelineAsync(competitionId);
                var starters = tl.Rows.OrderBy(r => r.Sec).Select(r =>
                {
                    resultLookup.TryGetValue($"{r.MemberId}|{r.WeaponClass}", out var res);
                    return new
                    {
                        startOrder = r.StartOrder,
                        startTime = r.StartTime,
                        name = r.Name,
                        club = r.Club,
                        weaponClass = r.WeaponClass,
                        ageGenderClass = r.AgeGenderClass,
                        memberId = r.MemberId,
                        listName = r.ListName,
                        nodeId = r.NodeId,
                        status = r.Status,
                        isDns = r.Status == "DNS",
                        checkedIn = !checkedIn.TryGetValue(r.MemberId, out var ci) || ci,
                        sprintTimeSeconds = res?.SprintTimeSeconds
                    };
                }).ToList();

                return Json(new
                {
                    success = true,
                    competitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "Tävling",
                    starters,
                    freeSlots = tl.FreeSlots
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building Springskytte starter state for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>Free start slots for the whole competition (pause gaps + DNS'd times) — for the move pickers.</summary>
        [HttpGet]
        public async Task<IActionResult> GetSpringskytteFreeSlots(int competitionId)
        {
            try
            {
                if (competitionId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });
                var tl = await BuildTimelineAsync(competitionId);
                return Json(new { success = true, freeSlots = tl.FreeSlots });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building free slots for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Rullande start (on-site drop-in): slot a walk-in into an existing start list at the picked
        /// free time, giving them the next start number for their weapon class. Springskytte has no
        /// patrols — each shooter is an individual interval start — so this is the discipline's analogue
        /// of Fältskytte's AssignWalkInToPatrol / precision's AssignWalkInToStartListTeam. Called by the
        /// desk "Anmäl och betala" modal after the registration is created, once per registered class.
        /// Appending a new number (max+1 for the class) never renumbers or disturbs anyone already listed.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignSpringskytteWalkInStartTime([FromBody] SpringskytteWalkInStartTimeRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0
                    || string.IsNullOrWhiteSpace(request.ShootingClass) || string.IsNullOrWhiteSpace(request.StartTime))
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                if (!TimeSpan.TryParse(request.StartTime.Trim(), out var ts)
                    || ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
                    return Json(new { success = false, message = "Ogiltig starttid. Använd formatet HH:MM eller HH:MM:SS." });
                var startTime = ts.ToString(@"hh\:mm\:ss");
                var newSec = (int)ts.TotalSeconds;

                var weaponClass = ExtractWeaponClass(request.ShootingClass);
                var ageGenderClass = ExtractAgeGenderClass(request.ShootingClass);

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                    return Json(new { success = false, message = "Tävling hittades inte." });

                // Pick the target list: prefer the one whose CoveredClasses covers this registration
                // class; fall back to the node the picked slot came from (NodeId). Weapon classes are
                // never mixed, so a slot's node is always the right list for that class.
                var startListNodes = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList").ToList();

                Umbraco.Cms.Core.Models.IContent? targetNode = null;
                SpringskytteStartListConfig? targetConfig = null;
                foreach (var node in startListNodes)
                {
                    var cfgJson = node.GetValue<string>("configurationData");
                    if (string.IsNullOrEmpty(cfgJson)) continue;
                    SpringskytteStartListConfig? cfg = null;
                    try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                    if (cfg == null) continue;
                    bool covers = cfg.CoveredClasses != null && cfg.CoveredClasses
                        .Any(cc => string.Equals(cc?.Trim(), request.ShootingClass.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (covers || (request.NodeId.HasValue && node.Id == request.NodeId.Value))
                    {
                        targetNode = node;
                        targetConfig = cfg;
                        if (covers) break;  // a covering list beats a bare NodeId match
                    }
                }

                if (targetNode == null || targetConfig == null)
                    return Json(new { success = false, message = "Ingen startlista täcker klassen. Generera startlistan först." });

                targetConfig.Starters ??= new List<SpringskytteStartListEntry>();

                // Resolve display name / club (fall back to the member record).
                var info = LoadMemberInfo(new List<int> { request.MemberId });
                var name = info.TryGetValue(request.MemberId, out var mi) && !string.IsNullOrWhiteSpace(mi.Name)
                    ? mi.Name : $"Skytt {request.MemberId}";
                var club = info.TryGetValue(request.MemberId, out var mc) ? mc.Club : "";

                // Guard: the time must be free for THIS weapon class (never mix classes). Reuse the same
                // shared timeline the start-line move-tool uses, so "occupied" means exactly the same thing.
                var tl = await BuildTimelineAsync(request.CompetitionId);

                // Idempotent: if this member is already a starter in this weapon class (e.g. a double
                // submit), just move them to the picked time instead of adding a duplicate row.
                var existing = targetConfig.Starters.FirstOrDefault(s =>
                    s.MemberId == request.MemberId && s.WeaponClass == weaponClass);

                if (existing == null && tl.OccupiedFor(weaponClass).Contains(newSec))
                    return Json(new { success = false, message = "Starttiden är upptagen av en annan skytt. Välj en ledig lucka." });

                int startOrder;
                if (existing != null)
                {
                    existing.StartTime = startTime;
                    existing.AgeGenderClass = ageGenderClass;
                    if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                    if (!string.IsNullOrWhiteSpace(club)) existing.Club = club;
                    startOrder = existing.StartOrder;
                }
                else
                {
                    // Next start number in THIS weapon class (never reused within the class, never
                    // renumbers others) — same "max + 1" approach as Fältskytte patrols. Scoped to the
                    // class because A and C have separate number ledgers.
                    startOrder = tl.Rows
                        .Where(r => string.Equals(r.WeaponClass, weaponClass, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.StartOrder).DefaultIfEmpty(0).Max() + 1;
                    targetConfig.Starters.Add(new SpringskytteStartListEntry
                    {
                        StartOrder = startOrder,
                        StartTime = startTime,
                        MemberId = request.MemberId,
                        Name = name,
                        Club = club,
                        WeaponClass = weaponClass,
                        AgeGenderClass = ageGenderClass
                    });
                }

                // Keep the array in start-time order so the public list, paus detection and generated
                // HTML stay correct (same invariant UpdateSpringskytteStarter maintains).
                targetConfig.Starters = targetConfig.Starters
                    .OrderBy(s => ParseStartTimeSeconds(s.StartTime))
                    .ThenBy(s => s.StartOrder)
                    .ToList();

                if (existing == null)
                    AppendNumberingEvent(targetConfig, "walk-in",
                        $"#{startOrder} tilldelat {name}", await CurrentMemberNameAsync());

                targetNode.SetValue("configurationData", JsonConvert.SerializeObject(targetConfig));
                targetNode.SetValue("startListContent", BuildStartListHtml(targetConfig.Starters));
                _contentService.Save(targetNode);

                // Mirror onto the DB result row if one already exists (normally none for a fresh walk-in);
                // best-effort — the saved config is authoritative for the start-list page.
                try
                {
                    using var db = _umbracoDatabaseFactory.CreateDatabase();
                    await db.ExecuteAsync(
                        @"UPDATE SpringskytteResultEntry SET StartOrder = @0, StartTime = @1, LastModified = @2
                          WHERE CompetitionId = @3 AND MemberId = @4 AND WeaponClass = @5",
                        startOrder, startTime, DateTime.Now,
                        request.CompetitionId, request.MemberId, weaponClass);
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "DB mirror of walk-in start-time failed (config saved); comp={Comp} member={Member}", request.CompetitionId, request.MemberId);
                }

                try
                {
                    var publishResult = _contentService.Publish(targetNode, new[] { "*" });
                    if (!publishResult.Success)
                        _logger.LogWarning("Failed to publish start list node {NodeId}: {Result}", targetNode.Id, publishResult.Result);
                }
                catch (Exception pubEx)
                {
                    _logger.LogWarning(pubEx, "Publish of start list node {NodeId} failed after walk-in insert; saved config is authoritative", targetNode.Id);
                }

                _logger.LogInformation("Springskytte walk-in slotted comp={Comp} member={Member} wc={Wc} -> #{Order} @ {Time}",
                    request.CompetitionId, request.MemberId, weaponClass, startOrder, startTime);

                return Json(new
                {
                    success = true,
                    message = $"Skytten tilldelad startnummer {startOrder} kl {ts:hh\\:mm} i vapengrupp {weaponClass}.",
                    startOrder,
                    startTime,
                    weaponClass
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning Springskytte walk-in start time");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Mark/unmark a shooter DNS. DNS frees the start slot (distinct from Närvaro/arrival) and
        /// ranks the shooter last; un-DNS restores them as scheduled (RM re-assigns a slot if taken).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSpringskytteDns([FromBody] SpringskytteDnsRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0 || string.IsNullOrEmpty(request.WeaponClass))
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                SpringskytteStartListEntry? starter = null;
                var comp = _contentService.GetById(request.CompetitionId);
                if (comp != null)
                    foreach (var node in _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                                 .Where(c => c.ContentType.Alias == "precisionStartList"))
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        SpringskytteStartListConfig? cfg = null;
                        try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                        var st = cfg?.Starters?.FirstOrDefault(s => s.MemberId == request.MemberId && s.WeaponClass == request.WeaponClass);
                        if (st != null) { starter = st; break; }
                    }

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int enteredBy = currentMember != null && int.TryParse(currentMember.Id, out var mid) ? mid : 0;
                var now = DateTime.Now;

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var existing = await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                    "WHERE CompetitionId=@0 AND MemberId=@1 AND WeaponClass=@2",
                    request.CompetitionId, request.MemberId, request.WeaponClass);

                if (existing != null)
                {
                    if (request.IsDns)
                        await db.ExecuteAsync(
                            @"UPDATE SpringskytteResultEntry SET Status='DNS', SprintTimeSeconds=NULL, TotalTimeSeconds=NULL, LastModified=@0
                              WHERE CompetitionId=@1 AND MemberId=@2 AND WeaponClass=@3",
                            now, request.CompetitionId, request.MemberId, request.WeaponClass);
                    else
                        await db.ExecuteAsync(
                            @"UPDATE SpringskytteResultEntry SET Status=NULL, LastModified=@0
                              WHERE CompetitionId=@1 AND MemberId=@2 AND WeaponClass=@3",
                            now, request.CompetitionId, request.MemberId, request.WeaponClass);
                }
                else
                {
                    if (starter == null) return Json(new { success = false, message = "Skytten finns inte i startlistan." });
                    await db.InsertAsync(new SpringskytteResultEntry
                    {
                        CompetitionId = request.CompetitionId,
                        MemberId = request.MemberId,
                        WeaponClass = request.WeaponClass,
                        AgeGenderClass = starter.AgeGenderClass,
                        StartOrder = starter.StartOrder,
                        StartTime = starter.StartTime,
                        SprintTimeSeconds = null,
                        ShootingScore = 0,
                        PenaltyMultiplier = 1,
                        TotalTimeSeconds = null,
                        Shots = "[]",
                        Status = request.IsDns ? "DNS" : null,
                        EnteredBy = enteredBy,
                        EnteredAt = now,
                        LastModified = now
                    });
                }

                return Json(new
                {
                    success = true,
                    isDns = request.IsDns,
                    message = request.IsDns ? "Markerad som DNS – starttiden är nu ledig." : "DNS borttagen."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Springskytte DNS");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>Which start lists a member appears in (for the delete-confirmation warning).</summary>
        [HttpGet]
        public async Task<IActionResult> GetStartListMembership(int competitionId, int memberId)
        {
            try
            {
                if (competitionId <= 0 || memberId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(competitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                var comp = _contentService.GetById(competitionId);
                var inLists = new List<object>();
                if (comp != null)
                    foreach (var node in _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                                 .Where(c => c.ContentType.Alias == "precisionStartList"))
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        SpringskytteStartListConfig? cfg = null;
                        try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                        var matches = cfg?.Starters?.Where(s => s.MemberId == memberId).ToList();
                        if (matches == null || matches.Count == 0) continue;
                        bool official = node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList");
                        var listName = !string.IsNullOrWhiteSpace(cfg!.ListName) ? cfg.ListName : (node.Name ?? "Startlista");
                        foreach (var m in matches)
                            inLists.Add(new { listName, startTime = m.StartTime, weaponClass = m.WeaponClass, official });
                    }
                return Json(new { success = true, inLists });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Springskytte start-list membership");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Clean up a deleted registration: remove the member from every start list (their slot becomes
        /// a free gap; everyone else's number/time is preserved), re-publish official lists, and delete
        /// their result + time-adjustment rows.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CleanupDeletedRegistration([FromBody] SpringskytteCleanupRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId)) return Json(new { success = false, message = "Åtkomst nekad." });

                var freed = new List<object>();
                var comp = _contentService.GetById(request.CompetitionId);
                if (comp != null)
                    foreach (var node in _contentService.GetPagedChildren(comp.Id, 0, 50, out _)
                                 .Where(c => c.ContentType.Alias == "precisionStartList"))
                    {
                        var cfgJson = node.GetValue<string>("configurationData");
                        if (string.IsNullOrEmpty(cfgJson)) continue;
                        SpringskytteStartListConfig? cfg = null;
                        try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                        if (cfg?.Starters == null || cfg.Starters.Count == 0) continue;

                        var removed = cfg.Starters.Where(s => s.MemberId == request.MemberId).ToList();
                        if (removed.Count == 0) continue;

                        cfg.Starters = cfg.Starters.Where(s => s.MemberId != request.MemberId).ToList();
                        bool official = node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList");
                        node.SetValue("configurationData", JsonConvert.SerializeObject(cfg));
                        node.SetValue("startListContent", BuildStartListHtml(cfg.Starters));
                        _contentService.Save(node);
                        if (official)
                        {
                            try { _contentService.Publish(node, new[] { "*" }); }
                            catch (Exception pubEx) { _logger.LogWarning(pubEx, "Cleanup: publish failed for {NodeId} (saved is authoritative)", node.Id); }
                        }
                        foreach (var r in removed)
                            freed.Add(new { listName = cfg.ListName, startTime = r.StartTime, wasOfficial = official });
                    }

                // Drop result + adjustment rows for the departed member.
                try
                {
                    using var db = _umbracoDatabaseFactory.CreateDatabase();
                    await db.ExecuteAsync("DELETE FROM SpringskytteResultEntry WHERE CompetitionId=@0 AND MemberId=@1", request.CompetitionId, request.MemberId);
                    await db.ExecuteAsync("DELETE FROM SpringskytteTimeAdjustment WHERE CompetitionId=@0 AND MemberId=@1", request.CompetitionId, request.MemberId);
                }
                catch (Exception dbEx) { _logger.LogWarning(dbEx, "Cleanup: DB row delete failed for comp {Comp} member {Member}", request.CompetitionId, request.MemberId); }

                _logger.LogInformation("Cleaned up deleted Springskytte registration comp={Comp} member={Member}, freed {Count} slot(s)",
                    request.CompetitionId, request.MemberId, freed.Count);
                return Json(new { success = true, freed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up deleted Springskytte registration");
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
