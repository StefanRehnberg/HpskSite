using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Startkontroll för tävlingsansökan (fas 4). Saknas tabellerna faller varje ansökan, varje
    /// kretsbeslut och kretskalendern — och en klubb som ansökt får bara "något gick fel".
    ///
    /// <para>Critical, eftersom prod loggar Warning och uppåt. Kunde kontrollen inte genomföras
    /// sägs det, i stället för att påstå att tabellerna saknas (samma regel som svarslagret).</para>
    /// </summary>
    public class CompetitionApplicationSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(75);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CompetitionApplicationSchemaGuardHostedService> _logger;

        public CompetitionApplicationSchemaGuardHostedService(IServiceScopeFactory scopeFactory,
            ILogger<CompetitionApplicationSchemaGuardHostedService> logger)
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
                var svc = scope.ServiceProvider.GetRequiredService<CompetitionApplicationService>();
                if (svc.TablesExist())
                {
                    _logger.LogInformation("Tävlingsansökan: tabellerna finns.");
                    return;
                }
                _logger.LogCritical(
                    "TÄVLINGSANSÖKAN ÄR TRASIG: CompetitionApplication-tabellerna saknas. Klubbarnas ansökningar, "
                    + "kretsens beslut och kretskalendern faller. Kör Migrations/create-competition-application-tables.sql.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tävlingsansökans startkontroll kunde inte genomföras.");
            }
        }
    }
}
