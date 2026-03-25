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
using System.Text.Json;

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
            ILogger<AdminStatisticsController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _authService = authService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _databaseFactory = databaseFactory;
            _memoryCache = memoryCache;
            _logger = logger;
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
            int newMembersThisMonth = approvedMembers.Count(m => m.CreateDate >= currentMonthStart);

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

                    // Active club: has ≥1 approved member AND has event within last 3 months or future
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

                    var threeMonthsAgo = today.AddMonths(-3);
                    int activeClubs = allClubNodes.Count(club =>
                    {
                        if (!membersByClub.ContainsKey(club.Id)) return false;
                        // Check for events
                        var hasRecentEvent = club.Children
                            .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                            .Any(e =>
                            {
                                var eventDate = e.Value<DateTime?>("eventDate");
                                return eventDate.HasValue && eventDate.Value >= threeMonthsAgo;
                            });
                        return hasRecentEvent;
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

                    // ── 4. Training matches (SQL) ───────────────────────
                    int trainingMatches30d = 0;
                    int trainingMatchParticipants30d = 0;
                    int trainingMatchClubs30d = 0;
                    var trainingMatchesPerMonth = new List<object>();

                    using (var db = _databaseFactory.CreateDatabase())
                    {
                        // Per month (last 12)
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

                        // Matches 30d
                        trainingMatches30d = db.Single<int>(
                            "SELECT COUNT(*) FROM TrainingMatches WHERE CreatedDate >= @0", thirtyDaysAgo);

                        // Unique participants 30d
                        trainingMatchParticipants30d = db.Single<int>(@"
                            SELECT COUNT(DISTINCT tmp.MemberId)
                            FROM TrainingMatchParticipants tmp
                            JOIN TrainingMatches tm ON tm.Id = tmp.TrainingMatchId
                            WHERE tm.CreatedDate >= @0 AND tmp.MemberId > 0", thirtyDaysAgo);

                        // Unique clubs 30d — get distinct member IDs, resolve from loaded members
                        var participantMemberIds = db.Fetch<int>(@"
                            SELECT DISTINCT tmp.MemberId
                            FROM TrainingMatchParticipants tmp
                            JOIN TrainingMatches tm ON tm.Id = tmp.TrainingMatchId
                            WHERE tm.CreatedDate >= @0 AND tmp.MemberId > 0", thirtyDaysAgo);

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

                        // ── 6. Training scores per month ────────────────
                        var monthlyScores = db.Fetch<dynamic>(@"
                            SELECT YEAR(TrainingDate) AS Y, MONTH(TrainingDate) AS M, COUNT(*) AS Cnt
                            FROM TrainingScores
                            WHERE TrainingDate >= @0
                            GROUP BY YEAR(TrainingDate), MONTH(TrainingDate)
                            ORDER BY Y, M", yearAgo);

                        var scoresDict = monthlyScores.ToDictionary(
                            r => $"{(int)r.Y:D4}-{(int)r.M:D2}",
                            r => (int)r.Cnt);

                        var trainingScoresPerMonth = Enumerable.Range(0, 12)
                            .Select(i =>
                            {
                                var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11 + i);
                                var key = monthStart.ToString("yyyy-MM");
                                return (object)new { month = key, count = scoresDict.GetValueOrDefault(key, 0) };
                            })
                            .ToList();

                        // ── 5. Training stairs (Skyttetrappan) ──────────
                        int trainingStairsActiveTrainees = 0;
                        int trainingStairsStepsCompleted30d = 0;
                        int trainingStairsActiveGroups = 0;
                        var trainingStairsMembersPerLevel = new List<object>();

                        // Active trainees: members with trainingStartDate not null
                        trainingStairsActiveTrainees = approvedMembers.Count(m =>
                        {
                            if (!m.HasProperty("trainingStartDate")) return false;
                            var val = m.GetValue<string>("trainingStartDate");
                            return !string.IsNullOrEmpty(val);
                        });

                        // Members per level
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

                        // Steps completed in last 30 days
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

                        // Active training groups
                        trainingStairsActiveGroups = db.Single<int>(
                            "SELECT COUNT(*) FROM TrainingGroups WHERE IsActive = 1");

                        // ── 6b. Active members (activity windows) ───────
                        int activeMembers30d = 0;
                        int activeMembers90d = 0;
                        int activeMembers365d = 0;

                        foreach (var m in approvedMembers)
                        {
                            if (!m.HasProperty("lastActiveDate")) continue;
                            var dateStr = m.GetValue<string>("lastActiveDate");
                            if (string.IsNullOrEmpty(dateStr)) continue;
                            if (!DateTime.TryParse(dateStr, out var lastActive)) continue;

                            if (lastActive >= thirtyDaysAgo) activeMembers30d++;
                            if (lastActive >= ninetyDaysAgo) activeMembers90d++;
                            if (lastActive >= yearAgo) activeMembers365d++;
                        }

                        return new
                        {
                            totalMembers,
                            newMembersThisMonth,
                            totalClubs,
                            activeClubs,
                            newClubs30d,
                            competitionsThisYear = competitionsThisYearCount,
                            trainingMatches30d,
                            newMembersPerMonth,
                            membersByRegion,
                            competitionsByDiscipline,
                            topCompetitions,
                            trainingMatchesPerMonth,
                            trainingMatchParticipants30d,
                            trainingMatchClubs30d,
                            trainingStairsActiveTrainees,
                            trainingStairsStepsCompleted30d,
                            trainingStairsActiveGroups,
                            trainingStairsMembersPerLevel,
                            trainingScoresPerMonth,
                            activeMembers30d,
                            activeMembers90d,
                            activeMembers365d
                        };
                    }
                }
            }

            // Fallback if no content available
            return new
            {
                totalMembers,
                newMembersThisMonth,
                totalClubs = 0,
                activeClubs = 0,
                newClubs30d = 0,
                competitionsThisYear = 0,
                trainingMatches30d = 0,
                newMembersPerMonth,
                membersByRegion,
                competitionsByDiscipline = new List<object>(),
                topCompetitions = new List<object>(),
                trainingMatchesPerMonth = new List<object>(),
                trainingMatchParticipants30d = 0,
                trainingMatchClubs30d = 0,
                trainingStairsActiveTrainees = 0,
                trainingStairsStepsCompleted30d = 0,
                trainingStairsActiveGroups = 0,
                trainingStairsMembersPerLevel = new List<object>(),
                trainingScoresPerMonth = new List<object>(),
                activeMembers30d = 0,
                activeMembers90d = 0,
                activeMembers365d = 0
            };
        }
    }
}
