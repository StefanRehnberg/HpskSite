using HpskSite.Services.Hosting;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Startkontroll för klubbsidorna steg 1: larmar om <c>dbo.SiteOwner</c> saknas. Utan tabellen fungerar sajten
    /// som förut (den nya sidstrukturen är AV för alla), men ingen klubb kan slås på. Stoppar inte starten.
    /// En kontroll får aldrig översätta "kunde inte fråga" till "saknas": ett fel loggas som just det.
    /// </summary>
    public class SiteOwnerSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SiteOwnerSchemaGuardHostedService> _logger;

        public SiteOwnerSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<SiteOwnerSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IUmbracoDatabaseFactory>();
                using var db = factory.CreateDatabase();
                var exists = db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0) IS NULL THEN 0 ELSE 1 END", "dbo.SiteOwner") == 1;
                if (!exists)
                    _logger.LogCritical(
                        "KLUBBSIDOR: tabellen SiteOwner saknas — den nya sidstrukturen är AV för alla klubbar och kretsar. Kör Migrations/create-site-owner-table.sql (guardad, säker att köra om).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startkontrollen för klubbsidornas tabell kunde inte genomföras.");
            }
        }
    }
}
