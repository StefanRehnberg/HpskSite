using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Startkontroll för verifikationsliggaren: skriker om en tabell, en kolumn eller — framför
    /// allt — en <b>trigger</b> saknas.
    ///
    /// <para><b>⚠️⚠️ TRIGGARNA ÄR DET VIKTIGASTE DEN KONTROLLERAR, och skälet till att den finns.</b>
    /// Oföränderligheten är inte en regel i koden utan <c>INSTEAD OF UPDATE, DELETE</c>-triggers i
    /// databasen. En tabell utan sina triggers <b>ser fullständigt frisk ut</b>: den tar emot
    /// poster, den läser tillbaka dem, ingenting felar. Det enda som är borta är garantin att
    /// siffrorna inte kan ändras i efterhand — och det upptäcks först när en revisor ifrågasätter
    /// en siffra och vi inte kan svara. En databas som återställts från backup, en migrering som
    /// körts delvis, eller ett skript som körts i fel ordning räcker för att hamna där.</para>
    ///
    /// <para><b>⚠️ SJÄLVA KONTROLLEN BOR I <see cref="LedgerSchemaInspector"/></b>, som också
    /// <c>/health/ledger</c> använder. Skriv aldrig en andra kontroll här — två uppfattningar om
    /// samma schema är fria att säga emot varandra, och då är den ena tyst fel.</para>
    ///
    /// <para><b>⚠️ PÅ PROD SYNS BARA KLAGOMÅLEN.</b> Serilog kör där på <c>Warning</c> och uppåt, så
    /// framgångsraden nedan (<c>LogInformation</c>) finns inte i produktionsloggen. "Inga nyheter"
    /// går alltså inte att skilja från "kontrollen kördes aldrig" — vilket är ironiskt för en vakt
    /// vars hela syfte är att fånga tystnad. Det är precis därför <c>/health/ledger</c> finns:
    /// den svarar på begäran i stället för att skrika en gång och sedan tiga.</para>
    /// </summary>
    public class LedgerSchemaGuardHostedService : BackgroundService
    {
        /// <summary>Låt sajten starta klart först — databasen kan ännu inte vara nåbar.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private readonly LedgerSchemaInspector _inspector;
        private readonly ILogger<LedgerSchemaGuardHostedService> _logger;

        public LedgerSchemaGuardHostedService(
            LedgerSchemaInspector inspector,
            ILogger<LedgerSchemaGuardHostedService> logger)
        {
            _inspector = inspector;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var report = _inspector.Inspect();

            switch (report.Status)
            {
                case LedgerSchemaStatus.NotMigrated:
                    _logger.LogInformation(
                        "Verifikationsliggaren är inte migrerad ännu — ingen av de {Antal} tabellerna "
                        + "finns. Kör {Skript} när bokföringen ska tas i bruk.",
                        _inspector.TableCount, LedgerSchemaInspector.MigrationScript);
                    break;

                case LedgerSchemaStatus.HalfMigrated:
                    _logger.LogCritical(
                        "VERIFIKATIONSLIGGAREN ÄR HALVMIGRERAD: tabellerna {Saknade} saknas medan "
                        + "övriga finns. Bokföring som skrivs nu blir ofullständig. Kör {Skript} "
                        + "(guardad, säker att köra om).",
                        string.Join(", ", report.MissingTables), LedgerSchemaInspector.MigrationScript);
                    break;

                case LedgerSchemaStatus.MissingColumns:
                    _logger.LogCritical(
                        "VERIFIKATIONSLIGGAREN SAKNAR KOLUMNER: {Saknade}. Tabellerna finns och "
                        + "SELECT fungerar, men varje sparning mot dem faller på 'Invalid column "
                        + "name'. Kör {Skript} igen (guardad per kolumn).",
                        string.Join(", ", report.MissingColumns), LedgerSchemaInspector.MigrationScript);
                    break;

                case LedgerSchemaStatus.MissingTriggers:
                    _logger.LogCritical(
                        "⚠️ VERIFIKATIONSLIGGARENS SPÄRRAR SAKNAS: {Saknade}. Bokförda verifikationer "
                        + "GÅR ATT ÄNDRA OCH RADERA, och ett fastställt räkenskapsår tar emot nya "
                        + "poster. Ingenting felar och ingenting syns — granskningsbarheten är bara "
                        + "borta. Kör {Skript} igen; den återskapar triggarna.",
                        string.Join(", ", report.MissingTriggers), LedgerSchemaInspector.MigrationScript);
                    break;

                case LedgerSchemaStatus.RolesIncomplete:
                    _logger.LogWarning(
                        "Verifikationsliggaren: {Antal} utställare bokför men saknar kontoroller "
                        + "({Utstallare}). Varje roll utan konto är en post som inte går att bokföra. "
                        + "Komplettera mappningen i ekonomiinställningarna.",
                        report.IssuerRoleGaps.Count, string.Join(", ", report.IssuerRoleGaps));
                    break;

                case LedgerSchemaStatus.CouldNotCheck:
                    _logger.LogError(
                        "Verifikationsliggarens startkontroll kunde inte genomföras: {Fel}",
                        report.Error);
                    break;

                default:
                    _logger.LogInformation(
                        "Verifikationsliggaren: {Tabeller} tabeller, alla kolumner och alla {Triggers} "
                        + "spärrar på plats.",
                        _inspector.TableCount, _inspector.TriggerCount);
                    break;
            }
        }
    }
}
