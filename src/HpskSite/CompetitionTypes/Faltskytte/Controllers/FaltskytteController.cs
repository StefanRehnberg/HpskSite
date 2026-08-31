using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
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
using HpskSite.CompetitionTypes.Common.Utilities;
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
        private readonly FaltskytteShootOffService _shootOffService;
        private readonly IDataProtectionProvider _dataProtectionProvider;
        private readonly StandardMedalMaterializationService _medalMaterialization;

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
            UmbracoStartListRepository startListRepository,
            FaltskytteShootOffService shootOffService,
            IDataProtectionProvider dataProtectionProvider,
            StandardMedalMaterializationService medalMaterialization)
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
            _shootOffService = shootOffService;
            _dataProtectionProvider = dataProtectionProvider;
            _medalMaterialization = medalMaterialization;
        }

        // ── Authorization helpers ───────────────────────────────────

        private async Task<bool> IsAuthorizedForCatalog()
        {
            if (await _adminAuthorizationService.IsCurrentUserAdminAsync()) return true;
            var regions = await _adminAuthorizationService.GetManagedRegions();
            return regions.Any();
        }

        /// <summary>
        /// May the current member operate THIS Fältskytte competition — patrols, station config,
        /// results, shoot-offs? Gates 35 endpoints in this controller.
        ///
        /// ⚠️ FIXED 2026-08-25. It used to read:
        ///     var regions = await GetManagedRegions();
        ///     if (regions.Any()) return true;   // "Regional admins can manage any competition"
        /// — i.e. a regional admin of ANY krets could manage EVERY Fältskytte competition in the
        /// country, while every other surface asks about *this* competition's region. Confirmed a bug
        /// with Stefan (2026-08-25): not everyone should reach every fältskytte competition.
        ///
        /// Delegates to <see cref="AdminAuthorizationService.HasCompetitionStaffAccessAsync"/>, which
        /// already answers exactly this question and — the part that matters — handles BOTH host
        /// shapes: a club-hosted competition (clubId set; IsClubAdminForClub folds in that club's
        /// regional admins) and a region-hosted one (clubId unset, regionalFederation set — the SM
        /// shape). Writing the host check out by hand is what has gone wrong here repeatedly, always
        /// by handling only one of the two shapes.
        ///
        /// Grants the same set as before minus the hole: site admin, competition manager (incl.
        /// Bemanning app access), club admin or Skjutledare of the organising club, or the regional
        /// admin of the hosting krets. Skjutledare stay in deliberately — running the firing line is
        /// their role, and they were already included here.
        /// </summary>
        private Task<bool> IsAuthorizedForCompetition(int competitionId) =>
            _adminAuthorizationService.HasCompetitionStaffAccessAsync(competitionId);

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
        /// Authorises a self-service WRITE: staff bypass, otherwise requires
        /// the self-service flag on, the user logged in and in the patrol.
        ///
        /// Cursor logic (cursor = highest station the patrol has reached):
        ///   - Cursor NULL, equal to the requested station, or BEHIND the
        ///     requested station → forward motion (or first save). Cursor is
        ///     advanced to the requested station and the save is allowed.
        ///   - Cursor AHEAD of the requested station → patrol has already moved
        ///     past this one. Reject with a clear "låst"-message; ask staff for
        ///     help to edit an older station.
        /// Staff (handled by the bypass above) can edit any station regardless.
        /// </summary>
        private async Task<(bool Ok, string? Error)> AuthorizeSelfServiceWriteAsync(
            int competitionId, int patrolNumber, int stationNumber)
        {
            if (await IsAuthorizedForCompetition(competitionId)) return (true, null);

            if (!IsSelfServiceEnabledFor(competitionId))
            {
                _logger.LogWarning("Fältskytte self-service write rejected: self-service is not enabled for competition {CompId}", competitionId);
                return (false, "Självservice är inte aktiverat för denna tävling.");
            }

            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0)
            {
                _logger.LogWarning("Fältskytte self-service write rejected: caller is not logged in (competition {CompId})", competitionId);
                return (false, "Du måste vara inloggad.");
            }

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrol = await FaltskytteSelfServiceQueries
                .GetPatrolAsync(db, competitionId, patrolNumber);
            if (patrol == null)
            {
                _logger.LogWarning("Fältskytte self-service write rejected: patrol {PatrolNumber} not found in competition {CompId}", patrolNumber, competitionId);
                return (false, "Patrullen hittades inte.");
            }

            var inPatrol = await FaltskytteSelfServiceQueries
                .IsMemberInPatrolAsync(db, patrol.Id, memberId);
            if (!inPatrol)
            {
                _logger.LogWarning("Fältskytte self-service write rejected: member {MemberId} is not in patrol {PatrolId} (competition {CompId})", memberId, patrol.Id, competitionId);
                return (false, "Du är inte med i denna patrull.");
            }

            // Cursor AHEAD of the requested station → patrol has moved past this one,
            // station is locked for shooters. (Forward motion and same-station are
            // both fine and handled by the auto-advance below.)
            if (patrol.CurrentStation.HasValue && patrol.CurrentStation.Value > stationNumber)
            {
                _logger.LogInformation("Fältskytte self-service write rejected: patrol {PatrolId} cursor is at station {Cursor}, write requested for older station {Requested}", patrol.Id, patrol.CurrentStation.Value, stationNumber);
                return (false,
                    $"Den här stationen är låst eftersom patrullen har gått vidare till station {patrol.CurrentStation.Value}. " +
                    $"Be en funktionär att hjälpa till om resultatet på station {stationNumber} behöver rättas.");
            }

            // Cursor NULL, equal, or behind — natural forward motion. Auto-advance.
            // The UPDATE skips when CurrentStation already equals stationNumber.
            await FaltskytteSelfServiceQueries.AdvanceCursorAsync(db, patrol.Id, stationNumber);
            return (true, null);
        }

        // ── Station Config ──────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetStationConfig(int competitionId)
        {
            // Station layouts are secret — only staff or a logged-in participant
            // (self-service) of this competition may read the config. QR-1's public
            // Förutsättningar page renders server-side, NOT via this endpoint.
            if (!await CanReadStationAsync(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet att se den här tävlingens stationer." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var config = ParseCompetitionConfig(competition);
            // Config's own _scoringMode wins over the competition's mirrored property,
            // which only syncs at Anslut time. See FaltskytteScoringMode.
            var scoringMode = FaltskytteScoringMode.Resolve(config, competition.GetValue<string>("scoringMode"));
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

        /// <summary>
        /// Live, lightweight per-station overview for the "Stationer" tab: config
        /// summary, assigned station chief, last patrol that entered + when, and a
        /// completion ratio. Staff-gated. "Last patrol" uses EnteredAt (immutable
        /// first-entry time) so a late correction doesn't reorder the flow.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStationOverview(int competitionId)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            var config = ParseCompetitionConfig(competition);
            var firstWc = config.WeaponConfigs.Values.FirstOrDefault();
            var stationNumbers = (firstWc?.Stations ?? new List<FaltskytteStationConfig>())
                .Where(s => !s.IsShootOffOnly)
                .Select(s => s.Station)
                .Distinct().OrderBy(n => n).ToList();

            var managers = new Dictionary<string, StationManagerDto>();
            var mgrJson = competition.GetValue<string>("faltskytteStationManagers");
            if (!string.IsNullOrWhiteSpace(mgrJson))
            {
                try { managers = JsonConvert.DeserializeObject<Dictionary<string, StationManagerDto>>(mgrJson) ?? new(); }
                catch { managers = new(); }
            }

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                "WHERE CompetitionId = @0", competitionId);
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0", competitionId);
            var patrolIds = patrols.Select(p => p.Id).ToList();
            // Expected roster = total patrol-member rows (every (member,class) shoots every station once).
            var expectedCount = patrolIds.Any()
                ? await db.ExecuteScalarAsync<int>(
                    $"SELECT COUNT(1) FROM FaltskyttePatrolMember WHERE PatrolId IN ({string.Join(",", patrolIds)})")
                : 0;

            var byStation = allResults.GroupBy(r => r.StationNumber).ToDictionary(g => g.Key, g => g.ToList());

            // Resolve operator names once per member (a handful of stations → memoise to avoid repeat lookups).
            var nameCache = new Dictionary<int, string>();
            string ResolveName(int memberId)
            {
                if (memberId <= 0) return "";
                if (nameCache.TryGetValue(memberId, out var cached)) return cached;
                var nm = _memberService.GetById(memberId)?.Name ?? "";
                nameCache[memberId] = nm;
                return nm;
            }

            var stations = stationNumbers.Select(n =>
            {
                var sample = config.WeaponConfigs.Values
                    .Select(wc => wc.Stations.FirstOrDefault(s => s.Station == n))
                    .FirstOrDefault(st => st != null);
                // Small figure thumbnails so the card is instantly recognisable as the real station.
                var figures = (sample?.TargetGroups ?? new List<FaltskytteTargetGroup>())
                    .SelectMany(tg => tg.Figures)
                    .Select(f => new { imageUrl = f.ImageUrl, behavior = f.Behavior, isPoangmal = f.IsPoangmal })
                    .ToList();

                FaltskytteResultEntry? last = null;
                FaltskytteResultEntry? lastActive = null;
                int entryCount = 0, distinctPatrols = 0;
                if (byStation.TryGetValue(n, out var rows) && rows.Count > 0)
                {
                    last = rows.OrderByDescending(r => r.EnteredAt).First();
                    // "Senast aktiv" = most recent server contact. LastModified moves on corrections/re-saves,
                    // so a chief re-saving an older row still reads as active now (unlike EnteredAt, which is
                    // deliberately frozen at first registration for "Senaste patrull").
                    lastActive = rows.OrderByDescending(r => r.LastModified).First();
                    entryCount = rows.Count;
                    distinctPatrols = rows.Select(r => r.PatrolNumber).Distinct().Count();
                }

                managers.TryGetValue(n.ToString(), out var mgr);

                return new
                {
                    station = n,
                    figureCount = figures.Count,
                    figures,
                    managerName = mgr?.Name ?? "",
                    managerPhone = mgr?.Phone ?? "",
                    managerMemberId = mgr?.MemberId,
                    lastPatrolNumber = last?.PatrolNumber,
                    lastEnteredAt = last?.EnteredAt,
                    // Who last registered/corrected here + when — a battery-free, cellular-safe proxy for
                    // "who's manning the station" (the field device sends nothing extra; this is derived from
                    // saves it makes anyway). Rendered as "Senast aktiv", not live presence.
                    lastActiveBy = lastActive != null ? ResolveName(lastActive.EnteredBy) : "",
                    lastActiveAt = lastActive?.LastModified,
                    // Freshness computed server-side in UTC (both sides UtcNow-based) so the "aktiv nyss"
                    // colour can't be thrown off by DateTime serialization/timezone quirks. -1 = no activity yet.
                    lastActiveMinsAgo = lastActive != null ? (int)Math.Max(0, (DateTime.UtcNow - lastActive.LastModified).TotalMinutes) : -1,
                    entryCount,
                    distinctPatrols
                };
            }).ToList();

            // The attached standalone config id (if any) — lets the tab open the configurator.
            int? attachedConfigId = null;
            try
            {
                var rawCfg = competition.GetValue<string>("stationConfig");
                if (!string.IsNullOrWhiteSpace(rawCfg))
                    attachedConfigId = Newtonsoft.Json.Linq.JObject.Parse(rawCfg).Value<int?>("_attachedConfigId");
            }
            catch { /* inline/legacy config without an attached id */ }

            return Json(new { success = true, expectedCount, attachedConfigId, stations });
        }

        /// <summary>Saves the per-station chief assignments (JSON keyed by station number).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveStationManagers([FromBody] SaveStationManagersRequest request)
        {
            if (request == null || !await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var competition = _contentService.GetById(request.CompetitionId);
            if (competition == null)
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            competition.SetValue("faltskytteStationManagers", request.ManagersJson ?? "");
            _contentService.Save(competition);
            _contentService.Publish(competition, new[] { "*" }, -1);

            return Json(new { success = true, message = "Stationschefer sparade." });
        }

        /// <summary>Returns a member's name + phone for the station-chief picker autofill (staff-gated).</summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberContact(int competitionId, int memberId)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var m = _memberService.GetById(memberId);
            if (m == null)
                return Json(new { success = false, message = "Medlem hittades inte." });

            var phone = m.HasProperty("phoneNumber") ? (m.GetValue<string>("phoneNumber") ?? "") : "";
            return Json(new { success = true, name = m.Name ?? "", phone });
        }

        /// <summary>
        /// Public patrol-list state for the send-off screen (/patrullista): patrols
        /// (ordered by number) + members + DepartedAt. No auth — the wall screen and
        /// the starters' phones both poll this; the list is not secret. Only returns
        /// patrols once published.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPatrolListState(int competitionId)
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
            var members = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>(
                    $"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();

            var result = patrols.Select(p => new
            {
                patrolId = p.Id,
                patrolNumber = p.PatrolNumber,
                weaponGroup = string.IsNullOrEmpty(p.WeaponGroup) ? "?" : p.WeaponGroup,
                startTime = p.StartTime,
                label = p.Label,
                departedAt = p.DepartedAt,
                held = p.Held,
                members = members.Where(m => m.PatrolId == p.Id).Select(m => new
                {
                    patrolMemberId = m.Id,
                    name = m.MemberName,
                    club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                    shootingClass = m.ShootingClass,
                    status = m.Status
                }).ToList()
            }).ToList();

            return Json(new { success = true, published = true, patrols = result });
        }

        /// <summary>Marks/unmarks a patrol as sent off from the start line (staff-gated).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPatrolDeparted([FromBody] SetPatrolDepartedRequest request)
        {
            if (request == null || !await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                "WHERE Id = @0 AND CompetitionId = @1", request.PatrolId, request.CompetitionId);
            if (patrol == null)
                return Json(new { success = false, message = "Patrullen hittades inte." });

            object when = request.Departed ? DateTime.UtcNow : (object)DBNull.Value;
            await db.ExecuteAsync("UPDATE FaltskyttePatrol SET DepartedAt = @0 WHERE Id = @1", when, request.PatrolId);
            return Json(new { success = true, departed = request.Departed });
        }

        /// <summary>
        /// Starter "hold/wait": parks/unparks a patrol (staff-gated). A held patrol is skipped in the
        /// send-off screen's next-up calc so the starter can send the next patrol; it's NOT departed.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPatrolHeld([FromBody] SetPatrolHeldRequest request)
        {
            if (request == null || !await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var patrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                "WHERE Id = @0 AND CompetitionId = @1", request.PatrolId, request.CompetitionId);
            if (patrol == null)
                return Json(new { success = false, message = "Patrullen hittades inte." });

            await db.ExecuteAsync("UPDATE FaltskyttePatrol SET Held = @0 WHERE Id = @1", request.Held ? 1 : 0, request.PatrolId);
            return Json(new { success = true, held = request.Held });
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
                        HasResult = completedKeys.Contains(m.MemberId + "_" + m.ShootingClass),
                        Status = m.Status
                    }).ToList(),
                    CompletedCount = members.Count(m => completedKeys.Contains(m.MemberId + "_" + m.ShootingClass))
                };
            }).ToList();

            // Build per-weapon-class station configs for this station number
            // Tävlingstyp from the config first (the property is a stale-able mirror).
            var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.GetValue<string>("scoringMode"));
            var wcStations = new Dictionary<string, FaltskytteStationConfig>();
            foreach (var kvp in competitionConfig.WeaponConfigs)
            {
                var st = kvp.Value.Stations.FirstOrDefault(s => s.Station == stationNumber);
                if (st != null) wcStations[kvp.Key] = st;
            }
            // Station name: first non-empty across weapon classes (uniform in simple mode).
            var stationName = wcStations.Values
                .Select(s => s.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            return Json(new
            {
                success = true,
                data = new FaltskytteStationView
                {
                    CompetitionId = competitionId,
                    StationNumber = stationNumber,
                    StationName = stationName,
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
                // writes when the competition has self-service on, the writer is logged in and
                // in the patrol, and the patrol's cursor is either NULL (first save for this
                // patrol) or already on this station. The cursor is auto-advanced inside the
                // auth check, so the save is self-contained — no separate AdvancePatrolCursor
                // round-trip is required. A patrol that's moved on to a later station returns
                // a specific "låst"-message so the shooter knows why.
                var (ok, authError) = await AuthorizeSelfServiceWriteAsync(
                    request.CompetitionId, request.PatrolNumber, request.StationNumber);
                if (!ok)
                    return Json(new FaltskylteSaveResultResponse { Success = false, Message = authError ?? "Du har inte behörighet." });

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
        public async Task<IActionResult> AnalyzeFaltskytteMerges(int competitionId, bool subCompetitionOnly = false)
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
                IEnumerable<FaltskytteResultEntry> filteredResults = allResults;
                if (subCompetitionOnly)
                {
                    var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                    var subCompMemberIds = new HashSet<int>(
                        registrations.Where(r => r.IsSubCompetition).Select(r => r.MemberId));
                    filteredResults = allResults.Where(r => subCompMemberIds.Contains(r.MemberId));
                }
                var classCounts = filteredResults
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g => g.Key)
                    .GroupBy(k => HpskSite.Models.ShootingClasses.GetById(k.ShootingClass)?.Name ?? k.ShootingClass)
                    .ToDictionary(g => g.Key, g => g.Count());

                var service = new ClassMergingService();
                var analysis = service.AnalyzeFromCounts(classCounts, compType);

                // Load saved merge config — sub-comp lives on the competitionResult node,
                // main lives on the competition itself (legacy location).
                string savedConfig;
                if (subCompetitionOnly)
                {
                    var resultPageNode = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                    savedConfig = resultPageNode != null && resultPageNode.HasProperty("subCompetitionMergeConfig")
                        ? resultPageNode.GetValue<string>("subCompetitionMergeConfig") ?? ""
                        : "";
                }
                else
                {
                    savedConfig = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";
                }

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

                var competitionConfig = ParseCompetitionConfig(competition);
                // Tävlingstyp from the config first (the property is a stale-able mirror) —
                // this decides Normalfält "32/22" vs Poängfält "54 p" scoring below.
                var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.GetValue<string>("scoringMode"));
                // For result display, use the first available weapon class config to determine station count.
                // Stations marked IsShootOffOnly are NOT counted — they don't contribute to the qualification
                // ranking and they're filtered out everywhere else (admin links, public station card).
                var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
                var stationCount = firstWcConfig?.Stations.Count(s => !s.IsShootOffOnly) ?? 0;
                var shootOffOnlyStationNumbers = (firstWcConfig?.Stations
                    .Where(s => s.IsShootOffOnly)
                    .Select(s => s.Station)
                    .ToHashSet()) ?? new HashSet<int>();

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                    "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competitionId);

                // Belt-and-braces: even if any legacy FaltskytteResultEntry rows exist for a
                // station that's now marked IsShootOffOnly, exclude them from the qualification
                // totals. Shoot-off scores live in FaltskytteShootOffEntry.
                if (shootOffOnlyStationNumbers.Count > 0)
                    allResults = allResults.Where(r => !shootOffOnlyStationNumbers.Contains(r.StationNumber)).ToList();

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

                // Locate the competitionResult child node — used both for the sub-comp's
                // own merge config / official flag and as a fallback when nothing was passed
                // in. The node may not exist yet if results have never been published.
                var resultPageNode = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                // Build merge lookup from config (if provided)
                var mergeLookup = new Dictionary<string, string>(); // source class → combined group name
                if (string.IsNullOrEmpty(mergeConfig))
                {
                    // Sub-comp reads from its own slot on the competitionResult node;
                    // main reads from the competition's mergeConfig (existing pattern).
                    if (subCompetitionOnly)
                    {
                        mergeConfig = resultPageNode != null && resultPageNode.HasProperty("subCompetitionMergeConfig")
                            ? resultPageNode.GetValue<string>("subCompetitionMergeConfig") ?? ""
                            : "";
                    }
                    else
                    {
                        mergeConfig = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";
                    }
                }
                if (!string.IsNullOrEmpty(mergeConfig))
                {
                    try
                    {
                        var mergeActions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfig);
                        if (mergeActions != null)
                        {
                            // Use union-find so multi-source merges (C2 Dam + C3 Dam + C Vet Y all → C2)
                            // collapse into ONE combined group, matching the Precision fix.
                            var unified = ClassMergingService.BuildMergeGroupLookup(mergeActions);
                            foreach (var kv in unified)
                                mergeLookup[kv.Key] = kv.Value;
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

                // Standard medals are calculated on whatever shooter set we have — for the
                // Deltävling that's the (smaller) filtered subset, so 1/9 silver and 1/3 bronze
                // quotas are computed over the Deltävling participants only. Gated on
                // isAwardingStandardMedals AND !isClubOnly per BR-PS.1.3 (club competitions
                // never award standard medals). When either gate fails the StandardMedal field
                // on each shooter stays empty and the views drop the Std column.
                var isAwardingStandardMedals = competition.GetValue<bool>("isAwardingStandardMedals");
                var isClubOnly = competition.GetValue<bool>("isClubOnly");
                var competitionScope = competition.GetValue<string>("competitionScope") ?? "";
                // SHB 2026: standard medals are split per C-category at SM AND Landsdelsmästerskap
                // (pre-existing hardcode; KrM/KM use the merged C grouping). Keep the SM-only split here
                // because the StandardMedalService's `isChampionship` flag specifically gates the C-split.
                var isSmOrLdm = competitionScope == CompetitionScopeHelper.SvensktMasterskap
                             || competitionScope == CompetitionScopeHelper.Landsdelsmasterskap;
                if (isAwardingStandardMedals && !isClubOnly)
                {
                    var medalService = new Services.FaltskytteStandardMedalService();
                    medalService.CalculateStandardMedals(shooterResults, scoringMode, stationCount, isSmOrLdm);
                }

                // ── Särskjutning (championship-only, medal places 1–3) ──
                // Replaces the old SM+LDM hardcode with the unified IsChampionshipScope helper
                // (FR-205, applies to KrM and KM as well).
                if (CompetitionScopeHelper.IsChampionshipScope(competitionScope))
                {
                    var competitionType = competition.GetValue<string>("competitionType") ?? "Faltskytte";
                    var comparer = FaltskytteShootOffService.ComparerFor(competitionType, scoringMode);
                    var shootOffEntries = await _shootOffService.GetEntriesForCompetitionAsync(competitionId);
                    var entriesByMember = shootOffEntries.ToLookup(e => e.MemberId);

                    foreach (var classGroup in classGroups)
                    {
                        var tied = FaltskytteShootOffService.DetectTiedMedalGroups(
                            classGroup.Shooters, scoringMode, competitionType);
                        if (tied.Count == 0) continue;

                        FaltskytteShootOffService.ApplyShootOffOverride(
                            classGroup.Shooters, tied, entriesByMember, comparer);

                        classGroup.TiedMedalGroups = tied;

                        foreach (var g in tied)
                        {
                            if (!g.Resolved || g.Shooters.Count < 2) continue;
                            var ordered = g.Shooters
                                .Where(s => s.Rounds != null && s.Rounds.Count > 0)
                                .ToList();
                            if (ordered.Count < 2) continue;
                            var medalNouns = FaltskytteShootOffService.MedalNounsForRange(g.FirstRank, g.LastRank);
                            var parts = ordered.Select(s =>
                            {
                                var lastRound = s.Rounds.OrderByDescending(r => r.Round).First();
                                return $"{s.Name} {lastRound.Display}";
                            });
                            classGroup.ShootOffNotes.Add(
                                $"Särskjutning avgjorde {medalNouns}: {string.Join(" vs ", parts)}");
                        }
                    }
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

                // IsOfficial reflects whichever flag is relevant for this payload:
                //   sub-comp → resultPageNode.subCompetitionIsOfficial
                //   main    → competition.faltskytteResultsOfficial
                bool isOfficialForPayload;
                if (subCompetitionOnly)
                {
                    isOfficialForPayload = resultPageNode != null
                        && resultPageNode.HasProperty("subCompetitionIsOfficial")
                        && resultPageNode.GetValue<bool>("subCompetitionIsOfficial");
                }
                else
                {
                    isOfficialForPayload = competition.HasProperty("faltskytteResultsOfficial")
                        && competition.GetValue<bool>("faltskytteResultsOfficial");
                }

                var subCompetitionName = competition.HasProperty("subCompetitionName")
                    ? competition.GetValue<string>("subCompetitionName") ?? ""
                    : "";

                return Json(new
                {
                    success = true,
                    results = new FaltskylteFinalResults
                    {
                        CompetitionId = competitionId,
                        UpdatedAt = DateTime.Now,
                        IsOfficial = isOfficialForPayload,
                        ScoringMode = scoringMode,
                        StationCount = stationCount,
                        Config = competitionConfig,
                        ClassGroups = classGroups,
                        CompetitionName = competitionName,
                        CompetitionDate = competitionDateStr,
                        OrganizerName = organizerName,
                        IsSubCompetition = subCompetitionOnly,
                        SubCompetitionName = subCompetitionName,
                        IsAwardingStandardMedals = isAwardingStandardMedals && !isClubOnly
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

            if (request.IsSubCompetition)
            {
                // Deltävling merge config lives on the competitionResult node so it stays
                // bundled with the published Deltävling state. Create the node lazily —
                // we don't want SaveMergeConfig to require Publish to run first.
                var resultPageNode = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPageNode == null)
                {
                    resultPageNode = _contentService.Create("Resultat", competition.Id, "competitionResult");
                    resultPageNode.SetValue("resultType", "Final Results");
                }
                if (resultPageNode.HasProperty("subCompetitionMergeConfig"))
                {
                    resultPageNode.SetValue("subCompetitionMergeConfig", request.MergeConfig ?? "");
                    _contentService.Save(resultPageNode);
                    _contentService.Publish(resultPageNode, new[] { "*" }, -1);
                }
                else
                {
                    _logger.LogWarning("competitionResult node for comp {CompId} missing 'subCompetitionMergeConfig' property — Deltävling merge config not saved. Add this property to the competitionResult document type.", request.CompetitionId);
                }
            }
            else if (competition.HasProperty("mergeConfig"))
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

        // ── Särskjutning endpoints ──────────────────────────────────────────
        // Shoot-off shot entries + station config storage for Fältskytte/MagnumFält
        // championship medal tie resolution. See FaltskytteShootOffService for the
        // pluggable per-variation resolver.

        [HttpGet]
        public async Task<IActionResult> GetFaltskytteShootOffStatus(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltigt tävlings-ID." });
                if (!await IsAuthorizedForCompetition(competitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                // Reuse the typed FaltskylteFinalResults built by GetFaltskytteResults so the
                // detection + override logic stays in one place. We deserialize via Newtonsoft
                // through a typed wrapper to dodge any camelCase/PascalCase JSON-casing fragility.
                var resultsResponse = await GetFaltskytteResults(competitionId, null, subCompetitionOnly);
                if (resultsResponse is not JsonResult jr || jr.Value == null)
                    return resultsResponse;

                var raw = JsonConvert.SerializeObject(jr.Value);
                var wrapper = JsonConvert.DeserializeObject<FaltskytteResultsWrapper>(raw,
                    new JsonSerializerSettings { ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver() });
                if (wrapper == null || !wrapper.success || wrapper.results == null)
                    return jr;

                var classGroups = wrapper.results.ClassGroups
                    .Where(cg => cg.TiedMedalGroups != null && cg.TiedMedalGroups.Count > 0)
                    .Select(cg => new
                    {
                        className = cg.ClassName,
                        displayClassName = cg.DisplayClassName,
                        groups = cg.TiedMedalGroups
                    })
                    .ToList<object>();

                return Json(new { success = true, classGroups });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetFaltskytteShootOffStatus failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Typed wrapper for the GetFaltskytteResults Json() payload so we can pull out
        /// TiedMedalGroups without dynamic property access.</summary>
        private class FaltskytteResultsWrapper
        {
            public bool success { get; set; }
            public FaltskylteFinalResults? results { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> GetFaltskytteShootOffConfig(int competitionId, bool subCompetitionOnly = false)
        {
            try
            {
                if (!await IsAuthorizedForCompetition(competitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                var propName = subCompetitionOnly ? "subCompetitionFaltskytteShootOffConfig" : "faltskytteShootOffConfig";
                var json = (resultPage != null && resultPage.HasProperty(propName))
                    ? resultPage.GetValue<string>(propName) ?? ""
                    : "";
                return Json(new { success = true, config = json });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetFaltskytteShootOffConfig failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveFaltskytteShootOffConfig([FromBody] FaltskytteShootOffConfigRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await IsAuthorizedForCompetition(request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

                // Lazy-create the result page (mirrors SaveMergeConfig behaviour).
                var resultPage = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
                if (resultPage == null)
                {
                    resultPage = _contentService.Create("Resultat", competition.Id, "competitionResult");
                    resultPage.SetValue("resultType", "Final Results");
                }

                var propName = request.IsSubCompetition
                    ? "subCompetitionFaltskytteShootOffConfig"
                    : "faltskytteShootOffConfig";
                if (!resultPage.HasProperty(propName))
                {
                    _logger.LogWarning("competitionResult node missing '{Prop}' property — shoot-off config not saved.", propName);
                    return Json(new { success = false, message = $"Egenskapen '{propName}' saknas på doctypen competitionResult. Lägg till den (Textarea) i Umbraco-backoffice." });
                }

                resultPage.SetValue(propName, request.ConfigJson ?? "");
                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" }, -1);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveFaltskytteShootOffConfig failed for competition {CompetitionId}", request?.CompetitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveFaltskytteShootOffEntry([FromBody] FaltskytteShootOffEntryRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0
                    || string.IsNullOrWhiteSpace(request.ShootingClass) || request.Round <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await IsAuthorizedForCompetition(request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var actingMemberId = await GetCurrentMemberIdAsync();
                var (ok, err) = await _shootOffService.SaveEntryAsync(
                    request.CompetitionId, request.MemberId, request.ShootingClass, request.Round,
                    request.Hits, request.Figures, request.HitDistribution,
                    request.TiebreakerScore, request.PoangmalScores,
                    actingMemberId);
                if (!ok) return Json(new { success = false, message = err ?? "Kunde inte spara." });
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveFaltskytteShootOffEntry failed");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteFaltskytteShootOffEntry([FromBody] FaltskytteShootOffDeleteRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.MemberId <= 0
                    || string.IsNullOrWhiteSpace(request.ShootingClass) || request.Round <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await IsAuthorizedForCompetition(request.CompetitionId))
                    return Json(new { success = false, message = "Du har inte behörighet." });

                var (ok, err) = await _shootOffService.DeleteEntryAsync(
                    request.CompetitionId, request.MemberId, request.ShootingClass, request.Round);
                return Json(new { success = ok, message = err });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteFaltskytteShootOffEntry failed");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
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

            // Main publish requires the legacy flag on the competition; sub-comp publish
            // only writes to the competitionResult node so we don't require that flag.
            if (!request.IsSubCompetition && !competition.HasProperty("faltskytteResultsOfficial"))
                return Json(new { success = false, message = "Egenskapen 'faltskytteResultsOfficial' saknas på tävlingens dokumenttyp. Lägg till den i Umbraco backoffice (True/False)." });

            if (!request.IsSubCompetition)
            {
                competition.SetValue("faltskytteResultsOfficial", request.IsOfficial);
                _contentService.Save(competition);
                _contentService.Publish(competition, new[] { "*" }, -1);

                // Materialize won Standard medals into the ledger (Fältskytte computes medals
                // live, so we compute them here rather than from a stored snapshot).
                await MaterializeFaltskytteMedalsAsync(competition, request.IsOfficial);
            }

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

            if (request.IsSubCompetition)
            {
                if (!resultPage.HasProperty("subCompetitionIsOfficial"))
                    return Json(new { success = false, message = "Egenskapen 'subCompetitionIsOfficial' saknas på dokumenttypen competitionResult. Lägg till den i Umbraco backoffice (True/False)." });
                resultPage.SetValue("subCompetitionIsOfficial", request.IsOfficial);
            }
            else
            {
                resultPage.SetValue("isOfficial", request.IsOfficial);
            }
            resultPage.SetValue("lastUpdated", DateTime.Now);
            _contentService.Save(resultPage);
            _contentService.Publish(resultPage, new[] { "*" }, -1);

            // Phase 2 auto-trigger: notify registered shooters that results are published. Opt-in per
            // competition (autoNotifyParticipants, default off), main publish only, fire-and-forget.
            if (!request.IsSubCompetition && request.IsOfficial && competition.GetValue<bool>("autoNotifyParticipants"))
            {
                try
                {
                    var notifier = HttpContext?.RequestServices?
                        .GetService(typeof(HpskSite.Services.Messaging.ParticipantNotificationService))
                        as HpskSite.Services.Messaging.ParticipantNotificationService;
                    notifier?.Notify(request.CompetitionId, "All", null,
                        "Resultatlistan är nu publicerad.", "Normal", 0, "");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-notify participants failed for competition {CompetitionId}", request.CompetitionId);
                }
            }

            return Json(new { success = true });
        }

        /// <summary>
        /// Materialize Fältskytte/MagnumFält Standard medals into the Standardmedalj ledger.
        /// On publish (official=true) the medals are computed live and upserted; on un-publish
        /// the on-site awards are removed. Never blocks the publish flow.
        /// </summary>
        private async Task MaterializeFaltskytteMedalsAsync(Umbraco.Cms.Core.Models.IContent competition, bool isOfficial)
        {
            try
            {
                if (!isOfficial)
                {
                    await _medalMaterialization.RemoveOnSiteForCompetitionAsync(competition.Id);
                    return;
                }

                var discipline = competition.GetValue<string>("competitionType") ?? StandardMedals.Faltskytte;
                var competitionDate = competition.GetValue<DateTime?>("competitionDate");
                var year = competitionDate?.Year ?? DateTime.Now.Year;
                var competitionName = competition.GetValue<string>("competitionName");
                if (string.IsNullOrWhiteSpace(competitionName)) competitionName = competition.Name;

                var medals = await ComputeFaltskytteOnSiteMedalsAsync(competition);
                await _medalMaterialization.UpsertOnSiteMedalsAsync(
                    competition.Id, discipline, year, competitionName, competitionDate, medals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to materialize Fältskytte standard medals for competition {CompetitionId}", competition.Id);
            }
        }

        /// <summary>
        /// Compute the won Standard medals for a Fältskytte/MagnumFält competition. Mirrors the
        /// medal computation in GetFaltskytteResults / FaltskytteStatsService: medals are assigned
        /// on the flat per-(member,class) result list (class merging is display-only and does not
        /// affect medal assignment). Returns empty when medals aren't awarded for this competition.
        /// </summary>
        private async Task<List<OnSiteMedal>> ComputeFaltskytteOnSiteMedalsAsync(Umbraco.Cms.Core.Models.IContent competition)
        {
            var result = new List<OnSiteMedal>();

            // BR-PS.1.3: club competitions never award standard medals.
            if (!competition.GetValue<bool>("isAwardingStandardMedals") || competition.GetValue<bool>("isClubOnly"))
                return result;

            var competitionConfig = ParseCompetitionConfig(competition);
            // Tävlingstyp from the config first (the property is a stale-able mirror) —
            // standardmedalj thresholds differ between Normalfält and Poängfält.
            var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.GetValue<string>("scoringMode"));
            var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
            var stationCount = firstWcConfig?.Stations.Count(s => !s.IsShootOffOnly) ?? 0;
            var shootOffOnlyStationNumbers = (firstWcConfig?.Stations
                .Where(s => s.IsShootOffOnly).Select(s => s.Station).ToHashSet()) ?? new HashSet<int>();

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competition.Id);
            if (shootOffOnlyStationNumbers.Count > 0)
                allResults = allResults.Where(r => !shootOffOnlyStationNumbers.Contains(r.StationNumber)).ToList();
            if (!allResults.Any()) return result;

            var shooterResults = allResults
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g =>
                {
                    var stationResults = g.OrderBy(r => r.StationNumber)
                        .Select(r => new FaltskytteStationResult
                        {
                            StationNumber = r.StationNumber,
                            Hits = r.Hits,
                            Figures = r.Figures,
                            TiebreakerScore = r.TiebreakerScore
                        }).ToList();
                    return new FaltskytteShooterResult
                    {
                        MemberId = g.Key.MemberId,
                        ShootingClass = g.Key.ShootingClass,
                        Stations = stationResults,
                        TotalHits = stationResults.Sum(s => s.Hits),
                        TotalFigures = stationResults.Sum(s => s.Figures),
                        TotalPoints = stationResults.Sum(s => s.Points),
                        TotalTiebreakerScore = stationResults.Where(s => s.TiebreakerScore.HasValue).Sum(s => s.TiebreakerScore!.Value)
                    };
                }).ToList();

            var scope = competition.GetValue<string>("competitionScope") ?? "";
            var isSmOrLdm = scope == CompetitionScopeHelper.SvensktMasterskap
                         || scope == CompetitionScopeHelper.Landsdelsmasterskap;
            new Services.FaltskytteStandardMedalService().CalculateStandardMedals(shooterResults, scoringMode, stationCount, isSmOrLdm);

            foreach (var s in shooterResults)
            {
                if (StandardMedals.IsMedal(s.StandardMedal))
                    result.Add(new OnSiteMedal(s.MemberId, s.ShootingClass, s.StandardMedal!));
            }
            return result;
        }

        /// <summary>
        /// One-shot backfill: creates the missing "Resultat" competitionResult child page
        /// for any Faltskytte/MagnumFalt competition that has faltskytteResultsOfficial=true
        /// but was published before the publish-flow fix and therefore lacks the page node.
        /// Idempotent — safe to run multiple times. Site-admin only.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BackfillResultPages()
        {
            if (!await _adminAuthorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast sajtadmin." });

            var candidates = UmbracoContext.Content.GetAtRoot()
                .SelectMany(r => r.DescendantsOrSelf())
                .Where(c => c.ContentType.Alias == "competition")
                .Where(c =>
                {
                    var t = c.Value<string>("competitionType") ?? "";
                    return t == "Faltskytte" || t == "MagnumFalt";
                })
                .Where(c => c.Value<bool>("faltskytteResultsOfficial"))
                .ToList();

            int created = 0, alreadyOk = 0, synced = 0, failed = 0, medalsMaterialized = 0;
            var createdNames = new List<string>();
            var syncedNames = new List<string>();

            foreach (var compNode in candidates)
            {
                try
                {
                    var compContent = _contentService.GetById(compNode.Id);
                    if (compContent == null) { failed++; continue; }

                    // Re-materialize won Standard medals into the ledger (idempotent upsert; the
                    // service gates on isAwardingStandardMedals && !isClubOnly). Backfills comps
                    // published before medal materialization existed, so the persisted ledger —
                    // which drives Min sida, the club-secretary view, and SPSF reporting — is
                    // complete and authoritative.
                    await MaterializeFaltskytteMedalsAsync(compContent, true);
                    medalsMaterialized++;

                    var existing = _contentService.GetPagedChildren(compContent.Id, 0, int.MaxValue, out _)
                        .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

                    if (existing != null)
                    {
                        // Existing page — make sure its isOfficial flag matches the comp's
                        // faltskytteResultsOfficial. They can drift apart if an old publish
                        // ran before the publish-flow fix that writes both in lockstep, or
                        // if a publish of the result page failed silently. Without this
                        // sync, the comp page's "Visa resultat" button stays hidden even
                        // when the admin sees "Officiell" in the results panel.
                        var current = existing.GetValue<bool>("isOfficial");
                        if (!current)
                        {
                            existing.SetValue("isOfficial", true);
                            existing.SetValue("lastUpdated", DateTime.Now);
                            _contentService.Save(existing);
                            _contentService.Publish(existing, new[] { "*" }, -1);
                            synced++;
                            syncedNames.Add(compNode.Name);
                        }
                        else
                        {
                            alreadyOk++;
                        }
                        continue;
                    }

                    var resultPage = _contentService.Create("Resultat", compContent.Id, "competitionResult");
                    resultPage.SetValue("resultType", "Final Results");
                    resultPage.SetValue("isOfficial", true);
                    resultPage.SetValue("lastUpdated", DateTime.Now);
                    _contentService.Save(resultPage);
                    _contentService.Publish(resultPage, new[] { "*" }, -1);

                    created++;
                    createdNames.Add(compNode.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backfill failed for competition {CompetitionId}", compNode.Id);
                    failed++;
                }
            }

            return Json(new
            {
                success = true,
                total = candidates.Count,
                created,
                synced,
                alreadyOk,
                failed,
                medalsMaterialized,
                createdNames,
                syncedNames
            });
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
                    TargetsPerFigure = t.TargetsPerFigure,
                    SizeGroup = t.SizeGroup,
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

        /// <summary>Updates a field target: name, size group, targets-per-figure, and variant names/colors. Admin only.</summary>
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
                if (request.TargetsPerFigure.HasValue) target.TargetsPerFigure = request.TargetsPerFigure.Value;
                if (request.SizeGroup.HasValue) target.SizeGroup = Math.Clamp(request.SizeGroup.Value, 1, 15);
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
                    TargetsPerFigure = request.TargetsPerFigure,
                    SizeGroup = Math.Clamp(request.SizeGroup, 1, 15)
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

            // Find latest patrol for this weapon group with space. DNS'd shooters don't count toward
            // capacity, so a did-not-start seat is reused before spilling to a new patrol.
            var openPatrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                @"SELECT p.* FROM FaltskyttePatrol p
                  WHERE p.CompetitionId = @0 AND p.WeaponGroup = @1
                  AND (SELECT COUNT(*) FROM FaltskyttePatrolMember WHERE PatrolId = p.Id AND (Status IS NULL OR Status <> 'DNS')) < @2
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
            var png = QrPng(url);
            return png == null ? StatusCode(500) : File(png, "image/png");
        }

        /// <summary>
        /// Returns the QR PNG for a station's read-only Förutsättningar page
        /// (QR-1 on the station card). Mints the opaque IDataProtector token,
        /// builds the absolute `/station?t=…` URL, and renders the QR in one call
        /// so the print stays synchronous. Staff-gated — only functionaries print
        /// cards, which stops a shooter minting tokens for stations the patrol
        /// hasn't reached. The token is non-enumerable + non-forgeable; no DB.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStationInfoQr(int competitionId, int stationNumber)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Forbid();
            var protector = _dataProtectionProvider.CreateProtector("Faltskytte.StationInfoQr.v1");
            var token = protector.Protect($"{competitionId}:{stationNumber}");
            var url = $"{Request.Scheme}://{Request.Host}/station?t={Uri.EscapeDataString(token)}";
            var png = QrPng(url);
            return png == null ? StatusCode(500) : File(png, "image/png");
        }

        /// <summary>Renders a QR code PNG for arbitrary URL text; null on failure.</summary>
        private byte[]? QrPng(string url)
        {
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
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating QR code");
                return null;
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
                Label = p.Label,
                Members = allMembers.Where(m => m.PatrolId == p.Id)
                    .Select(m => new FaltskyttePatrolMemberView
                    {
                        PatrolMemberId = m.Id,
                        MemberId = m.MemberId,
                        Position = m.Position,
                        Name = m.MemberName,
                        Club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                        ShootingClass = m.ShootingClass,
                        Status = m.Status
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

        /// <summary>
        /// Mark/unmark a shooter as DNS (did-not-start). The membership row is kept (shooter stays
        /// visible + flagged) but a DNS'd shooter is excluded from the patrol's capacity count, so
        /// their seat frees for a late registration (see JoinNextPatrol / AssignWalkInToPatrol).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetShooterDns([FromBody] SetShooterDnsRequest request)
        {
            if (request == null || !await IsAuthorizedForCompetition(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            object status = request.IsDns ? "DNS" : (object)DBNull.Value;
            await db.ExecuteAsync("UPDATE FaltskyttePatrolMember SET Status = @0 WHERE Id = @1", status, request.PatrolMemberId);
            return Json(new { success = true, isDns = request.IsDns });
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

            // Patrol capacity for "nästa lediga" — from the rolling-start config (default 6). Lets a
            // walk-in fall into a patrol that has a free (or DNS-freed) seat before a new one is made.
            int walkInPatrolSize = 6;
            var walkInCompNode = _contentService.GetById(request.CompetitionId);
            var walkInRsJson = walkInCompNode?.GetValue<string>("rollingStart");
            if (!string.IsNullOrEmpty(walkInRsJson))
            {
                try
                {
                    var rs = Newtonsoft.Json.Linq.JObject.Parse(walkInRsJson);
                    var psTok = rs["patrolSize"];
                    if (psTok != null && int.TryParse(psTok.ToString(), out int ps) && ps > 0)
                        walkInPatrolSize = ps;
                }
                catch { }
            }

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
                    // Fill the lowest-numbered patrol of this group that still has a free (non-DNS)
                    // seat — so a DNS'd shooter's freed slot gets used before spilling to a new patrol.
                    var openPatrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                        @"SELECT p.* FROM FaltskyttePatrol p
                          WHERE p.CompetitionId = @0
                          AND (p.WeaponGroup = @1 OR p.WeaponGroup = '' OR p.WeaponGroup IS NULL)
                          AND (SELECT COUNT(*) FROM FaltskyttePatrolMember WHERE PatrolId = p.Id AND (Status IS NULL OR Status <> 'DNS')) < @2
                          ORDER BY p.PatrolNumber ASC",
                        request.CompetitionId, weaponGroup, walkInPatrolSize);

                    if (openPatrol != null)
                    {
                        patrolId = openPatrol.Id;
                        patrolNumber = openPatrol.PatrolNumber;
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
        private static async Task<List<(int PatrolId, int OldNumber, int NewNumber)>> RenumberAllPatrolsAsync(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId)
        {
            var changes = new List<(int PatrolId, int OldNumber, int NewNumber)>();

            var allPatrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber, Id", competitionId);
            if (allPatrols.Count == 0) return changes;

            // Snapshot the original ordering AND the original numbers before we mutate anything.
            // The numbers are what the result rows carry, so they are needed to migrate those too.
            var ordered = allPatrols.Select(p => (p.Id, p.PatrolNumber)).ToList();

            // Phase 1: lift every row out of the 1..N target range. bump > count
            // guarantees the post-bump range and the target range don't overlap.
            var bump = allPatrols.Count + 1000;
            await db.ExecuteAsync(
                "UPDATE FaltskyttePatrol SET PatrolNumber = PatrolNumber + @0 WHERE CompetitionId = @1",
                bump, competitionId);

            // Phase 2: walk the original order and reassign sequential numbers.
            for (int i = 0; i < ordered.Count; i++)
            {
                var newNumber = i + 1;
                await db.ExecuteAsync(
                    "UPDATE FaltskyttePatrol SET PatrolNumber = @0 WHERE Id = @1",
                    newNumber, ordered[i].Id);
                if (ordered[i].PatrolNumber != newNumber)
                    changes.Add((ordered[i].Id, ordered[i].PatrolNumber, newNumber));
            }

            // ⚠️ FaltskytteResultEntry carries a COPY of the patrol number, and nothing was keeping
            // the two in step. Renumbering left every already-entered result pointing at the number
            // its patrol used to have — silently, because the results themselves stay correct and
            // only the ATTRIBUTION rots. FaltskytteStatsController joins result rows to patrols on
            // PatrolNumber, so the flow statistics then credit each patrol's legs to another
            // patrol's weapon group. Migrate them inside the same operation or not at all.
            //
            // Two-phase for the same reason as above: an old number and a new number can collide
            // mid-walk (patrol 3 becoming 2 while the real 2 has not moved yet), which would merge
            // two patrols' rows under one number with no way back.
            if (changes.Count > 0)
            {
                await db.ExecuteAsync(
                    "UPDATE FaltskytteResultEntry SET PatrolNumber = PatrolNumber + @0 WHERE CompetitionId = @1",
                    bump, competitionId);

                foreach (var (_, oldNumber, newNumber) in changes)
                {
                    await db.ExecuteAsync(
                        "UPDATE FaltskytteResultEntry SET PatrolNumber = @0 WHERE CompetitionId = @1 AND PatrolNumber = @2",
                        newNumber, competitionId, oldNumber + bump);
                }

                // Anything still carrying the bump belonged to a patrol whose number did NOT change,
                // so it comes back down untouched. Doing this as one statement rather than per number
                // keeps a result row for a deleted patrol from being stranded 1000 numbers up.
                // >= not >, so a row that was sitting on 0 (bad legacy data) also comes back.
                await db.ExecuteAsync(
                    "UPDATE FaltskytteResultEntry SET PatrolNumber = PatrolNumber - @0 WHERE CompetitionId = @1 AND PatrolNumber >= @0",
                    bump, competitionId);

                // ⚠️ One case this CANNOT resolve, and it is the very case the renumber exists for:
                // if two patrols shared a number, their result rows are indistinguishable — the rows
                // only ever recorded the number — so both fold into whichever of the two is walked
                // first. There is no information left to split them with. Say so rather than imply
                // the migration is lossless in every state.
            }

            return changes;
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
            var changes = await RenumberAllPatrolsAsync(db, request.CompetitionId);
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskyttePatrol WHERE CompetitionId = @0", request.CompetitionId);

            // The trail. A patrol number is printed on the patrol list, the station cards and the
            // shooters' own copies, so "which patrol was 7 this morning" is a real question after the
            // fact — and it had no answer anywhere. Logged rather than stored: it is a rare
            // administrative act, and a new column would need a migration to say the same thing.
            if (changes.Count > 0)
            {
                _logger.LogInformation(
                    "Renumbered {ChangedCount} of {TotalCount} Fältskytte patrols for competition {CompId}: {Mapping}",
                    changes.Count, count, request.CompetitionId,
                    string.Join(", ", changes.Select(c => $"{c.OldNumber}→{c.NewNumber}")));
            }

            return Json(new
            {
                success = true,
                count,
                changed = changes.Count,
                changes = changes.Select(c => new { oldNumber = c.OldNumber, newNumber = c.NewNumber })
            });
        }

        /// <summary>
        /// What a renumber WOULD do, without doing it. Exists so the confirm dialog can name the
        /// patrols that change instead of asking about "alla patruller" — the numbers are already
        /// printed and handed out, so overwriting them blind is the thing to prevent.
        /// Writes nothing; safe to call on every dialog open.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PreviewRenumberPatrols(int competitionId)
        {
            if (!await IsAuthorizedForCompetition(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            using var db = _umbracoDatabaseFactory.CreateDatabase();

            // Same ORDER BY as RenumberAllPatrolsAsync, so the preview and the act cannot disagree
            // about which patrol becomes which number.
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 ORDER BY PatrolNumber, Id", competitionId);

            var resultCounts = await db.FetchAsync<PatrolResultCount>(
                "SELECT PatrolNumber, COUNT(*) AS ResultCount FROM FaltskytteResultEntry "
                + "WHERE CompetitionId = @0 GROUP BY PatrolNumber", competitionId);
            var resultsByNumber = resultCounts.ToDictionary(r => r.PatrolNumber, r => r.ResultCount);

            var rows = new List<object>();
            for (int i = 0; i < patrols.Count; i++)
            {
                var newNumber = i + 1;
                if (patrols[i].PatrolNumber == newNumber) continue;
                rows.Add(new
                {
                    oldNumber = patrols[i].PatrolNumber,
                    newNumber,
                    weaponGroup = patrols[i].WeaponGroup ?? "",
                    label = patrols[i].Label ?? "",
                    // Results follow the renumber (see RenumberAllPatrolsAsync), but the operator
                    // should still be told the competition is under way before numbers move.
                    resultCount = resultsByNumber.TryGetValue(patrols[i].PatrolNumber, out var rc) ? rc : 0
                });
            }

            // Duplicate numbers are the state the renumber exists to repair, AND the one state where
            // the result rows cannot be told apart afterwards. Surface it as its own warning.
            var duplicateNumbers = patrols
                .GroupBy(x => x.PatrolNumber)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(n => n)
                .ToList();

            return Json(new
            {
                success = true,
                total = patrols.Count,
                changes = rows,
                duplicateNumbers,
                hasResults = resultsByNumber.Values.Any(v => v > 0)
            });
        }

        private class PatrolResultCount
        {
            public int PatrolNumber { get; set; }
            public int ResultCount { get; set; }
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
                "UPDATE FaltskyttePatrol SET StartTime = @0, Label = @1 WHERE Id = @2 AND CompetitionId = @3",
                request.StartTime,
                string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
                request.PatrolId,
                request.CompetitionId);

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

            // Already published? Distinguishes a first publish from a re-publish, which say different
            // things to a shooter ("dina tider finns nu" vs "tiderna kan ha ändrats").
            var wasPublished = competition.HasProperty("faltskyttePatrolsPublished")
                && competition.GetValue<bool>("faltskyttePatrolsPublished");

            competition.SetValue("faltskyttePatrolsPublished", request.Publish);

            // The publish dialog's "stäng självanmälan" checkbox rides along on the save this method
            // already does — a second Save()+Publish() of the same node would only bump the version.
            // A missing doctype property is reported rather than silently no-op'd (SetValue on a
            // property that does not exist writes nothing and returns nothing).
            var gatePropertyMissing = !HpskSite.Services.RegistrationGate.StartListRegistrationGate
                .SetChoice(competition, request.CloseRegistration);

            _contentService.Save(competition);
            var pub = _contentService.Publish(competition, new[] { "*" }, -1);

            // Don't report a false success: if the competition node itself fails to publish (e.g. a
            // mandatory field is empty), the flag stays only on the draft and the public competition
            // page — which reads the PUBLISHED cache — keeps showing "har inte publicerats än".
            if (!pub.Success)
            {
                var invalid = pub.InvalidProperties != null && pub.InvalidProperties.Any()
                    ? " Ogiltiga/obligatoriska fält: " + string.Join(", ", pub.InvalidProperties.Select(p => p.Alias))
                    : "";
                _logger.LogWarning("PublishPatrolList: node publish failed for comp {CompetitionId}: {Result}{Invalid}",
                    request.CompetitionId, pub.Result, invalid);
                return Json(new { success = false, message = $"Patrullistan sparades men tävlingen kunde inte publiceras ({pub.Result}).{invalid} Åtgärda detta i tävlingens inställningar och publicera igen." });
            }

            // Shooter-facing "patrullistan är publicerad / tiderna har ändrats". Opt-in per competition
            // (autoNotifyParticipants), best-effort. Matters because the calendar export from
            // /mitt-schema is a one-shot snapshot — this is what tells a shooter to fetch it again.
            if (request.Publish)
            {
                try
                {
                    if (competition.GetValue<bool>("autoNotifyParticipants"))
                    {
                        var notifier = HttpContext?.RequestServices
                            .GetService(typeof(HpskSite.Services.Messaging.ParticipantNotificationService))
                            as HpskSite.Services.Messaging.ParticipantNotificationService;
                        notifier?.Notify(request.CompetitionId, "All", null,
                            wasPublished
                                ? "Patrullistan har uppdaterats — kontrollera din starttid. Du ser dina tider under Mitt schema."
                                : "Patrullistan är publicerad. Du ser din patrull och starttid under Mitt schema.",
                            null, 0, "Arrangören");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Patrol publish notification failed for comp {CompetitionId}", request.CompetitionId);
                }
            }

            if (gatePropertyMissing)
            {
                return Json(new
                {
                    success = true,
                    published = request.Publish,
                    message = $"Patrullistan publicerades, men anmälan kunde inte stängas: egenskapen '{HpskSite.Services.RegistrationGate.StartListRegistrationGate.PropertyAlias}' saknas på dokumenttypen competition. Lägg till den (True/False) i backoffice."
                });
            }

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
                label = p.Label,
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
