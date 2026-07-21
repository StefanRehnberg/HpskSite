using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Print-friendly Fältskytte materiellista (BOM) for a competition, derived from the attached station
    /// configuration: per-station target figures (with images + counts) + a competition-wide order roll-up.
    /// Reached from the Förberedelser page (Planering) when a config is attached. Staff-gated; routed
    /// controller, no backoffice node (mirrors FaltskyttePrintController). See COMPETITION_STAFFING_SYSTEM.md.
    /// </summary>
    [Route("faltskytte/materiel/{competitionId:int}")]
    public class FaltskytteMaterielController : Controller
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _dbFactory;
        private readonly AdminAuthorizationService _auth;

        public FaltskytteMaterielController(IContentService contentService, IUmbracoDatabaseFactory dbFactory, AdminAuthorizationService auth)
        {
            _contentService = contentService;
            _dbFactory = dbFactory;
            _auth = auth;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            if (!await IsStaffForCompetition(competitionId))
                return Unauthorized();

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var config = FaltskytteConfigParser.Parse(competition.GetValue<string>("stationConfig") ?? "");

            // Figurkatalog size lookup (Name → SizeGroup) so BOM rows can show storleksgrupp.
            var sizeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var db = _dbFactory.CreateDatabase();
                foreach (var t in db.Fetch<FieldTarget>("SELECT * FROM FieldTarget"))
                    if (!string.IsNullOrWhiteSpace(t.Name)) sizeByName[t.Name.Trim()] = t.SizeGroup;
            }
            catch { /* catalog is best-effort — BOM still works without size groups */ }

            var bom = FaltskytteBom.Build(config, sizeByName);
            bom.CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";

            return View("~/Views/FaltskytteMateriel.cshtml", bom);
        }

        // Mirrors FaltskyttePrintController.IsStaffForCompetition.
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
