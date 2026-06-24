namespace HpskSite.Services.Ranking
{
    /// <summary>
    /// Builds the Träningsmatch ranking snapshot nightly (~03:00) and once shortly after startup,
    /// so the board is never empty after a deploy. Reads always come from the persisted snapshot.
    /// </summary>
    public class RankingSnapshotHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RankingSnapshotHostedService> _logger;

        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
        private const int RunHour = 3; // local time

        public RankingSnapshotHostedService(IServiceScopeFactory scopeFactory, ILogger<RankingSnapshotHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RankingSnapshotHostedService started.");

            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            // Build once on startup so a fresh deploy populates immediately.
            await RunSafelyAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = DelayUntilNextRun();
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }

                await RunSafelyAsync(stoppingToken);
            }

            _logger.LogInformation("RankingSnapshotHostedService stopped.");
        }

        private async Task RunSafelyAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var builder = scope.ServiceProvider.GetRequiredService<RankingSnapshotService>();
                // Nightly/startup run sends improvement pushes; the manual RebuildSnapshot endpoint does not
                // (avoids spamming during testing).
                await builder.BuildSnapshotAsync(ct, sendNotifications: true);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RankingSnapshot build failed.");
            }
        }

        private static TimeSpan DelayUntilNextRun()
        {
            var now = DateTime.Now;
            var next = now.Date.AddHours(RunHour);
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
    }
}
