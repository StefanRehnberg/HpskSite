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

        public FaltskyttePatrolListController(IContentService contentService, AdminAuthorizationService auth)
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
                // Anonymous wall screen → false (read-only). Logged-in starter (staff) → true (send-off buttons).
                CanSendOff = await IsStaffForCompetition(competitionId)
            };

            return View("~/Views/FaltskyttePatrolList.cshtml", model);
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
