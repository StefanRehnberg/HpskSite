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
        private readonly ILogger<FaltskytteShootingCardController> _log;

        public FaltskytteShootingCardController(IUmbracoDatabaseFactory dbFactory, AdminAuthorizationService auth,
            HpskSite.Services.Competitions.ICompetitionStore competitions, ILogger<FaltskytteShootingCardController> log)
        {
            _dbFactory = dbFactory;
            _auth = auth;
            _competitions = competitions;
            _log = log;
        }

        /// <param name="layout">Layoutens id (<see cref="ShootingCardLayouts"/>): fig, tot, blk (Skyttblock), card (Skyttkort). Okänt id ger en synlig notis.</param>
        /// <param name="poang">row = poängrutorna på en egen rad/i egna kolumner (Skyttblock, Skyttkort); annars vid figuren.</param>
        /// <param name="names">blank = tomma rader i stället för förtryckta namn (Skyttblock, Skyttkort).</param>
        /// <param name="color">1 = färg som komplement för att skilja rutsorterna åt (Skyttblock, Skyttkort).</param>
        /// <param name="preview">1 = förhandsvisning i dialogen: bara första arket, ingen verktygsrad, ingen automatisk utskrift.</param>
        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId, string? layout = null, string? paper = null, int per = 1,
            int sum = 0, int notes = 1, int blank = 0, string? patrols = null, string? wg = null, int print = 0,
            string? poang = null, string? names = null, int color = 0, int preview = 0)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Unauthorized();
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            if ((competition.CompetitionType ?? "") is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            // Ett okänt layoutval får inte tyst ge en annan layout (ny dialog mot gammal programfil, gammal länk): säg det och logga.
            var notices = new List<string>();
            var impl = ShootingCardLayouts.Get(layout);
            if (impl == null)
            {
                if (!string.IsNullOrWhiteSpace(layout))
                {
                    notices.Add($"Okänd layout \"{layout}\". Skriver ut \"{ShootingCardLayouts.Default.Name}\" i stället.");
                    _log.LogWarning("Skjutkort: okänd layout {Layout} för tävling {CompetitionId}", layout, competitionId);
                }
                impl = ShootingCardLayouts.Default;
            }
            var poangRow = poang == "row";

            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            using var db = _dbFactory.CreateDatabase();
            var built = await ShootingCardBuilder.BuildAsync(db, config, competitionId,
                new ShootingCardSelection(ShootingCardBuilder.ParsePatrolNumbers(patrols), wg), Math.Clamp(blank, 0, 50), impl.Id, poangRow);
            notices.AddRange(built.Notices);

            var options = new ShootingCardOptions
            {
                CompetitionName = competition.CompetitionName ?? competition.Name ?? "",
                Layout = impl.Id,
                Paper = ShootingCardLayout.NormalizePaper(paper),
                PerSheet = ShootingCardLayout.NormalizePerSheet(per),
                SumColumn = sum == 1,
                Notes = notes != 0,
                PoangRow = poangRow,
                BlankNames = names == "blank",
                Color = color == 1,
                Preview = preview == 1,
                AutoPrint = print == 1 && preview != 1,
                Notices = notices
            };
            _log.LogInformation("Skjutkort: tävling {CompetitionId}, layout {Layout}, {Cards} kort", competitionId, impl.Id, built.Cards.Count);
            return Content(ShootingCardRenderer.RenderDocument(built.Cards, options), "text/html; charset=utf-8");
        }

        /// <summary>
        /// Rutornas bredd för varje pappersformat och layout, för den mest krävande stationsuppsättningen i tävlingen.
        /// Dialogen visar den som en varning innan man skriver ut. Samma räkning som utskriften (<see cref="ShootingCardLayout"/>).
        /// </summary>
        [HttpGet("matt")]
        public async Task<IActionResult> Matt(int competitionId, int sum = 0, string? poang = null)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Json(new { success = false, message = "Du har inte behörighet." });
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            if ((competition.CompetitionType ?? "") is not ("Faltskytte" or "MagnumFalt")) return Json(new { success = false, message = "Tävlingen är ingen fälttävling." });

            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            var allSets = ShootingCardBuilder.AllConfigStations(config);
            var stations = ShootingCardBuilder.MostDemanding(allSets);
            if (stations == null) return Json(new { success = true, hasStations = false });

            // Mått per layout (varje layout väljer den stationsuppsättning som blir trängst för just den) och pappersformat.
            var fitOptions = new ShootingCardOptions { SumColumn = sum == 1, PoangRow = poang == "row" };
            var layouts = ShootingCardLayouts.All.Select(l =>
            {
                var worst = ShootingCardBuilder.MostDemanding(allSets, l) ?? stations;
                return new
                {
                    id = l.Id, name = l.Name, description = l.Description, locked = l.Formats.Count == 1, capacity = l.Capacity,
                    formats = l.Formats.Select(f =>
                    {
                        var rect = ShootingCardLayout.Cards(f.Paper, f.PerSheet)[0];
                        var r = l.Fit(worst, l.Capacity, rect, fitOptions);
                        var lim = r.Limiting;
                        return new { id = f.Id, label = f.Label, paper = f.Paper, perSheet = f.PerSheet, level = r.Level, w = lim.Shown, limit = lim.Label, text = r.ChipText };
                    }).ToList()
                };
            }).ToList();

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
                layouts,
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
