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
    /// <para><b>⚠️ Saknas tabellen svarar läsningen med standardvärdet</b> och loggar en varning. Det är
    /// rätt riktning för de växlar som finns: standard = nodlagringen, ingen frysning — alltså dagens
    /// beteende. En växel vars standardvärde är det OSÄKRA läget hör inte hit.</para>
    /// </summary>
    public class SiteSettingService
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
        private const string CachePrefix = "sitesetting_";

        private readonly IScopeProvider _scopeProvider;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SiteSettingService> _logger;

        public SiteSettingService(IScopeProvider scopeProvider, IMemoryCache cache, ILogger<SiteSettingService> logger)
        {
            _scopeProvider = scopeProvider;
            _cache = cache;
            _logger = logger;
        }

        public string Get(string key, string defaultValue)
        {
            return _cache.GetOrCreate(CachePrefix + key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                try
                {
                    using var scope = _scopeProvider.CreateScope(autoComplete: true);
                    var value = scope.Database.ExecuteScalar<string?>(
                        "SELECT SettingValue FROM dbo.SiteSetting WHERE SettingKey = @0", key);
                    return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Inställningen {Key} kunde inte läsas — standardvärdet {Default} används", key, defaultValue);
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10);
                    return defaultValue;
                }
            }) ?? defaultValue;
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
            _logger.LogWarning("Inställningen {Key} satt till {Value} av medlem {MemberId}", key, value, byMemberId);
        }
    }
}
