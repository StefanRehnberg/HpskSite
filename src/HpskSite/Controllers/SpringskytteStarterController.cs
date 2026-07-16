using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Start-line "starter" screen for Springskytte (item 7): a big clock showing the current
    /// time, the next shooter up + the one after, with 30 s / 10 s / 3-2-1-start visual + audio
    /// cues. Also hosts the range-master's live "move a late shooter to a free slot" tool (item 8).
    /// Chromeless routed page (like /station, /live, /patrullista) — polls GetSpringskytteStarterState.
    /// Routed at /startlinje/{competitionId}.
    /// </summary>
    [Route("startlinje/{competitionId:int}")]
    public class SpringskytteStarterController : Controller
    {
        private readonly IContentService _contentService;

        public SpringskytteStarterController(IContentService contentService)
        {
            _contentService = contentService;
        }

        [HttpGet("")]
        public IActionResult Index(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition") return NotFound();
            if ((competition.GetValue<string>("competitionType") ?? "") != "Springskytte") return NotFound();

            ViewData["CompetitionId"] = competitionId;
            ViewData["CompetitionName"] = competition.GetValue<string>("competitionName") ?? competition.Name ?? "Tävling";
            return View("~/Views/SpringskytteStarter.cshtml");
        }
    }
}
