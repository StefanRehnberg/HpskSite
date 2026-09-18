using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using HpskSite.Services.Ledger;
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
        private readonly LedgerSchemaInspector _ledgerSchema;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            IScopeProvider scopeProvider,
            LedgerSchemaInspector ledgerSchema,
            ILogger<HealthController> logger)
        {
            _scopeProvider = scopeProvider;
            _ledgerSchema = ledgerSchema;
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

        /// <summary>
        /// <c>GET /health/ledger</c> — verifikationsliggarens schema, på begäran.
        ///
        /// <para><b>⚠️⚠️ FINNS FÖR ATT STARTKONTROLLEN ÄR OSYNLIG PÅ PROD.</b>
        /// <see cref="LedgerSchemaGuardHostedService"/> skriker på <c>Critical</c> om schemat är
        /// trasigt, men skriver sin framgångsrad på <c>Information</c> — och prod kör Serilog på
        /// <c>Warning</c> och uppåt. Där går alltså "allt är helt" inte att skilja från "kontrollen
        /// kördes aldrig", vilket är precis den tystnad guarden finns för att bryta. Den här
        /// endpointen svarar när man frågar.</para>
        ///
        /// <para><b>⚠️ 503 BARA NÄR SCHEMAT ÄR TRASIGT.</b> "Inte migrerad ännu" är ett väntat
        /// tillstånd före att bokföringen tas i bruk, och en ofullständig rollmappning är en
        /// inställning som saknas — inget av dem är ett driftavbrott, och ett larm som lyser på
        /// dem slutar betyda något. De svarar 200 med <c>INFO</c> respektive <c>WARN</c> som
        /// första ord, så en människa ser skillnaden direkt.</para>
        ///
        /// <para>Anonym av samma skäl som <see cref="Db"/>: övervakaren kan inte logga in. Den
        /// lämnar inte ut någon bokföringsdata — bara namnen på våra egna tabeller och triggrar,
        /// och bara när de SAKNAS.</para>
        /// </summary>
        [HttpGet("ledger")]
        public IActionResult Ledger()
        {
            // Övervakning får aldrig läsa ett cachat svar — hela poängen är läget just nu.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

            var sw = Stopwatch.StartNew();
            var report = _ledgerSchema.Inspect();
            sw.Stop();

            var ms = sw.ElapsedMilliseconds;

            // ⚠️ charset måste stå med. Svaret är svensk text, och utan den läser en webbläsare
            //    det som Latin-1 — då blir "spärrar" till "spÃ¤rrar" i exakt det meddelande någon
            //    ska agera på klockan sju på morgonen.
            const string PlainText = "text/plain; charset=utf-8";

            switch (report.Status)
            {
                case LedgerSchemaStatus.Ok:
                    return Content(
                        $"OK ledger {ms}ms tables={_ledgerSchema.TableCount} triggers={_ledgerSchema.TriggerCount}\n",
                        PlainText);

                case LedgerSchemaStatus.NotMigrated:
                    return Content(
                        $"INFO ledger {ms}ms: inte migrerad ännu — ingen av de {_ledgerSchema.TableCount} "
                        + $"tabellerna finns. Kör {LedgerSchemaInspector.MigrationScript} när bokföringen "
                        + "ska tas i bruk.\n",
                        PlainText);

                case LedgerSchemaStatus.RolesIncomplete:
                    return Content(
                        $"WARN ledger {ms}ms: {report.IssuerRoleGaps.Count} utställare bokför men saknar "
                        + $"kontoroller ({string.Join("; ", report.IssuerRoleGaps)}). Schemat är helt. "
                        + "Komplettera mappningen i ekonomiinställningarna.\n",
                        PlainText);

                case LedgerSchemaStatus.CouldNotCheck:
                    // Kunde inte fråga. Det är inte samma sak som ett trasigt schema, men det är
                    // heller inte "friskt" — en övervakare ska titta.
                    _logger.LogWarning("Hälsokontroll: liggarens schema kunde inte läsas ({Ms} ms).", ms);
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Content($"FAIL ledger {ms}ms: kunde inte läsa schemat: {report.Error}\n", PlainText);
            }

            // Kvar: de tre trasiga lägena. Skriv ut VAD som saknas och VILKET skript som lagar det —
            // det är det första en människa behöver, och ett meddelande som pekar på fel skript
            // skickar operatören att köra om ett som inte hjälper.
            var what = report.Status switch
            {
                LedgerSchemaStatus.HalfMigrated => "tabeller saknas: " + string.Join(", ", report.MissingTables),
                LedgerSchemaStatus.MissingColumns => "kolumner saknas: " + string.Join(", ", report.MissingColumns),
                _ => "SPÄRRAR SAKNAS (verifikationer går att ändra och radera): "
                     + string.Join(", ", report.MissingTriggers)
            };

            _logger.LogWarning("Hälsokontroll: liggarens schema är ofullständigt — {Vad}", what);

            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Content(
                $"FAIL ledger {ms}ms: {what}\nKör {LedgerSchemaInspector.MigrationScript} (guardad, säker att köra om).\n",
                PlainText);
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
