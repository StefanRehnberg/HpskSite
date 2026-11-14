using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

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
    /// när stationerna inte är uppkopplade. Personal-grindad, i sajtens Master-layout. Själva sparandet går genom
    /// FaltskytteCardEntryController (surface).
    ///
    /// Master.cshtml (UmbracoViewPage) kräver en IPublishedContent som Model — som TavlingsplaneringController
    /// skickas rotnoden, och sidans egen modell ligger i ViewData["Card"].
    /// </summary>
    [Route("kortinmatning/{competitionId:int}")]
    public class FaltskytteCardEntryPageController : Controller
    {
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public FaltskytteCardEntryPageController(AdminAuthorizationService auth, HpskSite.Services.Competitions.ICompetitionStore competitions,
            IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager)
        {
            _auth = auth;
            _competitions = competitions;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.CompetitionType ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/kortinmatning/{competitionId}")}");

            ViewData["Card"] = new FaltskytteCardEntryPageModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.CompetitionName ?? competition.Name ?? "",
                CanEnter = await _auth.HasCompetitionStaffAccessAsync(competitionId)
            };
            return View("~/Views/FaltskytteCardEntry.cshtml", rootNode);
        }
    }
}
