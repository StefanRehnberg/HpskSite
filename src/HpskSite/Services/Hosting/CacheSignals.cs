using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Talar om för de ANDRA instanserna att en cache är inaktuell (skalplanen D4, 2026-10-19).
    ///
    /// <para>Fakturalistan, "Det här händer", sajtinställningarna, kretskalendern, händelseadresserna och
    /// seriernas resultat cachas i minnet och töms lokalt när data ändras. Med två instanser visar den andra
    /// gammal data tills cachetiden gått ut (upp till fem minuter) — en markerad betalning ser obetald ut, och
    /// en frysning av lagringen märks inte på den andra servern på en minut. Varje lokal tömning anropar därför
    /// <see cref="Notify"/>, som räknar upp en rad i <c>dbo.CacheSignal</c>; <see cref="CacheSignalPollingService"/>
    /// läser raderna var tredje sekund och tömmer samma cache på sin instans.</para>
    ///
    /// <para><b>Bara när <c>ScaleOut:ServerRole</c> är satt.</b> Annars gör <see cref="Notify"/> ingenting och
    /// pollningen startar inte — exakt som förut. Samma princip som tävlingarnas versionsrad
    /// (<c>CompetitionCacheVersion</c>), som redan gäller för tävlingarna.</para>
    ///
    /// <para>⚠️ En ny cache med lokal tömning: lägg ett namn här, registrera tömningen i
    /// <see cref="Invalidators"/> och anropa <see cref="Notify"/> där den töms lokalt. Tömningen i registret
    /// får INTE själv anropa <see cref="Notify"/> — då pingar instanserna varandra i en slinga.</para>
    /// </summary>
    public sealed class CacheSignals
    {
        public const string Invoices = "invoices";
        public const string WhatsHappening = "whats-happening";
        public const string SiteSettings = "site-settings";
        public const string KretsCalendar = "kretskalender";
        public const string PageAddresses = "page-addresses";
        public const string SeriesResults = "series-results";

        /// <summary>Namn → lokal tömning (körs på de andra instanserna).</summary>
        public static readonly IReadOnlyDictionary<string, Action<IServiceProvider>> Invalidators =
            new Dictionary<string, Action<IServiceProvider>>
            {
                [Invoices] = sp => sp.GetRequiredService<Umbraco.Cms.Core.Cache.AppCaches>().RuntimeCache.ClearByRegex("^admin_invoices_"),
                [WhatsHappening] = sp => sp.GetRequiredService<WhatsHappeningService>().ClearLocal(),
                [SiteSettings] = sp => sp.GetRequiredService<SiteSettings.SiteSettingService>().ClearLocal(),
                [KretsCalendar] = sp => sp.GetRequiredService<Umbraco.Cms.Core.Cache.AppCaches>().RuntimeCache.Clear(Kretsgranskning.KretsCalendarService.IndexCacheKey),
                [PageAddresses] = sp => Addresses.PageAddressService.InvalidateLocal(sp.GetRequiredService<Umbraco.Cms.Core.Cache.AppCaches>()),
                [SeriesResults] = sp => SeriesCalculationService.ClearLocal(sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()),
            };

        private static CacheSignals? _current;
        private readonly string? _connectionString;
        private readonly IOptionsMonitor<ScaleOutOptions> _options;
        private readonly ILogger<CacheSignals> _logger;
        private volatile bool _warned;

        public CacheSignals(IConfiguration config, IOptionsMonitor<ScaleOutOptions> options, ILogger<CacheSignals> logger)
        {
            var cs = config.GetConnectionString("umbracoDbDSN");
            _connectionString = string.IsNullOrWhiteSpace(cs) || cs.Contains("|DataDirectory|") ? null : cs;
            _options = options;
            _logger = logger;
            _current = this;
        }

        public bool Enabled => _connectionString != null && _options.CurrentValue.RoleConfigured;

        /// <summary>
        /// Säg till de andra instanserna att cachen <paramref name="name"/> ska tömmas. Statisk, så att varje
        /// lokal tömning kan anropa den utan ett nytt beroende. Kastar aldrig.
        /// </summary>
        public static void Notify(string name) => _current?.Bump(name);

        private void Bump(string name)
        {
            if (!Enabled) return;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"MERGE dbo.CacheSignal WITH (HOLDLOCK) AS t
USING (SELECT @name AS Name) AS s ON t.Name = s.Name
WHEN MATCHED THEN UPDATE SET Version = t.Version + 1
WHEN NOT MATCHED THEN INSERT (Name, Version) VALUES (s.Name, 1);";
                cmd.Parameters.AddWithValue("@name", name);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    _logger.LogError(ex, "Cachesignalen kunde inte skrivas — de andra instanserna visar gammal data tills cachetiden gått ut. Kör Migrations/create-cache-signal-table.sql.");
                }
            }
        }

        /// <summary>Alla signaler och deras versioner.</summary>
        public Dictionary<string, long> Read()
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name, Version FROM dbo.CacheSignal";
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);
            return result;
        }
    }

    /// <summary>Läser <c>dbo.CacheSignal</c> var tredje sekund och tömmer de cacher andra instanser ändrat.</summary>
    public sealed class CacheSignalPollingService : IsolatedBackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
        private readonly CacheSignals _signals;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<CacheSignalPollingService> _logger;

        public CacheSignalPollingService(CacheSignals signals, IServiceScopeFactory scopes, ILogger<CacheSignalPollingService> logger)
        {
            _signals = signals;
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            Dictionary<string, long>? seen = null;
            var failing = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_signals.Enabled)
                {
                    try
                    {
                        var now = _signals.Read();
                        if (seen != null)
                        {
                            foreach (var (name, version) in now)
                            {
                                if (seen.TryGetValue(name, out var before) && before == version) continue;
                                if (!CacheSignals.Invalidators.TryGetValue(name, out var invalidate)) continue;
                                using var scope = _scopes.CreateScope();
                                try { invalidate(scope.ServiceProvider); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Cachesignalen {Name} kunde inte tömma cachen.", name); }
                            }
                        }
                        seen = now;
                        if (failing) { failing = false; _logger.LogInformation("Cachesignalerna läses igen."); }
                    }
                    catch (Exception ex)
                    {
                        if (!failing)
                        {
                            failing = true;
                            _logger.LogError(ex, "Cachesignalerna kunde inte läsas — den här instansen kan visa gammal data. Kör Migrations/create-cache-signal-table.sql.");
                        }
                    }
                }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
