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
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Services;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Faltskytte.Controllers
{
    public class FaltskytteController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly ILogger<FaltskytteController> _logger;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _adminAuthorizationService;
        private readonly UmbracoStartListRepository _startListRepository;

        public FaltskytteController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<FaltskytteController> logger,
            ClubService clubService,
            AdminAuthorizationService adminAuthorizationService,
            UmbracoStartListRepository startListRepository)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _memberManager = memberManager;
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _logger = logger;
            _clubService = clubService;
            _adminAuthorizationService = adminAuthorizationService;
            _startListRepository = startListRepository;
        }

        // ── Authorization helpers ───────────────────────────────────

        private async Task<bool> IsAuthorizedForCatalog()
        {
            if (await _adminAuthorizationService.IsCurrentUserAdminAsync()) return true;
            var regions = await _adminAuthorizationService.GetManagedRegions();
            return regions.Any();
        }

        private async Task<bool> IsAuthorizedForCompetition(int competitionId)
        {
            if (await _adminAuthorizationService.IsCurrentUserAdminAsync())
                return true;
            if (await _adminAuthorizationService.IsCompetitionManager(competitionId))
                return true;

            // Regional admins can manage any competition
            var regions = await _adminAuthorizationService.GetManagedRegions();
            if (regions.Any())
                return true;

            var competition = _contentService.GetById(competitionId);
            var clubId = competition?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0)
            {
                if (await _adminAuthorizationService.IsClubAdminForClub(clubId))
                    return true;
                if (await _adminAuthorizationService.IsSkjutledareForClub(clubId))
                    return true;
            }
            return false;
        }

        // ── Self-service auth helpers ───────────────────────────────
        // Used when faltskytteSelfServiceResults is on for a competition: a
        // logged-in shooter who's in a patrol can read all stations of that
        // competition, and write scores at the patrol's CurrentStation.

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var data = _memberService.GetByEmail(current.Email ?? "");
            return data?.Id ?? 0;
        }

        private bool IsSelfServiceEnabledFor(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            return competition != null
                && competition.HasProperty("faltskytteSelfServiceResults")
                && competition.GetValue<bool>("faltskytteSelfServiceResults");
        }

        /// <summary>
        /// True when the current user can read this competition's station data.
        /// Staff (existing four-tier) always can; otherwise a logged-in member
        /// who has any patrol in this competition AND self-service is on can.
        /// </summary>
        private async Task<bool> CanReadStationAsync(int competitionId)
        {
            if (await IsAuthorizedForCompetition(competitionId)) return true;
            if (!IsSelfServiceEnabledFor(competitionId)) return false;
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0) return false;
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrols = await FaltskytteSelfServiceQueries
                .GetPatrolsForMemberAsync(db, competitionId, memberId);
            return patrols.Any();
        }

        /// <summary>
        /// True when the current user can WRITE a result for the given patrol
        /// at the given station. Staff bypass — always true. Otherwise requires
        /// self-service flag on, the user is in this patrol, and the patrol's
        /// CurrentStation matches stationNumber (older stations are locked).
        /// </summary>
        private async Task<bool> IsAuthorizedForSelfServiceWriteAsync(
            int competitionId, int patrolNumber, int stationNumber)
        {
            if (await IsAuthorizedForCompetition(competitionId)) return true;
            if (!IsSelfServiceEnabledFor(competitionId)) return false;
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0) return false;
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrol = await FaltskytteSelfServiceQueries
                .GetPatrolAsync(db, competitionId, patrolNumber);
            if (patrol == null) return false;
            if (patrol.CurrentStation != stationNumber) return false;
            return await FaltskytteSelfServiceQueries
                .IsMemberInPatrolAsync(db, patrol.Id, memberId);
        }

        // ── Station Config ──────────────────────────────────────────

        [HttpGet]
        public IActionResult GetStationConfig(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var config = ParseCompetitionConfig(competition);
            var scoringMode = competition.GetValue<string>("scoringMode") ?? "Normal";
            var maxReshoots = competition.GetValue<int>("maxReshoots");

            return Json(new { success = true, config, scoringMode, maxReshoots });
        }

        /// <summary>Parses the station config from competition, handling both old and new format.</summary>
        /// <summary>Sort order for class names in result lists: C→B→A→R→M, then by level and variant.</summary>
        private static int GetClassSortOrder(string className)
        {
            if (string.IsNullOrEmpty(className)) return 9999;
            // Weapon group order
            var weaponOrder = className[0] switch { 'C' => 100, 'L' => 200, 'B' => 300, 'A' => 400, 'R' => 500, 'M' => 600, _ => 800 };
            // Sub-order within weapon group: class number, then variant
            var sub = 0;
            if (className.Contains("1")) sub = 10;
            else if (className.Contains("2")) sub = 20;
            else if (className.Contains("3")) sub = 30;
            // Variant suffix
            if (className.Contains("Dam")) sub += 1;
            else if (className.Contains("Vet Y")) sub += 2;
            else if (className.Contains("Vet \u00c4")) sub += 3;
            else if (className.Contains("Vet")) sub += 2;
            else if (className.Contains("Jun")) sub += 4;
            // Merged classes (contain +) sort after their base
            if (className.Contains("+")) sub += 5;
            return weaponOrder + sub;
        }

        private static FaltskytteCompetitionConfig ParseCompetitionConfig(Umbraco.Cms.Core.Models.IContent competition)
        {
            var configJson = competition.GetValue<string>("stationConfig");
            return FaltskytteConfigParser.Parse(configJson);
        }

        /// <summary>Gets station config for a specific weapon class and station number.</summary>
        private static FaltskytteStationConfig? GetStationForWeaponClass(
            FaltskytteCompetitionConfig config, string weaponClass, int stationNumber)
        {
            var wcConfig = config.GetForWeaponClass(weaponClass);
            return wcConfig?.Stations.FirstOrDefault(s => s.Station == stationNumber);
        }

        /// <summary>Saves station config directly to the competition content node.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveStationConfig([FromBody] SaveStationConfigRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCompetition(request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                competition.SetValue("stationConfig", request.StationConfigJson ?? "");
                _contentService.Save(competition);
                _contentService.Publish(competition, new[] { "*" }, -1);

                _logger.LogInformation("Saved Fältskytte station config for competition {CompId}", request.CompetitionId);
                return Json(new { success = true, message = "Stationskonfiguration sparad." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving station config");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Station Entry View ──────────────────────────────────────

        /// <summary>
        /// Gets data for the station entry UI: station config + patrols with completion status.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStationEntryData(int competitionId, int stationNumber)
        {
            if (!await CanReadStationAsync(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            // Get station config (per-weapon-class)
            var competitionConfig = ParseCompetitionConfig(competition);
            var maxReshoots = competition.GetValue<int>("maxReshoots");

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // Get patrols
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);

            // Get patrol members
            var patrolIds = patrols.Select(p => p.Id).ToList();
            var allMembers = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>(
                    $"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();

            // Get existing results for this station
            var existingResults = await db.FetchAsync<FaltskytteResultEntry>(
                "WHERE CompetitionId = @0 AND StationNumber = @1", competitionId, stationNumber);
            // Track completion by (MemberId, ShootingClass) to support multi-class shooters
            var completedKeys = new HashSet<string>(existingResults.Select(r => r.MemberId + "_" + r.ShootingClass));

            // Build response
            var patrolViews = patrols.Select(p =>
            {
                var members = allMembers.Where(m => m.PatrolId == p.Id).ToList();
                return new FaltskyttePatrolView
                {
                    PatrolId = p.Id,
                    PatrolNumber = p.PatrolNumber,
                    StartTime = p.StartTime,
                    WeaponGroup = p.WeaponGroup,
                    CurrentStation = p.CurrentStation,
                    Members = members.Select(m => new FaltskyttePatrolMemberView
                    {
                        PatrolMemberId = m.Id,
                        MemberId = m.MemberId,
                        Position = m.Position,
                        Name = m.MemberName,
                        Club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                        ShootingClass = m.ShootingClass,
                        HasResult = completedKeys.Contains(m.MemberId + "_" + m.ShootingClass)
                    }).ToList(),
                    CompletedCount = members.Count(m => completedKeys.Contains(m.MemberId + "_" + m.ShootingClass))
                };
            }).ToList();

            // Build per-weapon-class station configs for this station number
            var scoringMode = competition.GetValue<string>("scoringMode") ?? "Normal";
            var wcStations = new Dictionary<string, FaltskytteStationConfig>();
            foreach (var kvp in competitionConfig.WeaponConfigs)
            {
                var st = kvp.Value.Stations.FirstOrDefault(s => s.Station == stationNumber);
                if (st != null) wcStations[kvp.Key] = st;
            }

            return Json(new
            {
                success = true,
                data = new FaltskytteStationView
                {
                    CompetitionId = competitionId,
                    StationNumber = stationNumber,
                    MaxReshoots = maxReshoots,
                    ScoringMode = scoringMode,
                    WeaponClassStations = wcStations,
                    Patrols = patrolViews
                }
            });
        }

        // ── Self-service: advance patrol cursor ─────────────────────

        public class AdvancePatrolCursorRequest
        {
            public int CompetitionId { get; set; }
            public int PatrolId { get; set; }
            public int StationNumber { get; set; }
        }

        /// <summary>
        /// Advances a patrol's CurrentStation cursor in self-service mode. Called
        /// once by the station page on initial load when a self-service shooter
        /// resolves to a single patrol. Re-scanning the same station is a no-op
        /// (the UPDATE WHERE clause skips). Staff loads of /station never call
        /// this endpoint, so cursor moves are driven exclusively by shooter scans.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdvancePatrolCursor([FromBody] AdvancePatrolCursorRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.PatrolId <= 0 || request.StationNumber <= 0)
                return Json(new { success = false, message = "Saknar parametrar." });

            if (!IsSelfServiceEnabledFor(request.CompetitionId))
                return Json(new { success = false, message = "Självservice är inte aktiverat." });

            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            // Verify the patrol belongs to this competition AND the caller is in it
            // — otherwise this could be used to move someone else's cursor.
            var patrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                "WHERE Id = @0 AND CompetitionId = @1", request.PatrolId, request.CompetitionId);
            if (patrol == null)
                return Json(new { success = false, message = "Patrullen hittades inte." });

            var inPatrol = await FaltskytteSelfServiceQueries
                .IsMemberInPatrolAsync(db, request.PatrolId, memberId);
            if (!inPatrol)
                return Json(new { success = false, message = "Du är inte med i denna patrull." });

            await FaltskytteSelfServiceQueries
                .AdvanceCursorAsync(db, request.PatrolId, request.StationNumber);

            return Json(new { success = true, currentStation = request.StationNumber });
        }

        // ── Re-shoot Info ───────────────────────────────────────────

        /// <summary>
        /// Gets total re-shoots used by a shooter across all stations in this competition.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetReshootInfo(int competitionId, int memberId, string? shootingClass = null)
        {
            if (!await CanReadStationAsync(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(competitionId);
            var maxReshoots = competition?.GetValue<int>("maxReshoots") ?? 0;

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            List<FaltskytteResultEntry> entries;
            if (!string.IsNullOrEmpty(shootingClass))
            {
                // Filter by weapon group — reshoots are per weapon class.
                // Resolve the requested class to its weapon group via the registry, then
                // expand that group to the list of shooting class IDs in the SAME group.
                // (Cannot use LEFT(ShootingClass, 1) here because A_opt_X would falsely match A.)
                var requestedGroup = ShootingClasses.GetWeaponClassCode(shootingClass);
                var sameGroupIds = ShootingClasses.All
                    .Where(sc => sc.Weapon.ToString() == requestedGroup)
                    .Select(sc => sc.Id)
                    .ToList();
                if (sameGroupIds.Count == 0) sameGroupIds.Add(shootingClass); // safety
                entries = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 AND MemberId = @1 AND Reshoots > 0 AND ShootingClass IN (@2)",
                    competitionId, memberId, sameGroupIds);
            }
            else
            {
                entries = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 AND MemberId = @1 AND Reshoots > 0",
                    competitionId, memberId);
            }

            var totalReshoots = entries.Sum(e => e.Reshoots);

            return Json(new
            {
                success = true,
                info = new FaltskytteReshootInfo
                {
                    MemberId = memberId,
                    TotalReshoots = totalReshoots,
                    MaxReshoots = maxReshoots,
                    LimitReached = maxReshoots > 0 && totalReshoots >= maxReshoots,
                    ReshootStations = entries.Select(e => e.StationNumber).ToList()
                }
            });
        }

        // ── Save Result (per shooter) ───────────────────────────────

        /// <summary>Gets a single shooter's saved result at a station.</summary>
        [HttpGet]
        public async Task<IActionResult> GetShooterStationResult(int competitionId, int stationNumber, int memberId, string? shootingClass = null)
        {
            try
            {
                if (!await CanReadStationAsync(competitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                FaltskytteResultEntry? result;
                if (!string.IsNullOrEmpty(shootingClass))
                {
                    result = await db.FirstOrDefaultAsync<FaltskytteResultEntry>(
                        "WHERE CompetitionId = @0 AND StationNumber = @1 AND MemberId = @2 AND ShootingClass = @3",
                        competitionId, stationNumber, memberId, shootingClass);
                }
                else
                {
                    result = await db.FirstOrDefaultAsync<FaltskytteResultEntry>(
                        "WHERE CompetitionId = @0 AND StationNumber = @1 AND MemberId = @2",
                        competitionId, stationNumber, memberId);
                }

                if (result == null)
                    return Json(new { success = false });

                return Json(new { success = true, result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting shooter station result");
                return Json(new { success = false });
            }
        }

        /// <summary>
        /// Saves one shooter's result at one station.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveStationResult([FromBody] FaltskylteSaveResultRequest request)
        {
            try
            {
                // Staff bypass via the standard four-tier check; otherwise allow self-service
                // writes when (a) the competition has self-service on, (b) the writer is in the
                // patrol whose results they're saving, and (c) the patrol's CurrentStation
                // cursor matches the station they're writing to (older stations are locked).
                if (!await IsAuthorizedForSelfServiceWriteAsync(
                        request.CompetitionId, request.PatrolNumber, request.StationNumber))
                    return Json(new FaltskylteSaveResultResponse { Success = false, Message = "Du har inte behörighet." });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new FaltskylteSaveResultResponse { Success = false, Message = "Du måste vara inloggad." });

                var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? "");
                var enteredBy = currentMemberData?.Id ?? 0;

                // Calculate hits and figures from HitsPerFigure array
                var totalHits = request.HitsPerFigure.Sum();
                var totalFigures = request.HitsPerFigure.Count(h => h > 0);
                var hitDistJson = JsonConvert.SerializeObject(request.HitsPerFigure);

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Check for existing entry (upsert) — includes ShootingClass to support multi-class shooters
                var existing = await db.FirstOrDefaultAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 AND StationNumber = @1 AND MemberId = @2 AND ShootingClass = @3",
                    request.CompetitionId, request.StationNumber, request.MemberId, request.ShootingClass);

                if (existing != null)
                {
                    existing.Hits = totalHits;
                    existing.Figures = totalFigures;
                    existing.HitDistribution = hitDistJson;
                    existing.TiebreakerScore = request.TiebreakerScore;
                    existing.PoangmalScores = request.PoangmalScores != null ? JsonConvert.SerializeObject(request.PoangmalScores) : null;
                    existing.Reshoots = request.Reshoots;
                    existing.EnteredBy = enteredBy;
                    existing.LastModified = DateTime.UtcNow;
                    await db.UpdateAsync(existing);

                    return Json(new FaltskylteSaveResultResponse
                    {
                        Success = true,
                        Message = "Resultat uppdaterat.",
                        ResultId = existing.Id,
                        TotalHits = totalHits,
                        TotalFigures = totalFigures
                    });
                }

                var entry = new FaltskytteResultEntry
                {
                    CompetitionId = request.CompetitionId,
                    StationNumber = request.StationNumber,
                    MemberId = request.MemberId,
                    PatrolNumber = request.PatrolNumber,
                    ShootingClass = request.ShootingClass,
                    Hits = totalHits,
                    Figures = totalFigures,
                    HitDistribution = hitDistJson,
                    TiebreakerScore = request.TiebreakerScore,
                    PoangmalScores = request.PoangmalScores != null ? JsonConvert.SerializeObject(request.PoangmalScores) : null,
                    Reshoots = request.Reshoots,
                    EnteredBy = enteredBy,
                    EnteredAt = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow
                };

                await db.InsertAsync(entry);

                _logger.LogInformation(
                    "Saved Fältskytte result: Competition={CompId}, Station={Station}, Member={Member}, Hits={Hits}/{Figures}",
                    request.CompetitionId, request.StationNumber, request.MemberId, totalHits, totalFigures);

                return Json(new FaltskylteSaveResultResponse
                {
                    Success = true,
                    Message = "Resultat sparat.",
                    ResultId = entry.Id,
                    TotalHits = totalHits,
                    TotalFigures = totalFigures
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Fältskytte result");
                return Json(new FaltskylteSaveResultResponse { Success = false, Message = "Fel: " + ex.Message });
            }
        }

        // ── Get Results (for result list generation) ────────────────

        /// <summary>
        /// Gets all results for a competition, grouped by class.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> AnalyzeFaltskytteMerges(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                var compType = competition.GetValue<string>("competitionType") ?? "Faltskytte";

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Count distinct members per class from result entries (a participant = has at least one station result)
                var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0", competitionId);
                var classCounts = allResults
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g => g.Key)
                    .GroupBy(k => HpskSite.Models.ShootingClasses.GetById(k.ShootingClass)?.Name ?? k.ShootingClass)
                    .ToDictionary(g => g.Key, g => g.Count());

                var service = new ClassMergingService();
                var analysis = service.AnalyzeFromCounts(classCounts, compType);

                // Load saved merge config
                var savedConfig = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";

                return Json(new { success = true, analysis, savedConfig });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing merges for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetFaltskytteResults(int competitionId, string? mergeConfig = null, bool subCompetitionOnly = false)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                var scoringMode = competition.GetValue<string>("scoringMode") ?? "Normal";
                var competitionConfig = ParseCompetitionConfig(competition);
                // For result display, use the first available weapon class config to determine station count
                var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
                var stationCount = firstWcConfig?.Stations.Count ?? 0;

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competitionId);

                if (!allResults.Any())
                    return Json(new { success = false, message = "Inga resultat finns." });

                // Get patrol members for name/club lookup
                var patrols = await db.FetchAsync<FaltskyttePatrol>(
                    "WHERE CompetitionId = @0", competitionId);
                var patrolIds = patrols.Select(p => p.Id).ToList();
                var allMembers = patrolIds.Any()
                    ? await db.FetchAsync<FaltskyttePatrolMember>(
                        $"WHERE PatrolId IN ({string.Join(",", patrolIds)})")
                    : new List<FaltskyttePatrolMember>();
                var memberLookup = allMembers
                    .GroupBy(m => m.MemberId)
                    .ToDictionary(g => g.Key, g => g.First());

                // Build shooter results
                var shooterResults = allResults
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g =>
                    {
                        var memberId = g.Key.MemberId;
                        var member = memberLookup.GetValueOrDefault(memberId);
                        var stationResults = g.OrderBy(r => r.StationNumber)
                            .Select(r => new FaltskytteStationResult
                            {
                                StationNumber = r.StationNumber,
                                Hits = r.Hits,
                                Figures = r.Figures,
                                TiebreakerScore = r.TiebreakerScore
                            }).ToList();

                        var totalHits = stationResults.Sum(s => s.Hits);
                        var totalFigures = stationResults.Sum(s => s.Figures);
                        var totalPoints = stationResults.Sum(s => s.Points);
                        var totalTiebreaker = stationResults.Where(s => s.TiebreakerScore.HasValue)
                            .Sum(s => s.TiebreakerScore!.Value);

                        return new FaltskytteShooterResult
                        {
                            MemberId = memberId,
                            Name = member?.MemberName ?? "Okänd skytt",
                            Club = HpskSite.Helpers.ClubNameHelper.Shorten(member?.ClubName ?? ""),
                            ShootingClass = HpskSite.Models.ShootingClasses.GetById(g.Key.ShootingClass)?.Name
                                ?? g.Key.ShootingClass,
                            Stations = stationResults,
                            TotalHits = totalHits,
                            TotalFigures = totalFigures,
                            TotalPoints = totalPoints,
                            TotalTiebreakerScore = totalTiebreaker
                        };
                    }).ToList();

                // Filter for sub-competition if requested
                if (subCompetitionOnly)
                {
                    var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                    var subCompMemberIds = new HashSet<int>(
                        registrations.Where(r => r.IsSubCompetition).Select(r => r.MemberId));
                    shooterResults = shooterResults.Where(s => subCompMemberIds.Contains(s.MemberId)).ToList();
                }

                // Build merge lookup from config (if provided)
                var mergeLookup = new Dictionary<string, string>(); // source class → combined group name
                if (string.IsNullOrEmpty(mergeConfig))
                {
                    // Try loading saved merge config from competition
                    mergeConfig = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";
                }
                if (!string.IsNullOrEmpty(mergeConfig))
                {
                    try
                    {
                        var mergeActions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfig);
                        if (mergeActions != null)
                        {
                            foreach (var action in mergeActions)
                            {
                                var combinedName = ClassMergingService.GetCombinedClassName(action.SourceClass, action.TargetClass);
                                mergeLookup[action.SourceClass] = combinedName;
                                if (!mergeLookup.ContainsKey(action.TargetClass))
                                    mergeLookup[action.TargetClass] = combinedName;
                            }
                        }
                    }
                    catch { /* ignore invalid merge config */ }
                }

                // Group by class (applying merge lookup) and rank
                var isPoang = scoringMode.Equals("Poang", StringComparison.OrdinalIgnoreCase);
                var tieBreaker = new Services.FaltskylteTieBreaker(isPoang);
                var classGroups = shooterResults
                    .GroupBy(s => mergeLookup.GetValueOrDefault(s.ShootingClass, s.ShootingClass))
                    .Select(g => new FaltskytteClassGroup
                    {
                        ClassName = g.Key,
                        Shooters = g.OrderByDescending(s => s, tieBreaker).ToList()
                    })
                    .OrderBy(g => GetClassSortOrder(g.ClassName))
                    .ToList();

                // Calculate standard medals (not for sub-competitions)
                if (!subCompetitionOnly)
                {
                    var medalService = new Services.FaltskytteStandardMedalService();
                    var scope = competition.GetValue<string>("competitionScope") ?? "";
                    var isChampionship = scope == "Svenskt Mästerskap" || scope == "Landsdelsmästerskap";
                    medalService.CalculateStandardMedals(shooterResults, scoringMode, stationCount, isChampionship);
                }

                // Header metadata for the result-list printout / on-screen card —
                // matches what the Precision result page surfaces (competition
                // name, date, organiser, status).
                var competitionName = competition.Name ?? competition.GetValue<string>("competitionName") ?? "";
                var competitionDateValue = competition.GetValue<DateTime?>("competitionDate");
                var competitionDateStr = competitionDateValue.HasValue
                    ? competitionDateValue.Value.ToString("yyyy-MM-dd")
                    : "";
                var organizerClubId = competition.GetValue<int>("clubId");
                var organizerName = organizerClubId > 0
                    ? (_clubService.GetClubNameById(organizerClubId) ?? "")
                    : "";

                return Json(new
                {
                    success = true,
                    results = new FaltskylteFinalResults
                    {
                        CompetitionId = competitionId,
                        UpdatedAt = DateTime.Now,
                        IsOfficial = competition.HasProperty("faltskytteResultsOfficial") && competition.GetValue<bool>("faltskytteResultsOfficial"),
                        ScoringMode = scoringMode,
                        StationCount = stationCount,
                        Config = competitionConfig,
                        ClassGroups = classGroups,
                        CompetitionName = competitionName,
                        CompetitionDate = competitionDateStr,
                        OrganizerName = organizerName
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Fältskytte results for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Saves merge config for Fältskytte results.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMergeConfig([FromBody] SaveMergeConfigRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(request.CompetitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            if (competition.HasProperty("mergeConfig"))
            {
                competition.SetValue("mergeConfig", request.MergeConfig ?? "");
                _contentService.Save(competition);
                _contentService.Publish(competition, new[] { "*" }, -1);
            }
            else
            {
                _logger.LogWarning("Competition {CompId} missing 'mergeConfig' property — merge config not saved. Add this property to the competition document type.", request.CompetitionId);
            }

            return Json(new { success = true });
        }

        /// <summary>Marks Fältskytte results as official or preliminary.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishResults([FromBody] PublishResultsRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(request.CompetitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            if (!competition.HasProperty("faltskytteResultsOfficial"))
                return Json(new { success = false, message = "Egenskapen 'faltskytteResultsOfficial' saknas på tävlingens dokumenttyp. Lägg till den i Umbraco backoffice (True/False)." });

            competition.SetValue("faltskytteResultsOfficial", request.IsOfficial);
            _contentService.Save(competition);
            _contentService.Publish(competition, new[] { "*" }, -1);

            // Ensure a competitionResult child page exists so the comp gets a /resultat/ URL.
            // CompetitionResult.cshtml renders Fältskytte by fetching live results from
            // GetFaltskytteResults — no resultData needs to be serialized here.
            var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

            if (resultPage == null)
            {
                resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                resultPage.SetValue("resultType", "Final Results");
            }
            resultPage.SetValue("isOfficial", request.IsOfficial);
            resultPage.SetValue("lastUpdated", DateTime.Now);
            _contentService.Save(resultPage);
            _contentService.Publish(resultPage, new[] { "*" }, -1);

            return Json(new { success = true });
        }

        // ── Target Catalog ───────────────────────────────────────────

        /// <summary>Returns all field targets with variants for the target picker.</summary>
        [HttpGet]
        public async Task<IActionResult> GetTargetCatalog()
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var targets = await db.FetchAsync<FieldTarget>("ORDER BY Name");
                var allVariants = await db.FetchAsync<FieldTargetVariant>("ORDER BY TargetId, Color");

                var variantsByTarget = allVariants.GroupBy(v => v.TargetId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var result = targets.Select(t => new FieldTargetView
                {
                    Id = t.Id,
                    Name = t.Name,
                    MaxDistanceC = t.MaxDistanceC,
                    MaxDistanceB = t.MaxDistanceB,
                    MaxDistanceA = t.MaxDistanceA,
                    MaxDistanceR = t.MaxDistanceR,
                    TargetsPerFigure = t.TargetsPerFigure,
                    Variants = variantsByTarget.GetValueOrDefault(t.Id, new())
                        .Select(v => new FieldTargetVariantView
                        {
                            Id = v.Id,
                            FullName = v.FullName,
                            ImageName = v.ImageName,
                            Color = v.Color
                        }).ToList()
                }).ToList();

                return Json(new { success = true, targets = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading target catalog");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Updates max distances for a field target. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTargetDistances([FromBody] UpdateTargetDistancesRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var target = await db.FirstOrDefaultAsync<FieldTarget>("WHERE Id = @0", request.TargetId);
                if (target == null)
                    return Json(new { success = false, message = "Figuren hittades inte." });

                target.MaxDistanceC = request.MaxDistanceC;
                target.MaxDistanceB = request.MaxDistanceB;
                target.MaxDistanceA = request.MaxDistanceA;
                target.MaxDistanceR = request.MaxDistanceR;
                await db.UpdateAsync(target);

                return Json(new { success = true, message = "Avstånd uppdaterade." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating target distances");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Updates a field target: name, distances, and variant names/colors. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTarget([FromBody] UpdateTargetRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var target = await db.FirstOrDefaultAsync<FieldTarget>("WHERE Id = @0", request.TargetId);
                if (target == null)
                    return Json(new { success = false, message = "Figuren hittades inte." });

                if (!string.IsNullOrEmpty(request.Name)) target.Name = request.Name;
                target.MaxDistanceC = request.MaxDistanceC;
                target.MaxDistanceB = request.MaxDistanceB;
                target.MaxDistanceA = request.MaxDistanceA;
                target.MaxDistanceR = request.MaxDistanceR;
                if (request.TargetsPerFigure.HasValue) target.TargetsPerFigure = request.TargetsPerFigure.Value;
                await db.UpdateAsync(target);

                if (request.Variants != null)
                {
                    foreach (var vReq in request.Variants)
                    {
                        var variant = await db.FirstOrDefaultAsync<FieldTargetVariant>("WHERE Id = @0", vReq.Id);
                        if (variant == null) continue;
                        if (!string.IsNullOrEmpty(vReq.FullName)) variant.FullName = vReq.FullName;
                        if (vReq.Color != null) variant.Color = vReq.Color;
                        await db.UpdateAsync(variant);
                    }
                }

                return Json(new { success = true, message = "Figur uppdaterad." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating target");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Creates a new field target with optional variants. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTarget([FromBody] CreateTargetRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                if (string.IsNullOrWhiteSpace(request.Name))
                    return Json(new { success = false, message = "Namn krävs." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var target = new FieldTarget
                {
                    Name = request.Name,
                    MaxDistanceC = request.MaxDistanceC,
                    MaxDistanceB = request.MaxDistanceB,
                    MaxDistanceA = request.MaxDistanceA,
                    MaxDistanceR = request.MaxDistanceR,
                    TargetsPerFigure = request.TargetsPerFigure
                };
                await db.InsertAsync(target);

                if (request.Variants != null)
                {
                    foreach (var v in request.Variants)
                    {
                        await db.InsertAsync(new FieldTargetVariant
                        {
                            TargetId = target.Id,
                            FullName = v.FullName,
                            ImageName = v.ImageName,
                            Color = v.Color
                        });
                    }
                }

                return Json(new { success = true, message = "Figur skapad.", targetId = target.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating target");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Deletes a field target and all its variants. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTarget([FromBody] DeleteTargetRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                // FK cascade deletes variants
                var target = await db.FirstOrDefaultAsync<FieldTarget>("WHERE Id = @0", request.TargetId);
                if (target == null)
                    return Json(new { success = false, message = "Figuren hittades inte." });
                await db.DeleteAsync(target);
                return Json(new { success = true, message = "Figur borttagen." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting target");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Adds a variant to an existing target. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddVariant([FromBody] AddVariantRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var variant = new FieldTargetVariant
                {
                    TargetId = request.TargetId,
                    FullName = request.FullName,
                    ImageName = request.ImageName,
                    Color = request.Color
                };
                await db.InsertAsync(variant);
                return Json(new { success = true, message = "Variant tillagd.", variantId = variant.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding variant");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Deletes a variant. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteVariant([FromBody] DeleteVariantRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var variant = await db.FirstOrDefaultAsync<FieldTargetVariant>("WHERE Id = @0", request.VariantId);
                if (variant == null)
                    return Json(new { success = false, message = "Varianten hittades inte." });
                await db.DeleteAsync(variant);
                return Json(new { success = true, message = "Variant borttagen." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting variant");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Moves a variant to a different target. Admin only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveVariant([FromBody] MoveVariantRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCatalog())
                    return Json(new { success = false, message = "Endast administratörer och kretsadministratörer." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var variant = await db.FirstOrDefaultAsync<FieldTargetVariant>("WHERE Id = @0", request.VariantId);
                if (variant == null)
                    return Json(new { success = false, message = "Varianten hittades inte." });

                var newTarget = await db.FirstOrDefaultAsync<FieldTarget>("WHERE Id = @0", request.NewTargetId);
                if (newTarget == null)
                    return Json(new { success = false, message = "Målfiguren hittades inte." });

                var oldTargetId = variant.TargetId;
                variant.TargetId = request.NewTargetId;
                await db.UpdateAsync(variant);

                // If old target now has no variants, optionally clean up
                var remainingCount = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM FieldTargetVariant WHERE TargetId = @0", oldTargetId);

                return Json(new { success = true, message = "Variant flyttad till " + newTarget.Name + ".", oldTargetEmpty = remainingCount == 0, oldTargetId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving variant");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Uploads an image for a catalog variant, saves to wwwroot/images/field-targets/.</summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadVariantImage(IFormFile file, int variantId)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return Json(new { success = false, message = "Ingen fil vald." });
                if (file.Length > 5 * 1024 * 1024)
                    return Json(new { success = false, message = "Max 5 MB." });

                var ext = Path.GetExtension(file.FileName).ToLower();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp" && ext != ".gif")
                    return Json(new { success = false, message = "Endast JPG, PNG, WebP eller GIF." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var variant = await db.FirstOrDefaultAsync<FieldTargetVariant>("WHERE Id = @0", variantId);
                if (variant == null)
                    return Json(new { success = false, message = "Varianten hittades inte." });

                var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "field-targets");
                Directory.CreateDirectory(dir);

                // Use a clean filename
                var fileName = $"target_{variant.TargetId}_v{variant.Id}{ext}";
                var filePath = Path.Combine(dir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Update variant's ImageName in DB
                variant.ImageName = fileName;
                await db.UpdateAsync(variant);

                var imageUrl = $"/images/field-targets/{fileName}";
                return Json(new { success = true, imageUrl, imageName = fileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading variant image");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Target Group Image Upload ────────────────────────────────

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadTargetGroupImage(IFormFile file, int competitionId, string weaponClass, int stationNumber, int groupNumber)
        {
            try
            {
                if (!await IsAuthorizedForCompetition(competitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                if (file == null || file.Length == 0)
                    return Json(new { success = false, message = "Ingen fil vald." });

                if (file.Length > 5 * 1024 * 1024)
                    return Json(new { success = false, message = "Filen är för stor (max 5 MB)." });

                var ext = Path.GetExtension(file.FileName).ToLower();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                    return Json(new { success = false, message = "Endast JPG, PNG eller WebP." });

                var dir = Path.Combine("wwwroot", "images", "faltskytte", competitionId.ToString());
                var fullDir = Path.Combine(Directory.GetCurrentDirectory(), dir);
                Directory.CreateDirectory(fullDir);

                var fileName = $"st{stationNumber}_{weaponClass}_tg{groupNumber}{ext}";
                var filePath = Path.Combine(fullDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var imageUrl = $"/images/faltskytte/{competitionId}/{fileName}";
                _logger.LogInformation("Uploaded target group image: {Url}", imageUrl);

                return Json(new { success = true, imageUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading target group image");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Has Results Check ───────────────────────────────────────

        /// <summary>Checks if any results exist for this competition.</summary>
        [HttpGet]
        public async Task<IActionResult> HasResults(int competitionId)
        {
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskytteResultEntry WHERE CompetitionId = @0", competitionId);
            return Json(new { success = true, hasResults = count > 0, resultCount = count });
        }

        // ── Rolling Start ───────────────────────────────────────────

        /// <summary>Adds a shooter to the next available patrol, creating one if needed.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> JoinNextPatrol([FromBody] JoinNextPatrolRequest request)
        {
            try
            {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var weaponGroup = !string.IsNullOrEmpty(request.ShootingClass)
                ? (ShootingClasses.GetWeaponClassCode(request.ShootingClass) is { Length: > 0 } code ? code : "C")
                : "C";
            var patrolSize = request.PatrolSize > 0 ? request.PatrolSize : 2;

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // Check if shooter is already in a patrol for this weapon group
            var existingAssignment = await db.FirstOrDefaultAsync<FaltskyttePatrolMember>(
                @"SELECT pm.* FROM FaltskyttePatrolMember pm
                  INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id
                  WHERE p.CompetitionId = @0 AND pm.MemberId = @1 AND LEFT(pm.ShootingClass, 1) = @2",
                request.CompetitionId, request.MemberId, weaponGroup);
            if (existingAssignment != null)
            {
                var existingPatrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>("WHERE Id = @0", existingAssignment.PatrolId);
                return Json(new { success = true, patrolNumber = existingPatrol?.PatrolNumber ?? 0, alreadyAssigned = true,
                    message = "Skytten finns redan i patrull " + (existingPatrol?.PatrolNumber ?? 0) });
            }

            // Find latest patrol for this weapon group with space
            var openPatrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                @"SELECT p.* FROM FaltskyttePatrol p
                  WHERE p.CompetitionId = @0 AND p.WeaponGroup = @1
                  AND (SELECT COUNT(*) FROM FaltskyttePatrolMember WHERE PatrolId = p.Id) < @2
                  ORDER BY p.PatrolNumber DESC",
                request.CompetitionId, weaponGroup, patrolSize);

            if (openPatrol == null)
            {
                // Create new patrol — global numbering across weapon groups so
                // each patrol's number is unique competition-wide. Per-group
                // numbering would both duplicate ("Patrull 1" in C and Patrull 1
                // in A) and trip the (CompetitionId, PatrolNumber) UQ constraint
                // once a second weapon group joins.
                var maxNum = await db.ExecuteScalarAsync<int>(
                    "SELECT ISNULL(MAX(PatrolNumber), 0) FROM FaltskyttePatrol WHERE CompetitionId = @0",
                    request.CompetitionId);
                openPatrol = new FaltskyttePatrol
                {
                    CompetitionId = request.CompetitionId,
                    PatrolNumber = maxNum + 1,
                    StartTime = null,
                    WeaponGroup = weaponGroup
                };
                await db.InsertAsync(openPatrol);
            }

            // Add shooter to patrol
            var maxPos = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(Position), 0) FROM FaltskyttePatrolMember WHERE PatrolId = @0", openPatrol.Id);
            await db.InsertAsync(new FaltskyttePatrolMember
            {
                PatrolId = openPatrol.Id,
                MemberId = request.MemberId,
                Position = maxPos + 1,
                ShootingClass = request.ShootingClass,
                MemberName = request.MemberName,
                ClubName = request.ClubName
            });

            var memberCount = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskyttePatrolMember WHERE PatrolId = @0", openPatrol.Id);

            return Json(new { success = true, patrolNumber = openPatrol.PatrolNumber, memberCount, patrolSize, weaponGroup });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── QR Code Generation ─────────────────────────────────────

        /// <summary>Generates a QR code PNG for the given URL text.</summary>
        [HttpGet]
        public IActionResult GenerateQrCode(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return BadRequest("URL required");
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);
                using var img = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: SixLabors.ImageSharp.Color.Black,
                    lightColor: SixLabors.ImageSharp.Color.White,
                    drawQuietZones: true);
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return File(ms.ToArray(), "image/png");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating QR code");
                return StatusCode(500);
            }
        }

        // ── Patrol Management ───────────────────────────────────────

        /// <summary>Gets weapon classes that have registrations for this competition.</summary>
        [HttpGet]
        public async Task<IActionResult> GetAvailableWeaponClasses(int competitionId)
        {
            try
            {
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);

                var competition = _contentService.GetById(competitionId);
                var compType = competition?.GetValue<string>("competitionType") ?? "Faltskytte";
                var isMagnumFalt = compType == "MagnumFalt";

                // Extract unique weapon classes/groups
                var weaponClasses = registrations
                    .Select(r => {
                        if (isMagnumFalt)
                        {
                            // For MagnumFält: use full class ID (M1, M2, etc.)
                            var sc = HpskSite.Models.ShootingClasses.GetById(r.MemberClass)
                                ?? HpskSite.Models.ShootingClasses.GetByName(r.MemberClass);
                            return sc?.Id ?? r.MemberClass;
                        }
                        // Standard: use weapon group code (A, A_Opt, B, C, R) via the registry
                        return ShootingClasses.GetWeaponClassCode(r.MemberClass);
                    })
                    .Where(w => !string.IsNullOrEmpty(w))
                    .Distinct()
                    .OrderBy(w => w)
                    .ToList();

                return Json(new { success = true, weaponClasses });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available weapon classes");
                return Json(new { success = false, weaponClasses = new[] { "C" } });
            }
        }

        /// <summary>Generates patrols from registrations.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GeneratePatrols([FromBody] GeneratePatrolsRequest request)
        {
            try
            {
                if (!await IsAuthorizedForCompetition(request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen hittades inte." });

                var patrolSize = request.PatrolSize > 0 ? request.PatrolSize : 6;
                var intervalMinutes = request.PatrolIntervalMinutes > 0 ? request.PatrolIntervalMinutes : 15;

                // Fetch registrations
                var allRegistrations = await _startListRepository.GetCompetitionRegistrations(request.CompetitionId);
                if (!allRegistrations.Any())
                    return Json(new { success = false, message = "Inga anmälningar hittades." });

                // Filter by selected weapon classes
                var registrations = allRegistrations;
                if (request.WeaponClasses?.Any() == true)
                {
                    var selectedWcs = new HashSet<string>(request.WeaponClasses, StringComparer.OrdinalIgnoreCase);
                    registrations = allRegistrations
                        .Where(r =>
                        {
                            var wg = ShootingClasses.GetWeaponClassCode(r.MemberClass);
                            return selectedWcs.Contains(wg);
                        })
                        .ToList();
                }

                if (!registrations.Any())
                    return Json(new { success = false, message = "Inga anmälningar för valda vapenklasser." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Determine next patrol number (append to existing)
                var existingMaxNumber = await db.ExecuteScalarAsync<int>(
                    "SELECT ISNULL(MAX(PatrolNumber), 0) FROM FaltskyttePatrol WHERE CompetitionId = @0",
                    request.CompetitionId);

                // Determine weapon group label for these patrols
                var weaponGroupLabel = request.WeaponClasses?.Any() == true
                    ? string.Join("+", request.WeaponClasses.OrderBy(w => w))
                    : "Alla";

                // Load existing patrol start times for members being generated
                // This ensures gap enforcement across separate generation runs
                var memberIds = registrations.Select(r => r.MemberId).Distinct().ToList();
                var existingMemberTimes = new Dictionary<int, List<DateTime>>();
                if (request.MultiClassGapMinutes > 0 && memberIds.Any())
                {
                    var existingPatrols = await db.FetchAsync<FaltskyttePatrol>(
                        "WHERE CompetitionId = @0 AND StartTime IS NOT NULL", request.CompetitionId);
                    var existingMembers = await db.FetchAsync<FaltskyttePatrolMember>(
                        $"WHERE PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @0)", request.CompetitionId);
                    var patrolTimeMap = existingPatrols.ToDictionary(p => p.Id, p => p.StartTime!.Value);

                    foreach (var pm in existingMembers)
                    {
                        if (patrolTimeMap.TryGetValue(pm.PatrolId, out var startTime))
                        {
                            if (!existingMemberTimes.ContainsKey(pm.MemberId))
                                existingMemberTimes[pm.MemberId] = new List<DateTime>();
                            existingMemberTimes[pm.MemberId].Add(startTime);
                        }
                    }
                }

                // Generate patrols
                var generator = new Services.FaltskyttePatrolGenerator();
                var result = generator.Generate(registrations, patrolSize, intervalMinutes, request.FirstStartTime, request.WeaponGrouping ?? "MixAll", request.MultiClassGapMinutes, existingMemberTimes);

                if (!result.Patrols.Any())
                    return Json(new { success = false, message = "Kunde inte skapa patruller." });

                // Override weapon group label and adjust patrol numbers
                foreach (var patrol in result.Patrols)
                {
                    patrol.PatrolNumber += existingMaxNumber;
                    patrol.WeaponGroup = weaponGroupLabel;
                }

                // Insert new patrols (append, don't delete existing)
                foreach (var patrol in result.Patrols)
                {
                    var dbPatrol = new FaltskyttePatrol
                    {
                        CompetitionId = request.CompetitionId,
                        PatrolNumber = patrol.PatrolNumber,
                        StartTime = patrol.StartTime,
                        WeaponGroup = patrol.WeaponGroup
                    };
                    await db.InsertAsync(dbPatrol);

                    foreach (var member in patrol.Members)
                    {
                        await db.InsertAsync(new FaltskyttePatrolMember
                        {
                            PatrolId = dbPatrol.Id,
                            MemberId = member.MemberId,
                            Position = member.Position,
                            ShootingClass = member.ShootingClass,
                            MemberName = member.Name,
                            ClubName = member.Club
                        });
                    }
                }

                // Defensive global renumber. Generations performed by older code
                // paths could leave per-weapon-group "1, 2, 3" sequences in the
                // database; this pass closes gaps and resolves any duplicates so
                // every patrol in the competition has a unique number 1..N.
                await RenumberAllPatrolsAsync(db, request.CompetitionId);

                _logger.LogInformation("Generated {PatrolCount} Fältskytte patrols ({Group}) for competition {CompId}",
                    result.TotalPatrols, weaponGroupLabel, request.CompetitionId);

                return Json(new { success = true, result.Message, result.TotalPatrols, result.TotalShooters });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating patrols for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Deletes all patrols for a competition.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePatrols([FromBody] DeletePatrolsRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "DELETE FROM FaltskyttePatrolMember WHERE PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @0)",
                request.CompetitionId);
            var deleted = await db.ExecuteAsync("DELETE FROM FaltskyttePatrol WHERE CompetitionId = @0", request.CompetitionId);

            return Json(new { success = true, message = $"{deleted} patruller borttagna." });
        }

        /// <summary>Deletes patrols for a specific weapon group.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePatrolsByGroup([FromBody] DeletePatrolsByGroupRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "DELETE FROM FaltskyttePatrolMember WHERE PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @0 AND WeaponGroup = @1)",
                request.CompetitionId, request.WeaponGroup);
            var deleted = await db.ExecuteAsync(
                "DELETE FROM FaltskyttePatrol WHERE CompetitionId = @0 AND WeaponGroup = @1",
                request.CompetitionId, request.WeaponGroup);

            return Json(new { success = true, message = $"{deleted} patruller för {request.WeaponGroup} borttagna." });
        }

        /// <summary>Gets all patrols for a competition.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPatrols(int competitionId)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);

            var patrolIds = patrols.Select(p => p.Id).ToList();
            var allMembers = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>(
                    $"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();

            var result = patrols.Select(p => new FaltskyttePatrolView
            {
                PatrolId = p.Id,
                PatrolNumber = p.PatrolNumber,
                StartTime = p.StartTime,
                WeaponGroup = p.WeaponGroup,
                Members = allMembers.Where(m => m.PatrolId == p.Id)
                    .Select(m => new FaltskyttePatrolMemberView
                    {
                        PatrolMemberId = m.Id,
                        MemberId = m.MemberId,
                        Position = m.Position,
                        Name = m.MemberName,
                        Club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                        ShootingClass = m.ShootingClass
                    }).ToList()
            }).ToList();

            return Json(new { success = true, patrols = result });
        }

        // ── Patrol Editing ─────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreatePatrol([FromBody] CreatePatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            int newPatrolNumber;
            if (request.AfterPatrolNumber.HasValue && request.AfterPatrolNumber.Value > 0)
            {
                // Insert after specified patrol number
                newPatrolNumber = request.AfterPatrolNumber.Value + 1;
                // Shift subsequent patrols up by 1
                await db.ExecuteAsync(
                    "UPDATE FaltskyttePatrol SET PatrolNumber = PatrolNumber + 1 WHERE CompetitionId = @0 AND PatrolNumber >= @1",
                    request.CompetitionId, newPatrolNumber);
            }
            else
            {
                var maxNum = await db.ExecuteScalarAsync<int>(
                    "SELECT ISNULL(MAX(PatrolNumber), 0) FROM FaltskyttePatrol WHERE CompetitionId = @0", request.CompetitionId);
                newPatrolNumber = maxNum + 1;
            }

            var patrol = new FaltskyttePatrol
            {
                CompetitionId = request.CompetitionId,
                PatrolNumber = newPatrolNumber,
                StartTime = request.StartTime,
                WeaponGroup = request.WeaponGroup
            };
            await db.InsertAsync(patrol);

            // Renumber all patrols sequentially to close any gaps
            var allPatrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber, Id", request.CompetitionId);
            for (int i = 0; i < allPatrols.Count; i++)
            {
                if (allPatrols[i].PatrolNumber != i + 1)
                {
                    allPatrols[i].PatrolNumber = i + 1;
                    await db.UpdateAsync(allPatrols[i]);
                }
            }

            return Json(new { success = true, patrolId = patrol.Id, patrolNumber = patrol.PatrolNumber });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePatrol([FromBody] DeletePatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM FaltskyttePatrolMember WHERE PatrolId = @0", request.PatrolId);
            await db.ExecuteAsync("DELETE FROM FaltskyttePatrol WHERE Id = @0 AND CompetitionId = @1", request.PatrolId, request.CompetitionId);

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddShooterToPatrol([FromBody] AddShooterToPatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // Remove from any existing patrol within the same weapon group (allows "move" via add).
            // A shooter in a C patrol should not be removed when adding to an A patrol — and
            // A_opt is its own group, so an A_opt assignment should not affect plain A patrols.
            var weaponGroup = ShootingClasses.GetWeaponClassCode(request.ShootingClass);
            if (!string.IsNullOrEmpty(weaponGroup))
            {
                var sameGroupIds = ShootingClasses.All
                    .Where(sc => sc.Weapon.ToString() == weaponGroup)
                    .Select(sc => sc.Id)
                    .ToList();
                if (sameGroupIds.Count == 0) sameGroupIds.Add(request.ShootingClass);
                await db.ExecuteAsync(
                    @"DELETE FROM FaltskyttePatrolMember WHERE MemberId = @0
                      AND ShootingClass IN (@2)
                      AND PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @1)",
                    request.MemberId, request.CompetitionId, sameGroupIds);
            }

            var maxPos = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(Position), 0) FROM FaltskyttePatrolMember WHERE PatrolId = @0", request.PatrolId);

            var member = new FaltskyttePatrolMember
            {
                PatrolId = request.PatrolId,
                MemberId = request.MemberId,
                Position = maxPos + 1,
                ShootingClass = request.ShootingClass,
                MemberName = request.MemberName,
                ClubName = request.ClubName
            };
            await db.InsertAsync(member);

            return Json(new { success = true, patrolMemberId = member.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveShooterFromPatrol([FromBody] RemoveShooterFromPatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM FaltskyttePatrolMember WHERE Id = @0", request.PatrolMemberId);

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveShooterToPatrol([FromBody] MoveShooterToPatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var maxPos = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(Position), 0) FROM FaltskyttePatrolMember WHERE PatrolId = @0", request.TargetPatrolId);

            await db.ExecuteAsync(
                "UPDATE FaltskyttePatrolMember SET PatrolId = @0, Position = @1 WHERE Id = @2",
                request.TargetPatrolId, maxPos + 1, request.PatrolMemberId);

            return Json(new { success = true });
        }

        /// <summary>
        /// Cashier walk-in: drop a freshly-registered shooter on a patrol in one round trip.
        /// The endpoint reads the registration so the caller doesn't have to forward member /
        /// class / club details (they're already on the registration document).
        ///
        /// Multi-class registrations are handled by grouping classes by weapon group: each
        /// weapon group resolves a target patrol independently (a shooter doing A1 + B1 lands
        /// on a patrol for A and a patrol for B). Mutex in the walk-in form prevents two
        /// classes in the same weapon group, but the dedupe below tolerates it just in case.
        ///
        /// Target hint resolution (applied per weapon group):
        ///   "nextAvailable" — highest-numbered existing patrol whose WeaponGroup matches the
        ///                     shooter (or no group set); creates a new patrol when none exist.
        ///   "newPatrol"     — always creates a new appended patrol with this group.
        ///   "&lt;patrolId&gt;"     — uses the explicit patrol when its WeaponGroup matches this
        ///                     class's group (or the patrol has no group set); otherwise
        ///                     falls back to "nextAvailable" semantics for that group.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignWalkInToPatrol([FromBody] AssignWalkInToPatrolRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var registration = _contentService.GetById(request.RegistrationId);
            if (registration == null)
                return Json(new { success = false, message = "Anmälan hittades inte." });

            var shootingClassesJson = registration.GetValue<string>("shootingClasses") ?? "";
            var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);
            var validClasses = shootingClasses
                .Where(sc => !string.IsNullOrEmpty(sc.Class))
                .ToList();
            if (validClasses.Count == 0)
                return Json(new { success = false, message = "Anmälan saknar vapenklass." });

            var memberId = registration.GetValue<int>("memberId");
            var memberName = registration.GetValue<string>("memberName") ?? "";
            var clubId = registration.GetValue<int>("clubId");
            var clubName = clubId > 0 ? (_clubService.GetClubNameById(clubId) ?? "") : "";

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // If the operator picked an explicit patrol, look up its group once so we can
            // tell per class whether it's the right home or whether the class needs a fresh
            // resolution by its own weapon group.
            int? explicitPatrolId = null;
            string explicitPatrolGroup = "";
            if (int.TryParse(request.Target, out var pickedId) && pickedId > 0)
            {
                var picked = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                    "WHERE Id = @0 AND CompetitionId = @1", pickedId, request.CompetitionId);
                if (picked == null)
                    return Json(new { success = false, message = "Patrullen kunde inte hittas." });
                explicitPatrolId = picked.Id;
                explicitPatrolGroup = picked.WeaponGroup ?? "";
            }

            // Group classes by weapon group; each group lands on its own patrol.
            var classesByGroup = validClasses
                .GroupBy(sc => ShootingClasses.GetWeaponClassCode(sc.Class) ?? "")
                .ToList();

            var assignments = new List<object>();
            foreach (var grp in classesByGroup)
            {
                var weaponGroup = grp.Key;
                int patrolId;
                int patrolNumber;
                bool createdNew = false;

                if (request.Target == "newPatrol")
                {
                    patrolId = await CreateAppendedPatrolAsync(db, request.CompetitionId, weaponGroup);
                    patrolNumber = await db.ExecuteScalarAsync<int>(
                        "SELECT PatrolNumber FROM FaltskyttePatrol WHERE Id = @0", patrolId);
                    createdNew = true;
                }
                else if (explicitPatrolId.HasValue
                    && (string.IsNullOrEmpty(explicitPatrolGroup) || explicitPatrolGroup == weaponGroup))
                {
                    patrolId = explicitPatrolId.Value;
                    patrolNumber = await db.ExecuteScalarAsync<int>(
                        "SELECT PatrolNumber FROM FaltskyttePatrol WHERE Id = @0", patrolId);
                }
                else // "nextAvailable" — also the fallback when the explicit pick is in the wrong group
                {
                    var existing = await db.FetchAsync<FaltskyttePatrol>(
                        @"WHERE CompetitionId = @0
                          AND (WeaponGroup = @1 OR WeaponGroup = '' OR WeaponGroup IS NULL)
                          ORDER BY PatrolNumber DESC",
                        request.CompetitionId, weaponGroup);

                    if (existing.Any())
                    {
                        patrolId = existing.First().Id;
                        patrolNumber = existing.First().PatrolNumber;
                    }
                    else
                    {
                        patrolId = await CreateAppendedPatrolAsync(db, request.CompetitionId, weaponGroup);
                        patrolNumber = await db.ExecuteScalarAsync<int>(
                            "SELECT PatrolNumber FROM FaltskyttePatrol WHERE Id = @0", patrolId);
                        createdNew = true;
                    }
                }

                // Same-group dedupe (matches AddShooterToPatrol's behaviour) — moving a shooter
                // between patrols of the same weapon group via add must not leave them on both.
                if (!string.IsNullOrEmpty(weaponGroup))
                {
                    var sameGroupIds = ShootingClasses.All
                        .Where(sc => sc.Weapon.ToString() == weaponGroup)
                        .Select(sc => sc.Id)
                        .ToList();
                    if (sameGroupIds.Count == 0)
                        sameGroupIds.AddRange(grp.Select(g => g.Class));
                    await db.ExecuteAsync(
                        @"DELETE FROM FaltskyttePatrolMember WHERE MemberId = @0
                          AND ShootingClass IN (@2)
                          AND PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @1)",
                        memberId, request.CompetitionId, sameGroupIds);
                }

                // Insert one patrol-member row per class in this group. Increment maxPos
                // across the inserts so two classes from the same shooter on the same
                // patrol get consecutive positions.
                var maxPos = await db.ExecuteScalarAsync<int>(
                    "SELECT ISNULL(MAX(Position), 0) FROM FaltskyttePatrolMember WHERE PatrolId = @0", patrolId);

                foreach (var classEntry in grp)
                {
                    maxPos++;
                    var memberRow = new FaltskyttePatrolMember
                    {
                        PatrolId = patrolId,
                        MemberId = memberId,
                        Position = maxPos,
                        ShootingClass = classEntry.Class,
                        MemberName = memberName,
                        ClubName = clubName
                    };
                    await db.InsertAsync(memberRow);
                }

                assignments.Add(new
                {
                    weaponGroup,
                    patrolId,
                    patrolNumber,
                    createdNewPatrol = createdNew,
                    classCount = grp.Count()
                });
            }

            return Json(new
            {
                success = true,
                assignments
            });
        }

        private static async Task<int> CreateAppendedPatrolAsync(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId, string weaponGroup)
        {
            var maxNum = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(PatrolNumber), 0) FROM FaltskyttePatrol WHERE CompetitionId = @0", competitionId);
            var patrol = new FaltskyttePatrol
            {
                CompetitionId = competitionId,
                PatrolNumber = maxNum + 1,
                WeaponGroup = weaponGroup
            };
            await db.InsertAsync(patrol);
            return patrol.Id;
        }

        /// <summary>
        /// Renumber every patrol in the competition continuously (1..N) preserving
        /// existing relative order. Closes gaps and resolves any duplicate
        /// PatrolNumber values — older code paths or off-script imports could leave
        /// per-weapon-group "1, 2, 3" series in place; this pass turns those into a
        /// single global sequence.
        /// Two-phase to avoid the (CompetitionId, PatrolNumber) UQ collision when
        /// fixing duplicates: bump everyone above the target range first, then walk
        /// in order assigning 1..N.
        /// </summary>
        private static async Task RenumberAllPatrolsAsync(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId)
        {
            var allPatrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber, Id", competitionId);
            if (allPatrols.Count == 0) return;

            // Snapshot the original ordering before we mutate the in-memory list.
            var ordered = allPatrols.Select(p => p.Id).ToList();

            // Phase 1: lift every row out of the 1..N target range. bump > count
            // guarantees the post-bump range and the target range don't overlap.
            var bump = allPatrols.Count + 1000;
            await db.ExecuteAsync(
                "UPDATE FaltskyttePatrol SET PatrolNumber = PatrolNumber + @0 WHERE CompetitionId = @1",
                bump, competitionId);

            // Phase 2: walk the original order and reassign sequential numbers.
            for (int i = 0; i < ordered.Count; i++)
            {
                await db.ExecuteAsync(
                    "UPDATE FaltskyttePatrol SET PatrolNumber = @0 WHERE Id = @1",
                    i + 1, ordered[i]);
            }
        }

        /// <summary>
        /// Force a global renumber of all patrols in the competition. Closes gaps
        /// and resolves any per-weapon-group duplicate numbering left over from
        /// older data.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RenumberPatrols([FromBody] CompetitionIdRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await RenumberAllPatrolsAsync(db, request.CompetitionId);
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskyttePatrol WHERE CompetitionId = @0", request.CompetitionId);
            return Json(new { success = true, count });
        }

        public class CompetitionIdRequest
        {
            public int CompetitionId { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMoveShooters([FromBody] FaltskylteBulkMoveShootersRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var maxPos = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(Position), 0) FROM FaltskyttePatrolMember WHERE PatrolId = @0", request.TargetPatrolId);

            foreach (var pmId in request.PatrolMemberIds)
            {
                maxPos++;
                await db.ExecuteAsync(
                    "UPDATE FaltskyttePatrolMember SET PatrolId = @0, Position = @1 WHERE Id = @2",
                    request.TargetPatrolId, maxPos, pmId);
            }

            return Json(new { success = true, moved = request.PatrolMemberIds.Count });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePatrolTime([FromBody] UpdatePatrolTimeRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "UPDATE FaltskyttePatrol SET StartTime = @0 WHERE Id = @1 AND CompetitionId = @2",
                request.StartTime, request.PatrolId, request.CompetitionId);

            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> SearchAvailableShooters(int competitionId, string? query, string? weaponGroup, bool showAll = false)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // Build assigned lookup: (memberId, classPrefix) pairs — a shooter in a C patrol is NOT assigned for A
            var assignedMembers = await db.FetchAsync<FaltskyttePatrolMember>(
                "SELECT pm.* FROM FaltskyttePatrolMember pm INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id WHERE p.CompetitionId = @0",
                competitionId);
            // Use the registry's weapon-class code so A_opt classes form their own bucket
            // and don't collide with plain A in patrol assignment lookups.
            string MemberKey(int memberId, string? memberClass) =>
                memberId + "_" + ShootingClasses.GetWeaponClassCode(memberClass ?? "");

            var assignedPairs = new HashSet<string>(
                assignedMembers.Select(m => MemberKey(m.MemberId, m.ShootingClass)));

            // Build patrol lookup for display: (memberId, weaponGroup) → patrolNumber
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0", competitionId);
            var patrolDict = patrols.ToDictionary(p => p.Id, p => p.PatrolNumber);
            var patrolLookup = new Dictionary<string, int>();
            foreach (var am in assignedMembers)
            {
                if (patrolDict.TryGetValue(am.PatrolId, out var pn))
                    patrolLookup[MemberKey(am.MemberId, am.ShootingClass)] = pn;
            }

            // Parse weapon group into allowed weapon-group codes (e.g. "A+R" → ["A","R"])
            HashSet<string>? allowedGroups = null;
            if (!string.IsNullOrWhiteSpace(weaponGroup))
            {
                allowedGroups = new HashSet<string>(
                    weaponGroup.Split('+').Select(w => w.Trim()).Where(w => w.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }

            var available = registrations
                .Where(r =>
                {
                    if (showAll) return true;
                    return !assignedPairs.Contains(MemberKey(r.MemberId, r.MemberClass));
                })
                .Where(r =>
                {
                    if (allowedGroups == null) return true;
                    var wg = ShootingClasses.GetWeaponClassCode(r.MemberClass ?? "");
                    return !string.IsNullOrEmpty(wg) && allowedGroups.Contains(wg);
                })
                .Select(r => new
                {
                    memberId = r.MemberId,
                    name = r.MemberName ?? "",
                    club = r.MemberClub ?? "",
                    shootingClass = r.MemberClass ?? "",
                    assignedToPatrol = patrolLookup.TryGetValue(MemberKey(r.MemberId, r.MemberClass), out var pn)
                        ? (int?)pn : null
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim().ToLower();
                available = available.Where(a => a.name.ToLower().Contains(q) || a.club.ToLower().Contains(q)).ToList();
            }

            return Json(new { success = true, shooters = available });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishPatrolList([FromBody] PublishPatrolListRequest request)
        {
            if (!await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(request.CompetitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            competition.SetValue("faltskyttePatrolsPublished", request.Publish);
            _contentService.Save(competition);
            _contentService.Publish(competition, new[] { "*" }, -1);

            return Json(new { success = true, published = request.Publish });
        }

        /// <summary>Public endpoint — returns patrols only if published.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPublicPatrols(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var published = competition.HasProperty("faltskyttePatrolsPublished")
                && competition.GetValue<bool>("faltskyttePatrolsPublished");
            if (!published)
                return Json(new { success = true, published = false, patrols = Array.Empty<object>() });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);

            var patrolIds = patrols.Select(p => p.Id).ToList();
            var allMembers = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>(
                    $"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();

            var result = patrols.Select(p => new
            {
                patrolNumber = p.PatrolNumber,
                startTime = p.StartTime,
                weaponGroup = p.WeaponGroup,
                members = allMembers.Where(m => m.PatrolId == p.Id)
                    .Select(m => new {
                        name = m.MemberName,
                        club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                        shootingClass = m.ShootingClass
                    }).ToList()
            }).ToList();

            return Json(new { success = true, published = true, patrols = result });
        }
    }
}
