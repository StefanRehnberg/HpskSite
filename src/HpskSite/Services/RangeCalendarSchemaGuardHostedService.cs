using Microsoft.Extensions.DependencyInjection;
using HpskSite.Services.Hosting;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll för banans kalender (2026-10-11): larmar Critical om
    /// <c>create-range-calendar-tables.sql</c> inte är körd.
    ///
    /// <para><b>⚠️ Varför den behövs:</b> NPoco skriver <c>ClubTraining.RangeSectionId</c> vid varje
    /// sparning av en träning så snart POCO:n bär egenskapen. Utan kolumnen faller alltså inte bara
    /// banans kalender utan VARJE "Spara" i Admin → Träningar, varje kopiering och migreringen av gamla
    /// träningshändelser.</para>
    ///
    /// <para>Stoppar inte starten — samma avvägning som <see cref="BoardWorkSchemaGuardHostedService"/>.</para>
    /// </summary>
    public class RangeCalendarSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RangeCalendarSchemaGuardHostedService> _logger;

        public RangeCalendarSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<RangeCalendarSchemaGuardHostedService> logger)
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

                // Banans besiktning (2026-10-21): NPoco skriver InspectionIntervalYears vid varje sparning av en bana.
                var inspMissing = scope.ServiceProvider.GetRequiredService<ShootingRangeService>().MissingInspectionSchema();
                if (inspMissing.Count > 0)
                    _logger.LogCritical(
                        "BANANS BESIKTNING ÄR TRASIG: {Missing} saknas i databasen. Varje sparning av en skjutbana faller. "
                        + "Kör Migrations/create-range-inspection.sql (guardad, säker att köra om).",
                        string.Join(", ", inspMissing));

                var missing = scope.ServiceProvider.GetRequiredService<RangeCalendarService>().MissingSchema();
                if (missing.Count == 0)
                {
                    _logger.LogInformation("Banans kalender: schemat finns.");
                    return;
                }
                _logger.LogCritical(
                    "BANANS KALENDER ÄR TRASIG: {Missing} saknas i databasen. Varje sparning av en träning faller. "
                    + "Kör Migrations/create-range-calendar-tables.sql (guardad, säker att köra om).",
                    string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Banans kalenders startkontroll kunde inte genomföras.");
            }
        }
    }
}
