using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Standalone patrol-list / send-off page (/patrullista/{competitionId}) for Fältskytte.
    /// Public read-only wall screen for the clubhouse; for a logged-in staff member it also
    /// shows "Skicka iväg" controls so the starter(s) can tick patrols off as they leave the
    /// start line. The page polls GetPatrolListState; departures are stored on the patrol
    /// (DepartedAt). "Next" = the lowest patrol number not yet departed.
    /// </summary>
    [Route("patrullista/{competitionId:int}")]
    public class FaltskyttePatrolListController : Controller
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

        public FaltskyttePatrolListController(IContentService contentService, AdminAuthorizationService auth,
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
                // Anonymous wall screen → false (read-only). Logged-in starter (staff) → true (send-off buttons).
                CanSendOff = await IsStaffForCompetition(competitionId)
            };

            return View("~/Views/FaltskyttePatrolList.cshtml", model);
        }

        // Personalregeln (C3-planen S2, rättat 2026-10-14). Förut släpptes varje kretsadmin i landet in.
        private Task<bool> IsStaffForCompetition(int competitionId) =>
            _auth.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved);
    }
}
