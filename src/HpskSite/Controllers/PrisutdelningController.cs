using HpskSite.Models.PrizeGiving;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Prisutdelningssidan — /prisutdelning/{competitionId}[?grupp=C].
    ///
    /// Chromeless och funktionärsgrindad, samma mönster som /valvet, /skjutledare och
    /// /patrullista: routad controller, ingen Umbraco-nod.
    ///
    /// ⚠️ ?grupp= FINNS FÖR ATT CEREMONIN DELAS UPP. SHB C.4.3.1.11: *"För att vinna tid kan
    /// prisutdelningen vid större tävlingar försiggå vid flera bord samtidigt, exempelvis ett
    /// för varje vapengrupp."* Varje bord öppnar sin egen adress — det är därför den ligger i
    /// URL:en och inte bara i ett filter på sidan: adressen ska gå att sätta på en QR-affisch
    /// per bord.
    /// </summary>
    [Route("prisutdelning")]
    public class PrisutdelningController : Controller
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;
        private readonly PrizeGivingService _prizeGiving;
        private readonly ILogger<PrisutdelningController> _logger;

        public PrisutdelningController(
            IContentService contentService,
            AdminAuthorizationService auth,
            PrizeGivingService prizeGiving,
            ILogger<PrisutdelningController> logger)
        {
            _contentService = contentService;
            _auth = auth;
            _prizeGiving = prizeGiving;
            _logger = logger;
        }

        [HttpGet("{competitionId:int}")]
        public async Task<IActionResult> Index(int competitionId, string? grupp = null)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition") return NotFound();

            // ⚠️ Grinden är HasCompetitionStaffAccessAsync och inte en egen clubId-kontroll.
            // En tävling är antingen klubbvärdad (clubId satt) eller kretsvärdad (clubId tom,
            // regionalFederation satt — SM-formen), och en handskriven clubId-kontroll låser ut
            // den arrangerande kretsen från sin egen tävling. Det felet är gjort fyra gånger i
            // den här kodbasen.
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId))
            {
                return View("~/Views/PrisutdelningDenied.cshtml", competitionId);
            }

            var canEdit = await _auth.HasCompetitionManagementAccess(competitionId);
            var model = await _prizeGiving.BuildAsync(competitionId, grupp, canEdit);
            if (model == null) return NotFound();

            return View("~/Views/Prisutdelning.cshtml", model);
        }

        /// <summary>
        /// Sparar arrangörens hedersprisfördelning — kategorinamn → antal.
        ///
        /// ⚠️ EGEN SKRIVVÄG mot en EGEN egenskap, aldrig via resultatlistans omräkning.
        /// Fördelningen är ett manuellt beslut och måste överleva att resultatlistan räknas om.
        /// </summary>
        [HttpPost("{competitionId:int}/hederspriser")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveHonorary(int competitionId, [FromBody] SaveHonoraryRequest? request)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition")
                return Json(new { success = false, message = "Tävlingen hittades inte." });

            if (!await _auth.HasCompetitionManagementAccess(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet att ändra fördelningen." });

            var resultNode = _prizeGiving.GetResultNode(competitionId);
            if (resultNode == null)
                return Json(new { success = false, message = "Tävlingen har ingen resultatlista att spara fördelningen på." });

            // ⚠️ Saknad doctype-egenskap: VÄGRA och namnge den. SetValue på en egenskap som inte
            // finns är en TYST no-op, så en sparning skulle rapportera lyckat och vara borta vid
            // nästa laddning — exakt den sortens fel som tar en helg att hitta.
            if (!_prizeGiving.CanStoreHonoraryConfig(resultNode))
            {
                return Json(new
                {
                    success = false,
                    message = $"Egenskapen '{PrizeGivingService.HonoraryConfigProperty}' saknas på "
                            + "dokumenttypen competitionResult. Lägg till den i Umbraco backoffice "
                            + "(Textarea) för att kunna spara en egen fördelning."
                });
            }

            try
            {
                if (request?.Reset == true)
                {
                    // Återställ till systemets förslag = ta bort arrangörens siffror helt.
                    // Att skriva ner förslagets siffror hade låst dem: nästa gång deltagarantalet
                    // ändras skulle "förslaget" vara ett gammalt förslag.
                    resultNode.SetValue(PrizeGivingService.HonoraryConfigProperty, "");
                }
                else
                {
                    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in request?.Counts ?? new Dictionary<string, int>())
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        counts[kv.Key.Trim()] = Math.Max(0, kv.Value);
                    }
                    resultNode.SetValue(
                        PrizeGivingService.HonoraryConfigProperty,
                        JsonConvert.SerializeObject(counts));
                }

                _contentService.Save(resultNode);

                // ⚠️ Publicera BARA en nod som redan var publicerad. Att publicera ett utkast som
                // sidoeffekt av en hedersprisändring skulle göra en okontrollerad resultatlista
                // publik — samma regel som startlistornas klubbrättelse följer.
                if (resultNode.Published)
                {
                    _contentService.Publish(resultNode, new[] { "*" }, -1);
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte spara hedersprisfördelningen för tävling {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel vid sparande: " + ex.Message });
            }
        }

        public class SaveHonoraryRequest
        {
            /// <summary>Kategorinamn → antal hederspriser.</summary>
            public Dictionary<string, int>? Counts { get; set; }

            /// <summary>True = radera arrangörens fördelning och gå tillbaka till förslaget.</summary>
            public bool Reset { get; set; }
        }
    }
}
