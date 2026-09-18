using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Driftsövervakningens måltavla — <c>/health/db</c>.
    ///
    /// <para><b>⚠️⚠️ FINNS FÖR ATT FÖRSTASIDAN LJUGER.</b> 2026-09-18 låg pistol.nu nere för varje
    /// inloggad medlem i 1,5 timme (blockerande lås på prod-DB, se
    /// <c>migrations-gitignored-and-rerun-hazards</c>). Under HELA incidenten svarade
    /// <c>http://pistol.nu</c> HTTP 200 på 0,5 s, eftersom förstasidan serveras ur cache utan att ta
    /// ett enda lås. Simplys webbövervakning pekade på just den sidan och larmade följaktligen
    /// aldrig — felet upptäcktes för att en människa råkade surfa in. En uppe/nere-koll på en
    /// cachad sida är blind för hela den felklassen.</para>
    ///
    /// <para><b>Därför mäter den här endpointen det som faktiskt gick sönder</b>, inte att en
    /// HTML-sida kan levereras: går det att nå databasen, och går det att ta Umbracos distribuerade
    /// lås? Det var låsen som inte gick att ta, och det var därför varje skrivning blev 500.</para>
    ///
    /// <para><b>⚠️ SVARAR ALLTID SNABBT, ALDRIG MED EN HÄNGNING.</b> Prods anslutningssträng har
    /// <c>Command Timeout=120</c>. En hälsokontroll som hänger i två minuter är värdelös — övervakaren
    /// hinner sitt eget timeout-fönster och rapporterar "nere" utan att säga varför, och vid en
    /// låsstorm blir endpointen dessutom ännu en väntande session. Därför sätts både
    /// <c>LOCK_TIMEOUT</c> och kommandots timeout lågt: vi vill ha ett SVAR, inte ett resultat.</para>
    ///
    /// <para><b>⚠️ 503, inte 500.</b> 503 betyder "tillfälligt otillgänglig" och är vad en övervakare
    /// ska larma på. Ett 500 hade sett ut som en kodbugg i den här controllern.</para>
    ///
    /// <para><b>⚠️ Läser, skriver aldrig.</b> Läslåset tas och släpps direkt i samma sats. En
    /// hälsokontroll som skriver vore själv en potentiell låshållare — precis det vi övervakar.</para>
    ///
    /// <para>Anonym med flit: övervakaren hos Simply kan inte logga in. Den lämnar inte ut något
    /// annat än om databasen svarar.</para>
    /// </summary>
    [Route("health")]
    public class HealthController : Controller
    {
        /// <summary>
        /// Hur länge en låsbegäran får vänta. Tajt: är låset taget just nu är det svaret vi vill ha,
        /// och en frisk databas lämnar ifrån sig det här låset på millisekunder.
        /// </summary>
        private const int LockTimeoutMs = 3000;

        /// <summary>Backstop om något annat än låset hänger. Måste vara &gt; LockTimeoutMs.</summary>
        private const int CommandTimeoutSeconds = 10;

        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<HealthController> _logger;

        public HealthController(IScopeProvider scopeProvider, ILogger<HealthController> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>
        /// <c>GET /health/db</c> — 200 när databasen svarar OCH låsen går att ta, annars 503.
        /// </summary>
        [HttpGet("db")]
        public IActionResult Db()
        {
            // Övervakning får aldrig läsa ett cachat svar — hela poängen är läget just nu.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

            var sw = Stopwatch.StartNew();

            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                scope.Database.CommandTimeout = CommandTimeoutSeconds;

                // ⚠️ REPEATABLEREAD är inte en detalj — det är exakt så Umbraco själv tar sitt
                //    distribuerade LÄSLÅS (SqlServerDistributedLockingMechanism). En vanlig SELECT
                //    hade sluppit förbi en blockerande skrivtransaktion och rapporterat "friskt"
                //    mitt under haveriet. Vi vill träffa samma vägg som appen träffar.
                //
                // ⚠️ Inga lås-id:n hårdkodas. Vilket id som klämmer varierar med vad som gått fel
                //    (2026-09-18 var det -335), och konstantnamnen är Umbracos att ändra. Hela
                //    umbracoLock är några få rader — läs dem allihop och slipp gissa.
                var locks = scope.Database.ExecuteScalar<int>(
                    $"SET LOCK_TIMEOUT {LockTimeoutMs}; SELECT COUNT(*) FROM umbracoLock WITH (REPEATABLEREAD);");

                sw.Stop();

                if (locks <= 0)
                {
                    // Svarade, men tomt. Då är det inte databasen vi pratar med som vi tror.
                    _logger.LogError("Hälsokontroll: umbracoLock är tom — fel databas eller trasigt schema.");
                    return Fail("umbracoLock är tom", sw);
                }

                return Content(
                    $"OK db+lock {sw.ElapsedMilliseconds}ms locks={locks}\n",
                    "text/plain");
            }
            catch (Exception ex)
            {
                sw.Stop();

                // ⚠️ Warning, inte Error. Den här raden kan komma en gång per minut under ett
                //    haveri; på Error dränker den den FÖRSTA felraden, och det är den som är
                //    orsaken. Larmet är HTTP-statusen — loggen är bara för efterhandsanalysen.
                _logger.LogWarning(ex, "Hälsokontroll: databasen svarade inte inom {Ms} ms.", sw.ElapsedMilliseconds);

                return Fail(ex.Message, sw);
            }
        }

        private IActionResult Fail(string reason, Stopwatch sw)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

            // Ett 1222 här betyder blockerande lås — samma sak som tog ner sajten 2026-09-18.
            // Skriv ut det råa skälet: det är det första en människa behöver kl 07 på morgonen.
            return Content(
                $"FAIL db+lock {sw.ElapsedMilliseconds}ms: {reason}\n",
                "text/plain");
        }
    }
}
