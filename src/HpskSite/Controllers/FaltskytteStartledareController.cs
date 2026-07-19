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

        public FaltskytteStartledareController(IContentService contentService, AdminAuthorizationService auth)
        {
            _contentService = contentService;
            _auth = auth;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var model = new FaltskyttePatrolListModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                Published = competition.HasProperty("faltskyttePatrolsPublished")
                    && competition.GetValue<bool>("faltskyttePatrolsPublished"),
                // Operator screen is staff-only. A non-staff visitor gets an access notice, not controls.
                CanSendOff = await IsStaffForCompetition(competitionId)
            };

            return View("~/Views/FaltskytteStartledare.cshtml", model);
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
