using HpskSite.Services.Hosting;
using HpskSite.Services.SiteSettings;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// Startkontroll för resultatlistornas tabell (skalplanen fas C1): larmar Critical om
    /// <c>create-competition-result-list-table.sql</c> inte är körd.
    ///
    /// <para>Står växeln <c>Results:Store</c> på <c>table</c> utan tabellen faller varje resultatlista —
    /// publicering, omräkning, prisutdelning och den publika sidan. Står den på <c>node</c> är det bara
    /// flytten som inte kan köras, och då är larmet en varning.</para>
    ///
    /// <para>Stoppar inte starten — samma avvägning som de andra startkontrollerna.</para>
    /// </summary>
    public class CompetitionResultSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CompetitionResultSchemaGuardHostedService> _logger;

        public CompetitionResultSchemaGuardHostedService(
            IServiceScopeFactory scopeFactory, ILogger<CompetitionResultSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>Om tabellen finns. Kastar när databasen inte går att fråga — en kontroll får aldrig
        /// översätta "kunde inte fråga" till ett svar.</summary>
        public static bool TableExists(IScopeProvider scopeProvider)
        {
            using var scope = scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT CASE WHEN OBJECT_ID('dbo.CompetitionResultList', 'U') IS NULL THEN 0 ELSE 1 END") == 1;
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
                    _logger.LogInformation("Resultatlistornas lagring: tabellen finns.");
                    return;
                }
                var usesTable = CompetitionResultStoreSettings.UsesTable(scope.ServiceProvider.GetRequiredService<SiteSettingService>());
                if (usesTable)
                    _logger.LogCritical("RESULTATLISTORNAS TABELL SAKNAS men Results:Store står på 'table' — varje resultatlista faller. "
                        + "Kör Migrations/create-competition-result-list-table.sql eller slå tillbaka växeln till 'node'.");
                else
                    _logger.LogWarning("Resultatlistornas tabell saknas (Migrations/create-competition-result-list-table.sql). "
                        + "Noderna gäller, så ingenting faller — men flytten kan inte köras.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatlistornas startkontroll kunde inte genomföras.");
            }
        }
    }
}
