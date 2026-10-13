using HpskSite.Services.Hosting;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// C3-planen F6: skriver <see cref="RequestErrorCounter"/> till <c>dbo.RequestErrorLog</c> en gång i
    /// minuten (MERGE per dag, status, område, bot) och rensar rader äldre än <see cref="KeepDays"/> en gång
    /// per dygn.
    ///
    /// <para>Egen databasanslutning (<see cref="IUmbracoDatabaseFactory.CreateDatabase"/>), aldrig Umbracos
    /// scope. Går skrivningen inte läggs räkningen tillbaka och prövas nästa minut; saknas tabellen slängs
    /// den (startkontrollen larmar Critical om det).</para>
    /// </summary>
    public class RequestErrorFlushService : IsolatedBackgroundService
    {
        public const int KeepDays = 120;
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private readonly RequestErrorCounter _counter;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<RequestErrorFlushService> _logger;
        private DateTime _lastPrune = DateTime.MinValue;
        private bool _missingTableLogged;

        public RequestErrorFlushService(RequestErrorCounter counter, IUmbracoDatabaseFactory databaseFactory,
            ILogger<RequestErrorFlushService> logger)
        {
            _counter = counter;
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                Flush();
            }
            Flush();
        }

        /// <summary>Skriver det som räknats. Publik för driftskollens "Skriv nu" och för sviten.</summary>
        public int Flush()
        {
            var rows = _counter.Drain();
            if (rows.Count == 0 && DateTime.Now - _lastPrune < TimeSpan.FromDays(1)) return 0;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.RequestErrorLog', 'U')") == null)
                {
                    if (!_missingTableLogged)
                    {
                        _missingTableLogged = true;
                        _logger.LogWarning("Felräknaren: dbo.RequestErrorLog saknas, räkningen sparas inte. Kör Migrations/create-ops-tables.sql.");
                    }
                    return 0;
                }
                foreach (var r in rows)
                {
                    db.Execute(@"MERGE dbo.RequestErrorLog WITH (HOLDLOCK) AS t
USING (SELECT @0 AS [Day], @1 AS StatusCode, @2 AS Area, @3 AS IsBot) AS s
  ON t.[Day] = s.[Day] AND t.StatusCode = s.StatusCode AND t.Area = s.Area AND t.IsBot = s.IsBot
WHEN MATCHED THEN UPDATE SET t.[Count] = t.[Count] + @4,
  t.LastPath = CASE WHEN @6 >= t.LastAt THEN @5 ELSE t.LastPath END,
  t.LastAt = CASE WHEN @6 >= t.LastAt THEN @6 ELSE t.LastAt END
WHEN NOT MATCHED THEN INSERT ([Day], StatusCode, Area, IsBot, [Count], LastPath, LastAt)
  VALUES (@0, @1, @2, @3, @4, @5, @6);",
                        r.Key.Day, r.Key.Status, r.Key.Area, r.Key.IsBot, r.Count, r.LastPath, r.LastAt);
                }
                if (DateTime.Now - _lastPrune >= TimeSpan.FromDays(1))
                {
                    _lastPrune = DateTime.Now;
                    db.Execute("DELETE FROM dbo.RequestErrorLog WHERE [Day] < @0", DateTime.Today.AddDays(-KeepDays));
                }
                return rows.Count;
            }
            catch (Exception ex)
            {
                _counter.Restore(rows);
                _logger.LogWarning(ex, "Felräknaren kunde inte skriva {Rows} rader; prövar igen om en minut.", rows.Count);
                return 0;
            }
        }
    }
}
