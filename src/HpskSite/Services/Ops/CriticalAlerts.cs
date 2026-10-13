using System.Collections.Concurrent;
using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// C3-planen F6: larm på Critical. Prod loggar till fil och ingen läser filen förrän något redan gått
    /// sönder — en startkontroll som skriker Critical om en saknad tabell hördes därför aldrig. Den här
    /// sinken fångar varje Critical-rad (Serilogs <c>Fatal</c>), <see cref="CriticalAlertService"/> sparar den
    /// i <c>dbo.OpsAlert</c> och mejlar <c>Email:AdminEmail</c>.
    ///
    /// <para><b>⚠️ En Serilog-sink, inte en <c>ILoggerProvider</c>.</b> Umbraco registrerar en
    /// <c>SerilogLoggerFactory</c> som inte tar med providers ur DI — en provider fick aldrig en rad, inte ens
    /// via <c>ILoggerFactory.AddProvider</c> (driftskollens provlarm visade det). Umbraco bygger däremot om sin
    /// logger med <c>ReadFrom.Services(sp)</c>, som tar med varje <see cref="Serilog.Core.ILogEventSink"/> i DI.
    /// Registrera den därför som en sådan.</para>
    /// </summary>
    public sealed class CriticalAlertSink : Serilog.Core.ILogEventSink
    {
        private readonly CriticalAlertQueue _queue;
        public CriticalAlertSink(CriticalAlertQueue queue) => _queue = queue;

        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            if (logEvent.Level != Serilog.Events.LogEventLevel.Fatal) return;
            try
            {
                var category = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                    ? sc.ToString().Trim('"') : "";
                _queue.Enqueue(new CriticalAlertQueue.Item(logEvent.Timestamp.LocalDateTime, category,
                    Render(logEvent), logEvent.Exception?.ToString()));
            }
            catch { /* ett larm får aldrig fälla det som loggade */ }
        }

        /// <summary>Meddelandet med strängvärden utan Serilogs citattecken — det är en människa som läser det.</summary>
        internal static string Render(Serilog.Events.LogEvent e)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in e.MessageTemplate.Tokens)
            {
                if (t is Serilog.Parsing.PropertyToken p && e.Properties.TryGetValue(p.PropertyName, out var v))
                    sb.Append(v is Serilog.Events.ScalarValue { Value: string s } ? s : v.ToString());
                else
                    sb.Append(t.ToString());
            }
            return sb.ToString();
        }
    }

    /// <summary>Kön mellan loggprovidern (som inte får göra I/O) och tjänsten som sparar och mejlar.</summary>
    public sealed class CriticalAlertQueue
    {
        public sealed record Item(DateTime At, string Category, string Message, string? Exception);
        private readonly ConcurrentQueue<Item> _items = new();
        public const int MaxQueued = 500;
        public void Enqueue(Item item)
        {
            if (_items.Count >= MaxQueued) return;
            _items.Enqueue(item);
        }
        public List<Item> DrainAll()
        {
            var list = new List<Item>();
            while (_items.TryDequeue(out var i)) list.Add(i);
            return list;
        }
    }

    /// <summary>
    /// Sparar köade Critical-rader och mejlar dem. <b>Strypt:</b> samma kategori och meddelande räknas upp
    /// på en befintlig rad i stället för en ny inom <see cref="DedupeWindow"/>, och ett mejl går högst en gång
    /// per <see cref="MailEvery"/> med alla nya rader sedan förra mejlet. Loggar själv aldrig på Critical
    /// (det hade blivit en slinga).
    /// </summary>
    public class CriticalAlertService : IsolatedBackgroundService
    {
        public static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(6);
        public static readonly TimeSpan MailEvery = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        private readonly CriticalAlertQueue _queue;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CriticalAlertService> _logger;
        private DateTime _lastMail = DateTime.MinValue;

        public CriticalAlertService(CriticalAlertQueue queue, IUmbracoDatabaseFactory databaseFactory,
            IServiceScopeFactory scopeFactory, ILogger<CriticalAlertService> logger)
        {
            _queue = queue;
            _databaseFactory = databaseFactory;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                await ProcessAsync(force: false);
            }
        }

        /// <summary>Sparar kön och mejlar om det är dags. Returnerar (sparade, mejlade rader, mejlet gick iväg).</summary>
        public async Task<(int Saved, int Mailed, bool MailSent)> ProcessAsync(bool force)
        {
            var items = _queue.DrainAll();
            var saved = 0;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.OpsAlert', 'U')") == null)
                {
                    if (items.Count > 0)
                        _logger.LogError("Larmet: dbo.OpsAlert saknas, {Count} Critical-rader sparas inte. Kör Migrations/create-ops-tables.sql.", items.Count);
                    return (0, 0, false);
                }
                foreach (var it in items)
                {
                    var msg = Cap(it.Message, 2000);
                    var updated = db.Execute(@"UPDATE TOP (1) dbo.OpsAlert SET [Count] = [Count] + 1, LastAt = @2
WHERE Category = @0 AND [Message] = @1 AND LastAt >= @3", it.Category, msg, it.At, it.At - DedupeWindow);
                    if (updated == 0)
                        db.Execute(@"INSERT INTO dbo.OpsAlert (FirstAt, LastAt, Category, [Message], [Exception], [Count])
VALUES (@0, @0, @1, @2, @3, 1)", it.At, it.Category, msg, it.Exception == null ? null : Cap(it.Exception, 8000));
                    saved++;
                }

                if (!force && DateTime.Now - _lastMail < MailEvery) return (saved, 0, false);
                var pending = db.Fetch<AlertRow>(@"SELECT TOP 50 Id, FirstAt, LastAt, Category, [Message], [Count]
FROM dbo.OpsAlert WHERE MailedAt IS NULL ORDER BY Id");
                if (pending.Count == 0) return (saved, 0, false);
                _lastMail = DateTime.Now;
                bool sent;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
                    sent = await mail.SendOpsAlertAsync(pending.Select(p =>
                        $"{p.LastAt:yyyy-MM-dd HH:mm} · {p.Category}{(p.Count > 1 ? $" · {p.Count} gånger" : "")}\n{p.Message}").ToList());
                }
                // Markeras som mejlade bara när mejlet faktiskt gick iväg — annars prövas de igen.
                if (sent)
                    db.Execute("UPDATE dbo.OpsAlert SET MailedAt = @0 WHERE Id IN (@1)", DateTime.Now, pending.Select(p => p.Id).ToList());
                else
                    _logger.LogError("Larmet: mejlet om {Count} Critical-rader kunde inte skickas. Raderna finns i driftskollen.", pending.Count);
                return (saved, pending.Count, sent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Larmet kunde inte spara eller mejla {Count} Critical-rader.", items.Count);
                return (saved, 0, false);
            }
        }

        private static string Cap(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        public sealed class AlertRow
        {
            public int Id { get; set; }
            public DateTime FirstAt { get; set; }
            public DateTime LastAt { get; set; }
            public string Category { get; set; } = "";
            public string Message { get; set; } = "";
            public int Count { get; set; }
        }
    }
}
