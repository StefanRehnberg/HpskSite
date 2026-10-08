using Microsoft.Extensions.DependencyInjection;
using HpskSite.Services.Hosting;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll för ärendekön och motionerna (2026-10-08): larmar Critical om
    /// <c>create-board-issues-and-motions.sql</c> inte är körd.
    ///
    /// <para><b>⚠️ Varför den är så viktig här:</b> migreringen lägger kolumner på
    /// <c>BoardMeetingAgendaItems</c> och <c>BoardMeetings</c>, och NPoco skriver alla egenskaper vid
    /// varje uppdatering. Utan kolumnerna faller alltså inte bara de nya funktionerna utan VARJE
    /// sparad anteckning, varje beslut och varje justering i styrelsearbetet.</para>
    ///
    /// <para>Stoppar inte starten — samma avvägning som <c>MailReplySchemaGuardHostedService</c>.</para>
    /// </summary>
    public class BoardWorkSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BoardWorkSchemaGuardHostedService> _logger;

        public BoardWorkSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<BoardWorkSchemaGuardHostedService> logger)
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
                var missing = scope.ServiceProvider.GetRequiredService<BoardWorkService>().MissingSchema();
                if (missing.Count == 0)
                {
                    _logger.LogInformation("Styrelsearbetet: ärendekön och motionerna har sitt schema.");
                    return;
                }
                _logger.LogCritical(
                    "STYRELSEARBETET ÄR TRASIGT: {Missing} saknas i databasen. Varje sparning av en dagordningspunkt "
                    + "eller ett möte faller. Kör Migrations/create-board-issues-and-motions.sql (guardad, säker att köra om).",
                    string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Styrelsearbetets startkontroll kunde inte genomföras.");
            }
        }
    }
}
