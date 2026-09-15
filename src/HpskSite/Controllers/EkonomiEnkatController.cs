using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <c>/ekonomifragor</c> — den öppna sidan där medlemmar med redovisningskompetens svarar på
    /// frågorna inför ombyggnaden av betalningar och bokföring. Direktlänken mejlas ut.
    ///
    /// <para><b>Routad MVC-controller, ingen Umbraco-nod.</b> Samma mönster som
    /// <c>ReceiptController</c> och <c>FaltskyttePrintController</c>: sidan behöver ingen doctype,
    /// inget operatörssteg och ingen publicering — och en doctype-ändring som tappas när appen
    /// stryps är ett av de fel som redan kostat tid här.</para>
    ///
    /// <para><b>⚠️ ÖPPEN SIDA.</b> Ingen inloggning — hela poängen är att länken ska fungera direkt
    /// ur ett mejl. Det betyder också att den är exponerad för robotar: därför honungsfällan nedan,
    /// och därför bär raden inget medlems-id som skulle kunna förfalskas.</para>
    /// </summary>
    [Route("ekonomifragor")]
    public class EkonomiEnkatController : Controller
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly AdminAuthorizationService _auth;
        private readonly ILogger<EkonomiEnkatController> _logger;

        public EkonomiEnkatController(
            IScopeProvider scopeProvider,
            AdminAuthorizationService auth,
            ILogger<EkonomiEnkatController> logger)
        {
            _scopeProvider = scopeProvider;
            _auth = auth;
            _logger = logger;
        }

        [HttpGet("")]
        public IActionResult Index(bool tack = false)
        {
            ViewData["Tack"] = tack;
            return View("~/Views/EkonomiEnkat.cshtml");
        }

        /// <summary>
        /// Tar emot ett svar. <b>Kompetensen är enda obligatoriska fältet</b> — namn och e-post är
        /// frivilliga, för den som bara vill svara ska kunna göra det.
        /// </summary>
        [HttpPost("svara")]
        [ValidateAntiForgeryToken]
        public IActionResult Svara(
            string kompetens, string? kompetensFritext, string? namn, string? epost, string? klubb,
            string? svarF1, string? svarF2, string? svarF3, string? svarF4,
            string? svarF5, string? svarF6, string? svarF7, string? svarF8, string? ovrigt,
            bool villHjalpaTill = false, string? webbplats = null)
        {
            // ⚠️ HONUNGSFÄLLA. `webbplats` är ett dolt fält som en människa aldrig ser och därför
            // aldrig fyller i; en formulärrobot fyller i allt den hittar. Tyst 302 tillbaka —
            // säger vi "spam" lär sig roboten vilket fält som fällde den.
            if (!string.IsNullOrWhiteSpace(webbplats))
                return RedirectToAction(nameof(Index), new { tack = true });

            if (!EkonomiEnkat.IsValid(kompetens))
            {
                ViewData["Fel"] = "Välj vilken bakgrund du har, så vet vi hur vi ska väga svaret.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            // Ett svar utan innehåll är inte ett svar. Utan den här spärren fylls tabellen med
            // tomma rader från den som klickade sig fram av nyfikenhet.
            var svar = new[] { svarF1, svarF2, svarF3, svarF4, svarF5, svarF6, svarF7, svarF8, ovrigt };
            if (svar.All(string.IsNullOrWhiteSpace) && !villHjalpaTill)
            {
                ViewData["Fel"] = "Skriv något i minst en fråga — eller kryssa i att du vill vara med och granska.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            var row = new EkonomiEnkatSvar
            {
                Kompetens        = kompetens,
                KompetensFritext = Trim(kompetensFritext, 200),
                Namn             = Trim(namn, 150),
                Epost            = Trim(epost, 200),
                Klubb            = Trim(klubb, 150),
                SvarF1 = Trim(svarF1), SvarF2 = Trim(svarF2), SvarF3 = Trim(svarF3),
                SvarF4 = Trim(svarF4), SvarF5 = Trim(svarF5), SvarF6 = Trim(svarF6),
                SvarF7 = Trim(svarF7), SvarF8 = Trim(svarF8), Ovrigt = Trim(ovrigt),
                VillHjalpaTill   = villHjalpaTill,
                SkapadDatum      = DateTime.Now,
            };

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Insert(row);
            }
            catch (Exception ex)
            {
                // ⚠️ Säg att det inte gick. Ett tyst fel här betyder att någon lagt en halvtimme på
                // ett svar som aldrig kom fram — och hen skriver det inte en andra gång.
                _logger.LogError(ex, "Kunde inte spara ekonomienkätssvar.");
                ViewData["Fel"] = "Svaret kunde tyvärr inte sparas. Hör gärna av dig direkt i stället.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            _logger.LogInformation("Ekonomienkät: nytt svar, bakgrund {Kompetens}.", kompetens);
            return RedirectToAction(nameof(Index), new { tack = true });
        }

        /// <summary>
        /// Svaren, tyngsta bakgrunden först. Sajtadmin bara — raderna bär namn och e-post.
        /// </summary>
        [HttpGet("svar")]
        public async Task<IActionResult> Svar()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Unauthorized();

            List<EkonomiEnkatSvar> rows;
            using (var uow = _scopeProvider.CreateScope(autoComplete: true))
            {
                rows = uow.Database.Fetch<EkonomiEnkatSvar>(
                    "SELECT * FROM EkonomiEnkatSvar ORDER BY SkapadDatum DESC");
            }

            // ⚠️ Sorteras på TYNGD, inte på antal. Listan finns för att kunna läsa de svar som
            // väger tyngst först — den ska aldrig läsas som en omröstning.
            return View("~/Views/EkonomiEnkatSvar.cshtml",
                rows.OrderByDescending(r => r.Tyngd).ThenByDescending(r => r.SkapadDatum).ToList());
        }

        private static string? Trim(string? s, int max = 8000)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length > max ? t[..max] : t;
        }
    }
}
