using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kretsgranskning per krets för Statistik-fliken (fas 4): vilka kretsar som har kommit igång —
    /// uppdragen tillsatta, inställningarna valda — och vad som faktiskt händer i ansökningarna,
    /// resultatgranskningen och bangranskningen.
    ///
    /// <para><b>Varför per krets:</b> det är kretsarna som ska flytta in, och en total ("12
    /// ansökningar") säger inte om det är tolv kretsar eller en. Tabellen visar varje krets på en rad,
    /// också de som inte gjort något — en saknad rad läses som "finns inte".</para>
    ///
    /// <para>Egen endpoint, som ekonomin: läser fyra tabeller och får aldrig hålla tillbaka resten av
    /// sidan. Varje källa ligger i en egen try — en saknad tabell ger tomma tal och en namngiven
    /// varning i stället för att sektionen faller.</para>
    /// </summary>
    public partial class AdminStatisticsController
    {
        private const string KretsgranskningCacheKey = "admin_kretsgranskning_stats";

        [HttpGet]
        public async Task<IActionResult> GetKretsgranskningStats(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Access denied" });

            if (!force && _memoryCache.TryGetValue(KretsgranskningCacheKey, out object? cached) && cached != null)
                return Json(cached);

            try
            {
                var result = new { success = true, data = BuildKretsgranskningStats() };
                _memoryCache.Set(KretsgranskningCacheKey, result, CacheDuration);
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building kretsgranskning statistics");
                return Json(new { success = false, message = "Kretsgranskningens statistik kunde inte läsas: " + ex.Message });
            }
        }

        private sealed class KgRow
        {
            public int RegionId; public string Name = "";
            public bool HasResultatgranskare, HasBangranskare, HasTavlingsansvarig;
            public bool RequireResultApproval, RequireCourseReview, OwnNeighbours;
            public int Deadlines, ChecklistItems;
            public int AppsAtKrets, AppsAtForbundet, AppsGranted, AppsRejected, AppsTotal;
            public int ResultPending, ResultApproved, ResultTotal;
            public int CoursePending, CourseApproved, CourseTotal;
            public DateTime? LastActivity;
        }

        private sealed class CountRow { public int RegionId { get; set; } public string Status { get; set; } = ""; public int N { get; set; } public DateTime? Last { get; set; } }
        private sealed class RoleRow { public int OwnerId { get; set; } public string RoleKey { get; set; } = ""; }

        private object BuildKretsgranskningStats()
        {
            var rows = new Dictionary<int, KgRow>();
            var excludedClubIds = new HashSet<int>();
            var excludedRegionIds = new HashSet<int>();
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                if (root != null)
                {
                    ComputeDemoExclusions(root, excludedClubIds, excludedRegionIds);
                    foreach (var rp in root.Children.Where(c => c.ContentType.Alias == "regionalPage" && !excludedRegionIds.Contains(c.Id)))
                        rows[rp.Id] = new KgRow { RegionId = rp.Id, Name = rp.Value<string>("regionName") is { Length: > 0 } n ? n : rp.Name ?? "" };
                }
            }

            var warnings = new List<string>();
            using var db = _databaseFactory.CreateDatabase();

            void Each(string label, string sql, Action<KgRow, CountRow> apply)
            {
                try
                {
                    foreach (var c in db.Fetch<CountRow>(sql))
                        if (rows.TryGetValue(c.RegionId, out var r))
                        {
                            apply(r, c);
                            if (c.Last != null && (r.LastActivity == null || c.Last > r.LastActivity)) r.LastActivity = c.Last;
                        }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kretsgranskningens statistik: {Label} kunde inte läsas.", label);
                    warnings.Add(label + " kunde inte läsas (saknas tabellen?).");
                }
            }

            static bool In(string s, IEnumerable<string> set) => set.Contains(s);

            Each("Ansökningarna",
                "SELECT RegionId, Status, COUNT(*) AS N, MAX(UpdatedAt) AS Last FROM CompetitionApplication GROUP BY RegionId, Status",
                (r, c) =>
                {
                    if (c.Status == CompetitionApplicationStatus.Utkast) return;   // ett utkast har kretsen inte sett
                    r.AppsTotal += c.N;
                    if (In(c.Status, CompetitionApplicationStatus.AtKrets)) r.AppsAtKrets += c.N;
                    else if (c.Status == CompetitionApplicationStatus.HosForbundet) r.AppsAtForbundet += c.N;
                    else if (c.Status == CompetitionApplicationStatus.Beviljad) r.AppsGranted += c.N;
                    else if (c.Status == CompetitionApplicationStatus.Avslagen) r.AppsRejected += c.N;
                });
            Each("Resultatgranskningen",
                "SELECT RegionId, Status, COUNT(*) AS N, MAX(UpdatedAt) AS Last FROM CompetitionResultReview GROUP BY RegionId, Status",
                (r, c) =>
                {
                    r.ResultTotal += c.N;
                    if (c.Status == ResultReviewStatus.Inskickad) r.ResultPending += c.N;
                    else if (c.Status == ResultReviewStatus.Godkand) r.ResultApproved += c.N;
                });
            Each("Bangranskningen",
                "SELECT RegionId, Status, COUNT(*) AS N, MAX(UpdatedAt) AS Last FROM CompetitionCourseReview WHERE Route = 'Krets' GROUP BY RegionId, Status",
                (r, c) =>
                {
                    r.CourseTotal += c.N;
                    if (c.Status == CourseReviewStatus.Inskickad) r.CoursePending += c.N;
                    else if (c.Status == CourseReviewStatus.Godkand) r.CourseApproved += c.N;
                });
            Each("Sista ansökningsdagar",
                "SELECT RegionId, '' AS Status, COUNT(*) AS N, CAST(NULL AS datetime) AS Last FROM RegionApplicationDeadline GROUP BY RegionId",
                (r, c) => r.Deadlines = c.N);
            Each("Arrangörschecklistan",
                "SELECT RegionId, '' AS Status, COUNT(*) AS N, MAX(UpdatedAt) AS Last FROM RegionOrganiserChecklistItem WHERE IsActive = 1 GROUP BY RegionId",
                (r, c) => r.ChecklistItems = c.N);

            try
            {
                foreach (var s in db.Fetch<RegionCalendarSettings>("SELECT * FROM RegionCalendarSettings"))
                    if (rows.TryGetValue(s.RegionId, out var r))
                    {
                        r.RequireResultApproval = s.RequireResultApproval;
                        r.RequireCourseReview = s.RequireCourseReview;
                        r.OwnNeighbours = s.NeighbourOverrides != null;
                    }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Kretsgranskningens statistik: inställningarna kunde inte läsas."); warnings.Add("Kretsarnas inställningar kunde inte läsas."); }

            try
            {
                var keys = new[] { BoardRoleDefinitions.RoleResultatgranskare, BoardRoleDefinitions.RoleBangranskare, BoardRoleDefinitions.RoleTavlingsansvarig };
                foreach (var rr in db.Fetch<RoleRow>("SELECT DISTINCT OwnerId, RoleKey FROM BoardRoles WHERE OwnerType = 1 AND IsActive = 1 AND RoleKey IN (@0, @1, @2)", keys[0], keys[1], keys[2]))
                    if (rows.TryGetValue(rr.OwnerId, out var r))
                    {
                        if (rr.RoleKey == keys[0]) r.HasResultatgranskare = true;
                        else if (rr.RoleKey == keys[1]) r.HasBangranskare = true;
                        else r.HasTavlingsansvarig = true;
                    }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Kretsgranskningens statistik: uppdragen kunde inte läsas."); warnings.Add("Kretsarnas uppdrag kunde inte läsas."); }

            var list = rows.Values.OrderBy(r => r.Name).ToList();
            // "Igång" = minst ett uppdrag tillsatt eller minst ett ärende — en krets som bara finns som nod har inte börjat.
            bool Started(KgRow r) => r.HasResultatgranskare || r.HasBangranskare || r.HasTavlingsansvarig || r.AppsTotal + r.ResultTotal + r.CourseTotal > 0;
            return new
            {
                totals = new
                {
                    regions = list.Count,
                    started = list.Count(Started),
                    withAnyUppdrag = list.Count(r => r.HasResultatgranskare || r.HasBangranskare || r.HasTavlingsansvarig),
                    applications = list.Sum(r => r.AppsTotal),
                    applicationsAtKrets = list.Sum(r => r.AppsAtKrets),
                    resultReviews = list.Sum(r => r.ResultTotal),
                    resultPending = list.Sum(r => r.ResultPending),
                    courseReviews = list.Sum(r => r.CourseTotal),
                    coursePending = list.Sum(r => r.CoursePending),
                    requireResult = list.Count(r => r.RequireResultApproval),
                    requireCourse = list.Count(r => r.RequireCourseReview),
                    withChecklist = list.Count(r => r.ChecklistItems > 0)
                },
                regions = list.Select(r => new
                {
                    r.RegionId, r.Name, started = Started(r),
                    uppdrag = new { resultatgranskare = r.HasResultatgranskare, bangranskare = r.HasBangranskare, tavlingsansvarig = r.HasTavlingsansvarig },
                    settings = new { requireResult = r.RequireResultApproval, requireCourse = r.RequireCourseReview, ownNeighbours = r.OwnNeighbours, deadlines = r.Deadlines, checklist = r.ChecklistItems },
                    applications = new { total = r.AppsTotal, atKrets = r.AppsAtKrets, atForbundet = r.AppsAtForbundet, granted = r.AppsGranted, rejected = r.AppsRejected },
                    results = new { total = r.ResultTotal, pending = r.ResultPending, approved = r.ResultApproved },
                    courses = new { total = r.CourseTotal, pending = r.CoursePending, approved = r.CourseApproved },
                    lastActivity = r.LastActivity?.ToString("yyyy-MM-dd")
                }),
                warnings
            };
        }
    }
}
