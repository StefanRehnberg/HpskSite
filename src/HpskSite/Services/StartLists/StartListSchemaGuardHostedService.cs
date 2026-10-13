using HpskSite.Services.Hosting;
using HpskSite.Services.SiteSettings;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// Startkontroll för startlistornas tabell (skalplanen fas C2): larmar Critical om
    /// <c>create-competition-start-list-table.sql</c> inte är körd.
    ///
    /// <para>Står växeln <c>StartLists:Store</c> på <c>table</c> utan tabellen faller varje startlista —
    /// generering, redigering, publicering, anmälningsspärren och den publika sidan. Står den på
    /// <c>node</c> är det bara flytten som inte kan köras, och då är larmet en varning.</para>
    ///
    /// <para>Stoppar inte starten — samma avvägning som de andra startkontrollerna.</para>
    /// </summary>
    public class StartListSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StartListSchemaGuardHostedService> _logger;

        public StartListSchemaGuardHostedService(
            IServiceScopeFactory scopeFactory, ILogger<StartListSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>Om tabellen finns i rätt form. Kastar när databasen inte går att fråga — en kontroll får
        /// aldrig översätta "kunde inte fråga" till ett svar.
        /// <para>En tabell från en äldre version av skriptet (negativ identitet) räknas som saknad: kör om
        /// <c>create-competition-start-list-table.sql</c>, som bygger om en tom tabell (LAG: id-reglerna).</para></summary>
        public static bool TableExists(IScopeProvider scopeProvider)
        {
            using var scope = scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                @"SELECT CASE WHEN OBJECT_ID('dbo.CompetitionStartList', 'U') IS NULL
                                OR IDENT_INCR('dbo.CompetitionStartList') < 0 THEN 0 ELSE 1 END") == 1;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                if (TableExists(scope.ServiceProvider.GetRequiredService<IScopeProvider>()))
                {
                    _logger.LogInformation("Startlistornas lagring: tabellen finns.");
                    return;
                }
                var usesTable = StartListStoreSettings.UsesTable(scope.ServiceProvider.GetRequiredService<SiteSettingService>());
                if (usesTable)
                    _logger.LogCritical("STARTLISTORNAS TABELL SAKNAS men StartLists:Store står på 'table' — varje startlista faller. "
                        + "Kör Migrations/create-competition-start-list-table.sql eller slå tillbaka växeln till 'node'.");
                else
                    _logger.LogWarning("Startlistornas tabell saknas (Migrations/create-competition-start-list-table.sql). "
                        + "Noderna gäller, så ingenting faller — men flytten kan inte köras.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startlistornas startkontroll kunde inte genomföras.");
            }
        }
    }
}
