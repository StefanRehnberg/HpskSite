using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Services
{
    /// <summary>
    /// Kör <see cref="DueReminderService"/> var 12:e timme (2026-10-21, plan del C). Ett datum i årshjulet rör sig
    /// sällan, och påminnelsen skickas en gång — ett tätare svep skulle bara läsa samma rader om.
    ///
    /// <para>⚠️ Frågar <c>MayRunScheduled</c> i början av varje varv (skalplanen D4): med två instanser hade
    /// annars båda mejlat. Spärren i databasen (claim-then-send) skyddar också, men varvet ska inte ens köras.</para>
    /// </summary>
    public class DueReminderHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(6);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DueReminderHostedService> _logger;

        public DueReminderHostedService(IServiceScopeFactory scopeFactory, ILogger<DueReminderHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_scopeFactory.MayRunScheduled(nameof(DueReminderHostedService)))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var svc = scope.ServiceProvider.GetRequiredService<DueReminderService>();
                        var missing = svc.MissingSchema();
                        if (missing.Count > 0)
                            _logger.LogCritical("PÅMINNELSERNA ÄR AVSTÄNGDA: {Missing} saknas. Kör Migrations/create-due-reminder-table.sql.",
                                string.Join(", ", missing));
                        else
                        {
                            var r = await svc.RunAsync(DateTime.Today, stoppingToken);
                            if (r.Claimed > 0)
                                _logger.LogInformation("Påminnelser 90 dagar före: {Claimed} nya, {Sent} skickade, {NotSent} kunde inte skickas.",
                                    r.Claimed, r.Sent, r.NotSent);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogError(ex, "Påminnelsesvepet (90 dagar före) misslyckades."); }
                }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
