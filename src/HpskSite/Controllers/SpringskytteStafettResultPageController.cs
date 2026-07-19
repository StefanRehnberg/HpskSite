using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Public Springskytte STAFETT (relay) result page — chromeless, castable/printable.
    ///   /stafettresultat/{competitionId}
    /// Mirrors the routed /startlista page. The ranked board is rendered client-side from the
    /// public GetSpringskytteStafettResults endpoint; the page is gated server-side on the
    /// SpringskytteStafettResultPublish "official" flag (preliminary results stay admin-only).
    /// </summary>
    [Route("stafettresultat/{competitionId:int}")]
    public class SpringskytteStafettResultPageController : Controller
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _dbFactory;

        public SpringskytteStafettResultPageController(IContentService contentService, IUmbracoDatabaseFactory dbFactory)
        {
            _contentService = contentService;
            _dbFactory = dbFactory;
        }

        [HttpGet("")]
        public IActionResult Index(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition") return NotFound();
            if ((competition.GetValue<string>("competitionType") ?? "") != "Springskytte") return NotFound();

            bool isOfficial = false;
            try
            {
                using var db = _dbFactory.CreateDatabase();
                var v = db.ExecuteScalar<int?>(
                    "SELECT CAST(IsOfficial AS INT) FROM SpringskytteStafettResultPublish WHERE CompetitionId = @0", competitionId);
                isOfficial = v == 1;
            }
            catch { /* table missing → treat as not published */ }

            ViewData["CompetitionId"] = competitionId;
            ViewData["CompetitionName"] = competition.GetValue<string>("competitionName") ?? competition.Name ?? "Tävling";
            ViewData["IsOfficial"] = isOfficial;
            return View("~/Views/SpringskytteStafettResultPage.cshtml");
        }
    }
}
