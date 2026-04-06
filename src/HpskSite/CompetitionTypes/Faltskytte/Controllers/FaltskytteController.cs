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

        // ── Authorization helper ────────────────────────────────────

        private async Task<bool> IsAuthorizedForCompetition(int competitionId)
        {
            if (await _adminAuthorizationService.IsCurrentUserAdminAsync())
                return true;
            if (await _adminAuthorizationService.IsCompetitionManager(competitionId))
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
                _contentService.Publish(competition, Array.Empty<string>(), -1);

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
            if (!await IsAuthorizedForCompetition(competitionId))
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
                    Members = members.Select(m => new FaltskyttePatrolMemberView
                    {
                        PatrolMemberId = m.Id,
                        MemberId = m.MemberId,
                        Position = m.Position,
                        Name = m.MemberName,
                        Club = m.ClubName,
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

        // ── Re-shoot Info ───────────────────────────────────────────

        /// <summary>
        /// Gets total re-shoots used by a shooter across all stations in this competition.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetReshootInfo(int competitionId, int memberId, string? shootingClass = null)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(competitionId);
            var maxReshoots = competition?.GetValue<int>("maxReshoots") ?? 0;

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            List<FaltskytteResultEntry> entries;
            if (!string.IsNullOrEmpty(shootingClass))
            {
                // Filter by weapon group prefix (A, B, C, R) — reshoots are per weapon class
                var prefix = shootingClass.Substring(0, 1);
                entries = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 AND MemberId = @1 AND Reshoots > 0 AND LEFT(ShootingClass, 1) = @2",
                    competitionId, memberId, prefix);
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
                if (!await IsAuthorizedForCompetition(request.CompetitionId))
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
        public async Task<IActionResult> GetFaltskytteResults(int competitionId, string? mergeConfig = null)
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
                            Club = member?.ClubName ?? "",
                            ShootingClass = HpskSite.Models.ShootingClasses.GetById(g.Key.ShootingClass)?.Name
                                ?? g.Key.ShootingClass,
                            Stations = stationResults,
                            TotalHits = totalHits,
                            TotalFigures = totalFigures,
                            TotalPoints = totalPoints,
                            TotalTiebreakerScore = totalTiebreaker
                        };
                    }).ToList();

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

                // Calculate standard medals
                var medalService = new Services.FaltskytteStandardMedalService();
                var scope = competition.GetValue<string>("competitionScope") ?? "";
                var isChampionship = scope == "Svenskt Mästerskap" || scope == "Landsdelsmästerskap";
                medalService.CalculateStandardMedals(shooterResults, scoringMode, stationCount, isChampionship);

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
                        ClassGroups = classGroups
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
                _contentService.Publish(competition, Array.Empty<string>());
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
            _contentService.Publish(competition, Array.Empty<string>());

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer kan ändra avstånd." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                    return Json(new { success = false, message = "Endast administratörer." });

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
                        // Standard: use weapon group letter (A, B, C, R)
                        return !string.IsNullOrEmpty(r.MemberClass) ? r.MemberClass.Substring(0, 1) : "";
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
                            var wg = !string.IsNullOrEmpty(r.MemberClass) ? r.MemberClass.Substring(0, 1) : "";
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
                        Club = m.ClubName,
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

            // Remove from any existing patrol with the same weapon group prefix (allows "move" via add)
            // A shooter in a C patrol should not be removed when adding to an A patrol
            var classPrefix = request.ShootingClass.Length > 0 ? request.ShootingClass.Substring(0, 1) : "";
            if (!string.IsNullOrEmpty(classPrefix))
            {
                await db.ExecuteAsync(
                    @"DELETE FROM FaltskyttePatrolMember WHERE MemberId = @0
                      AND LEFT(ShootingClass, 1) = @2
                      AND PatrolId IN (SELECT Id FROM FaltskyttePatrol WHERE CompetitionId = @1)",
                    request.MemberId, request.CompetitionId, classPrefix);
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
            var assignedPairs = new HashSet<string>(
                assignedMembers.Select(m => m.MemberId + "_" + (m.ShootingClass.Length > 0 ? m.ShootingClass.Substring(0, 1) : "")));

            // Build patrol lookup for display: (memberId, classPrefix) → patrolNumber
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0", competitionId);
            var patrolDict = patrols.ToDictionary(p => p.Id, p => p.PatrolNumber);
            var patrolLookup = new Dictionary<string, int>(); // "memberId_prefix" → patrolNumber
            foreach (var am in assignedMembers)
            {
                var key = am.MemberId + "_" + (am.ShootingClass.Length > 0 ? am.ShootingClass.Substring(0, 1) : "");
                if (patrolDict.TryGetValue(am.PatrolId, out var pn))
                    patrolLookup[key] = pn;
            }

            // Parse weapon group into allowed first-letter prefixes (e.g. "A+R" → ["A","R"])
            HashSet<string>? allowedPrefixes = null;
            if (!string.IsNullOrWhiteSpace(weaponGroup))
            {
                allowedPrefixes = new HashSet<string>(
                    weaponGroup.Split('+').Select(w => w.Trim()).Where(w => w.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }

            var available = registrations
                .Where(r =>
                {
                    if (showAll) return true;
                    var prefix = (r.MemberClass ?? "").Length > 0 ? r.MemberClass!.Substring(0, 1) : "";
                    return !assignedPairs.Contains(r.MemberId + "_" + prefix);
                })
                .Where(r =>
                {
                    if (allowedPrefixes == null) return true;
                    var cls = r.MemberClass ?? "";
                    return cls.Length > 0 && allowedPrefixes.Contains(cls.Substring(0, 1));
                })
                .Select(r => new
                {
                    memberId = r.MemberId,
                    name = r.MemberName ?? "",
                    club = r.MemberClub ?? "",
                    shootingClass = r.MemberClass ?? "",
                    assignedToPatrol = patrolLookup.TryGetValue(
                        r.MemberId + "_" + ((r.MemberClass ?? "").Length > 0 ? r.MemberClass!.Substring(0, 1) : ""),
                        out var pn) ? (int?)pn : null
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
            _contentService.Publish(competition, Array.Empty<string>());

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
                    .Select(m => new { name = m.MemberName, club = m.ClubName, shootingClass = m.ShootingClass }).ToList()
            }).ToList();

            return Json(new { success = true, published = true, patrols = result });
        }
    }
}
