using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Skjutkort: papperskortet som varje patrull bär med sig och som stationens funktionärer skriver resultatet på.
    /// För tävlingar där stationerna inte är uppkopplade eller där de digitala kunskaperna är små. Resultatet matas sedan in
    /// på /kortinmatning (samma skrivsätt: träff per figur, eller träff/figurer).
    ///
    /// Serverrenderad utskriftssida som stationskorten (<c>faltskytte/stationskort</c>): personalgrindad, öppnas i en ny flik.
    /// Geometrin (hur rutorna delar upp kortet på A4/A5) finns på ETT ställe: <see cref="ShootingCardLayout"/>.
    /// Anslagslistan (”Skriv ut” på Startlistor) är en annan sak: den visar starttider, inte resultat.
    /// </summary>
    [Route("faltskytte/skjutkort/{competitionId:int}")]
    [HpskSite.Services.Security.InCodeAuth("AdminAuthorizationService.HasCompetitionStaffAccessAsync(competitionId): personalen på tävlingen (samma grind som stationskorten)")]
    public class FaltskytteShootingCardController : Controller
    {
        private readonly IUmbracoDatabaseFactory _dbFactory;
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

        public FaltskytteShootingCardController(IUmbracoDatabaseFactory dbFactory, AdminAuthorizationService auth,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
        {
            _dbFactory = dbFactory;
            _auth = auth;
            _competitions = competitions;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId, string? layout = null, string? paper = null, int per = 1,
            int sum = 0, int notes = 1, int blank = 0, string? patrols = null, string? wg = null, int print = 0)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Unauthorized();
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            if ((competition.CompetitionType ?? "") is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            using var db = _dbFactory.CreateDatabase();
            var built = await ShootingCardBuilder.BuildAsync(db, config, competitionId,
                new ShootingCardSelection(ShootingCardBuilder.ParsePatrolNumbers(patrols), wg), Math.Clamp(blank, 0, 50));

            var options = new ShootingCardOptions
            {
                CompetitionName = competition.CompetitionName ?? competition.Name ?? "",
                Layout = ShootingCardLayout.NormalizeLayout(layout),
                Paper = ShootingCardLayout.NormalizePaper(paper),
                PerSheet = ShootingCardLayout.NormalizePerSheet(per),
                SumColumn = sum == 1,
                Notes = notes != 0,
                AutoPrint = print == 1,
                Notices = built.Notices
            };
            return Content(ShootingCardRenderer.RenderDocument(built.Cards, options), "text/html; charset=utf-8");
        }

        /// <summary>
        /// Rutornas bredd för varje pappersformat och layout, för den mest krävande stationsuppsättningen i tävlingen.
        /// Dialogen visar den som en varning innan man skriver ut. Samma räkning som utskriften (<see cref="ShootingCardLayout"/>).
        /// </summary>
        [HttpGet("matt")]
        public async Task<IActionResult> Matt(int competitionId, int sum = 0)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Json(new { success = false, message = "Du har inte behörighet." });
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            var stations = ShootingCardBuilder.MostDemanding(ShootingCardBuilder.AllConfigStations(config));
            if (stations == null) return Json(new { success = true, hasStations = false });

            object Entry(string layout, ShootingCardRect rect)
            {
                var m = ShootingCardLayout.Measure(layout, stations, ShootingCardLayout.MaxShootersPerCard, rect, sum == 1);
                var fit = ShootingCardLayout.Fit(layout, m.BoxW);
                return new { w = fit.BoxW, level = fit.Level };
            }

            return Json(new
            {
                success = true,
                hasStations = true,
                stationCount = stations.Count,
                figureCount = stations.Sum(s => s.Figures),
                poangStations = stations.Count(s => s.PoangmalCount > 0),
                formats = ShootingCardLayout.Formats.Select(f =>
                {
                    var rect = ShootingCardLayout.Cards(f.Paper, f.PerSheet)[0];
                    return new
                    {
                        id = f.Id, label = f.Label, paper = f.Paper, perSheet = f.PerSheet,
                        fig = Entry(ShootingCardLayout.Figures, rect),
                        tot = Entry(ShootingCardLayout.Total, rect)
                    };
                })
            });
        }
    }
}
