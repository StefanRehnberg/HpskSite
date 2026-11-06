using HpskSite.CompetitionTypes.Common;
using HpskSite.Services.Hosting;

namespace HpskSite.Services.SiteSettings
{
    /// <summary>
    /// Håller <see cref="OutOfCompetitionDisciplines"/> i takt med sajtinställningen <c>OutOfCompetition:Disciplines</c>: läser direkt vid start och sedan var
    /// 20:e sekund (inställningens egen cache är 60 s, så en ändring gäller inom ~80 s — samma tidsram som övriga nödbromsar). Ett läsfel lämnar den
    /// senast kända listan orörd ("kunde inte fråga" är inget svar). Ändras listan loggas det som Warning, med vem som kan ha ändrat den i klartext
    /// (inställningens egen logg bär medlemmen).
    /// </summary>
    public class OutOfCompetitionDisciplinesHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<OutOfCompetitionDisciplinesHostedService> _logger;

        public OutOfCompetitionDisciplinesHostedService(IServiceScopeFactory scopes, ILogger<OutOfCompetitionDisciplinesHostedService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Refresh();
                }
                catch (SiteSettingUnavailableException)
                {
                    // Behåll senast kända lista.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kunde inte läsa {Key}; listan över grenar lämnas som den är", OutOfCompetitionDisciplines.SettingKey);
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Läser inställningen och gör den gällande. Publik för test och för "läs om nu".</summary>
        public void Refresh()
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SiteSettingService>();
            var raw = settings.Get(OutOfCompetitionDisciplines.SettingKey, "");
            if (OutOfCompetitionDisciplines.Apply(raw, out var ignored))
                _logger.LogWarning("Grenar med 'Utanför mästerskapet' ändrade: {Disciplines} (inställningen {Key} = '{Raw}')",
                    string.Join(", ", OutOfCompetitionDisciplines.Current.OrderBy(d => d)), OutOfCompetitionDisciplines.SettingKey, raw);
            if (ignored.Count > 0)
                _logger.LogWarning("{Key} innehåller okända grenar som ignoreras: {Ignored}", OutOfCompetitionDisciplines.SettingKey, string.Join(", ", ignored));
        }
    }
}
