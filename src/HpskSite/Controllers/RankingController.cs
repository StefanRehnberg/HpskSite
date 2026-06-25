using HpskSite.Models.Ranking;
using HpskSite.Services;
using HpskSite.Services.Ranking;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Members-only read API for the Träningsmatch ranking (Träningsform).
    /// Identity is resolved server-side per viewer — the client never receives a full name
    /// or avatar for a shooter whose chosen visibility hides it.
    /// </summary>
    public class RankingController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly RankingService _rankingService;
        private readonly RankingSnapshotService _snapshotService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public RankingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            ClubService clubService,
            AdminAuthorizationService authorizationService,
            RankingService rankingService,
            RankingSnapshotService snapshotService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _clubService = clubService;
            _authorizationService = authorizationService;
            _rankingService = rankingService;
            _snapshotService = snapshotService;
            _databaseFactory = databaseFactory;
        }

        /// <summary>
        /// Admin-only: rebuild the ranking snapshot right now (don't wait for the nightly/startup run).
        /// Surfaces the row count and any error — useful to confirm the SQL table exists + eligibility.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> RebuildSnapshot()
        {
            var ctx = await GetViewerAsync();
            if (ctx == null || !ctx.IsAdmin) return Json(new { success = false, message = "Endast administratörer." });
            try
            {
                var rows = await _snapshotService.BuildSnapshotAsync();
                return Json(new { success = true, rows, message = $"Snapshot byggd: {rows} rader." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Admin-only: reconstruct baseline snapshots from historical match data (season start, ~30d, ~7d),
        /// then rebuild today's so the improvement boards + movement get real baselines immediately.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> BackfillSnapshots()
        {
            var ctx = await GetViewerAsync();
            if (ctx == null || !ctx.IsAdmin) return Json(new { success = false, message = "Endast administratörer." });
            try
            {
                var result = await _snapshotService.BackfillAsync();
                return Json(new { success = true, result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetRanking(string discipline = "Precision", string weaponGroup = "C",
            string scope = "club", string? scopeKey = null, string board = "index")
        {
            var ctx = await GetViewerAsync();
            if (ctx == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            // For club/region scope, scopeKey must be one the viewer belongs to (you drill into your own
            // club/region or see national — you can't enumerate arbitrary clubs).
            if (scope == "club" || scope == "region")
            {
                var allowed = scope == "club" ? ctx.ClubIds.Select(c => c.ToString()) : ctx.RegionCodes;
                if (string.IsNullOrEmpty(scopeKey) || !allowed.Contains(scopeKey, StringComparer.OrdinalIgnoreCase))
                {
                    // default to the viewer's primary membership
                    scopeKey = scope == "club" ? ctx.PrimaryClubId.ToString() : ctx.RegionCodes.FirstOrDefault();
                    if (string.IsNullOrEmpty(scopeKey)) scope = "national";
                }
            }

            var boardEnum = board switch
            {
                "improvement30" => RankingBoard.Improvement30,
                "improvementSeason" => RankingBoard.ImprovementSeason,
                _ => RankingBoard.Index
            };

            var label = scope switch
            {
                "national" => "Hela landet",
                "region" => $"Krets {scopeKey}",
                _ => (int.TryParse(scopeKey, out var cid) ? _clubService.GetClubNameById(cid) : null) ?? "Din klubb"
            };

            var result = _rankingService.GetRanking(discipline, weaponGroup, scope, scopeKey, boardEnum,
                ctx.MemberId, ctx.ClubIds, ctx.IsAdmin, label);

            return Json(new { success = true, result });
        }

        [HttpGet]
        public async Task<IActionResult> GetMyRankingContext()
        {
            var ctx = await GetViewerAsync();
            if (ctx == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var lines = _rankingService.GetMyRankingContext(ctx.MemberId);
            return Json(new { success = true, lines });
        }

        [HttpGet]
        public async Task<IActionResult> GetRankingScopes()
        {
            var ctx = await GetViewerAsync();
            if (ctx == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var clubs = ctx.ClubIds.Select(id => new { value = id.ToString(), label = _clubService.GetClubNameById(id) ?? $"Klubb {id}" }).ToList();
            var regions = ctx.RegionCodes.Select(rc => new { value = rc, label = $"Krets {rc}" }).ToList();

            var member = _memberService.GetById(ctx.MemberId);
            var visibility = RankingSnapshotService.NormalizeVisibility(member != null && member.HasProperty("identityVisibility") ? member.GetValue<string>("identityVisibility") : null);
            var showClub = member == null || RankingSnapshotService.ReadShowClubOnBoard(member);

            var availableClasses = _rankingService.GetAvailableClasses()
                .Select(c => new { discipline = c.Discipline, weaponGroup = c.WeaponGroup, count = c.Cnt }).ToList();

            return Json(new { success = true, clubs, regions, primaryClubId = ctx.PrimaryClubId, visibility, showClub, availableClasses });
        }

        [HttpPost]
        public async Task<IActionResult> SetIdentityVisibility([FromBody] IdentityVisibilityRequest request)
        {
            var ctx = await GetViewerAsync();
            if (ctx == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var visibility = request?.Visibility switch { "Halv" => "Halv", "Anonym" => "Anonym", _ => "Full" };
            var showClub = request?.ShowClub ?? true;

            var member = _memberService.GetById(ctx.MemberId);
            if (member == null) return Json(new { success = false, message = "Medlem hittades inte." });

            // Guard: SetValue silently no-ops if the property alias doesn't exist on the member type,
            // which looks exactly like "saving doesn't update". Fail loudly instead.
            if (!member.HasProperty("identityVisibility") || !member.HasProperty("showClubOnBoard"))
                return Json(new { success = false, message = "Egenskaperna 'identityVisibility' och 'showClubOnBoard' saknas på medlemstypen i backoffice." });

            member.SetValue("identityVisibility", visibility);
            member.SetValue("showClubOnBoard", showClub);
            _memberService.Save(member);

            // Apply immediately to today's snapshot rows so the change isn't masked until the nightly run.
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.Execute(
                    "UPDATE RankingSnapshot SET IdentityVisibility = @0, ShowClub = @1 WHERE MemberId = @2 AND SnapshotDate = @3",
                    visibility, showClub, ctx.MemberId, DateTime.Today);
            }
            catch { /* snapshot may not exist yet — nightly run will pick up the member values */ }

            return Json(new { success = true, visibility, showClub });
        }

        /// <summary>Admin-only diagnostic: distribution of today's improvement deltas + samples.</summary>
        [HttpGet]
        public async Task<IActionResult> RankingDiag()
        {
            var ctx = await GetViewerAsync();
            if (ctx == null || !ctx.IsAdmin) return Json(new { success = false, message = "Endast administratörer." });

            using var db = _databaseFactory.CreateDatabase();
            var today = DateTime.Today;
            var seasonStart = new DateTime(today.Year, 1, 1);

            var dist = db.SingleOrDefault<DiagDist>(
                @"SELECT COUNT(*) AS Total,
                    SUM(CASE WHEN ImprovementDeltaSeason IS NULL THEN 1 ELSE 0 END) AS SeasonNull,
                    SUM(CASE WHEN ImprovementDeltaSeason > 0 THEN 1 ELSE 0 END) AS SeasonPos,
                    SUM(CASE WHEN ImprovementDeltaSeason = 0 THEN 1 ELSE 0 END) AS SeasonZero,
                    SUM(CASE WHEN ImprovementDeltaSeason < 0 THEN 1 ELSE 0 END) AS SeasonNeg,
                    SUM(CASE WHEN ImprovementDelta30 > 0 THEN 1 ELSE 0 END) AS Delta30Pos
                  FROM RankingSnapshot WHERE SnapshotDate = @0", today);

            var baselineKeys = db.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM (
                    SELECT MemberId, Discipline, WeaponGroup FROM RankingSnapshot
                    WHERE SnapshotDate >= @0 AND SnapshotDate < @1
                    GROUP BY MemberId, Discipline, WeaponGroup) t", seasonStart, today);

            var dates = db.Fetch<DateTime>(
                "SELECT DISTINCT SnapshotDate FROM RankingSnapshot WHERE SnapshotDate < @0 AND SnapshotDate >= @1 ORDER BY SnapshotDate", today, seasonStart);

            var samples = db.Fetch<DiagSample>(
                @"SELECT TOP 8 MemberId, Discipline, WeaponGroup, HandicapIndex, ImprovementDelta30, ImprovementDeltaSeason
                  FROM RankingSnapshot WHERE SnapshotDate = @0
                  ORDER BY CASE WHEN ImprovementDeltaSeason IS NULL THEN 1 ELSE 0 END, ImprovementDeltaSeason DESC", today);

            return Json(new
            {
                success = true,
                today = today.ToString("yyyy-MM-dd"),
                distribution = dist,
                seasonBaselineKeys = baselineKeys,
                snapshotDatesBeforeToday = dates.Select(d => d.ToString("yyyy-MM-dd")),
                topSeasonSamples = samples
            });
        }

        public class DiagDist
        {
            public int Total { get; set; }
            public int SeasonNull { get; set; }
            public int SeasonPos { get; set; }
            public int SeasonZero { get; set; }
            public int SeasonNeg { get; set; }
            public int Delta30Pos { get; set; }
        }

        public class DiagSample
        {
            public int MemberId { get; set; }
            public string Discipline { get; set; } = "";
            public string WeaponGroup { get; set; } = "";
            public decimal HandicapIndex { get; set; }
            public decimal? ImprovementDelta30 { get; set; }
            public decimal? ImprovementDeltaSeason { get; set; }
        }

        // ---- helpers ----

        private async Task<ViewerContext?> GetViewerAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            var member = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (member == null) return null;

            var clubIds = new List<int>();
            int primaryClubId = 0;
            var primaryStr = member.GetValue<string>("primaryClubId");
            if (!string.IsNullOrEmpty(primaryStr) && int.TryParse(primaryStr, out var pc) && pc > 0)
            {
                primaryClubId = pc;
                clubIds.Add(pc);
            }
            foreach (var part in (member.GetValue<string>("memberClubIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out var cid) && cid > 0 && !clubIds.Contains(cid)) clubIds.Add(cid);

            var regionCodes = new List<string>();
            foreach (var cid in clubIds)
            {
                var node = _contentService.GetById(cid);
                var rc = node?.ContentType.Alias == "club" ? (node.GetValue<string>("regionalFederation") ?? "") : "";
                if (!string.IsNullOrEmpty(rc) && !regionCodes.Contains(rc)) regionCodes.Add(rc);
            }

            return new ViewerContext
            {
                MemberId = member.Id,
                ClubIds = clubIds,
                PrimaryClubId = primaryClubId,
                RegionCodes = regionCodes,
                IsAdmin = await _authorizationService.IsCurrentUserAdminAsync()
            };
        }

        private class ViewerContext
        {
            public int MemberId { get; set; }
            public List<int> ClubIds { get; set; } = new();
            public int PrimaryClubId { get; set; }
            public List<string> RegionCodes { get; set; } = new();
            public bool IsAdmin { get; set; }
        }

        public class IdentityVisibilityRequest
        {
            public string? Visibility { get; set; }
            public bool ShowClub { get; set; } = true;
        }
    }
}
