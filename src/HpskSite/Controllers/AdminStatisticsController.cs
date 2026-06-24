using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Services;
using HpskSite.Models;

namespace HpskSite.Controllers
{
    public class AdminStatisticsController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<AdminStatisticsController> _logger;
        private readonly DocumentService _documentService;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;

        private const string CacheKey = "admin_statistics";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        public AdminStatisticsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            AdminAuthorizationService authService,
            IMemoryCache memoryCache,
            ILogger<AdminStatisticsController> logger,
            DocumentService documentService,
            IConfiguration configuration,
            IWebHostEnvironment env)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _authService = authService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _databaseFactory = databaseFactory;
            _memoryCache = memoryCache;
            _logger = logger;
            _documentService = documentService;
            _configuration = configuration;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> GetStatistics(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (!force && _memoryCache.TryGetValue(CacheKey, out object? cached) && cached != null)
                {
                    return Json(cached);
                }

                var data = BuildStatistics();

                var result = new { success = true, data };
                _memoryCache.Set(CacheKey, result, CacheDuration);

                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building admin statistics");
                return Json(new { success = false, message = "Error loading statistics: " + ex.Message });
            }
        }

        /// <summary>
        /// Visitor stats for the Statistik tab — daily over the last 30 days and
        /// weekly (Monday-start) over the last 53 weeks. Source: VisitorLogs table.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetVisitorStats(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            const string cacheKey = "admin_visitor_stats";
            if (!force && _memoryCache.TryGetValue(cacheKey, out object? cached) && cached != null)
            {
                return Json(cached);
            }

            try
            {
                var today = DateTime.Today;
                var dailyStart = today.AddDays(-29); // include today => 30 entries
                var weeklyEnd = StartOfIsoWeek(today);
                var weeklyStart = weeklyEnd.AddDays(-7 * 52); // include current week => 53 entries

                using var db = _databaseFactory.CreateDatabase();

                // Daily aggregation (last 30 days, including today). "Engaged session" =
                // visited ≥ 2 distinct paths within the same day. The cookie-blind crawler
                // problem (each request from a cookie-less scraper gets a fresh SessionHash)
                // produces a flood of single-page sessions; filtering to ≥ 2 paths in a day
                // strips them out without needing UA-level rules.
                var dailyRows = await db.FetchAsync<DailyVisitorRow>(
                    @"SELECT
                          [Day],
                          COUNT(*) AS Visitors,
                          SUM(PageCount) AS PageViews
                      FROM (
                          SELECT
                              CAST(VisitedAt AS DATE) AS [Day],
                              SessionHash,
                              COUNT(*) AS PageCount
                          FROM [VisitorLogs]
                          WHERE VisitedAt >= @0
                          GROUP BY CAST(VisitedAt AS DATE), SessionHash
                          HAVING COUNT(DISTINCT [Path]) >= 2
                      ) AS s
                      GROUP BY [Day]
                      ORDER BY [Day]",
                    dailyStart);

                var dailyMap = dailyRows.ToDictionary(r => r.Day.Date, r => r);
                var daily = new List<object>(30);
                for (int i = 0; i < 30; i++)
                {
                    var d = dailyStart.AddDays(i);
                    if (dailyMap.TryGetValue(d, out var row))
                    {
                        daily.Add(new { date = d.ToString("yyyy-MM-dd"), visitors = row.Visitors, pageViews = row.PageViews });
                    }
                    else
                    {
                        daily.Add(new { date = d.ToString("yyyy-MM-dd"), visitors = 0, pageViews = 0 });
                    }
                }

                // Weekly aggregation (last 53 weeks, Monday-start, regardless of DATEFIRST).
                // 1900-01-01 was a Monday, so (DATEDIFF(day, '19000101', VisitedAt) % 7) gives 0 for Monday.
                // Same engagement filter as daily — sessions with ≥ 2 distinct paths within
                // the same week count as one engaged visitor; everything else is dropped.
                var weeklyRows = await db.FetchAsync<WeeklyVisitorRow>(
                    @"SELECT
                          WeekStart,
                          COUNT(*) AS Visitors,
                          SUM(PageCount) AS PageViews
                      FROM (
                          SELECT
                              DATEADD(day, -((DATEDIFF(day, '19000101', VisitedAt)) % 7), CAST(VisitedAt AS DATE)) AS WeekStart,
                              SessionHash,
                              COUNT(*) AS PageCount
                          FROM [VisitorLogs]
                          WHERE VisitedAt >= @0
                          GROUP BY DATEADD(day, -((DATEDIFF(day, '19000101', VisitedAt)) % 7), CAST(VisitedAt AS DATE)), SessionHash
                          HAVING COUNT(DISTINCT [Path]) >= 2
                      ) AS s
                      GROUP BY WeekStart
                      ORDER BY WeekStart",
                    weeklyStart);

                var weeklyMap = weeklyRows.ToDictionary(r => r.WeekStart.Date, r => r);
                var weekly = new List<object>(53);
                for (int i = 0; i < 53; i++)
                {
                    var w = weeklyStart.AddDays(7 * i);
                    if (weeklyMap.TryGetValue(w, out var row))
                    {
                        weekly.Add(new { weekStart = w.ToString("yyyy-MM-dd"), visitors = row.Visitors, pageViews = row.PageViews });
                    }
                    else
                    {
                        weekly.Add(new { weekStart = w.ToString("yyyy-MM-dd"), visitors = 0, pageViews = 0 });
                    }
                }

                // Feature popularity over the last 30 days — bucket each logged path into a
                // named feature and count engaged sessions + page views per feature. Same
                // "engaged session" filter as the daily/weekly charts (≥ 2 distinct paths in
                // the window) so cookie-blind scrapers don't inflate the public buckets.
                // Members-only features (Styrelse, Fältkonfig) are login-gated, so their
                // counts are genuine usage. Paths come from the route table / nav menu.
                var featureRows = await db.FetchAsync<FeatureUsageRow>(
                    @"SELECT Feature, COUNT(DISTINCT SessionHash) AS Sessions, COUNT(*) AS PageViews
                      FROM (
                          SELECT v.SessionHash,
                              CASE
                                  WHEN LOWER(v.[Path]) LIKE '/utbildning%'    THEN 'Utbildning'
                                  WHEN LOWER(v.[Path]) LIKE '/styrelse%'      THEN 'Styrelse'
                                  WHEN LOWER(v.[Path]) LIKE '/faltkonfig%'    THEN 'Fältkonfig'
                                  WHEN LOWER(v.[Path]) LIKE '/siktbild%'      THEN 'Siktbild'
                                  WHEN LOWER(v.[Path]) LIKE '/skjutban%'      THEN 'Skjutbanor'
                                  WHEN LOWER(v.[Path]) LIKE '/skyttetrappan%' THEN 'Skyttetrappan'
                                  WHEN LOWER(v.[Path]) LIKE '/traningsmatch%' THEN 'Träningsmatch'
                                  WHEN LOWER(v.[Path]) LIKE '/competitions%'  THEN 'Tävlingar'
                                  WHEN LOWER(v.[Path]) LIKE '/marken%'        THEN 'Märken'
                                  WHEN LOWER(v.[Path]) LIKE '/live%'          THEN 'Live-resultat'
                                  ELSE NULL
                              END AS Feature
                          FROM [VisitorLogs] v
                          JOIN (
                              SELECT SessionHash FROM [VisitorLogs]
                              WHERE VisitedAt >= @0
                              GROUP BY SessionHash HAVING COUNT(DISTINCT [Path]) >= 2
                          ) e ON e.SessionHash = v.SessionHash
                          WHERE v.VisitedAt >= @0
                      ) t
                      WHERE Feature IS NOT NULL
                      GROUP BY Feature
                      ORDER BY Sessions DESC",
                    dailyStart);

                var featureUsage = featureRows
                    .Select(r => (object)new { feature = r.Feature, sessions = r.Sessions, pageViews = r.PageViews })
                    .ToList();

                var result = new { success = true, daily, weekly, featureUsage };
                _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building visitor stats");
                return Json(new { success = false, message = "Error loading visitor stats: " + ex.Message });
            }
        }

        private static DateTime StartOfIsoWeek(DateTime d)
        {
            // Monday = 1, Sunday = 0 in C# DayOfWeek (where Sunday=0). Translate to Monday=0.
            int offset = ((int)d.DayOfWeek + 6) % 7;
            return d.Date.AddDays(-offset);
        }

        private class DailyVisitorRow
        {
            public DateTime Day { get; set; }
            public int Visitors { get; set; }
            public int PageViews { get; set; }
        }

        private class WeeklyVisitorRow
        {
            public DateTime WeekStart { get; set; }
            public int Visitors { get; set; }
            public int PageViews { get; set; }
        }

        private class FeatureUsageRow
        {
            public string Feature { get; set; } = "";
            public int Sessions { get; set; }
            public int PageViews { get; set; }
        }

        // Typed rows for the board-work (Styrelse) aggregates — POCOs rather than dynamic so
        // the in-memory LINQ stays type-safe (dynamic in LINQ has bitten us before).
        private class BoardMeetingRow
        {
            public int OwnerType { get; set; }
            public int OwnerId { get; set; }
            public DateTime MeetingDate { get; set; }
            public string? Status { get; set; }
            public DateTime? JusteringRequestedDate { get; set; }
            public DateTime? KallelseSentDate { get; set; }
        }

        private class BoardActionRow
        {
            public int OwnerType { get; set; }
            public int OwnerId { get; set; }
            public string? Status { get; set; }
            public DateTime? DueDate { get; set; }
            public DateTime? CompletedDate { get; set; }
        }

        private class BoardWheelRow
        {
            public int OwnerType { get; set; }
            public int OwnerId { get; set; }
            public bool Done { get; set; }
        }

        private class ViaCountRow
        {
            public string? Via { get; set; }
            public int Cnt { get; set; }
        }

        /// <summary>Zero-valued Styrelse (board work) stats — default and no-table fallback.</summary>
        private static object ZeroStyrelseStats() => new
        {
            boardsActive = 0,
            meetingsTotal = 0,
            meetingsThisYear = 0,
            meetingsJusteratThisYear = 0,
            justeringDigital = new { requested = 0, qr = 0, web = 0, email = 0 },
            actionsOpen = 0,
            actionsOverdue = 0,
            actionsCompletedThisYear = 0,
            yearWheelDone = 0,
            yearWheelTotal = 0,
            kallelserThisYear = 0,
            nominationsThisYear = 0,
            byOwner = new List<object>()
        };

        /// <summary>Zero-valued Fältkonfig stats — default and no-table fallback.</summary>
        private static object ZeroFaltkonfigStats() => new
        {
            total = 0,
            creators = 0,
            sharedConfigs = 0,
            byVisibility = new List<object>(),
            byApproval = new List<object>(),
            projectsActive = 0,
            projectsArchived = 0,
            configsInProjects = 0
        };

        /// <summary>Zero-valued Märken series stats — used as the default and the no-content fallback.</summary>
        private static object ZeroMarkenSeriesStats() => new
        {
            guldserier = new { total = 0, thisYear = 0 },
            snabbserier = new { total = 0, thisYear = 0, byType = new List<object>() },
            luftpistolserier = new { total = 0, thisYear = 0 }
        };

        private object BuildStatistics()
        {
            var now = DateTime.Now;
            var today = DateTime.Today;
            var thirtyDaysAgo = today.AddDays(-30);
            var ninetyDaysAgo = today.AddDays(-90);
            var yearAgo = today.AddMonths(-12);
            var currentMonthStart = new DateTime(now.Year, now.Month, 1);
            var currentYearStart = new DateTime(now.Year, 1, 1);

            // ── 1. Members ──────────────────────────────────────────
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).ToList();
            var approvedMembers = allMembers.Where(m => m.IsApproved).ToList();

            int totalMembers = approvedMembers.Count;
            int newMembers30d = approvedMembers.Count(m => m.CreateDate >= thirtyDaysAgo);

            // New members per month (last 12 months)
            var newMembersPerMonth = Enumerable.Range(0, 12)
                .Select(i =>
                {
                    var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11 + i);
                    var monthEnd = monthStart.AddMonths(1);
                    return new
                    {
                        month = monthStart.ToString("yyyy-MM"),
                        count = approvedMembers.Count(m => m.CreateDate >= monthStart && m.CreateDate < monthEnd)
                    };
                })
                .ToList();

            // Members by region — resolve via primaryClubId → club node → regionalFederation
            var membersByRegion = new List<object>();
            var clubRegionCache = new Dictionary<int, string>();

            if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) && umbracoContext.Content != null)
            {
                var root = umbracoContext.Content.GetAtRoot().FirstOrDefault();
                if (root != null)
                {
                    // Build club → region lookup
                    var regionalPages = root.Children.Where(c => c.ContentType.Alias == "regionalPage").ToList();
                    foreach (var rp in regionalPages)
                    {
                        var clubsPage = rp.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (clubsPage != null)
                        {
                            foreach (var club in clubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                            {
                                clubRegionCache[club.Id] = club.Value<string>("regionalFederation") ?? "Okänd";
                            }
                        }
                    }

                    // Also check root-level clubsPage (legacy)
                    var rootClubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (rootClubsHub != null)
                    {
                        foreach (var club in rootClubsHub.Children.Where(c => c.ContentType.Alias == "club"))
                        {
                            if (!clubRegionCache.ContainsKey(club.Id))
                                clubRegionCache[club.Id] = club.Value<string>("regionalFederation") ?? "Okänd";
                        }
                    }

                    // Group members by region
                    var regionGroups = approvedMembers
                        .Select(m =>
                        {
                            var clubIdStr = m.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(clubIdStr) && int.TryParse(clubIdStr, out var cid) && clubRegionCache.TryGetValue(cid, out var region))
                                return region;
                            return "Okänd";
                        })
                        .GroupBy(r => r)
                        .Select(g => new { region = g.Key, count = g.Count() })
                        .OrderByDescending(g => g.count)
                        .ToList();

                    membersByRegion = regionGroups.Cast<object>().ToList();

                    // ── 2. Clubs ────────────────────────────────────────
                    var allClubNodes = new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();
                    foreach (var rp in regionalPages)
                    {
                        var clubsPage = rp.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (clubsPage != null)
                            allClubNodes.AddRange(clubsPage.Children.Where(c => c.ContentType.Alias == "club"));
                    }
                    if (rootClubsHub != null)
                        allClubNodes.AddRange(rootClubsHub.Children.Where(c => c.ContentType.Alias == "club"));

                    int totalClubs = allClubNodes.Count;

                    // Active club: has ≥1 approved member AND (has event within last 3 months
                    //              OR has a club competition within last 3 months)
                    var membersByClub = approvedMembers
                        .Select(m =>
                        {
                            var clubIdStr = m.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(clubIdStr) && int.TryParse(clubIdStr, out var cid))
                                return cid;
                            return 0;
                        })
                        .Where(cid => cid > 0)
                        .GroupBy(cid => cid)
                        .ToDictionary(g => g.Key, g => g.Count());

                    int clubsWithMembers = allClubNodes.Count(club => membersByClub.ContainsKey(club.Id));

                    var threeMonthsAgo = today.AddMonths(-3);

                    // Pre-compute the set of club IDs that have a competition with date
                    // within the last 3 months. Done once here instead of per-club inside
                    // the Count() loop. Note: the main competitions section further down
                    // re-loads allCompetitions for its own purposes — keep them separate
                    // for now since they need slightly different filters.
                    var clubIdsWithRecentComp = (root.Children
                            .FirstOrDefault(c => c.ContentType.Alias == "competitionsHub")
                            ?.Descendants()
                            .Where(c => c.ContentType.Alias == "competition")
                            ?? Enumerable.Empty<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>())
                        .Where(c =>
                        {
                            var d = c.Value<DateTime?>("competitionDate");
                            return d.HasValue && d.Value >= threeMonthsAgo;
                        })
                        .Select(c => c.Value<int>("clubId"))
                        .Where(id => id > 0)
                        .ToHashSet();

                    int activeClubs = allClubNodes.Count(club =>
                    {
                        if (!membersByClub.ContainsKey(club.Id)) return false;
                        var hasRecentEvent = club.Children
                            .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                            .Any(e =>
                            {
                                var eventDate = e.Value<DateTime?>("eventDate");
                                return eventDate.HasValue && eventDate.Value >= threeMonthsAgo;
                            });
                        var hasRecentClubComp = clubIdsWithRecentComp.Contains(club.Id);
                        return hasRecentEvent || hasRecentClubComp;
                    });

                    // New clubs (last 30 days): earliest member CreateDate for that club within 30 days
                    var earliestMemberByClub = approvedMembers
                        .Select(m =>
                        {
                            var clubIdStr = m.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(clubIdStr) && int.TryParse(clubIdStr, out var cid))
                                return new { ClubId = cid, m.CreateDate };
                            return null;
                        })
                        .Where(x => x != null)
                        .GroupBy(x => x!.ClubId)
                        .ToDictionary(g => g.Key, g => g.Min(x => x!.CreateDate));

                    int newClubs30d = earliestMemberByClub.Count(kvp => kvp.Value >= thirtyDaysAgo);

                    // ── 3. Competitions ─────────────────────────────────
                    var competitionsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                    var allCompetitions = competitionsHub?.Descendants()
                        .Where(c => c.ContentType.Alias == "competition")
                        .ToList() ?? new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

                    var competitionsThisYear = allCompetitions
                        .Where(c =>
                        {
                            var d = c.Value<DateTime?>("competitionDate");
                            return d.HasValue && d.Value.Year == now.Year;
                        })
                        .ToList();

                    int competitionsThisYearCount = competitionsThisYear.Count;

                    var competitionsByDiscipline = competitionsThisYear
                        .GroupBy(c => c.Value<string>("competitionType") ?? "Okänd")
                        .Select(g => new { discipline = g.Key, count = g.Count() })
                        .OrderByDescending(g => g.count)
                        .Cast<object>()
                        .ToList();

                    // Top 5 competitions by registration count
                    var topCompetitions = competitionsThisYear
                        .Select(c =>
                        {
                            var registrations = c.Children
                                .Where(ch => ch.ContentType.Alias == "competitionRegistration" || ch.ContentType.Alias == "registration")
                                .Count();
                            // Also count from registrationInvoicesHub children would be separate,
                            // but registration nodes are what we want
                            if (registrations == 0)
                            {
                                // Try counting descendants that look like registrations
                                registrations = c.Descendants()
                                    .Count(ch => ch.ContentType.Alias == "competitionRegistration" || ch.ContentType.Alias == "registration");
                            }
                            return new
                            {
                                name = c.Name ?? "",
                                discipline = c.Value<string>("competitionType") ?? "",
                                registrations
                            };
                        })
                        .OrderByDescending(c => c.registrations)
                        .Take(5)
                        .Cast<object>()
                        .ToList();

                    // ── 3b. Club activities (events) ──────────────────
                    var allClubEvents = allClubNodes
                        .SelectMany(club => club.Children
                            .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                            .Select(e => new
                            {
                                clubId = club.Id,
                                clubName = club.Name ?? "",
                                eventType = e.Value<string>("eventType") ?? "Annat",
                                eventDate = e.Value<DateTime?>("eventDate")
                            }))
                        .ToList();

                    int clubEventsThisYear = allClubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == now.Year);
                    int clubEventsLastYear = allClubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == now.Year - 1);
                    int clubEventsYearBefore = allClubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == now.Year - 2);
                    int clubEvents30d = allClubEvents.Count(e => e.eventDate.HasValue && e.eventDate >= thirtyDaysAgo);

                    var clubEventsByType = allClubEvents
                        .Where(e => e.eventDate.HasValue && e.eventDate.Value.Year == now.Year)
                        .GroupBy(e => e.eventType)
                        .Select(g => new { type = g.Key, count = g.Count() })
                        .OrderByDescending(g => g.count)
                        .Cast<object>()
                        .ToList();

                    var clubEventsPerMonth = Enumerable.Range(1, 12)
                        .Select(m =>
                        {
                            var monthStart = new DateTime(now.Year, m, 1);
                            var monthEnd = monthStart.AddMonths(1);
                            return new
                            {
                                month = monthStart.ToString("yyyy-MM"),
                                count = allClubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value >= monthStart && e.eventDate.Value < monthEnd)
                            };
                        })
                        .Cast<object>()
                        .ToList();

                    // Lookup tables used by the top-clubs cards on the frontend so each
                    // row can render the club name as a link to the club's published page.
                    var clubNameLookup = allClubNodes.ToDictionary(c => c.Id, c => c.Name ?? "");
                    var clubUrlLookup = allClubNodes.ToDictionary(c => c.Id, c => c.Url() ?? "");

                    var topClubsByEvents = allClubEvents
                        .Where(e => e.eventDate.HasValue && e.eventDate.Value.Year == now.Year)
                        .GroupBy(e => e.clubId)
                        .Select(g => new
                        {
                            clubId = g.Key,
                            club = clubNameLookup.GetValueOrDefault(g.Key, g.First().clubName),
                            count = g.Count(),
                            url = clubUrlLookup.GetValueOrDefault(g.Key, "")
                        })
                        .OrderByDescending(g => g.count)
                        .Take(25)
                        .Cast<object>()
                        .ToList();

                    var topClubsByMembers = membersByClub
                        .Where(kvp => clubNameLookup.ContainsKey(kvp.Key))
                        .Select(kvp => new
                        {
                            clubId = kvp.Key,
                            club = clubNameLookup[kvp.Key],
                            count = kvp.Value,
                            url = clubUrlLookup.GetValueOrDefault(kvp.Key, "")
                        })
                        .OrderByDescending(x => x.count)
                        .Take(25)
                        .Cast<object>()
                        .ToList();

                    // ── 4-7. All SQL stats (single DB connection, consolidated queries) ──
                    int trainingMatches30d = 0;
                    int trainingMatchParticipants30d = 0;
                    int trainingMatchClubs30d = 0;
                    var trainingMatchesPerMonth = new List<object>();
                    var trainingScoresPerMonth = new List<object>();
                    int trainingScoresThisYear = 0;
                    var scoresByWeaponClass = new List<object>();
                    var scoresByDiscipline = new List<object>();
                    int uniqueTrainers30d = 0;
                    int trainingMatchesTotal = 0;
                    int trainingStairsActiveGroups = 0;
                    object markenSeriesStats = ZeroMarkenSeriesStats();
                    var devicesByPlatform = new List<object>();
                    int uniqueLogins30d = 0;
                    long totalUsedBytes = 0;
                    var storageByOwner = new List<dynamic>();
                    var storageQuotas = new List<DocumentStorageQuota>();
                    object styrelseStats = ZeroStyrelseStats();
                    object faltkonfigStats = ZeroFaltkonfigStats();

                    using (var db = _databaseFactory.CreateDatabase())
                    {
                        // ── Combined training matches query (replaces 3 separate queries) ──
                        var matchStats = db.Single<dynamic>(@"
                            SELECT
                                COUNT(*) AS Total,
                                SUM(CASE WHEN CreatedDate >= @0 THEN 1 ELSE 0 END) AS Last30d
                            FROM TrainingMatches", thirtyDaysAgo);
                        trainingMatchesTotal = (int)matchStats.Total;
                        trainingMatches30d = (int)matchStats.Last30d;

                        // Training matches per month
                        var monthlyMatches = db.Fetch<dynamic>(@"
                            SELECT YEAR(CreatedDate) AS Y, MONTH(CreatedDate) AS M, COUNT(*) AS Cnt
                            FROM TrainingMatches
                            WHERE CreatedDate >= @0
                            GROUP BY YEAR(CreatedDate), MONTH(CreatedDate)
                            ORDER BY Y, M", yearAgo);
                        var monthlyDict = monthlyMatches.ToDictionary(
                            r => $"{(int)r.Y:D4}-{(int)r.M:D2}",
                            r => (int)r.Cnt);
                        trainingMatchesPerMonth = Enumerable.Range(0, 12)
                            .Select(i =>
                            {
                                var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11 + i);
                                var key = monthStart.ToString("yyyy-MM");
                                return (object)new { month = key, count = monthlyDict.GetValueOrDefault(key, 0) };
                            })
                            .ToList();

                        // ── Combined participants query (replaces 2 separate queries) ──
                        var participantMemberIds = db.Fetch<int>(@"
                            SELECT DISTINCT tmp.MemberId
                            FROM TrainingMatchParticipants tmp
                            JOIN TrainingMatches tm ON tm.Id = tmp.TrainingMatchId
                            WHERE tm.CreatedDate >= @0 AND tmp.MemberId > 0", thirtyDaysAgo);
                        trainingMatchParticipants30d = participantMemberIds.Count;

                        var memberClubLookup = approvedMembers
                            .Select(m =>
                            {
                                var clubIdStr = m.GetValue<string>("primaryClubId");
                                if (!string.IsNullOrEmpty(clubIdStr) && int.TryParse(clubIdStr, out var cid))
                                    return new { MemberId = m.Id, ClubId = cid };
                                return null;
                            })
                            .Where(x => x != null)
                            .ToDictionary(x => x!.MemberId, x => x!.ClubId);
                        trainingMatchClubs30d = participantMemberIds
                            .Where(mid => memberClubLookup.ContainsKey(mid))
                            .Select(mid => memberClubLookup[mid])
                            .Distinct()
                            .Count();

                        // ── Combined training scores query (replaces 5 separate queries) ──
                        var scoreRows = db.Fetch<dynamic>(@"
                            SELECT
                                ISNULL(Discipline, 'Precision') AS Discipline,
                                WeaponClass,
                                YEAR(TrainingDate) AS Y,
                                MONTH(TrainingDate) AS M,
                                COUNT(*) AS Cnt,
                                COUNT(DISTINCT MemberId) AS UniqueMembersCnt
                            FROM TrainingScores
                            WHERE TrainingDate >= @0
                            GROUP BY ISNULL(Discipline, 'Precision'), WeaponClass, YEAR(TrainingDate), MONTH(TrainingDate)", yearAgo);

                        // Derive all training score stats from the single result set
                        var scoreRowsList = scoreRows.ToList();

                        // Per month (sum across all disciplines/weapons)
                        var scoresDict = scoreRowsList
                            .GroupBy(r => $"{(int)r.Y:D4}-{(int)r.M:D2}")
                            .ToDictionary(g => g.Key, g => g.Sum(r => (int)r.Cnt));
                        trainingScoresPerMonth = Enumerable.Range(0, 12)
                            .Select(i =>
                            {
                                var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11 + i);
                                var key = monthStart.ToString("yyyy-MM");
                                return (object)new { month = key, count = scoresDict.GetValueOrDefault(key, 0) };
                            })
                            .ToList();

                        // This year filter
                        var thisYearRows = scoreRowsList.Where(r => (int)r.Y == now.Year).ToList();
                        trainingScoresThisYear = thisYearRows.Sum(r => (int)r.Cnt);

                        scoresByWeaponClass = thisYearRows
                            .GroupBy(r => (string)r.WeaponClass)
                            .Select(g => (object)new { weaponClass = g.Key, count = g.Sum(r => (int)r.Cnt) })
                            .OrderByDescending(x => ((dynamic)x).count)
                            .ToList();

                        scoresByDiscipline = thisYearRows
                            .GroupBy(r => (string)r.Discipline)
                            .Select(g => (object)new { discipline = g.Key, count = g.Sum(r => (int)r.Cnt) })
                            .OrderByDescending(x => ((dynamic)x).count)
                            .ToList();

                        // Unique trainers 30d (need separate query — can't derive from grouped data)
                        uniqueTrainers30d = db.Single<int>(
                            "SELECT COUNT(DISTINCT MemberId) FROM TrainingScores WHERE TrainingDate >= @0", thirtyDaysAgo);

                        // ── Single query for remaining simple counts ──
                        trainingStairsActiveGroups = db.Single<int>(
                            "SELECT COUNT(*) FROM TrainingGroups WHERE IsActive = 1");

                        // ── Märken: validated series (Guldserie / Snabbserie by target / Luftpistolserie) ──
                        // Discipline is computed (not stored), so classify in-memory. Graceful if the
                        // MarkenSeries table doesn't exist yet — falls back to the zero object.
                        try
                        {
                            var verifiedSeries = db.Fetch<MarkenSeries>("WHERE Status = @0", Marken.StatusVerified);
                            int curYear = now.Year;
                            string Disc(MarkenSeries s) => Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target);
                            int Tot(Func<MarkenSeries, bool> p) => verifiedSeries.Count(p);
                            int Yr(Func<MarkenSeries, bool> p) => verifiedSeries.Count(s => s.Year == curYear && p(s));

                            var snabbByType = verifiedSeries
                                .Where(s => s.SeriesType == Marken.SeriesTypeSpeed)
                                .GroupBy(s => s.Target)
                                .Select(g => (object)new
                                {
                                    name = Marken.SpeedTargetDisplay(g.Key),
                                    total = g.Count(),
                                    thisYear = g.Count(s => s.Year == curYear)
                                })
                                .OrderByDescending(x => ((dynamic)x).total)
                                .ToList();

                            markenSeriesStats = new
                            {
                                guldserier = new
                                {
                                    total = Tot(s => Disc(s) == Marken.DisciplinePrecision),
                                    thisYear = Yr(s => Disc(s) == Marken.DisciplinePrecision)
                                },
                                snabbserier = new
                                {
                                    total = Tot(s => s.SeriesType == Marken.SeriesTypeSpeed),
                                    thisYear = Yr(s => s.SeriesType == Marken.SeriesTypeSpeed),
                                    byType = snabbByType
                                },
                                luftpistolserier = new
                                {
                                    total = Tot(s => Disc(s) == Marken.DisciplineAir),
                                    thisYear = Yr(s => Disc(s) == Marken.DisciplineAir)
                                }
                            };
                        }
                        catch { /* table not present — keep zero defaults */ }

                        // ── Combined device + login query (replaces 2 separate queries) ──
                        try
                        {
                            var platforms = db.Fetch<dynamic>(
                                "SELECT Platform, COUNT(*) AS Cnt FROM DeviceRegistrations GROUP BY Platform");
                            devicesByPlatform = platforms
                                .Select(p => (object)new { platform = (string)p.Platform, count = (int)p.Cnt })
                                .ToList();

                            uniqueLogins30d = db.Single<int>(
                                "SELECT COUNT(DISTINCT MemberId) FROM RefreshTokens WHERE CreatedAt >= @0", thirtyDaysAgo);
                        }
                        catch { /* Tables may not exist */ }

                        // ── Single query for all document storage (replaces N+M per-owner queries) ──
                        storageByOwner = db.Fetch<dynamic>(@"
                            SELECT OwnerType, OwnerId, SUM(FileSize) AS UsedBytes
                            FROM Documents
                            GROUP BY OwnerType, OwnerId");
                        totalUsedBytes = storageByOwner.Sum(r => (long)r.UsedBytes);

                        // Pre-load all quota overrides in one query (avoids N calls to GetStorageLimit)
                        storageQuotas = db.Fetch<DocumentStorageQuota>("SELECT * FROM DocumentStorageQuotas");

                        // ── Styrelse (Board Work) usage — overall + per club/region ──
                        // Board tables are small (a handful of meetings per club/year) so we
                        // fetch the rows and aggregate in-memory. Wrapped in try/catch: a prod
                        // that hasn't run the board migrations keeps the zero defaults.
                        try
                        {
                            int curYear = now.Year;

                            var meetingRows = db.Fetch<BoardMeetingRow>(
                                @"SELECT OwnerType, OwnerId, MeetingDate, Status, JusteringRequestedDate, KallelseSentDate
                                  FROM BoardMeetings WHERE IsActive = 1");
                            var actionRows = db.Fetch<BoardActionRow>(
                                @"SELECT OwnerType, OwnerId, Status, DueDate, CompletedDate
                                  FROM BoardMeetingActions WHERE IsActive = 1");
                            var wheelRows = db.Fetch<BoardWheelRow>(
                                @"SELECT OwnerType, OwnerId, Done
                                  FROM BoardYearWheelItems WHERE IsActive = 1 AND Year = @0", curYear);
                            var approvalRows = db.Fetch<ViaCountRow>(
                                @"SELECT ISNULL(a.ApprovedVia, 'web') AS Via, COUNT(*) AS Cnt
                                  FROM BoardMeetingAttendees a
                                  JOIN BoardMeetings m ON m.Id = a.MeetingId
                                  WHERE m.IsActive = 1 AND a.ApprovedDate IS NOT NULL
                                  GROUP BY a.ApprovedVia");
                            int nominationsThisYear = db.Single<int>(
                                "SELECT COUNT(*) FROM BoardNominations WHERE IsActive = 1 AND Year = @0", curYear);

                            int approvalsQr = 0, approvalsWeb = 0, approvalsEmail = 0;
                            foreach (var ar in approvalRows)
                            {
                                var via = (ar.Via ?? "web").ToLowerInvariant();
                                if (via == "qr") approvalsQr += ar.Cnt;
                                else if (via == "email") approvalsEmail += ar.Cnt;
                                else approvalsWeb += ar.Cnt;
                            }

                            // Name lookup for the per-owner expander (clubNameLookup + region names).
                            var ownerName = new Dictionary<(int, int), string>();
                            foreach (var kvp in clubNameLookup) ownerName[(DocumentOwnerType.Club, kvp.Key)] = kvp.Value;
                            foreach (var rp in regionalPages) ownerName[(DocumentOwnerType.Region, rp.Id)] = rp.Name ?? "";

                            var openByOwner = actionRows
                                .Where(r => r.Status == "Öppen")
                                .GroupBy(r => (r.OwnerType, r.OwnerId))
                                .ToDictionary(g => g.Key, g => g.Count());

                            var byOwner = meetingRows
                                .GroupBy(r => (r.OwnerType, r.OwnerId))
                                .Select(g =>
                                {
                                    var key = g.Key;
                                    var isRegion = key.OwnerType == DocumentOwnerType.Region;
                                    return new
                                    {
                                        name = ownerName.TryGetValue(key, out var nm) && !string.IsNullOrEmpty(nm)
                                            ? nm
                                            : (isRegion ? "Krets" : "Klubb") + " #" + key.OwnerId,
                                        type = isRegion ? "Krets" : "Klubb",
                                        meetings = g.Count(),
                                        meetingsThisYear = g.Count(r => r.MeetingDate.Year == curYear),
                                        openActions = openByOwner.GetValueOrDefault(key, 0),
                                        lastActivity = g.Max(r => r.MeetingDate).ToString("yyyy-MM-dd")
                                    };
                                })
                                .OrderByDescending(x => x.meetingsThisYear)
                                .ThenByDescending(x => x.meetings)
                                .Cast<object>()
                                .ToList();

                            styrelseStats = new
                            {
                                boardsActive = byOwner.Count,
                                meetingsTotal = meetingRows.Count,
                                meetingsThisYear = meetingRows.Count(r => r.MeetingDate.Year == curYear),
                                meetingsJusteratThisYear = meetingRows.Count(r => r.Status == "Justerat" && r.MeetingDate.Year == curYear),
                                justeringDigital = new { requested = meetingRows.Count(r => r.JusteringRequestedDate != null), qr = approvalsQr, web = approvalsWeb, email = approvalsEmail },
                                actionsOpen = actionRows.Count(r => r.Status == "Öppen"),
                                actionsOverdue = actionRows.Count(r => r.Status == "Öppen" && r.DueDate != null && r.DueDate.Value < today),
                                actionsCompletedThisYear = actionRows.Count(r => r.Status == "Klar" && r.CompletedDate != null && r.CompletedDate.Value.Year == curYear),
                                yearWheelDone = wheelRows.Count(r => r.Done),
                                yearWheelTotal = wheelRows.Count,
                                kallelserThisYear = meetingRows.Count(r => r.KallelseSentDate != null && r.KallelseSentDate.Value.Year == curYear),
                                nominationsThisYear,
                                byOwner
                            };
                        }
                        catch { /* board tables not present — keep zero defaults */ }

                        // ── Fältkonfig (standalone Fältskytte configurations) usage ──
                        // Independent try blocks: the visibility columns are oldest; approval +
                        // ProjectId and the project/collaborator tables came in later migrations,
                        // so a partially-migrated prod still yields whatever it can.
                        try
                        {
                            var cfg = db.Single<dynamic>(
                                @"SELECT
                                      COUNT(*) AS Total,
                                      COUNT(DISTINCT OwnerMemberId) AS Creators,
                                      ISNULL(SUM(CASE WHEN Visibility = 'Private' THEN 1 ELSE 0 END), 0) AS VisPrivate,
                                      ISNULL(SUM(CASE WHEN Visibility = 'Club'    THEN 1 ELSE 0 END), 0) AS VisClub,
                                      ISNULL(SUM(CASE WHEN Visibility = 'Region'  THEN 1 ELSE 0 END), 0) AS VisRegion,
                                      ISNULL(SUM(CASE WHEN Visibility = 'Public'  THEN 1 ELSE 0 END), 0) AS VisPublic
                                  FROM FaltskytteConfiguration");

                            var byVisibility = new List<object>
                            {
                                new { label = "Privat", count = Convert.ToInt32(cfg.VisPrivate) },
                                new { label = "Klubb",  count = Convert.ToInt32(cfg.VisClub) },
                                new { label = "Krets",  count = Convert.ToInt32(cfg.VisRegion) },
                                new { label = "Publik", count = Convert.ToInt32(cfg.VisPublic) }
                            };

                            var byApproval = new List<object>();
                            int configsInProjects = 0;
                            try
                            {
                                var appr = db.Single<dynamic>(
                                    @"SELECT
                                          ISNULL(SUM(CASE WHEN ApprovalStatus = 'Approved'        THEN 1 ELSE 0 END), 0) AS Approved,
                                          ISNULL(SUM(CASE WHEN ApprovalStatus = 'PendingApproval' THEN 1 ELSE 0 END), 0) AS Pending,
                                          ISNULL(SUM(CASE WHEN ApprovalStatus IS NULL OR ApprovalStatus = 'Draft' THEN 1 ELSE 0 END), 0) AS Draft,
                                          ISNULL(SUM(CASE WHEN ProjectId IS NOT NULL THEN 1 ELSE 0 END), 0) AS InProjects
                                      FROM FaltskytteConfiguration");
                                byApproval.Add(new { label = "Godkänd", count = Convert.ToInt32(appr.Approved) });
                                byApproval.Add(new { label = "Väntar", count = Convert.ToInt32(appr.Pending) });
                                byApproval.Add(new { label = "Utkast", count = Convert.ToInt32(appr.Draft) });
                                configsInProjects = Convert.ToInt32(appr.InProjects);
                            }
                            catch { /* approval / ProjectId columns not present */ }

                            int sharedConfigs = 0;
                            try { sharedConfigs = db.Single<int>("SELECT COUNT(DISTINCT ConfigId) FROM FaltskytteConfigurationCollaborator"); }
                            catch { /* collaborator table not present */ }

                            int projectsActive = 0, projectsArchived = 0;
                            try
                            {
                                var pr = db.Single<dynamic>(
                                    @"SELECT
                                          ISNULL(SUM(CASE WHEN Status = 'Archived' THEN 1 ELSE 0 END), 0) AS Archived,
                                          ISNULL(SUM(CASE WHEN Status <> 'Archived' OR Status IS NULL THEN 1 ELSE 0 END), 0) AS Active
                                      FROM FaltskytteProject");
                                projectsArchived = Convert.ToInt32(pr.Archived);
                                projectsActive = Convert.ToInt32(pr.Active);
                            }
                            catch { /* project table not present */ }

                            faltkonfigStats = new
                            {
                                total = Convert.ToInt32(cfg.Total),
                                creators = Convert.ToInt32(cfg.Creators),
                                sharedConfigs,
                                byVisibility,
                                byApproval,
                                projectsActive,
                                projectsArchived,
                                configsInProjects
                            };
                        }
                        catch { /* FaltskytteConfiguration table not present — keep zero defaults */ }
                    }

                    // ── 5. Training stairs (Skyttetrappan) — in-memory ──────────
                    int trainingStairsActiveTrainees = 0;
                    int trainingStairsStepsCompleted30d = 0;
                    var trainingStairsMembersPerLevel = new List<object>();

                    trainingStairsActiveTrainees = approvedMembers.Count(m =>
                    {
                        if (!m.HasProperty("trainingStartDate")) return false;
                        var val = m.GetValue<string>("trainingStartDate");
                        return !string.IsNullOrEmpty(val);
                    });

                    trainingStairsMembersPerLevel = approvedMembers
                        .Where(m => m.HasProperty("currentTrainingLevel"))
                        .Select(m =>
                        {
                            var levelStr = m.GetValue<string>("currentTrainingLevel");
                            if (!string.IsNullOrEmpty(levelStr) && int.TryParse(levelStr, out var level) && level > 0)
                                return level;
                            return 0;
                        })
                        .Where(level => level > 0)
                        .GroupBy(level => level)
                        .Select(g => (object)new { level = g.Key, count = g.Count() })
                        .OrderBy(x => ((dynamic)x).level)
                        .ToList();

                    foreach (var m in approvedMembers)
                    {
                        if (!m.HasProperty("completedTrainingSteps")) continue;
                        var json = m.GetValue<string>("completedTrainingSteps");
                        if (string.IsNullOrEmpty(json)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var step in doc.RootElement.EnumerateArray())
                                {
                                    if (step.TryGetProperty("CompletedDate", out var dateEl) ||
                                        step.TryGetProperty("completedDate", out dateEl))
                                    {
                                        if (DateTime.TryParse(dateEl.GetString(), out var completedDate) && completedDate >= thirtyDaysAgo)
                                        {
                                            trainingStairsStepsCompleted30d++;
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Skip malformed JSON
                        }
                    }

                    // ── 6b. Active members (activity windows + engagement) — in-memory ───────
                    int activeMembers30d = 0;
                    int activeMembers90d = 0;
                    int activeMembers365d = 0;
                    int webActive30d = 0;
                    int mobileActive30d = 0;
                    int bothActive30d = 0;
                    int engagementActive = 0;
                    int engagementOccasional = 0;
                    int engagementInfrequent = 0;
                    int engagementDormant = 0;
                    var activeCountByClub = new Dictionary<int, int>();

                    foreach (var m in approvedMembers)
                    {
                        DateTime? lastWeb = null;
                        DateTime? lastMobile = null;

                        if (m.HasProperty("lastActiveDate"))
                        {
                            var dateStr = m.GetValue<string>("lastActiveDate");
                            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var parsed))
                                lastWeb = parsed;
                        }

                        if (m.HasProperty("lastMobileActiveDate"))
                        {
                            var dateStr = m.GetValue<string>("lastMobileActiveDate");
                            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var parsed))
                                lastMobile = parsed;
                        }

                        if (lastWeb.HasValue)
                        {
                            if (lastWeb.Value >= thirtyDaysAgo) activeMembers30d++;
                            if (lastWeb.Value >= ninetyDaysAgo) activeMembers90d++;
                            if (lastWeb.Value >= yearAgo) activeMembers365d++;
                        }

                        bool isWeb30 = lastWeb.HasValue && lastWeb.Value >= thirtyDaysAgo;
                        bool isMobile30 = lastMobile.HasValue && lastMobile.Value >= thirtyDaysAgo;
                        if (isWeb30) webActive30d++;
                        if (isMobile30) mobileActive30d++;
                        if (isWeb30 && isMobile30) bothActive30d++;

                        var lastAny = lastWeb;
                        if (lastMobile.HasValue && (!lastAny.HasValue || lastMobile.Value > lastAny.Value))
                            lastAny = lastMobile;

                        if (!lastAny.HasValue)
                            engagementDormant++;
                        else if (lastAny.Value >= thirtyDaysAgo)
                            engagementActive++;
                        else if (lastAny.Value >= ninetyDaysAgo)
                            engagementOccasional++;
                        else if (lastAny.Value >= yearAgo)
                            engagementInfrequent++;
                        else
                            engagementDormant++;

                        // Track active members per club (active = any activity within 30d)
                        if (lastAny.HasValue && lastAny.Value >= thirtyDaysAgo)
                        {
                            var clubIdStr = m.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(clubIdStr) && int.TryParse(clubIdStr, out var cid) && cid > 0)
                            {
                                activeCountByClub[cid] = activeCountByClub.GetValueOrDefault(cid, 0) + 1;
                            }
                        }
                    }

                    // Reuse the clubName/Url lookups built earlier so the top-clubs cards
                    // on the frontend can render each row as a link to the club's page.
                    var topClubsByActiveMembers = activeCountByClub
                        .Where(kvp => clubNameLookup.ContainsKey(kvp.Key))
                        .Select(kvp => new
                        {
                            clubId = kvp.Key,
                            club = clubNameLookup[kvp.Key],
                            count = kvp.Value,
                            url = clubUrlLookup.GetValueOrDefault(kvp.Key, "")
                        })
                        .OrderByDescending(x => x.count)
                        .Take(25)
                        .Cast<object>()
                        .ToList();

                    // ── 7. Disk space usage (from pre-fetched storageByOwner) ───────
                    var totalDiskSpaceMB = _configuration.GetValue<int>("SiteSettings:TotalDiskSpaceMB", 5000);

                    var storageLookup = storageByOwner
                        .GroupBy(r => new { OwnerType = (int)r.OwnerType, OwnerId = (int)r.OwnerId })
                        .ToDictionary(g => g.Key, g => g.Sum(r => (long)r.UsedBytes));

                    // Build quota lookup from pre-loaded data
                    var quotaLookup = storageQuotas.ToDictionary(
                        q => (q.OwnerType, q.OwnerId), q => q.StorageLimitMB);
                    int defaultClubLimitMB = _configuration.GetValue("DocumentArchive:DefaultClubStorageLimitMB", 100);
                    int defaultRegionLimitMB = _configuration.GetValue("DocumentArchive:DefaultRegionStorageLimitMB", 200);

                    int GetLimitMB(int ownerType, int ownerId) =>
                        quotaLookup.TryGetValue((ownerType, ownerId), out var custom)
                            ? custom
                            : (ownerType == DocumentOwnerType.Region ? defaultRegionLimitMB : defaultClubLimitMB);

                    var clubStorageList = allClubNodes.Select(club =>
                    {
                        var key = new { OwnerType = DocumentOwnerType.Club, OwnerId = club.Id };
                        var usedBytes = storageLookup.GetValueOrDefault(key, 0L);
                        var limitMB = GetLimitMB(DocumentOwnerType.Club, club.Id);
                        var limitBytes = (long)limitMB * 1024 * 1024;
                        var pct = limitBytes > 0 ? Math.Round((double)usedBytes / limitBytes * 100, 1) : 0;
                        return new
                        {
                            name = club.Name ?? "",
                            usedMB = Math.Round((double)usedBytes / (1024 * 1024), 1),
                            limitMB,
                            percentage = pct
                        };
                    })
                    .Where(x => x.usedMB > 0)
                    .OrderByDescending(x => x.usedMB)
                    .Cast<object>()
                    .ToList();

                    var regionStorageList = regionalPages.Select(rp =>
                    {
                        var key = new { OwnerType = DocumentOwnerType.Region, OwnerId = rp.Id };
                        var usedBytes = storageLookup.GetValueOrDefault(key, 0L);
                        var limitMB = GetLimitMB(DocumentOwnerType.Region, rp.Id);
                        var limitBytes = (long)limitMB * 1024 * 1024;
                        var pct = limitBytes > 0 ? Math.Round((double)usedBytes / limitBytes * 100, 1) : 0;
                        return new
                        {
                            name = rp.Name ?? "",
                            usedMB = Math.Round((double)usedBytes / (1024 * 1024), 1),
                            limitMB,
                            percentage = pct
                        };
                    })
                    .Where(x => x.usedMB > 0)
                    .OrderByDescending(x => x.usedMB)
                    .Cast<object>()
                    .ToList();

                    var totalUsedMB = Math.Round((double)totalUsedBytes / (1024 * 1024), 1);
                    var totalDiskPercentage = totalDiskSpaceMB > 0
                        ? Math.Round(totalUsedMB / totalDiskSpaceMB * 100, 1)
                        : 0;

                    return new
                    {
                        totalMembers,
                        newMembers30d,
                        totalClubs,
                        clubsWithMembers,
                        activeClubs,
                        newClubs30d,
                        competitionsThisYear = competitionsThisYearCount,
                        trainingMatches30d,
                        newMembersPerMonth,
                        membersByRegion,
                        competitionsByDiscipline,
                        topCompetitions,
                        trainingMatchesPerMonth,
                        trainingMatchesTotal,
                        trainingMatchParticipants30d,
                        trainingMatchClubs30d,
                        trainingStairsActiveTrainees,
                        trainingStairsStepsCompleted30d,
                        trainingStairsActiveGroups,
                        trainingStairsMembersPerLevel,
                        trainingScoresPerMonth,
                        trainingScoresThisYear,
                        markenSeries = markenSeriesStats,
                        scoresByWeaponClass,
                        scoresByDiscipline,
                        uniqueTrainers30d,
                        activeMembers30d,
                        activeMembers90d,
                        activeMembers365d,
                        clubActivities = new
                        {
                            eventsThisYear = clubEventsThisYear,
                            eventsLastYear = clubEventsLastYear,
                            eventsYearBefore = clubEventsYearBefore,
                            events30d = clubEvents30d,
                            byType = clubEventsByType,
                            perMonth = clubEventsPerMonth,
                            topClubs = topClubsByEvents,
                            topClubsByMembers,
                            topClubsByActiveMembers
                        },
                        engagement = new
                        {
                            webActive30d,
                            mobileActive30d,
                            bothActive30d,
                            funnel = new { active = engagementActive, occasional = engagementOccasional, infrequent = engagementInfrequent, dormant = engagementDormant },
                            devicesByPlatform,
                            uniqueLogins30d
                        },
                        diskSpace = new
                        {
                            totalDiskSpaceMB,
                            totalUsedMB,
                            totalDiskPercentage,
                            clubStorage = clubStorageList,
                            regionStorage = regionStorageList
                        },
                        styrelse = styrelseStats,
                        faltkonfig = faltkonfigStats,
                        aiChat = BuildAiChatStats()
                    };
                }
            }

            // Fallback if no content available
            return new
            {
                totalMembers,
                newMembers30d,
                totalClubs = 0,
                clubsWithMembers = 0,
                activeClubs = 0,
                newClubs30d = 0,
                competitionsThisYear = 0,
                trainingMatches30d = 0,
                newMembersPerMonth,
                membersByRegion,
                competitionsByDiscipline = new List<object>(),
                topCompetitions = new List<object>(),
                trainingMatchesPerMonth = new List<object>(),
                trainingMatchesTotal = 0,
                trainingMatchParticipants30d = 0,
                trainingMatchClubs30d = 0,
                trainingStairsActiveTrainees = 0,
                trainingStairsStepsCompleted30d = 0,
                trainingStairsActiveGroups = 0,
                trainingStairsMembersPerLevel = new List<object>(),
                trainingScoresPerMonth = new List<object>(),
                trainingScoresThisYear = 0,
                markenSeries = ZeroMarkenSeriesStats(),
                scoresByWeaponClass = new List<object>(),
                scoresByDiscipline = new List<object>(),
                uniqueTrainers30d = 0,
                activeMembers30d = 0,
                activeMembers90d = 0,
                activeMembers365d = 0,
                clubActivities = new
                {
                    eventsThisYear = 0,
                    eventsLastYear = 0,
                    eventsYearBefore = 0,
                    events30d = 0,
                    byType = new List<object>(),
                    perMonth = new List<object>(),
                    topClubs = new List<object>(),
                    topClubsByMembers = new List<object>(),
                    topClubsByActiveMembers = new List<object>()
                },
                engagement = new
                {
                    webActive30d = 0,
                    mobileActive30d = 0,
                    bothActive30d = 0,
                    funnel = new { active = 0, occasional = 0, infrequent = 0, dormant = 0 },
                    devicesByPlatform = new List<object>(),
                    uniqueLogins30d = 0
                },
                diskSpace = new
                {
                    totalDiskSpaceMB = _configuration.GetValue<int>("SiteSettings:TotalDiskSpaceMB", 5000),
                    totalUsedMB = 0.0,
                    totalDiskPercentage = 0.0,
                    clubStorage = new List<object>(),
                    regionStorage = new List<object>()
                },
                styrelse = ZeroStyrelseStats(),
                faltkonfig = ZeroFaltkonfigStats(),
                aiChat = BuildAiChatStats()
            };
        }

        private object BuildAiChatStats()
        {
            var entries = ParseChatLogs();
            var now = DateTime.UtcNow;
            var thirtyDaysAgo = now.AddDays(-30);

            var totalMessages = entries.Count;
            var messages30d = entries.Count(e => e.Timestamp >= thirtyDaysAgo);
            var uniqueUsers = entries.Select(e => e.User).Distinct().Count();
            var uniqueUsers30d = entries.Where(e => e.Timestamp >= thirtyDaysAgo).Select(e => e.User).Distinct().Count();

            var perMonth = entries
                .GroupBy(e => e.Timestamp.ToString("yyyy-MM"))
                .OrderBy(g => g.Key)
                .Select(g => new { month = g.Key, count = g.Count() })
                .ToList();

            var topUsers = entries
                .GroupBy(e => e.User)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new { user = g.Key, count = g.Count() })
                .ToList();

            var recentEntries = entries
                .OrderByDescending(e => e.Timestamp)
                .Take(50)
                .Select(e => new { timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm"), user = e.User, question = e.Question, answer = Truncate(e.Answer, 200) })
                .ToList();

            return new { totalMessages, messages30d, uniqueUsers, uniqueUsers30d, perMonth, topUsers, recentEntries };
        }

        private List<ChatLogEntry> ParseChatLogs()
        {
            var entries = new List<ChatLogEntry>();
            var logDirs = new[]
            {
                Path.Combine(_env.ContentRootPath, "App_Data", "AiChatLogs"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "AiChatLogs"),
            };

            var logDir = logDirs.FirstOrDefault(Directory.Exists);
            if (logDir == null) return entries;

            foreach (var file in Directory.GetFiles(logDir, "chat-*.log"))
            {
                try
                {
                    var content = System.IO.File.ReadAllText(file);
                    var blocks = content.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);

                    foreach (var block in blocks)
                    {
                        var lines = block.Trim().Split('\n');
                        if (lines.Length < 3) continue;

                        // Parse: [2026-04-10 14:32:15] user@example.com
                        var headerMatch = Regex.Match(lines[0], @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s+(.+)");
                        if (!headerMatch.Success) continue;

                        if (!DateTime.TryParseExact(headerMatch.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                            continue;

                        var user = headerMatch.Groups[2].Value.Trim();
                        var question = lines.Length > 1 && lines[1].StartsWith("Q: ") ? lines[1].Substring(3) : "";
                        var answerLines = lines.Skip(2).Where(l => l.StartsWith("A: ") || !l.StartsWith("Q: ")).ToList();
                        var answer = string.Join(" ", answerLines).Replace("A: ", "");

                        entries.Add(new ChatLogEntry { Timestamp = timestamp, User = user, Question = question, Answer = answer });
                    }
                }
                catch { }
            }

            return entries;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        private class ChatLogEntry
        {
            public DateTime Timestamp { get; set; }
            public string User { get; set; } = "";
            public string Question { get; set; } = "";
            public string Answer { get; set; } = "";
        }
    }
}
