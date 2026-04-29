using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
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
    /// Statistik tab data for the Club admin panel. Same shape as AdminStatisticsController
    /// but scoped to a single club. All numbers are filtered to members whose primary club
    /// is the requested clubId.
    /// </summary>
    public class ClubStatisticsController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authService;
        private readonly ClubComparisonService _comparisonService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<ClubStatisticsController> _logger;

        public ClubStatisticsController(
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
            ILogger<ClubStatisticsController> logger)
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
        public async Task<IActionResult> GetClubStatistics(int clubId, bool force = false)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });

            if (!await _authService.IsClubAdminForClub(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            var cacheKey = $"club_statistics_{clubId}";
            if (!force && _memoryCache.TryGetValue(cacheKey, out object? cached) && cached != null)
            {
                return Json(cached);
            }

            try
            {
                var data = await BuildAsync(clubId);
                var result = new { success = true, data };
                _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building club statistics for club {ClubId}", clubId);
                return Json(new { success = false, message = "Fel vid hämtning av statistik: " + ex.Message });
            }
        }

        private async Task<object> BuildAsync(int clubId)
        {
            var today = DateTime.Today;
            var thirtyDaysAgo = today.AddDays(-30);
            var ninetyDaysAgo = today.AddDays(-90);
            var twelveMonthsAgo = today.AddMonths(-12);
            var currentYearStart = new DateTime(today.Year, 1, 1);
            var currentYearEnd = currentYearStart.AddYears(1);

            // ── Resolve club content node ──────────────────────────────
            string clubName = "";
            IPublishedContent? clubNode = null;
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                clubNode = ctx.Content.GetById(clubId);
                clubName = clubNode?.Name ?? clubNode?.Value<string>("clubName") ?? "";
            }

            // ── Members of this club ───────────────────────────────────
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).ToList();
            var clubMembers = allMembers.Where(m => GetPrimaryClubId(m) == clubId && m.IsApproved).ToList();
            var pendingApprovals = allMembers.Count(m => GetPrimaryClubId(m) == clubId && !m.IsApproved);

            int totalMembers = clubMembers.Count;
            int newMembers30d = clubMembers.Count(m => m.CreateDate >= thirtyDaysAgo);

            int active30d = clubMembers.Count(m =>
            {
                var web = m.GetValue<DateTime?>("lastActiveDate");
                var mob = m.GetValue<DateTime?>("lastMobileActiveDate");
                var seen = MaxNullable(web, mob);
                return seen.HasValue && seen.Value >= thirtyDaysAgo;
            });

            int inactive90d = clubMembers.Count(m =>
            {
                if (m.CreateDate >= ninetyDaysAgo) return false;
                var web = m.GetValue<DateTime?>("lastActiveDate");
                var mob = m.GetValue<DateTime?>("lastMobileActiveDate");
                var seen = MaxNullable(web, mob);
                return !seen.HasValue || seen.Value < ninetyDaysAgo;
            });

            // New members per month (last 12 months)
            var newMembersPerMonth = Enumerable.Range(0, 12)
                .Select(i =>
                {
                    var monthStart = new DateTime(today.Year, today.Month, 1).AddMonths(-11 + i);
                    var monthEnd = monthStart.AddMonths(1);
                    return new
                    {
                        month = monthStart.ToString("yyyy-MM"),
                        count = clubMembers.Count(m => m.CreateDate >= monthStart && m.CreateDate < monthEnd)
                    };
                })
                .ToList();

            // Members per Skyttetrappan level (0-9)
            var levelCounts = new int[10];
            foreach (var m in clubMembers)
            {
                var lv = m.GetValue<int?>("currentTrainingLevel");
                if (lv.HasValue && lv.Value >= 0 && lv.Value < 10) levelCounts[lv.Value]++;
            }
            var membersByLevel = Enumerable.Range(0, 10)
                .Select(i => new { level = i, count = levelCounts[i] })
                .ToList();

            // Step completions in last 30d (parsed from JSON on each member)
            int stepCompletions30d = 0;
            foreach (var m in clubMembers)
            {
                var json = m.GetValue<string>("completedTrainingSteps");
                if (!string.IsNullOrWhiteSpace(json))
                    stepCompletions30d += CountRecentStepCompletions(json, thirtyDaysAgo);
            }

            // ── Skjutledare + Föreningsinstruktör + Vapenkontrollant + Banläggare presence ──
            // For a small list (typically <100 club members) it's fine to look up roles
            // per member. Föreningsinstruktör appointment is not strictly limited to club
            // members, so we ALSO check across all approved members for that one.
            var skjutledareRole = $"Skjutledare_{clubId}";
            var foreningsinstruktorRole = $"Foreningsinstruktor_{clubId}";
            int skjutledareCount = 0;
            int foreningsinstruktorCount = 0;
            int vapenkontrollantCount = 0;
            int banlaggareCount = 0;
            foreach (var m in clubMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id) ?? Enumerable.Empty<string>();
                if (roles.Contains(skjutledareRole)) skjutledareCount++;
                if (roles.Contains(foreningsinstruktorRole)) foreningsinstruktorCount++;
                if (roles.Contains("Vapenkontrollant")) vapenkontrollantCount++;
                if (roles.Contains("Banlaggare")) banlaggareCount++;
            }
            // Föreningsinstruktör can be appointed for the club without being a club member
            // — fall back to scanning all approved members if the club-member pass found none.
            if (foreningsinstruktorCount == 0)
            {
                foreach (var m in allMembers.Where(x => x.IsApproved && GetPrimaryClubId(x) != clubId))
                {
                    var roles = _memberService.GetAllRoles(m.Id);
                    if (roles != null && roles.Contains(foreningsinstruktorRole)) foreningsinstruktorCount++;
                }
            }

            // ── Events: upcoming + by-year + per-type + per-month ──────
            int upcomingEvents = 0;
            bool hasEventThisYear = false;
            int eventsThisYear = 0, eventsLastYear = 0, eventsYearBefore = 0, events30d = 0;
            var clubEventsByType = new List<object>();
            var clubEventsPerMonth = new List<object>();

            if (clubNode != null)
            {
                var clubEvents = clubNode.Children
                    .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                    .Select(e => new
                    {
                        eventType = e.Value<string>("eventType") ?? "Annat",
                        eventDate = e.Value<DateTime?>("eventDate")
                    })
                    .ToList();

                upcomingEvents = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value >= today);
                eventsThisYear = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == today.Year);
                eventsLastYear = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == today.Year - 1);
                eventsYearBefore = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value.Year == today.Year - 2);
                events30d = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value >= thirtyDaysAgo);
                hasEventThisYear = eventsThisYear > 0;

                clubEventsByType = clubEvents
                    .Where(e => e.eventDate.HasValue && e.eventDate.Value.Year == today.Year)
                    .GroupBy(e => e.eventType)
                    .Select(g => (object)new { type = g.Key, count = g.Count() })
                    .OrderByDescending(o => ((dynamic)o).count)
                    .ToList();

                clubEventsPerMonth = Enumerable.Range(1, 12)
                    .Select(m =>
                    {
                        var ms = new DateTime(today.Year, m, 1);
                        var me = ms.AddMonths(1);
                        return (object)new
                        {
                            month = ms.ToString("yyyy-MM"),
                            count = clubEvents.Count(e => e.eventDate.HasValue && e.eventDate.Value >= ms && e.eventDate.Value < me)
                        };
                    })
                    .ToList();
            }

            // ── Competitions hosted by this club (this year) ───────────
            int competitionsThisYear = 0;
            var competitionsByDiscipline = new List<object>();
            var topCompetitions = new List<object>();
            if (ctx?.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                var compsHub = root?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (compsHub != null)
                {
                    var clubCompsThisYear = compsHub.Descendants()
                        .Where(c => c.ContentType.Alias == "competition")
                        .Where(c => c.Value<int>("clubId") == clubId)
                        .Where(c =>
                        {
                            var d = c.Value<DateTime?>("competitionDate");
                            return d.HasValue && d.Value.Year == today.Year;
                        })
                        .ToList();

                    competitionsThisYear = clubCompsThisYear.Count;

                    competitionsByDiscipline = clubCompsThisYear
                        .GroupBy(c => c.Value<string>("competitionType") ?? "Okänd")
                        .Select(g => (object)new { discipline = g.Key, count = g.Count() })
                        .OrderByDescending(o => ((dynamic)o).count)
                        .ToList();

                    topCompetitions = clubCompsThisYear
                        .Select(c => new
                        {
                            name = c.Name ?? "",
                            discipline = c.Value<string>("competitionType") ?? "",
                            registrations = c.Descendants()
                                .Count(ch => ch.ContentType.Alias == "competitionRegistration" || ch.ContentType.Alias == "registration")
                        })
                        .OrderByDescending(c => c.registrations)
                        .Take(5)
                        .Cast<object>()
                        .ToList();
                }
            }

            // ── Training matches & scores (DB-backed) ───────────────────
            var memberIds = clubMembers.Select(m => m.Id).ToList();
            var trainingMatchesPerMonth = new List<object>();
            var trainingScoresByDiscipline = new List<object>();
            int trainingMatches30d = 0;
            int trainingScores30d = 0;
            var topActiveMembers = new List<object>();

            if (memberIds.Any())
            {
                using var db = _databaseFactory.CreateDatabase();

                // Build IN clause with @0...@N parameters
                var paramNames = string.Join(",", memberIds.Select((_, i) => "@" + i));
                var memberIdParams = memberIds.Cast<object>().ToArray();

                // Training matches per month (last 12 months)
                var matchRows = await db.FetchAsync<MatchByMonthRow>(
                    $@"SELECT YEAR(m.CreatedDate) AS Y, MONTH(m.CreatedDate) AS M, COUNT(DISTINCT m.Id) AS C
                       FROM TrainingMatches m
                       INNER JOIN TrainingMatchParticipants p ON p.TrainingMatchId = m.Id
                       WHERE m.CreatedDate >= @{memberIds.Count} AND p.MemberId IN ({paramNames})
                       GROUP BY YEAR(m.CreatedDate), MONTH(m.CreatedDate)",
                    memberIdParams.Concat(new object[] { twelveMonthsAgo }).ToArray());
                var matchMap = matchRows.ToDictionary(r => r.Y * 100 + r.M, r => r.C);
                trainingMatchesPerMonth = Enumerable.Range(0, 12)
                    .Select(i =>
                    {
                        var ms = new DateTime(today.Year, today.Month, 1).AddMonths(-11 + i);
                        return (object)new
                        {
                            month = ms.ToString("yyyy-MM"),
                            count = matchMap.GetValueOrDefault(ms.Year * 100 + ms.Month, 0)
                        };
                    })
                    .ToList();

                trainingMatches30d = await db.ExecuteScalarAsync<int>(
                    $@"SELECT COUNT(DISTINCT m.Id)
                       FROM TrainingMatches m
                       INNER JOIN TrainingMatchParticipants p ON p.TrainingMatchId = m.Id
                       WHERE m.CreatedDate >= @{memberIds.Count} AND p.MemberId IN ({paramNames})",
                    memberIdParams.Concat(new object[] { thirtyDaysAgo }).ToArray());

                // Training scores by discipline (last 12 months)
                var disciplineRows = await db.FetchAsync<DisciplineCountRow>(
                    $@"SELECT ISNULL(NULLIF(Discipline, ''), 'Precision') AS Discipline, COUNT(*) AS C
                       FROM TrainingScores
                       WHERE TrainingDate >= @{memberIds.Count} AND MemberId IN ({paramNames})
                       GROUP BY ISNULL(NULLIF(Discipline, ''), 'Precision')",
                    memberIdParams.Concat(new object[] { twelveMonthsAgo }).ToArray());
                trainingScoresByDiscipline = disciplineRows
                    .Select(r => (object)new { discipline = r.Discipline, count = r.C })
                    .ToList();

                trainingScores30d = await db.ExecuteScalarAsync<int>(
                    $@"SELECT COUNT(*)
                       FROM TrainingScores
                       WHERE TrainingDate >= @{memberIds.Count} AND MemberId IN ({paramNames})",
                    memberIdParams.Concat(new object[] { thirtyDaysAgo }).ToArray());

                // Top 5 most-active members (training scores last 30d)
                var topRows = await db.FetchAsync<MemberCountRow>(
                    $@"SELECT TOP 5 MemberId, COUNT(*) AS C
                       FROM TrainingScores
                       WHERE TrainingDate >= @{memberIds.Count} AND MemberId IN ({paramNames})
                       GROUP BY MemberId
                       ORDER BY COUNT(*) DESC",
                    memberIdParams.Concat(new object[] { thirtyDaysAgo }).ToArray());
                var nameLookup = clubMembers.ToDictionary(m => m.Id, m => m.Name ?? "");
                topActiveMembers = topRows
                    .Select(r => (object)new
                    {
                        memberId = r.MemberId,
                        name = FormatCompactName(nameLookup.GetValueOrDefault(r.MemberId, "")),
                        scoreCount = r.C
                    })
                    .ToList();
            }
            else
            {
                trainingMatchesPerMonth = Enumerable.Range(0, 12)
                    .Select(i =>
                    {
                        var ms = new DateTime(today.Year, today.Month, 1).AddMonths(-11 + i);
                        return (object)new { month = ms.ToString("yyyy-MM"), count = 0 };
                    })
                    .ToList();
            }

            // ── Comparison medians ──────────────────────────────────────
            var snapshot = await _comparisonService.GetSnapshotAsync();
            var comparison = _comparisonService.GetForClub(clubId, snapshot);

            // 12-month growth %
            int newMembers12m = clubMembers.Count(m => m.CreateDate >= twelveMonthsAgo);
            int prior = totalMembers - newMembers12m;
            int growthPct12m = prior <= 0 ? (newMembers12m > 0 ? 100 : 0) : (int)Math.Round(newMembers12m * 100.0 / prior);

            return new
            {
                clubName,
                summary = new
                {
                    totalMembers,
                    newMembers30d,
                    active30d,
                    active30dMedian = comparison.Active30dMedian,
                    upcomingEvents,
                    competitionsThisYear,
                    growthPct12m,
                    growthPct12mMedian = comparison.GrowthPct12mMedian,
                    comparableClubCount = comparison.ComparableClubCount
                },
                nudges = new
                {
                    pendingApprovals,
                    inactive90d,
                    noEventsThisYear = !hasEventThisYear,
                    noSkjutledare = skjutledareCount == 0,
                    noForeningsinstruktor = foreningsinstruktorCount == 0
                },
                instructors = new
                {
                    foreningsinstruktor = foreningsinstruktorCount,
                    vapenkontrollant = vapenkontrollantCount,
                    banlaggare = banlaggareCount
                },
                newMembersPerMonth,
                membersByLevel,
                trainingMatchesPerMonth,
                trainingMatches30d,
                trainingMatches30dMedian = comparison.TrainingMatches30dMedian,
                trainingScoresByDiscipline,
                trainingScores30d,
                trainingScores30dMedian = comparison.TrainingScores30dMedian,
                stepCompletions30d,
                stepCompletions30dMedian = comparison.StepCompletions30dMedian,
                competitionsByDiscipline,
                topCompetitions,
                clubActivities = new
                {
                    eventsThisYear,
                    eventsLastYear,
                    eventsYearBefore,
                    events30d,
                    byType = clubEventsByType,
                    perMonth = clubEventsPerMonth
                },
                topActiveMembers
            };
        }

        private static int GetPrimaryClubId(IMember m)
        {
            var s = m.GetValue<string>("primaryClubId");
            return !string.IsNullOrEmpty(s) && int.TryParse(s, out int id) ? id : 0;
        }

        private static DateTime? MaxNullable(DateTime? a, DateTime? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return a > b ? a : b;
        }

        private static int CountRecentStepCompletions(string json, DateTime since)
        {
            int count = 0;
            int idx = 0;
            while (true)
            {
                idx = json.IndexOf("CompletedDate", idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var quote = json.IndexOf('"', idx + "CompletedDate".Length + 1);
                if (quote < 0) break;
                var quoteEnd = json.IndexOf('"', quote + 1);
                if (quoteEnd < 0) break;
                var dateStr = json.Substring(quote + 1, quoteEnd - quote - 1);
                if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var dt) && dt >= since)
                {
                    count++;
                }
                idx = quoteEnd + 1;
            }
            return count;
        }

        private static string FormatCompactName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "";
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0];
            return parts[0] + " " + parts[^1][0] + ".";
        }

        private class MatchByMonthRow { public int Y { get; set; } public int M { get; set; } public int C { get; set; } }
        private class DisciplineCountRow { public string Discipline { get; set; } = ""; public int C { get; set; } }
        private class MemberCountRow { public int MemberId { get; set; } public int C { get; set; } }
    }
}
