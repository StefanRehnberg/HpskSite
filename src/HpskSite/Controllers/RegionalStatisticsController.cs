using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Statistik tab data for the Regional admin panel. Scoped to all clubs in a single
    /// region. Focused on cross-club comparison and "where do I need to lend a hand" lists
    /// (clubs without events, without admins, without Skjutledare).
    /// </summary>
    public class RegionalStatisticsController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authService;
        private readonly ClubComparisonService _comparisonService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<RegionalStatisticsController> _logger;

        public RegionalStatisticsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            AdminAuthorizationService authService,
            ClubComparisonService comparisonService,
            IMemoryCache memoryCache,
            ILogger<RegionalStatisticsController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _authService = authService;
            _comparisonService = comparisonService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _databaseFactory = databaseFactory;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetRegionalStatistics(string regionCode, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = false, message = "Ogiltig kretskod." });

            if (!await _authService.IsRegionalAdminForRegion(regionCode))
                return Json(new { success = false, message = "Access denied" });

            var cacheKey = $"regional_statistics_{regionCode}";
            if (!force && _memoryCache.TryGetValue(cacheKey, out object? cached) && cached != null)
                return Json(cached);

            try
            {
                var data = await BuildAsync(regionCode);
                var result = new { success = true, data };
                _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building regional statistics for region {RegionCode}", regionCode);
                return Json(new { success = false, message = "Fel vid hämtning av statistik: " + ex.Message });
            }
        }

        private async Task<object> BuildAsync(string regionCode)
        {
            var today = DateTime.Today;
            var thirtyDaysAgo = today.AddDays(-30);
            var ninetyDaysAgo = today.AddDays(-90);
            var twelveMonthsAgo = today.AddMonths(-12);

            // ── Clubs in region ────────────────────────────────────────
            var clubIds = _authService.GetClubsInRegions(new List<string> { regionCode });
            var clubIdSet = new HashSet<int>(clubIds);

            // Map clubId -> name from content cache
            var clubNames = new Dictionary<int, string>();
            string regionName = regionCode;
            IPublishedContent? regionNode = null;
            List<IPublishedContent> clubNodes = new();

            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                if (root != null)
                {
                    regionNode = root.Children
                        .FirstOrDefault(c => c.ContentType.Alias == "regionalPage"
                                             && (c.Value<string>("regionCode") ?? "") == regionCode);
                    if (regionNode != null)
                    {
                        regionName = regionNode.Value<string>("regionName") ?? regionNode.Name ?? regionCode;
                    }

                    foreach (var id in clubIds)
                    {
                        var n = ctx.Content.GetById(id);
                        if (n != null)
                        {
                            clubNames[id] = n.Name ?? "";
                            clubNodes.Add(n);
                        }
                    }
                }
            }

            // ── Members in region ──────────────────────────────────────
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).ToList();
            var regionMembers = allMembers
                .Where(m => m.IsApproved)
                .Where(m =>
                {
                    var s = m.GetValue<string>("primaryClubId");
                    return !string.IsNullOrEmpty(s) && int.TryParse(s, out int cid) && clubIdSet.Contains(cid);
                })
                .ToList();

            int totalMembers = regionMembers.Count;
            int newMembers30d = regionMembers.Count(m => m.CreateDate >= thirtyDaysAgo);

            int active30d = regionMembers.Count(m =>
            {
                var web = m.GetValue<DateTime?>("lastActiveDate");
                var mob = m.GetValue<DateTime?>("lastMobileActiveDate");
                var seen = MaxNullable(web, mob);
                return seen.HasValue && seen.Value >= thirtyDaysAgo;
            });

            // Members per club (for the bar chart and the comparable-club lookup)
            var membersPerClub = regionMembers
                .Select(m =>
                {
                    int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out int cid);
                    return cid;
                })
                .Where(cid => clubIdSet.Contains(cid))
                .GroupBy(cid => cid)
                .ToDictionary(g => g.Key, g => g.Count());

            var membersPerClubList = clubIds
                .Select(id => new { clubId = id, clubName = clubNames.GetValueOrDefault(id, "?"), count = membersPerClub.GetValueOrDefault(id, 0) })
                .OrderByDescending(x => x.count)
                .Cast<object>()
                .ToList();

            // ── Competitions in region (this year) ─────────────────────
            int competitionsThisYear = 0;
            var competitionsPerClubMap = new Dictionary<int, int>();
            if (ctx?.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                var compsHub = root?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (compsHub != null)
                {
                    var allComps = compsHub.Descendants().Where(c => c.ContentType.Alias == "competition").ToList();
                    foreach (var c in allComps)
                    {
                        var cid = c.Value<int>("clubId");
                        if (!clubIdSet.Contains(cid)) continue;
                        var d = c.Value<DateTime?>("competitionDate");
                        if (!d.HasValue || d.Value.Year != today.Year) continue;
                        competitionsThisYear++;
                        competitionsPerClubMap[cid] = competitionsPerClubMap.GetValueOrDefault(cid, 0) + 1;
                    }
                }
            }

            var competitionsPerClub = clubIds
                .Where(id => competitionsPerClubMap.ContainsKey(id))
                .Select(id => (object)new { clubId = id, clubName = clubNames.GetValueOrDefault(id, "?"), count = competitionsPerClubMap[id] })
                .OrderByDescending(x => ((dynamic)x).count)
                .ToList();

            // ── Role presence: one pass across ALL approved members ────
            // Admins/Skjutledare/Instructors can be members of any club, not necessarily
            // the one they administer, so we scan everyone once.
            var adminPresent = new HashSet<int>();
            var skjutledarePresent = new HashSet<int>();
            var foreningsinstruktorPresent = new HashSet<int>();   // clubIds that have at least one Föreningsinstruktör
            var foreningsinstruktorCountByClub = new Dictionary<int, int>(); // clubId -> count
            int kretsinstruktorAppointedCount = 0;
            int regionVapenkontrollantCount = 0;
            int regionBanlaggareCount = 0;
            foreach (var m in allMembers.Where(m => m.IsApproved))
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles == null) continue;

                // Is this member primarily in this region? (For Vapen/Ban totals)
                int primaryClubId = 0;
                int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out primaryClubId);
                bool inRegion = primaryClubId > 0 && clubIdSet.Contains(primaryClubId);

                foreach (var role in roles)
                {
                    if (role.StartsWith("ClubAdmin_") && int.TryParse(role.AsSpan("ClubAdmin_".Length), out int aCid))
                        adminPresent.Add(aCid);
                    else if (role.StartsWith("Skjutledare_") && int.TryParse(role.AsSpan("Skjutledare_".Length), out int sCid))
                        skjutledarePresent.Add(sCid);
                    else if (role.StartsWith("Foreningsinstruktor_") && int.TryParse(role.AsSpan("Foreningsinstruktor_".Length), out int fCid))
                    {
                        foreningsinstruktorPresent.Add(fCid);
                        foreningsinstruktorCountByClub[fCid] = foreningsinstruktorCountByClub.GetValueOrDefault(fCid, 0) + 1;
                    }
                    else if (role == $"Kretsinstruktor_{regionCode}")
                        kretsinstruktorAppointedCount++;
                    else if (role == "Vapenkontrollant" && inRegion)
                        regionVapenkontrollantCount++;
                    else if (role == "Banlaggare" && inRegion)
                        regionBanlaggareCount++;
                }
            }

            // ── Nudges: clubs missing events / admins / skjutledare / Föreningsinstruktör ──
            var clubsWithoutEvent90d = new List<object>();
            var clubsWithoutAdmin = new List<object>();
            var clubsWithoutSkjutledare = new List<object>();
            var clubsWithoutForeningsinstruktor = new List<object>();
            foreach (var club in clubNodes)
            {
                var hasRecentEvent = club.Children
                    .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                    .Any(e =>
                    {
                        var d = e.Value<DateTime?>("eventDate");
                        return d.HasValue && d.Value >= ninetyDaysAgo;
                    });
                if (!hasRecentEvent)
                {
                    clubsWithoutEvent90d.Add(new { clubId = club.Id, clubName = club.Name ?? "?", url = club.Url() });
                }
                if (!adminPresent.Contains(club.Id))
                    clubsWithoutAdmin.Add(new { clubId = club.Id, clubName = club.Name ?? "?", url = club.Url() });
                if (!skjutledarePresent.Contains(club.Id))
                    clubsWithoutSkjutledare.Add(new { clubId = club.Id, clubName = club.Name ?? "?", url = club.Url() });
                if (!foreningsinstruktorPresent.Contains(club.Id))
                    clubsWithoutForeningsinstruktor.Add(new { clubId = club.Id, clubName = club.Name ?? "?", url = club.Url() });
            }

            // ── Member growth per month (12) ───────────────────────────
            var newMembersPerMonth = Enumerable.Range(0, 12)
                .Select(i =>
                {
                    var ms = new DateTime(today.Year, today.Month, 1).AddMonths(-11 + i);
                    var me = ms.AddMonths(1);
                    return (object)new
                    {
                        month = ms.ToString("yyyy-MM"),
                        count = regionMembers.Count(m => m.CreateDate >= ms && m.CreateDate < me)
                    };
                })
                .ToList();

            // ── Training matches per club (last 30d) — DB-backed ───────
            var memberClubMap = regionMembers
                .Select(m =>
                {
                    int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out int cid);
                    return new { m.Id, ClubId = cid };
                })
                .Where(x => clubIdSet.Contains(x.ClubId))
                .ToDictionary(x => x.Id, x => x.ClubId);

            var matchesPerClub = new Dictionary<int, int>();
            if (memberClubMap.Any())
            {
                using var db = _databaseFactory.CreateDatabase();
                var paramNames = string.Join(",", memberClubMap.Keys.Select((_, i) => "@" + i));
                var paramVals = memberClubMap.Keys.Cast<object>().ToArray();

                var rows = await db.FetchAsync<MatchPerMemberRow>(
                    $@"SELECT p.MemberId AS MemberId, COUNT(DISTINCT m.Id) AS C
                       FROM TrainingMatchParticipants p
                       INNER JOIN TrainingMatches m ON m.Id = p.TrainingMatchId
                       WHERE m.CreatedDate >= @{memberClubMap.Count} AND p.MemberId IN ({paramNames})
                       GROUP BY p.MemberId",
                    paramVals.Concat(new object[] { thirtyDaysAgo }).ToArray());

                foreach (var r in rows)
                {
                    if (memberClubMap.TryGetValue(r.MemberId, out int cid))
                        matchesPerClub[cid] = matchesPerClub.GetValueOrDefault(cid, 0) + r.C;
                }
            }

            var trainingMatchesPerClub = clubIds
                .Where(id => matchesPerClub.ContainsKey(id))
                .Select(id => (object)new { clubId = id, clubName = clubNames.GetValueOrDefault(id, "?"), count = matchesPerClub[id] })
                .OrderByDescending(x => ((dynamic)x).count)
                .ToList();

            // ── Top 5 growth (12-month %) ──────────────────────────────
            var snapshot = await _comparisonService.GetSnapshotAsync();
            var topGrowthClubs = clubIds
                .Where(id => membersPerClub.GetValueOrDefault(id, 0) >= 3) // skip very small clubs
                .Select(id => new
                {
                    clubId = id,
                    clubName = clubNames.GetValueOrDefault(id, "?"),
                    growthPct = snapshot.GrowthPct12mPerClub.GetValueOrDefault(id, 0),
                    members = membersPerClub.GetValueOrDefault(id, 0)
                })
                .OrderByDescending(x => x.growthPct)
                .Take(5)
                .Cast<object>()
                .ToList();

            // ── Top 5 active clubs (matches 30d) ───────────────────────
            var topActiveClubs = clubIds
                .Where(id => matchesPerClub.GetValueOrDefault(id, 0) > 0)
                .Select(id => new
                {
                    clubId = id,
                    clubName = clubNames.GetValueOrDefault(id, "?"),
                    matches30d = matchesPerClub.GetValueOrDefault(id, 0)
                })
                .OrderByDescending(x => x.matches30d)
                .Take(5)
                .Cast<object>()
                .ToList();

            // Föreningsinstruktörer per klubb (chart data)
            var foreningsinstruktorPerClub = clubIds
                .Select(id => new
                {
                    clubId = id,
                    clubName = clubNames.GetValueOrDefault(id, "?"),
                    count = foreningsinstruktorCountByClub.GetValueOrDefault(id, 0)
                })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.clubName)
                .Cast<object>()
                .ToList();

            return new
            {
                regionName,
                summary = new
                {
                    totalClubs = clubIds.Count,
                    totalMembers,
                    newMembers30d,
                    active30d,
                    competitionsThisYear
                },
                instructors = new
                {
                    kretsinstruktor = kretsinstruktorAppointedCount,
                    foreningsinstruktorTotal = foreningsinstruktorCountByClub.Values.Sum(),
                    vapenkontrollant = regionVapenkontrollantCount,
                    banlaggare = regionBanlaggareCount
                },
                membersPerClub = membersPerClubList,
                clubsWithoutEvent90d,
                clubsWithoutAdmin,
                clubsWithoutSkjutledare,
                clubsWithoutForeningsinstruktor,
                foreningsinstruktorPerClub,
                competitionsPerClub,
                newMembersPerMonth,
                trainingMatchesPerClub,
                topGrowthClubs,
                topActiveClubs,
                kretsinstruktorBelowMinimum = kretsinstruktorAppointedCount < 2
            };
        }

        private static DateTime? MaxNullable(DateTime? a, DateTime? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return a > b ? a : b;
        }

        private class MatchPerMemberRow { public int MemberId { get; set; } public int C { get; set; } }
    }
}
