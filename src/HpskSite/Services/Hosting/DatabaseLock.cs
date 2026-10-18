using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// <b>Ett lås som gäller för alla instanser</b> (skalplanen D1, 2026-10-18): <c>sp_getapplock</c> på en
    /// egen anslutning. Ersätter <c>lock</c>/<c>SemaphoreSlim</c> där korrektheten hänger på att bara en
    /// åt gången gör kontroll-och-skrivning (egenbokningens platser, rankningen, de gamla fakturamapparna).
    /// Ett lås i processen skyddar bara mot förfrågningar i SAMMA process — med två instanser, eller vid
    /// en omstart med överlapp, säljs samma plats två gånger.
    ///
    /// <para><b>⚠️ Egen anslutning, aldrig Umbracos scope.</b> Låset rör inga tabeller, så det kan inte
    /// vänta på någon annans transaktion (jfr minnet om bryggan i någon annans scope). Låsets ägare är
    /// SESSIONEN, så det kräver ingen transaktion och släpps när anslutningen stängs.</para>
    ///
    /// <para><b>Utan SQL Server</b> (ingen anslutningssträng, SQLite) eller när låset inte går att ta på
    /// grund av ett databasfel används ett lås i processen — samma skydd som före D1, aldrig sämre.
    /// Felet loggas.</para>
    ///
    /// <para>Resursnamnet är en del av kontraktet mellan instanserna: <c>"{sort}:{id}"</c>, högst 255
    /// tecken. Ändra aldrig ett namn utan att alla anropare ändras samtidigt.</para>
    /// </summary>
    public sealed class DatabaseLock
    {
        private readonly string? _connectionString;
        private readonly ILogger<DatabaseLock> _logger;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Local = new(StringComparer.Ordinal);

        public DatabaseLock(IConfiguration config, ILogger<DatabaseLock> logger)
        {
            var cs = config.GetConnectionString("umbracoDbDSN");
            var provider = config.GetConnectionString("umbracoDbDSN_ProviderName") ?? "";
            _connectionString = string.IsNullOrWhiteSpace(cs) || cs.Contains("|DataDirectory|")
                || provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? null : cs;
            _logger = logger;
        }

        /// <summary>Tar låset, väntar högst <paramref name="timeout"/>. Null = någon annan håller det.</summary>
        public Lease? TryAcquire(string resource, TimeSpan timeout)
        {
            Validate(resource);
            if (_connectionString != null)
            {
                SqlConnection? conn = null;
                try
                {
                    conn = new SqlConnection(_connectionString);
                    conn.Open();
                    var result = GetAppLock(conn, resource, timeout).ExecuteScalarOrReturn();
                    if (result >= 0) return new Lease(this, resource, conn, null);
                    conn.Dispose();
                    if (result == -1) return null;
                    _logger.LogWarning("sp_getapplock för {Resource} svarade {Result} — låset i processen används", resource, result);
                }
                catch (Exception ex)
                {
                    conn?.Dispose();
                    _logger.LogWarning(ex, "Låset {Resource} kunde inte tas i databasen — låset i processen används", resource);
                }
            }
            var local = Local.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
            return local.Wait(timeout) ? new Lease(this, resource, null, local) : null;
        }

        /// <inheritdoc cref="TryAcquire"/>
        public async Task<Lease?> TryAcquireAsync(string resource, TimeSpan timeout, CancellationToken ct = default)
        {
            Validate(resource);
            if (_connectionString != null)
            {
                SqlConnection? conn = null;
                try
                {
                    conn = new SqlConnection(_connectionString);
                    await conn.OpenAsync(ct);
                    var cmd = GetAppLock(conn, resource, timeout);
                    await cmd.ExecuteNonQueryAsync(ct);
                    var result = (int)cmd.Parameters["@result"].Value;
                    if (result >= 0) return new Lease(this, resource, conn, null);
                    await conn.DisposeAsync();
                    if (result == -1) return null;
                    _logger.LogWarning("sp_getapplock för {Resource} svarade {Result} — låset i processen används", resource, result);
                }
                catch (OperationCanceledException) { conn?.Dispose(); throw; }
                catch (Exception ex)
                {
                    conn?.Dispose();
                    _logger.LogWarning(ex, "Låset {Resource} kunde inte tas i databasen — låset i processen används", resource);
                }
            }
            var local = Local.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
            return await local.WaitAsync(timeout, ct) ? new Lease(this, resource, null, local) : null;
        }

        private static void Validate(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource) || resource.Length > 255)
                throw new ArgumentException("Låsets namn måste vara 1–255 tecken.", nameof(resource));
        }

        private static SqlCommand GetAppLock(SqlConnection conn, string resource, TimeSpan timeout)
        {
            var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "sp_getapplock";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = Math.Max(30, ms / 1000 + 30);
            cmd.Parameters.AddWithValue("@Resource", resource);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", ms);
            cmd.Parameters.Add(new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
            return cmd;
        }

        private void Release(string resource, SqlConnection? conn, SemaphoreSlim? local)
        {
            if (local != null) { local.Release(); return; }
            if (conn == null) return;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "sp_releaseapplock";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@Resource", resource);
                cmd.Parameters.AddWithValue("@LockOwner", "Session");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Sessionen bär låset tills den stängs. En poolad anslutning lever vidare, så poolen töms —
                // då stängs sessionen och låset släpps i stället för att hänga kvar till nästa omstart.
                _logger.LogWarning(ex, "Låset {Resource} kunde inte släppas — anslutningspoolen töms", resource);
                SqlConnection.ClearPool(conn);
            }
            finally
            {
                conn.Dispose();
            }
        }

        /// <summary>Ett taget lås. Släpps vid Dispose.</summary>
        public sealed class Lease : IDisposable, IAsyncDisposable
        {
            private readonly DatabaseLock _owner;
            private readonly string _resource;
            private SqlConnection? _conn;
            private SemaphoreSlim? _local;
            private int _released;

            internal Lease(DatabaseLock owner, string resource, SqlConnection? conn, SemaphoreSlim? local)
            {
                _owner = owner; _resource = resource; _conn = conn; _local = local;
            }

            /// <summary>Sant när låset gäller alla instanser (databasen), falskt när det bara gäller processen.</summary>
            public bool IsDistributed => _conn != null;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 1) return;
                _owner.Release(_resource, _conn, _local);
                _conn = null; _local = null;
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal static class DatabaseLockCommandExtensions
    {
        public static int ExecuteScalarOrReturn(this SqlCommand cmd)
        {
            cmd.ExecuteNonQuery();
            return (int)cmd.Parameters["@result"].Value;
        }
    }
}
