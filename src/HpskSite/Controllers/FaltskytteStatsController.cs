using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fältskytte competition flow/statistics page (/faltskytte/statistik/{competitionId}).
    /// Visualises how patrols moved through the stations and where the bottlenecks are,
    /// derived from result-entry timestamps (EnteredAt, the immutable first-entry time).
    /// Staff-gated. Reached from the "Stationer" tab.
    /// </summary>
    [Route("faltskytte/statistik/{competitionId:int}")]
    public class FaltskytteStatsController : Controller
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _dbFactory;
        private readonly AdminAuthorizationService _auth;

        public FaltskytteStatsController(IContentService contentService, IUmbracoDatabaseFactory dbFactory, AdminAuthorizationService auth)
        {
            _contentService = contentService;
            _dbFactory = dbFactory;
            _auth = auth;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            if (!await IsStaffForCompetition(competitionId)) return Unauthorized();

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var config = FaltskytteConfigParser.Parse(competition.GetValue<string>("stationConfig") ?? "");
            var firstWc = config.WeaponConfigs.Values.FirstOrDefault();
            var allStations = firstWc?.Stations ?? new List<FaltskytteStationConfig>();
            var stationNumbers = allStations.Where(s => !s.IsShootOffOnly).Select(s => s.Station).Distinct().OrderBy(n => n).ToList();
            var shootOff = allStations.Where(s => s.IsShootOffOnly).Select(s => s.Station).ToHashSet();

            using var db = _dbFactory.CreateDatabase();
            var results = (await db.FetchAsync<FaltskytteResultEntry>("WHERE CompetitionId = @0", competitionId))
                .Where(r => !shootOff.Contains(r.StationNumber)).ToList();
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0", competitionId);
            var wgByPatrol = patrols.GroupBy(p => p.PatrolNumber)
                .ToDictionary(g => g.Key, g => string.IsNullOrEmpty(g.First().WeaponGroup) ? "?" : g.First().WeaponGroup!);

            // Completion per (patrol, station) = latest first-entry among the patrol's shooters there.
            var compl = results
                .GroupBy(r => new { r.PatrolNumber, r.StationNumber })
                .Select(g => new { g.Key.PatrolNumber, g.Key.StationNumber, Time = g.Max(r => r.EnteredAt) })
                .ToList();

            var points = compl.Select(c => new
            {
                patrol = c.PatrolNumber,
                station = c.StationNumber,
                wg = wgByPatrol.TryGetValue(c.PatrolNumber, out var w) ? w : "?",
                t = c.Time
            }).ToList();

            // Per-patrol "leg" = time from completing the previous station (in time order) to this one,
            // attributed to the station just completed → the slowest leg on average = the bottleneck.
            var legByStation = new Dictionary<int, List<double>>();
            var patrolTotals = new List<double>();
            foreach (var g in compl.GroupBy(c => c.PatrolNumber))
            {
                var ordered = g.OrderBy(x => x.Time).ToList();
                if (ordered.Count >= 2)
                    patrolTotals.Add((ordered[^1].Time - ordered[0].Time).TotalMinutes);
                for (int i = 1; i < ordered.Count; i++)
                {
                    var mins = (ordered[i].Time - ordered[i - 1].Time).TotalMinutes;
                    if (!legByStation.TryGetValue(ordered[i].StationNumber, out var lst)) { lst = new List<double>(); legByStation[ordered[i].StationNumber] = lst; }
                    lst.Add(mins);
                }
            }

            var stationStats = stationNumbers.Select(s => new
            {
                station = s,
                patrolsDone = compl.Count(c => c.StationNumber == s),
                avgLegMinutes = legByStation.TryGetValue(s, out var l) && l.Count > 0 ? Math.Round(l.Average(), 1) : 0.0
            }).ToList();

            int? bottleneck = stationStats.Where(s => s.avgLegMinutes > 0)
                .OrderByDescending(s => s.avgLegMinutes).Select(s => (int?)s.station).FirstOrDefault();

            var model = new FaltskytteStatsModel
            {
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                PatrolCount = compl.Select(c => c.PatrolNumber).Distinct().Count(),
                StationCount = stationNumbers.Count,
                BottleneckStation = bottleneck,
                AvgPatrolMinutes = patrolTotals.Count > 0 ? Math.Round(patrolTotals.Average(), 0) : 0,
                StationsJson = JsonSerializer.Serialize(stationStats),
                PointsJson = JsonSerializer.Serialize(points)
            };

            return View("~/Views/FaltskytteStats.cshtml", model);
        }

        private async Task<bool> IsStaffForCompetition(int competitionId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (await _auth.IsCompetitionManager(competitionId)) return true;
            if ((await _auth.GetManagedRegions()).Any()) return true;
            var comp = _contentService.GetById(competitionId);
            var clubId = comp?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0 && (await _auth.IsClubAdminForClub(clubId) || await _auth.IsSkjutledareForClub(clubId)))
                return true;
            return false;
        }
    }
}
