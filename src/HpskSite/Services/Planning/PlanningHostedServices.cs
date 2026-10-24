using System;
using System.Threading;
using System.Threading.Tasks;
using HpskSite.Services.Hosting;
using HpskSite.Services.SiteSettings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Planeringens svep var 5:e minut: kvällsmejlet, timpushen, mötespåminnelserna och påminnelsen
    /// om svarsfristen. Kör bara på publiceraren (<c>MayRunScheduled</c>) och stängs av med
    /// sajtinställningen <c>Planning:Notifications = off</c> utan omstart.
    /// </summary>
    public class PlanningNotificationHostedService : IsolatedBackgroundService
    {
        public const string SwitchKey = "Planning:Notifications";
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlanningNotificationHostedService> _logger;

        public PlanningNotificationHostedService(IServiceScopeFactory scopeFactory, ILogger<PlanningNotificationHostedService> logger)
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
                try
                {
                    if (_scopeFactory.MayRunScheduled(nameof(PlanningNotificationHostedService)))
                        await RunOnceAsync(_scopeFactory, DateTime.Now, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Planeringens svep föll.");
                }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Ett varv. Även sajtadminens "kör nu" går hit, med en vald tid (prov utan att vänta till kl. 18).</summary>
        public static async Task<PlanningNotifier.SweepResult?> RunOnceAsync(IServiceScopeFactory scopes, DateTime now, ILogger logger)
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SiteSettingService>();
            if (string.Equals(settings.Get(SwitchKey, "on"), "off", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Planeringens svep: avstängt ({Key} = off).", SwitchKey);
                return null;
            }
            var notifier = scope.ServiceProvider.GetRequiredService<PlanningNotifier>();
            var result = await notifier.SweepAsync(now);
            if (result.Digests + result.Pushes + result.MeetingReminders + result.VoteReminders + result.TaskBatches > 0)
                logger.LogInformation("Planeringens svep: {Digests} kvällsmejl, {Pushes} pushar, {Meetings} mötespåminnelser, {Votes} svarspåminnelser.",
                    result.Digests, result.Pushes, result.MeetingReminders, result.VoteReminders);
            return result;
        }
    }

    /// <summary>
    /// Omgång 3 steg 10 (K7): rensningen av borttagna planeringar en gång om dygnet (<see cref="PlanningPurgeService"/>).
    /// Kör bara på publiceraren.
    /// </summary>
    public class PlanningPurgeHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(11);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlanningPurgeHostedService> _logger;

        public PlanningPurgeHostedService(IServiceScopeFactory scopeFactory, ILogger<PlanningPurgeHostedService> logger)
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
                try
                {
                    if (_scopeFactory.MayRunScheduled(nameof(PlanningPurgeHostedService)))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<PlanningPurgeService>().PurgeAsync(DateTime.Now);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Planeringsrensningen föll."); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Startkontroll: larmar Critical när planeringens tabeller saknas (migreringen
    /// <c>create-planning-tables.sql</c> inte körd). Stoppar inte starten.
    /// </summary>
    public class PlanningSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(75);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlanningSchemaGuardHostedService> _logger;

        public PlanningSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<PlanningSchemaGuardHostedService> logger)
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
                var missing = scope.ServiceProvider.GetRequiredService<PlanningService>().MissingSchema();
                if (missing.Count == 0)
                {
                    _logger.LogInformation("Tävlingsplaneringen: alla tabeller finns.");
                    return;
                }
                _logger.LogCritical(
                    "TÄVLINGSPLANERINGEN ÄR TRASIG: {Missing} saknas i databasen. Planeringarna, meddelandena och mötena går inte att läsa. "
                    + "Kör Migrations/create-planning-tables.sql (guardad, säker att köra om).", string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tävlingsplaneringens startkontroll kunde inte genomföras.");
            }
        }
    }
}
