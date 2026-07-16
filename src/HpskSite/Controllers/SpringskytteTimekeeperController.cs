using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Springskytte "Tidtagning &amp; straff" page (item 5): the timing/penalty role — separate from
    /// the shot-scoring surface — where staff enter each shooter's finish time (Måltid) and log
    /// manual penalties (rule offences, +1 min) and time reductions (compensation, −time).
    /// Uses field-scoped saves so it never overwrites the scoring role's shot data.
    /// Chromeless routed page at /tidtagning/{competitionId}. Actions are auth-gated server-side.
    /// </summary>
    [Route("tidtagning/{competitionId:int}")]
    public class SpringskytteTimekeeperController : Controller
    {
        private readonly IContentService _contentService;

        public SpringskytteTimekeeperController(IContentService contentService)
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
            return View("~/Views/SpringskytteTimekeeper.cshtml");
        }
    }
}
