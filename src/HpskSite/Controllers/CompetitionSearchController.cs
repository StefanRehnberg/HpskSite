using HpskSite.Models.Competitions;
using HpskSite.Services;
using HpskSite.Services.Competitions;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <b>Sök i alla år</b> på tävlingssidan (skalplanen, 2026-10-19). Sidan visar bara kommande tävlingar och de
    /// senaste 15 månaderna (<see cref="CompetitionHubWindow"/>); sökningen här går igenom alla, så att en äldre
    /// tävling aldrig ser ut att inte finnas. Samma synlighet som sidan: en klubbintern tävling bara för klubbens
    /// egna medlemmar. Öppen för alla, som sidan.
    /// </summary>
    public class CompetitionSearchController : SurfaceController
    {
        public const int MaxResults = 50;

        private readonly ICompetitionStore _competitions;
        private readonly ICompetitionSeriesStore _series;
        private readonly CompetitionLinks _links;
        private readonly ClubService _clubs;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;

        public CompetitionSearchController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            ICompetitionStore competitions, ICompetitionSeriesStore series, CompetitionLinks links, ClubService clubs,
            IMemberManager memberManager, IMemberService memberService, MemberClubService memberClubs)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _competitions = competitions;
            _series = series;
            _links = links;
            _clubs = clubs;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
        }

        [HttpGet]
        public async Task<IActionResult> Search(string? q)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Json(new { success = false, message = "Skriv minst två tecken." });

            var myClubIds = new HashSet<int>();
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is { } email)
                foreach (var id in _memberClubs.GetAllClubIds(_memberService.GetByEmail(email))) myClubIds.Add(id);

            var seriesNames = _series.List().ToDictionary(s => s.Id, s => s.SeriesName ?? s.Name);
            var clubNames = new Dictionary<int, string>();
            string ClubName(int id)
            {
                if (id <= 0) return "";
                if (!clubNames.TryGetValue(id, out var n)) clubNames[id] = n = _clubs.GetClubNameById(id) ?? "";
                return n;
            }

            var today = DateTime.Today;
            var hits = _competitions.List()
                .Where(c => !c.IsClubOnly || myClubIds.Contains(c.ClubId))
                .Select(c =>
                {
                    var series = c.SeriesId is int sid && seriesNames.TryGetValue(sid, out var sn) ? sn : "";
                    var type = HpskSite.Models.CompetitionTypes.GetFuzzy(c.CompetitionType ?? "")?.Name ?? c.CompetitionType ?? "";
                    var host = ClubName(c.ClubId);
                    var haystack = string.Join(" ", c.Name, c.CompetitionName, c.Venue, series, type, host, c.RegionalFederation,
                        c.CompetitionDate?.Year.ToString());
                    return new { c, series, type, host, haystack };
                })
                .Where(x => CompetitionHubWindow.Matches(x.haystack, q))
                .OrderByDescending(x => x.c.CompetitionDate ?? DateTime.MinValue)
                .ToList();

            return Json(new
            {
                success = true,
                total = hits.Count,
                truncated = hits.Count > MaxResults,
                results = hits.Take(MaxResults).Select(x => new
                {
                    id = x.c.Id,
                    name = x.c.Name,
                    date = x.c.CompetitionDate?.ToString("yyyy-MM-dd"),
                    endDate = x.c.LastDay is DateTime ld && x.c.CompetitionDate is DateTime cd && ld > cd.Date ? ld.ToString("yyyy-MM-dd") : null,
                    url = _links.Url(x.c.Id),
                    venue = x.c.Venue,
                    series = x.series,
                    type = x.type,
                    host = x.host,
                    inWindow = CompetitionHubWindow.InWindow(x.c, today),
                }),
            });
        }
    }
}
