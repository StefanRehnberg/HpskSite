using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    public class FaltskytteCardEntryPageModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public bool CanEnter { get; set; }
    }

    /// <summary>
    /// Sekretariatets kortinmatning (/kortinmatning/{competitionId}) — papperskorten från patrullerna matas in här
    /// när stationerna inte är uppkopplade. Personal-grindad, öppnas i ny flik från tävlingshanteringen
    /// (fliken Resultat). Själva sparandet går genom FaltskytteCardEntryController (surface).
    /// </summary>
    [Route("kortinmatning/{competitionId:int}")]
    public class FaltskytteCardEntryPageController : Controller
    {
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

        public FaltskytteCardEntryPageController(AdminAuthorizationService auth, HpskSite.Services.Competitions.ICompetitionStore competitions)
        {
            _auth = auth;
            _competitions = competitions;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.CompetitionType ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var model = new FaltskytteCardEntryPageModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.CompetitionName ?? competition.Name ?? "",
                CanEnter = await _auth.HasCompetitionStaffAccessAsync(competitionId)
            };
            return View("~/Views/FaltskytteCardEntry.cshtml", model);
        }
    }
}
