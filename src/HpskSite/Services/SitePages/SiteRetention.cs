using HpskSite.Services.Addresses;
using HpskSite.Services.Hosting;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>Vad en gallringskörning tog bort.</summary>
    public sealed record SiteRetentionResult(int PagesPurged, int VersionsDeleted, int DraftsDiscarded, int GrantsDeleted, int AuditDeleted, int IpHashesCleared)
    {
        public int Total => PagesPurged + VersionsDeleted + DraftsDiscarded + GrantsDeleted + AuditDeleted + IpHashesCleared;
    }

    /// <summary>
    /// Gallringen för klubbens och kretsens egna sidor (klubbsidorna 2b, plan v3 A6/A8, DPA §9). Ren SQL över <see cref="IDatabase"/>, så att varje regel
    /// bevisas mot riktiga rader. Körs i anroparens transaktion i omgångar om högst <see cref="Batch"/> rader per sats.
    ///
    /// <list type="bullet">
    /// <item><b>Raderade sidor:</b> slutligt borta 30 dagar efter <c>DeletedDate</c> (menyrader, adresser — aldrig efter sidan, så en 301 aldrig pekar på en
    /// borttagen sida — och sidan; versioner och referenser följer med CASCADE).</item>
    /// <item><b>Versioner:</b> <c>PublishedVersionId</c>, <c>DraftVersionId</c> och baslinjen raderas aldrig av åldersregeln. Övriga: de senaste 25 per sida
    /// behålls; äldre än 90 dagar utanför de 25 raderas; äldre än 12 månader raderas oavsett antal (en raderingsbegäran går alltid före); tak 100 per sida
    /// (äldst först).</item>
    /// <item><b>Utkast:</b> ett utkast som inte rörts på 90 dagar kastas — men bara på en sida som redan är publicerad (en aldrig publicerad sida har inget annat innehåll).</item>
    /// <item><b>Stödläge:</b> tillstånd raderas 90 dagar efter att de gått ut. <b>Audit:</b> raderas efter 24 månader; saltad IP-hash nollas efter 90 dagar.</item>
    /// </list>
    /// </summary>
    public static class SiteRetention
    {
        public const int Batch = 500;
        public const int KeepVersions = 25;
        public const int MaxVersionsPerPage = 100;
        public const int VersionDays = 90;
        public const int VersionMonthsHardLimit = 12;
        public const int DraftDays = 90;
        public const int DeletedPageDays = 30;
        public const int GrantDays = 90;
        public const int AuditMonths = 24;
        public const int IpHashDays = 90;
        private const int MaxRounds = 40;

        public static SiteRetentionResult Run(IDatabase db, DateTime now)
        {
            var pages = PurgeDeletedPages(db, now);
            var drafts = DiscardStaleDrafts(db, now);
            var versions = DeleteOldVersions(db, now);
            var grants = Repeat(() => db.Execute($"DELETE TOP ({Batch}) FROM dbo.SiteSupportGrant WHERE ExpiresDate < @0", now.AddDays(-GrantDays)));
            var audit = Repeat(() => db.Execute($"DELETE TOP ({Batch}) FROM dbo.SiteAudit WHERE CreatedDate < @0", now.AddMonths(-AuditMonths)));
            var ip = db.Execute("UPDATE dbo.SiteAudit SET IpHash = NULL WHERE IpHash IS NOT NULL AND CreatedDate < @0", now.AddDays(-IpHashDays));
            return new SiteRetentionResult(pages, versions, drafts, grants, audit, ip);
        }

        private static int Repeat(Func<int> step)
        {
            var total = 0;
            for (var i = 0; i < MaxRounds; i++)
            {
                var n = step();
                total += n;
                if (n < Batch) break;
            }
            return total;
        }

        private static int PurgeDeletedPages(IDatabase db, DateTime now)
        {
            var cut = now.AddDays(-DeletedPageDays);
            var total = 0;
            for (var i = 0; i < MaxRounds; i++)
            {
                var ids = db.Fetch<int>($"SELECT TOP ({Batch}) Id FROM dbo.SitePage WHERE DeletedDate IS NOT NULL AND DeletedDate < @0 ORDER BY Id", cut);
                if (ids.Count == 0) break;
                // Ett id i taget (ingen IN-lista: NPoco kan expandera den tyst fel). Adressen FÖRE sidan, menyraden före båda (FK).
                foreach (var id in ids)
                {
                    db.Execute("DELETE FROM dbo.SiteMenuItem WHERE PageId = @0", id);
                    db.Execute("DELETE FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1", PageAddressKinds.SitePage, id);
                    db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = NULL, DraftVersionId = NULL WHERE Id = @0", id);
                    total += db.Execute("DELETE FROM dbo.SitePage WHERE Id = @0", id);
                }
                if (ids.Count < Batch) break;
            }
            return total;
        }

        private static int DiscardStaleDrafts(IDatabase db, DateTime now)
        {
            var cut = now.AddDays(-DraftDays);
            var stale = db.Fetch<StaleDraft>(
                $@"SELECT TOP ({Batch}) p.Id AS PageId, v.Id AS VersionId
                   FROM dbo.SitePage p JOIN dbo.SitePageVersion v ON v.Id = p.DraftVersionId AND v.PageId = p.Id
                   WHERE p.DeletedDate IS NULL AND p.PublishedVersionId IS NOT NULL AND v.IsBaseline = 0 AND v.SavedDate < @0
                   ORDER BY p.Id", cut);
            foreach (var d in stale)
            {
                db.Execute("UPDATE dbo.SitePage SET DraftVersionId = NULL WHERE Id = @0 AND DraftVersionId = @1", d.PageId, d.VersionId);
                db.Execute("DELETE FROM dbo.SitePageVersion WHERE Id = @0 AND PageId = @1 AND IsBaseline = 0", d.VersionId, d.PageId);
            }
            return stale.Count;
        }

        private sealed class StaleDraft { public int PageId { get; set; } public int VersionId { get; set; } }

        private static int DeleteOldVersions(IDatabase db, DateTime now)
        {
            var soft = now.AddDays(-VersionDays);
            var hard = now.AddMonths(-VersionMonthsHardLimit);
            // Rangordning per sida, senaste först, utan pekare och baslinje (de räknas aldrig och raderas aldrig av åldersregeln).
            const string sql = @"
;WITH ranked AS (
    SELECT v.Id, v.SavedDate, ROW_NUMBER() OVER (PARTITION BY v.PageId ORDER BY v.Id DESC) AS rn
    FROM dbo.SitePageVersion v JOIN dbo.SitePage p ON p.Id = v.PageId
    WHERE v.IsBaseline = 0 AND v.Id <> ISNULL(p.PublishedVersionId, 0) AND v.Id <> ISNULL(p.DraftVersionId, 0)
      -- Lägg tillbaka (konvertera-planen K2): på en omgjord sida skyddas den senaste versionen i schema 1 (det sidan hade före omvandlingen), hur gammal den än är.
      AND NOT (v.SchemaVersion = 1
               AND v.Id = (SELECT MAX(x.Id) FROM dbo.SitePageVersion x WHERE x.PageId = v.PageId AND x.SchemaVersion = 1 AND x.IsBaseline = 0)
               AND EXISTS (SELECT 1 FROM dbo.SitePageVersion c WHERE c.PageId = p.Id AND c.SchemaVersion = 2 AND c.Id IN (ISNULL(p.PublishedVersionId, 0), ISNULL(p.DraftVersionId, 0)))))
DELETE TOP (@3) v FROM dbo.SitePageVersion v JOIN ranked r ON r.Id = v.Id
WHERE (r.rn > @4 AND r.SavedDate < @0) OR r.SavedDate < @1 OR r.rn > @2";
            return Repeat(() => db.Execute(sql, soft, hard, MaxVersionsPerPage, Batch, KeepVersions));
        }
    }

    /// <summary>
    /// Dygnsjobbet: kör <see cref="SiteRetention.Run"/> en gång per dygn, bara på publiceraren (<c>MayRunScheduled</c>). Ett fel loggas; gallringen
    /// försöker igen nästa dygn (den är idempotent). Saknas sidtabellerna gör jobbet ingenting (startkontrollen larmar redan).
    /// </summary>
    public class SiteRetentionHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(7);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SiteRetentionHostedService> _logger;

        public SiteRetentionHostedService(IServiceScopeFactory scopeFactory, ILogger<SiteRetentionHostedService> logger)
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
                try { RunOnce(); }
                catch (Exception ex) { _logger.LogError(ex, "Klubbsidornas gallring kunde inte köras."); }
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private void RunOnce()
        {
            if (!_scopeFactory.MayRunScheduled(nameof(SiteRetentionHostedService))) return;
            using var scope = _scopeFactory.CreateScope();
            var scopes = scope.ServiceProvider.GetRequiredService<IScopeProvider>();
            SiteRetentionResult r;
            using (var s = scopes.CreateScope())
            {
                if (s.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.SitePageVersion', 'U') IS NULL THEN 0 ELSE 1 END") == 0) { s.Complete(); return; }
                r = SiteRetention.Run(s.Database, DateTime.Now);
                s.Complete();
            }
            if (r.Total > 0) _logger.LogInformation("Klubbsidornas gallring: {Pages} sidor, {Versions} versioner, {Drafts} utkast, {Grants} stödlägen, {Audit} loggrader raderade, {Ip} IP-hashar nollade.",
                r.PagesPurged, r.VersionsDeleted, r.DraftsDiscarded, r.GrantsDeleted, r.AuditDeleted, r.IpHashesCleared);
        }
    }
}
