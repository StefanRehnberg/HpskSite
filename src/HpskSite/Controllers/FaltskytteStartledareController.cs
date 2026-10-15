using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Startledarvy — the Fältskytte starter's OPERATOR screen at /startledare/{competitionId}.
    /// Staff-only, pad-optimised: send patrols off, park/hold a patrol waiting for a shooter, and
    /// mark shooters DNS at the line. Opened from a "Startledarvy" button on the Startlistor tab —
    /// mirrors the Springskytte /startlinje + Precision /skjutledare role screens.
    ///
    /// The public, read-only big-screen wall is the separate /patrullista page (no controls).
    /// </summary>
    [Route("startledare/{competitionId:int}")]
    public class FaltskytteStartledareController : Controller
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

        public FaltskytteStartledareController(IContentService contentService, AdminAuthorizationService auth,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
        {
            _competitions = competitions;
            _contentService = contentService;
            _auth = auth;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.CompetitionType ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var model = new FaltskyttePatrolListModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.CompetitionName ?? competition.Name ?? "",
                Published = competition.FaltskyttePatrolsPublished,
                // Operator screen is staff-only. A non-staff visitor gets an access notice, not controls.
                CanSendOff = await IsStaffForCompetition(competitionId)
            };

            return View("~/Views/FaltskytteStartledare.cshtml", model);
        }

        // Personalregeln (C3-planen S2, rättat 2026-10-14). Förut släpptes varje kretsadmin i landet in.
        private Task<bool> IsStaffForCompetition(int competitionId) =>
            _auth.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved);
    }
}
