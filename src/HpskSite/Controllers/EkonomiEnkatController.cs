using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        /// <summary>
        /// Hur många svar en och samma avsändare får skicka per timme.
        ///
        /// <para><b>⚠️ Satt HÖGT med flit.</b> Spärren finns mot en robot som skriver hundratals
        /// rader, inte mot en människa som ändrar sig. Flera medlemmar kan dessutom dela utgående
        /// IP — en klubbstuga, ett kontor, en mobiloperatörs NAT — och en snål gräns hade då
        /// blockerat riktiga svar från just de ställen där skyttar sitter tillsammans. Ett bortfallet
        /// svar kostar oss mer än tio skräprader, som dessutom går att radera.</para>
        /// </summary>
        private const int MaxPerTimme = 8;

        /// <summary>
        /// Längsta svar vi tar emot per fråga — ungefär tjugo sidor text.
        ///
        /// <para><b>⚠️ Satt så högt att en människa i praktiken inte når det.</b> Den som svarar
        /// utförligt på en regelfråga är precis den vi vill höra från, och taket finns bara för att
        /// en robot inte ska kunna skicka megabyte per post. Nås det ändå AVVISAS svaret med besked
        /// om vilken fråga det gäller — det kapas aldrig tyst. En tyst kapning ger det värsta
        /// utfallet: kvittot säger tack, och slutet av resonemanget finns inte.</para>
        ///
        /// <para>Ligger långt under ASP.NET Cores egen gräns per formulärfält (4 MB), så
        /// ramverket hinner aldrig avvisa posten innan vi har ett begripligt meddelande att ge.</para>
        /// </summary>
        private const int MaxSvarslangd = 100_000;

        private static readonly TimeSpan Fonster = TimeSpan.FromHours(1);

        private readonly IScopeProvider _scopeProvider;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemoryCache _cache;
        private readonly ILogger<EkonomiEnkatController> _logger;

        public EkonomiEnkatController(
            IScopeProvider scopeProvider,
            AdminAuthorizationService auth,
            IMemoryCache cache,
            ILogger<EkonomiEnkatController> logger)
        {
            _scopeProvider = scopeProvider;
            _auth = auth;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Bästa gissning på avsändarens IP, med hänsyn till omvänd proxy.
        ///
        /// <para><b>⚠️⚠️ `X-Forwarded-For` MÅSTE läsas först.</b> Prod ligger bakom Simplys proxy, så
        /// <c>RemoteIpAddress</c> är PROXYNS adress för varje besökare — en spärr byggd på den hade
        /// räknat hela utskicket som EN avsändare och stängt av formuläret efter åtta svar totalt.
        /// Samma hjälpare som <c>DpaController.GetClientIp</c>.</para>
        /// </summary>
        private string? ClientIp()
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return forwarded.Split(',')[0].Trim();
            return HttpContext.Connection.RemoteIpAddress?.ToString();
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
            string? ovrigt, bool villHjalpaTill = false, string? webbplats = null)
        {
            // ⚠️ SVAREN LÄSES UR FORMULÄRET VIA FRÅGELISTAN, inte som elva parametrar. Elva
            // parametrar är en parallell lista som glider isär från EkonomiEnkat.Fragor så fort en
            // fråga läggs till — samma form som gav SvarF8-buggen och två misslyckade deployer.
            // Nu räcker det att lägga till frågan på ETT ställe.
            var svarPerFraga = EkonomiEnkat.Fragor
                .ToDictionary(f => f.Id, f => Request.Form["svar" + f.Id].ToString());

            // ⚠️ HONUNGSFÄLLA. `webbplats` är ett dolt fält som en människa aldrig ser och därför
            // aldrig fyller i; en formulärrobot fyller i allt den hittar. Tyst 302 tillbaka —
            // säger vi "spam" lär sig roboten vilket fält som fällde den.
            if (!string.IsNullOrWhiteSpace(webbplats))
                return RedirectToAction(nameof(Index), new { tack = true });

            // ⚠️ SPÄRREN LIGGER EFTER HONUNGSFÄLLAN men FÖRE all validering och all skrivning.
            // Efter fällan, så en robot som fastnat där inte äter upp kvoten för en riktig
            // avsändare bakom samma NAT. Före valideringen, så ett skript inte kan mala på med
            // ogiltiga poster utan att räknas.
            var ip = ClientIp();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                var nyckel = "ekonomienkat_ip_" + ip;
                var antal = _cache.TryGetValue<int>(nyckel, out var n) ? n : 0;

                if (antal >= MaxPerTimme)
                {
                    // ⚠️ INGEN loggrad per avvisat försök. En robot som mal skulle annars fylla
                    // loggen med brus och göra den oläsbar — vilket är ett eget slags angrepp.
                    // Den FÖRSTA avvisningen räcker för att en människa ska kunna se att det händer.
                    if (antal == MaxPerTimme)
                        _logger.LogWarning(
                            "Ekonomienkät: gränsen {Max} svar/timme nådd för en avsändare. "
                            + "Fler svar därifrån avvisas den närmaste timmen.", MaxPerTimme);

                    _cache.Set(nyckel, antal + 1, Fonster);

                    ViewData["Fel"] =
                        "Vi tar emot högst " + MaxPerTimme + " svar i timmen från samma uppkoppling, "
                        + "och den gränsen är nådd. Det du skrivit står kvar nedan — ladda inte om "
                        + "sidan, utan prova Skicka igen om en stund. Är ni flera som svarar från "
                        + "samma ställe, mejla gärna admin@pistol.nu så löser vi det.";
                    return View("~/Views/EkonomiEnkat.cshtml");
                }

                // Räknas vid FÖRSÖKET, inte vid en lyckad sparning — annars är ett skript som
                // skickar ogiltiga poster obegränsat.
                _cache.Set(nyckel, antal + 1, Fonster);
            }
            // ⚠️ Går IP:t inte att läsa släpps svaret igenom. En spärr som blockerar riktiga svar
            // för att den inte kunde läsa en adress är värre än skräpet den skyddar mot — hela
            // poängen med sidan är att samla in svar.

            if (!EkonomiEnkat.IsValid(kompetens))
            {
                ViewData["Fel"] = "Välj vilken bakgrund du har, så vet vi hur vi ska väga svaret.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            // Ett svar utan innehåll är inte ett svar. Utan den här spärren fylls tabellen med
            // tomma rader från den som klickade sig fram av nyfikenhet.
            if (svarPerFraga.Values.All(string.IsNullOrWhiteSpace)
                && string.IsNullOrWhiteSpace(ovrigt) && !villHjalpaTill)
            {
                ViewData["Fel"] = "Skriv något i minst en fråga — eller kryssa i att du vill vara med och granska.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            // ⚠️⚠️ ETT FÖR LÅNGT SVAR AVVISAS MED BESKED — det kapas ALDRIG tyst.
            // Fälten kapades förut på 8 000 tecken, alltså ett par sidor. Den som svarar utförligt
            // på en regelfråga skriver lätt mer än så, och en tyst kapning ger det värsta utfallet:
            // kvittot säger "tack", personen tror att hela resonemanget kom fram, och slutet är
            // borta. Taket ligger nu så högt att en människa i praktiken inte når det, och nås det
            // ändå får hen veta VILKEN fråga och hur mycket över — med texten kvar i formuläret.
            var forLanga = svarPerFraga
                .Where(kv => (kv.Value?.Length ?? 0) > MaxSvarslangd)
                .Select(kv => $"{kv.Key} ({kv.Value!.Length:N0} tecken)")
                .ToList();
            if ((ovrigt?.Length ?? 0) > MaxSvarslangd)
                forLanga.Add($"Övrigt ({ovrigt!.Length:N0} tecken)");

            if (forLanga.Count > 0)
            {
                ViewData["Fel"] =
                    "Det här är längre än vi kan ta emot i ett fält: " + string.Join(", ", forLanga)
                    + $". Gränsen är {MaxSvarslangd:N0} tecken per fråga. Texten står kvar nedan — "
                    + "korta ner, dela upp på flera frågor, eller mejla den till admin@pistol.nu "
                    + "så lägger vi in den i sin helhet.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            var row = new EkonomiEnkatSvar
            {
                Kompetens        = kompetens,
                KompetensFritext = Trim(kompetensFritext, 200),
                Namn             = Trim(namn, 150),
                Epost            = Trim(epost, 200),
                Klubb            = Trim(klubb, 150),
                Ovrigt           = Trim(ovrigt, MaxSvarslangd),
                VillHjalpaTill   = villHjalpaTill,
                SkapadDatum      = DateTime.Now,
            };

            // Samma källa som läsningen ovan — inga uppräknade fält, ingen lista att glömma.
            foreach (var (fragaId, text) in svarPerFraga)
                row.SetSvar(fragaId, Trim(text, MaxSvarslangd));

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Insert(row);
            }
            catch (Exception ex)
            {
                // ⚠️ SÄG ATT FELET ÄR VÅRT, OCH ATT TEXTEN FINNS KVAR. Ett tyst fel här betyder att
                // någon lagt en halvtimme på ett svar som aldrig kom fram — och hen skriver det inte
                // en andra gång. Det vanligaste sparfelet är en okörd migrering, och då hjälper det
                // inte att trycka igen: ett råd som inte kan hjälpa flyttar bara skulden till den som
                // inte kan göra något. Formuläret återfylls ur Request.Form, så svaret står kvar på
                // skärmen — det ska stå i meddelandet, annars laddar hen om sidan och förlorar det.
                _logger.LogError(ex, "Kunde inte spara ekonomienkätssvar (bakgrund {Kompetens}).", kompetens);
                ViewData["Fel"] =
                    "Svaret kunde inte sparas, och felet är vårt — inte något du gjort. "
                    + "Det du skrivit står kvar i formuläret nedan, så ladda inte om sidan. "
                    + "Prova gärna Skicka en gång till; går det fortfarande inte, "
                    + "mejla texten till admin@pistol.nu så lägger vi in den.";
                return View("~/Views/EkonomiEnkat.cshtml");
            }

            _logger.LogInformation("Ekonomienkät: nytt svar, bakgrund {Kompetens}.", kompetens);
            return RedirectToAction(nameof(Index), new { tack = true });
        }

        /// <summary>
        /// Tar bort ett svar. Sajtadmin bara, samma grind som listan.
        ///
        /// <para><b>⚠️ HELA SVARET LOGGAS FÖRE RADERINGEN, på Warning.</b> Sidan är öppen och kommer
        /// att samla skräp, så knappen behövs — men texten är någons halvtimme och det finns ingen
        /// annan kopia. Loggraden är ångerknappen: en felklickad radering går att läsa tillbaka ur
        /// loggen i stället för att vara borta. <b>Warning och inte Information</b> eftersom prod kör
        /// Serilog på Warning och uppåt — en Information-rad hade varit osynlig just där den behövs.
        /// Samma lärdom som startkontrollens larmnivå.</para>
        ///
        /// <para>PRG: omdirigerar tillbaka till listan, så en omladdning inte postar raderingen igen.</para>
        /// </summary>
        [HttpPost("radera")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Radera(int id)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Unauthorized();

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);

                var row = uow.Database.SingleOrDefault<EkonomiEnkatSvar>(
                    "SELECT * FROM EkonomiEnkatSvar WHERE Id = @0", id);

                if (row == null) return RedirectToAction(nameof(Svar), new { fel = "saknas" });

                _logger.LogWarning(
                    "Ekonomienkät: svar {Id} raderat (bakgrund {Kompetens}, namn {Namn}, e-post {Epost}). "
                    + "Innehållet sparas här som enda kopia: {Innehall}",
                    row.Id, row.Kompetens, row.Namn ?? "-", row.Epost ?? "-",
                    string.Join(" | ", EkonomiEnkat.Fragor
                        .Select(f => (f.Id, Svar: row.SvarFor(f.Id)))
                        .Where(x => !string.IsNullOrWhiteSpace(x.Svar))
                        .Select(x => $"{x.Id}: {x.Svar}")
                        .Append("Övrigt: " + (row.Ovrigt ?? "-"))));

                uow.Database.Delete<EkonomiEnkatSvar>(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte radera ekonomienkätssvar {Id}.", id);
                return RedirectToAction(nameof(Svar), new { fel = "misslyckades" });
            }

            return RedirectToAction(nameof(Svar), new { raderat = id });
        }

        /// <summary>
        /// Svaren, tyngsta bakgrunden först. Sajtadmin bara — raderna bär namn och e-post.
        /// </summary>
        [HttpGet("svar")]
        public async Task<IActionResult> Svar(int raderat = 0, string? fel = null)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Unauthorized();

            ViewData["Raderat"] = raderat;
            ViewData["Fel"] = fel;

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

        /// <summary>
        /// Trimmar och kapar vid <paramref name="max"/>.
        ///
        /// <para><b>⚠️ INGET DEFAULTVÄRDE, med flit.</b> Den hade förut <c>max = 8000</c>, och det
        /// defaultet kapade varje långt svar TYST — anroparen behövde aldrig ta ställning till hur
        /// långt fältet fick vara. Varje anropsplats måste nu säga sitt tak, så att en kapning är
        /// ett beslut och inte något som händer av sig självt. För svarsfälten är kapningen dessutom
        /// oåtkomlig: en överskridning avvisas med besked långt innan den här metoden nås.</para>
        /// </summary>
        private static string? Trim(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length > max ? t[..max] : t;
        }
    }
}
