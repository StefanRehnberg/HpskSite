using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll för avanmälan (2026-11-16): larmar Critical om <c>dbo.CompetitionWithdrawal</c> saknas.
    /// Utan tabellen kan en skytt fortfarande avanmäla sig, men arrangören får inget spår i sajten av att
    /// skytten betalat och ska ha pengarna tillbaka (bara mejlet, om tävlingen har en kontaktadress) —
    /// en tystnad som ser ut som att allt fungerar. Stoppar inte starten; ett fel i kontrollen loggas som
    /// just det, aldrig som "saknas".
    /// </summary>
    public class CompetitionWithdrawalSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CompetitionWithdrawalSchemaGuardHostedService> _logger;

        public CompetitionWithdrawalSchemaGuardHostedService(IServiceScopeFactory scopeFactory,
            ILogger<CompetitionWithdrawalSchemaGuardHostedService> logger)
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
                var exists = db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0) IS NULL THEN 0 ELSE 1 END", "dbo.CompetitionWithdrawal") == 1;
                if (!exists)
                    _logger.LogCritical(
                        "AVANMÄLNINGSTABELL SAKNAS: dbo.CompetitionWithdrawal finns inte — arrangören får ingen notis i Anmälningar när en skytt avanmäler sig med betald avgift. Kör Migrations/create-competition-withdrawal-table.sql (guardad, säker att köra om).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startkontrollen för avanmälningstabellen kunde inte genomföras.");
            }
        }
    }
}
