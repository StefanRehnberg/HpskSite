using HpskSite.Models.Schedule;
using HpskSite.Services.Messaging;
using HpskSite.Services.Notifications;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Sends "du börjar om 30 minuter" pushes for items on a member's personal itinerary.
    ///
    /// Shape of the sweep, and why:
    ///   - Runs every few minutes rather than scheduling one timer per item: start times MOVE (a
    ///     skjutlag gets re-timed, a patrol is held), and re-deriving the itinerary each pass means the
    ///     reminder always reflects current data instead of whatever was true when a timer was armed.
    ///   - Only opted-in members are considered, and that filter is applied FIRST — building itineraries
    ///     for everyone registered in a competition just to discover nobody wants a push is the
    ///     expensive way round. Opt-in lives per browser on WebPushSubscription.ScheduleRemindersEnabled
    ///     and defaults to off.
    ///   - Every send is written to ScheduleReminderLog, whose unique index is what actually guarantees
    ///     a member can't be reminded twice about the same item even if two sweeps overlap.
    ///
    /// Items with no absolute StartsAt (an undated skjutlag on a multi-day list) can't be reminded about
    /// and are skipped — there is no moment to count back from.
    /// </summary>
    public class ScheduleReminderHostedService : BackgroundService
    {
        /// <summary>How long before an item the reminder fires.</summary>
        private const int LeadMinutes = 30;

        /// <summary>Sweep cadence. Must be comfortably shorter than LeadMinutes or items slip past.</summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        /// <summary>Let the site finish starting before doing any work.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduleReminderHostedService> _logger;

        public ScheduleReminderHostedService(IServiceScopeFactory scopeFactory, ILogger<ScheduleReminderHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ScheduleReminderHostedService started (lead {Lead} min, every {Interval} min).",
                LeadMinutes, Interval.TotalMinutes);

            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSafelyAsync(stoppingToken);
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("ScheduleReminderHostedService stopped.");
        }

        private async Task RunSafelyAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var push = scope.ServiceProvider.GetRequiredService<WebPushService>();
                if (!push.IsConfigured) return;

                var optedIn = push.GetScheduleReminderMemberIds();
                if (optedIn.Count == 0) return;

                var schedule = scope.ServiceProvider.GetRequiredService<MyScheduleService>();
                var scopeProvider = scope.ServiceProvider.GetRequiredService<IScopeProvider>();

                var now = DateTime.Now;
                var horizon = now.AddMinutes(LeadMinutes);
                // A competition only matters today or tomorrow — anything further out has no item
                // inside a 30-minute horizon.
                var from = now.Date;
                var to = now.Date.AddDays(1);

                var sent = 0;
                foreach (var memberId in optedIn)
                {
                    if (ct.IsCancellationRequested) return;

                    foreach (var compId in schedule.GetCompetitionIdsForMember(memberId, from, to))
                    {
                        // Bypass the cache: a 30 s stale itinerary is fine for a page render but this is
                        // the one caller that must see a start time the organiser just corrected.
                        var sched = schedule.GetSchedule(memberId, compId, useCache: false);
                        if (!sched.HasAny) continue;

                        foreach (var item in sched.Days.SelectMany(d => d.Items))
                        {
                            if (item.StartsAt is not { } startsAt) continue;
                            if (startsAt <= now || startsAt > horizon) continue;
                            if (item.Kind == ScheduleItemKind.Praktiskt) continue;   // context, not a commitment

                            if (!TryClaim(scopeProvider, compId, memberId, item.SourceKey, startsAt)) continue;

                            var minutes = Math.Max(1, (int)Math.Round((startsAt - now).TotalMinutes));
                            var title = item.Kind == ScheduleItemKind.Funktionar
                                ? $"Ditt pass börjar om {minutes} min"
                                : $"Du startar om {minutes} min";
                            var body = string.IsNullOrWhiteSpace(item.Where)
                                ? $"{item.TimeLabel} {item.Title} — {sched.CompName}"
                                : $"{item.TimeLabel} {item.Title}, {item.Where} — {sched.CompName}";

                            try
                            {
                                await push.SendScheduleReminderAsync(memberId, title, body,
                                    $"/mitt-schema?c={compId}", $"schema-{compId}");
                                sent++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Schedule reminder push failed for member {Member}", memberId);
                            }
                        }
                    }
                }

                if (sent > 0) _logger.LogInformation("Schedule reminders sent: {Count}", sent);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Schedule reminder sweep failed.");
            }
        }

        /// <summary>
        /// Claims the right to remind about this item by inserting the log row FIRST. The unique index
        /// makes a duplicate insert throw, which is exactly the signal we want — claim-then-send means a
        /// crash between the two costs at most one missed reminder, whereas send-then-log could spam.
        /// </summary>
        private bool TryClaim(IScopeProvider scopeProvider, int competitionId, int memberId, string sourceKey, DateTime startsAt)
        {
            if (string.IsNullOrWhiteSpace(sourceKey)) return false;
            try
            {
                using var scope = scopeProvider.CreateScope();
                var n = scope.Database.Execute(@"
INSERT INTO ScheduleReminderLog (CompetitionId, MemberId, SourceKey, ItemStartsAt, SentAt)
SELECT @0, @1, @2, @3, GETDATE()
WHERE NOT EXISTS (
    SELECT 1 FROM ScheduleReminderLog
    WHERE CompetitionId = @0 AND MemberId = @1 AND SourceKey = @2)",
                    competitionId, memberId, sourceKey, startsAt);
                scope.Complete();
                return n > 0;
            }
            catch (Exception ex)
            {
                // Unique-index violation on a concurrent sweep, or the table isn't migrated yet. Either
                // way the safe answer is "don't send".
                _logger.LogDebug(ex, "Schedule reminder claim skipped for member {Member} item {Key}", memberId, sourceKey);
                return false;
            }
        }
    }
}
