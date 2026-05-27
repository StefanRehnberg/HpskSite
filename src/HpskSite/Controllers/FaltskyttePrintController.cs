using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Print-friendly Fältskytte station cards for a competition: Förutsättningar
    /// + figure timeline (reusing the FaltskytteStationInfoStatic partial) plus
    /// QR-1 (Förutsättningar) on the card and QR-2 (result entry) as a cut-out.
    /// Reached from the competition's "Stationer" tab, where competitionId is known
    /// — which is what lets the QR codes actually generate (the standalone
    /// /faltkonfig editor has no competition, so it can't). Staff-gated.
    /// </summary>
    [Route("faltskytte/stationskort/{competitionId:int}")]
    public class FaltskyttePrintController : Controller
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;

        public FaltskyttePrintController(IContentService contentService, AdminAuthorizationService auth)
        {
            _contentService = contentService;
            _auth = auth;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId, int? station = null)
        {
            if (!await IsStaffForCompetition(competitionId))
                return Unauthorized();

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound();
            var compType = competition.GetValue<string>("competitionType") ?? "";
            if (compType is not ("Faltskytte" or "MagnumFalt")) return NotFound();

            var config = FaltskytteConfigParser.Parse(competition.GetValue<string>("stationConfig") ?? "");
            var firstWc = config.WeaponConfigs.Values.FirstOrDefault();
            var stationNumbers = (firstWc?.Stations ?? new List<FaltskytteStationConfig>())
                .Where(s => !s.IsShootOffOnly)
                .Select(s => s.Station)
                .Distinct().OrderBy(n => n)
                .Where(n => station == null || n == station.Value)
                .ToList();

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var model = new FaltskyttePrintStationCardsModel
            {
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                ScoringMode = competition.GetValue<string>("scoringMode") ?? "Normal",
                Stations = stationNumbers.Select(n =>
                {
                    var byWc = new Dictionary<string, FaltskytteStationConfig>();
                    foreach (var kv in config.WeaponConfigs)
                    {
                        var st = kv.Value.Stations.FirstOrDefault(s => s.Station == n);
                        if (st != null) byWc[kv.Key] = st;
                    }
                    var entryUrl = $"{baseUrl}/station?c={competitionId}&s={n}";
                    return new StationPrintItem
                    {
                        Station = n,
                        StationsByWeaponClass = byWc,
                        // QR-1 mints its token + renders server-side; relative img src is fine.
                        Qr1Url = $"/umbraco/surface/Faltskytte/GetStationInfoQr?competitionId={competitionId}&stationNumber={n}",
                        // QR-2 encodes the absolute entry URL.
                        Qr2Url = $"/umbraco/surface/Faltskytte/GenerateQrCode?url={Uri.EscapeDataString(entryUrl)}"
                    };
                }).ToList()
            };

            return View("~/Views/FaltskyttePrintStationCards.cshtml", model);
        }

        // Mirrors FaltskytteController.IsAuthorizedForCompetition (staff four-tier + regional).
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
