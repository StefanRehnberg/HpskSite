using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// <b>Räknare per nyckel och tidsfönster, för alla instanser</b> (skalplanen D1, 2026-10-18), i
    /// <c>dbo.RateCounter</c>. Ersätter räknare i appens minne där svaret måste vara detsamma oavsett
    /// instans och omstart: spärren per IP-adress på ekonomienkäten och "låsningsmejlet är skickat".
    ///
    /// <para><see cref="Increment"/> räknar upp och returnerar räknaren EFTER det här anropet. Ett
    /// utgånget fönster börjar om på 1. Uppräkningen sker i en transaktion under UPDLOCK/HOLDLOCK, så två
    /// samtidiga anrop får 1 och 2, aldrig båda 1.</para>
    ///
    /// <para><b>⚠️ Egen anslutning, aldrig Umbracos scope.</b> Ett fel i ett scope som deltar i anroparens
    /// hade fällt anroparens transaktion; ingen annan rör tabellen, så den egna anslutningen kan inte vänta
    /// på någons lås (samma regel som <see cref="DatabaseLock"/>).</para>
    ///
    /// <para><b>Saknas tabellen</b> (migreringen inte körd), eller saknas SQL Server, räknas i minnet som
    /// före D1 — samma skydd, aldrig sämre — och det loggas en gång. En spärr får aldrig fälla en
    /// inloggning eller ett enkätsvar.</para>
    /// </summary>
    public sealed class RateCounterService
    {
        private readonly string? _connectionString;
        private readonly IMemoryCache _memory;
        private readonly ILogger<RateCounterService> _logger;
        private readonly object _memoryGate = new();
        private int _fallbackLogged;

        public RateCounterService(IConfiguration config, IMemoryCache memory, ILogger<RateCounterService> logger)
        {
            var cs = config.GetConnectionString("umbracoDbDSN");
            var provider = config.GetConnectionString("umbracoDbDSN_ProviderName") ?? "";
            _connectionString = string.IsNullOrWhiteSpace(cs) || cs.Contains("|DataDirectory|")
                || provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? null : cs;
            _memory = memory;
            _logger = logger;
        }

        public int Increment(string key, TimeSpan window)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
                throw new ArgumentException("Nyckeln måste vara 1–200 tecken.", nameof(key));
            var now = DateTime.UtcNow;
            if (_connectionString == null) return IncrementInMemory(key, window, now);
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                // Tiden räknas EFTER att anslutningen öppnats: en kall anslutning kan ta en halv sekund,
                // och fönstret ska mätas från uppräkningen, inte från anropet.
                now = DateTime.UtcNow;
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
IF EXISTS (SELECT 1 FROM dbo.RateCounter WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = @key)
    UPDATE dbo.RateCounter
       SET [Count] = CASE WHEN WindowEndsUtc <= @now THEN 1 ELSE [Count] + 1 END,
           WindowEndsUtc = CASE WHEN WindowEndsUtc <= @now THEN @ends ELSE WindowEndsUtc END
     WHERE [Key] = @key;
ELSE
    INSERT INTO dbo.RateCounter ([Key], [Count], WindowEndsUtc) VALUES (@key, 1, @ends);
SELECT [Count] FROM dbo.RateCounter WHERE [Key] = @key;";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.Add("@now", System.Data.SqlDbType.DateTime2).Value = now;
                cmd.Parameters.Add("@ends", System.Data.SqlDbType.DateTime2).Value = now.Add(window);
                var count = Convert.ToInt32(cmd.ExecuteScalar());

                // Städning i förbigående: rader vars fönster gick ut för mer än ett dygn sedan.
                if (Random.Shared.Next(50) == 0)
                {
                    using var clean = conn.CreateCommand();
                    clean.Transaction = tx;
                    clean.CommandText = "DELETE TOP (500) FROM dbo.RateCounter WHERE WindowEndsUtc < @old";
                    clean.Parameters.Add("@old", System.Data.SqlDbType.DateTime2).Value = now.AddDays(-1);
                    clean.ExecuteNonQuery();
                }

                tx.Commit();
                return count;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
                {
                    if (ex is SqlException { Number: 208 })
                        _logger.LogError("dbo.RateCounter saknas — räknarna gäller bara den här instansen. Kör Migrations/create-rate-counter-table.sql.");
                    else
                        _logger.LogError(ex, "RateCounter kunde inte räkna upp {Key} — räknarna gäller bara den här instansen tills vidare", key);
                }
                return IncrementInMemory(key, window, now);
            }
        }

        /// <summary>Sant första gången nyckeln räknas inom fönstret — "gör det här en gång per fönster".</summary>
        public bool TryClaimOnce(string key, TimeSpan window) => Increment(key, window) == 1;

        private int IncrementInMemory(string key, TimeSpan window, DateTime now)
        {
            var memKey = "ratecounter:" + key;
            lock (_memoryGate)
            {
                var (count, ends) = _memory.TryGetValue<(int Count, DateTime Ends)>(memKey, out var v) && v.Ends > now
                    ? v : (0, now.Add(window));
                count++;
                _memory.Set(memKey, (count, ends), new DateTimeOffset(DateTime.SpecifyKind(ends, DateTimeKind.Utc)));
                return count;
            }
        }
    }
}
