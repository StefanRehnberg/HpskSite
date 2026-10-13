using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SiteSettings
{
    /// <summary>
    /// Växlar som ska kunna slås om utan omstart, i tabellen <c>SiteSetting</c> (sajtstrukturen fas 1b).
    ///
    /// <para><b>Cachas 60 s.</b> En växel läses vid varje anmälan; en databasfråga per anrop är onödigt
    /// och 60 s är kort nog för ett underhållsfönster. <see cref="Set"/> tömmer cachen direkt på den här
    /// servern.</para>
    ///
    /// <para><b>⚠️ Ett läsfel faller ALDRIG tillbaka på standardvärdet</b> (genomgången 2026-10-13, A2).
    /// Förut svarade en kort låsning eller timeout med standardvärdet i 10 s — för lagringsväxlarna
    /// <c>node</c> — medan prod körde tabellerna: sajten läste och skrev de gamla noderna, och nodspärren
    /// släppte igenom eftersom den läser samma växel. Nu gäller:</para>
    /// <list type="bullet">
    /// <item>Läsfel med ett tidigare lyckat värde på den här servern → det senast kända värdet (det var rätt
    /// för högst 60 s sedan; en omslagning görs alltid härifrån eller syns inom en minut).</item>
    /// <item>Läsfel utan något känt värde → <see cref="SiteSettingUnavailableException"/>. Anropet vägras
    /// hellre än att gissa lagring.</item>
    /// <item>Tabellen saknas helt (ny databas, SQL-fel 208) → standardvärdet. Då har ingen växel någonsin
    /// slagits om, så standardvärdet ÄR det rätta.</item>
    /// </list>
    /// <para>Destruktiva steg (att tömma tabellerna) läser med <see cref="GetFresh"/>: förbi cachen och
    /// utan det senast kända värdet.</para>
    /// </summary>
    public class SiteSettingService
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);
        private const string CachePrefix = "sitesetting_";

        private readonly IScopeProvider _scopeProvider;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SiteSettingService> _logger;
        private readonly ConcurrentDictionary<string, string> _lastGood = new(StringComparer.Ordinal);

        public SiteSettingService(IScopeProvider scopeProvider, IMemoryCache cache, ILogger<SiteSettingService> logger)
        {
            _scopeProvider = scopeProvider;
            _cache = cache;
            _logger = logger;
        }

        public string Get(string key, string defaultValue)
        {
            if (_cache.TryGetValue(CachePrefix + key, out string? cached) && cached != null) return cached;

            string value;
            TimeSpan cacheFor = CacheFor;
            try
            {
                value = Normalize(ReadRaw(key), defaultValue);
                _lastGood[key] = value;
            }
            catch (Exception ex) when (IsMissingTable(ex))
            {
                _logger.LogWarning("Tabellen SiteSetting saknas — {Key} = standardvärdet {Default}", key, defaultValue);
                value = defaultValue;
            }
            catch (Exception ex)
            {
                if (!_lastGood.TryGetValue(key, out var known))
                {
                    _logger.LogError(ex, "Inställningen {Key} kunde inte läsas och inget tidigare värde finns — anropet vägras", key);
                    throw new SiteSettingUnavailableException(key, ex);
                }
                _logger.LogWarning(ex, "Inställningen {Key} kunde inte läsas — det senast kända värdet {Value} används", key, known);
                value = known;
                cacheFor = RetryAfterFailure;
            }

            _cache.Set(CachePrefix + key, value, cacheFor);
            return value;
        }

        /// <summary>
        /// Läser direkt ur databasen, förbi cachen och utan det senast kända värdet. Ett läsfel kastas.
        /// För steg som inte får gissa — t.ex. att tömma tabellerna, som bara är tillåtet när noderna gäller.
        /// </summary>
        public string GetFresh(string key, string defaultValue)
        {
            try
            {
                var value = Normalize(ReadRaw(key), defaultValue);
                _lastGood[key] = value;
                return value;
            }
            catch (Exception ex) when (IsMissingTable(ex))
            {
                return defaultValue;
            }
            catch (Exception ex)
            {
                throw new SiteSettingUnavailableException(key, ex);
            }
        }

        public void Set(string key, string value, int? byMemberId)
        {
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.Database.Execute(@"
MERGE dbo.SiteSetting AS t
USING (SELECT @0 AS SettingKey) AS s ON t.SettingKey = s.SettingKey
WHEN MATCHED THEN UPDATE SET SettingValue = @1, UpdatedUtc = SYSUTCDATETIME(), UpdatedByMemberId = @2
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue, UpdatedUtc, UpdatedByMemberId)
     VALUES (@0, @1, SYSUTCDATETIME(), @2);", key, value, byMemberId);
                scope.Complete();
            }
            _cache.Remove(CachePrefix + key);
            _lastGood[key] = value.Trim();
            _logger.LogWarning("Inställningen {Key} satt till {Value} av medlem {MemberId}", key, value, byMemberId);
        }

        /// <summary>Själva läsningen. Virtuell för testerna.</summary>
        protected virtual string? ReadRaw(string key)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<string?>(
                "SELECT SettingValue FROM dbo.SiteSetting WHERE SettingKey = @0", key);
        }

        private static string Normalize(string? raw, string defaultValue)
            => string.IsNullOrWhiteSpace(raw) ? defaultValue : raw.Trim();

        /// <summary>SQL-fel 208 = "Invalid object name": tabellen finns inte.</summary>
        internal static bool IsMissingTable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 208) return true;
            return false;
        }
    }

    /// <summary>
    /// En växel kunde inte läsas och det finns inget känt värde att lita på. Anropet ska vägras —
    /// aldrig gissa lagring.
    /// </summary>
    public class SiteSettingUnavailableException : InvalidOperationException
    {
        public SiteSettingUnavailableException(string key, Exception inner)
            : base($"Inställningen {key} kunde inte läsas just nu. Försök igen om en stund.", inner) { }
    }
}
