using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Startkontroll för kretsutlysningen (F2). Saknas tabellerna faller varje utlysning och varje utskrift av
    /// årsprogrammet — och kretsens styrelse får bara "något gick fel".
    ///
    /// <para>Critical, eftersom prod loggar Warning och uppåt. Kunde kontrollen inte genomföras sägs det, i stället för
    /// att påstå att tabellerna saknas (en kontroll får aldrig översätta "kunde inte fråga" till ett svar).</para>
    /// </summary>
    public class CompetitionCallSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(80);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CompetitionCallSchemaGuardHostedService> _logger;

        public CompetitionCallSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<CompetitionCallSchemaGuardHostedService> logger)
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
                var problem = scope.ServiceProvider.GetRequiredService<CompetitionCallService>().SchemaProblem();
                if (problem == null)
                    _logger.LogInformation("Kretsutlysningen: tabellerna finns.");
                else
                    _logger.LogCritical(
                        "KRETSUTLYSNINGEN ÄR TRASIG: {Missing} saknas. Kretsens utlysningar och utskriften av årsprogrammet faller. "
                        + "Kör Migrations/create-competition-call-tables.sql.", problem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kretsutlysningens startkontroll kunde inte genomföras.");
            }
        }
    }
}
