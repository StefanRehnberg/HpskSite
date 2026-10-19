using HpskSite.Models.Competitions;
using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Addresses
{
    /// <summary>
    /// <b>Umbracos lagrade omdirigeringar till tävlingsfamiljen och händelserna, kopierade till <c>dbo.LegacyRedirect</c></b>
    /// (C3-planen §5.5 punkt 2). Umbraco omdirigerar bara till PUBLICERAT innehåll — efter avpubliceringen gav en
    /// gammal adress till en omdöpt tävling 404 (mätt på kopian: 19 tävlingar, 10 listor och resultat, en serie) — och
    /// när noden raderas tas raderna i <c>umbracoRedirectUrl</c> bort med den. Kopian överlever båda;
    /// <see cref="HpskSite.Routing.LegacyRedirectFinder"/> svarar 301 till sidans nya adress.
    ///
    /// <para>Kopieringen är idempotent (en rad per adress, den senaste vinner bara när adressen inte redan finns) och
    /// körs vid varje start och före varje radering av noder.</para>
    /// </summary>
    public class LegacyRedirectService
    {
        public const string Table = "dbo.LegacyRedirect";

        /// <summary>Dokumenttyperna vars omdirigeringar kopieras. Sorten lagras som dokumenttypens alias.</summary>
        public static readonly string[] TargetKinds =
        {
            CompetitionFieldTable.NodeAlias,
            CompetitionSeriesFieldTable.NodeAlias,
            ResultKind,
            QualifyingStartListKind,
            FinalsStartListKind,
            EventKind,
        };

        public const string ResultKind = "competitionResult";            // resultstore-ok: omdirigeringens sort, ingen läsning
        public const string QualifyingStartListKind = "precisionStartList"; // startliststore-ok: omdirigeringens sort, ingen läsning
        public const string FinalsStartListKind = "finalsStartList";     // startliststore-ok: omdirigeringens sort, ingen läsning
        public const string EventKind = "clubSimpleEvent";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private readonly IScopeProvider _scopes;
        private readonly ILogger<LegacyRedirectService> _logger;
        private readonly object _gate = new();
        private Dictionary<string, Row>? _cache;
        private DateTime _cachedAt;

        public LegacyRedirectService(IScopeProvider scopes, ILogger<LegacyRedirectService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        public sealed record Row(string Url, string TargetKind, int TargetId, int? CompetitionId);

        private sealed class DbRow
        {
            public string Url { get; set; } = "";
            public string TargetKind { get; set; } = "";
            public int TargetId { get; set; }
            public int? CompetitionId { get; set; }
        }

        /// <summary>Sökvägen i den form raderna lagras: gemener, inget avslutande snedstreck, ingen frågesträng.</summary>
        public static string Normalize(string path)
        {
            var p = (path ?? "").Split('?')[0].Trim().ToLowerInvariant().TrimEnd('/');
            return p.StartsWith('/') ? p : "/" + p;
        }

        public bool TableExists()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.LegacyRedirect', 'U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        /// <summary>
        /// Kopierar raderna som saknas. Umbracos <c>url</c> kan bära en domänrots id före sökvägen (<c>1234/sökväg</c>);
        /// det tas bort. Finns samma adress flera gånger vinner den senaste. Returnerar antalet nya rader.
        /// </summary>
        public int CopyFromUmbraco()
        {
            using var s = _scopes.CreateScope();
            var n = s.Database.Execute(@"SET QUOTED_IDENTIFIER ON;
WITH src AS (
    SELECT LOWER(CASE WHEN r.url LIKE '[0-9]%/%' THEN SUBSTRING(r.url, CHARINDEX('/', r.url), 400) ELSE r.url END) AS RawUrl,
           ct.alias AS TargetKind, n.id AS TargetId,
           CASE WHEN ct.alias = @0 THEN n.parentId END AS CompetitionId,
           ROW_NUMBER() OVER (PARTITION BY LOWER(CASE WHEN r.url LIKE '[0-9]%/%' THEN SUBSTRING(r.url, CHARINDEX('/', r.url), 400) ELSE r.url END)
                              ORDER BY r.createDateUtc DESC) AS rn
    FROM umbracoRedirectUrl r
    JOIN umbracoNode n ON n.uniqueId = r.contentKey
    JOIN umbracoContent c ON c.nodeId = n.id
    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
    WHERE ct.alias IN (@1, @2, @3, @4, @5, @6)
), norm AS (
    SELECT CASE WHEN RIGHT(RawUrl, 1) = '/' AND LEN(RawUrl) > 1 THEN LEFT(RawUrl, LEN(RawUrl) - 1) ELSE RawUrl END AS Url,
           TargetKind, TargetId, CompetitionId
    FROM src WHERE rn = 1
)
INSERT INTO dbo.LegacyRedirect (Url, TargetKind, TargetId, CompetitionId, CopiedUtc)
SELECT CASE WHEN LEFT(Url, 1) = '/' THEN Url ELSE '/' + Url END, TargetKind, TargetId, CompetitionId, SYSUTCDATETIME()
FROM norm
WHERE NOT EXISTS (SELECT 1 FROM dbo.LegacyRedirect x WHERE x.Url = CASE WHEN LEFT(norm.Url, 1) = '/' THEN norm.Url ELSE '/' + norm.Url END)",
                ResultKind, TargetKinds[0], TargetKinds[1], TargetKinds[2], TargetKinds[3], TargetKinds[4], TargetKinds[5]);
            s.Complete();
            if (n > 0) Invalidate();
            return n;
        }

        public int Count()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.LegacyRedirect");
        }

        /// <summary>Raden för sökvägen, eller null. Tabellen är liten och läses i sin helhet, cachad några minuter.</summary>
        public Row? Find(string path)
        {
            var map = Load();
            return map.TryGetValue(Normalize(path), out var row) ? row : null;
        }

        public void Invalidate()
        {
            lock (_gate) _cache = null;
        }

        private Dictionary<string, Row> Load()
        {
            lock (_gate)
            {
                if (_cache != null && DateTime.UtcNow - _cachedAt < CacheTtl) return _cache;
            }
            Dictionary<string, Row> map;
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                map = s.Database.Fetch<DbRow>("SELECT Url, TargetKind, TargetId, CompetitionId FROM dbo.LegacyRedirect")
                    .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => { var r = g.First(); return new Row(r.Url, r.TargetKind, r.TargetId, r.CompetitionId); },
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // Saknas tabellen svarar uppslaget inget — som före kopieringen. Startkontrollen larmar.
                _logger.LogDebug(ex, "LegacyRedirect kunde inte läsas.");
                map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            }
            lock (_gate)
            {
                _cache = map;
                _cachedAt = DateTime.UtcNow;
            }
            return map;
        }
    }

    /// <summary>Kopierar omdirigeringarna en minut efter varje start. Larmar när tabellen saknas.</summary>
    public class LegacyRedirectCopyHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LegacyRedirectService _redirects;
        private readonly ILogger<LegacyRedirectCopyHostedService> _logger;

        public LegacyRedirectCopyHostedService(IServiceScopeFactory scopeFactory, LegacyRedirectService redirects,
            ILogger<LegacyRedirectCopyHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _redirects = redirects;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            // Skalplanen D4: skrivningen görs av publiceraren.
            if (!_scopeFactory.MayRunScheduled(nameof(LegacyRedirectCopyHostedService))) return;
            try
            {
                if (!_redirects.TableExists())
                {
                    _logger.LogCritical("GAMLA ADRESSER GER 404: dbo.LegacyRedirect saknas, så Umbracos omdirigeringar till "
                        + "avpublicerade tävlingar, listor och händelser kopieras inte. Kör Migrations/create-legacy-redirect-table.sql.");
                    return;
                }
                var n = _redirects.CopyFromUmbraco();
                _logger.LogInformation("Gamla omdirigeringar: {New} nya kopierade, {Total} totalt.", n, _redirects.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gamla omdirigeringar kunde inte kopieras vid start.");
            }
        }
    }
}
