using HpskSite.CompetitionTypes.Faltskytte.Models;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Standalone public patrol-list page (/patrullista/{competitionId}) for Fältskytte
    /// — so a club can put it on a screen in the clubhouse for shooters to see when their
    /// patrol leaves. Mirrors how "Visa resultat" opens its own page. Public (no login),
    /// but only renders the patrols once the organiser has published them
    /// (faltskyttePatrolsPublished). The list itself is not secret (unlike station layouts).
    /// </summary>
    [Route("patrullista/{competitionId:int}")]
    public class FaltskyttePatrolListController : Controller
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _dbFactory;

        public FaltskyttePatrolListController(IContentService contentService, IUmbracoDatabaseFactory dbFactory)
        {
            _contentService = contentService;
            _dbFactory = dbFactory;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var published = competition.HasProperty("faltskyttePatrolsPublished")
                && competition.GetValue<bool>("faltskyttePatrolsPublished");

            var model = new FaltskyttePatrolListModel
            {
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                Published = published
            };

            if (published)
            {
                using var db = _dbFactory.CreateDatabase();
                var patrols = await db.FetchAsync<FaltskyttePatrol>(
                    "WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);
                var patrolIds = patrols.Select(p => p.Id).ToList();
                var members = patrolIds.Any()
                    ? await db.FetchAsync<FaltskyttePatrolMember>(
                        $"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                    : new List<FaltskyttePatrolMember>();

                model.Patrols = patrols
                    .OrderBy(p => p.PatrolNumber)
                    .Select(p => new PatrolListRow
                    {
                        PatrolNumber = p.PatrolNumber,
                        WeaponGroup = string.IsNullOrEmpty(p.WeaponGroup) ? "?" : p.WeaponGroup,
                        StartTime = p.StartTime,
                        Label = p.Label,
                        Members = members.Where(m => m.PatrolId == p.Id).OrderBy(m => m.Position)
                            .Select(m => new PatrolListMember
                            {
                                Name = m.MemberName,
                                Club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                                ShootingClass = m.ShootingClass
                            }).ToList()
                    }).ToList();
            }

            return View("~/Views/FaltskyttePatrolList.cshtml", model);
        }
    }
}
