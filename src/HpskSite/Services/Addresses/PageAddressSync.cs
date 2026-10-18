using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Addresses
{
    /// <summary>
    /// Håller tävlingarnas och seriernas adresser i takt med deras värden: varje publicering räknar om
    /// adressen (värd, år, slug). Ändras inget skrivs inget. Ett fel här fäller aldrig publiceringen —
    /// sidan behåller då sin förra adress, och felet loggas.
    /// </summary>
    public class PageAddressPublishedHandler : INotificationHandler<ContentPublishedNotification>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly ILogger<PageAddressPublishedHandler> _logger;

        public PageAddressPublishedHandler(IServiceScopeFactory scopeFactory, IUmbracoContextFactory contextFactory,
            ILogger<PageAddressPublishedHandler> logger)
        {
            _scopeFactory = scopeFactory;
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public void Handle(ContentPublishedNotification notification)
        {
            var targets = notification.PublishedEntities
                .Where(e => e.ContentType.Alias is "competition" or "competitionSeries") // competitionstore-ok: publiceringen räknar om adressen
                .Select(e => (e.ContentType.Alias, e.Id)).ToList();
            if (targets.Count == 0) return;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                using var umbracoContext = _contextFactory.EnsureUmbracoContext();
                var svc = scope.ServiceProvider.GetRequiredService<CompetitionAddressService>();
                foreach (var (alias, id) in targets)
                    _ = alias == "competition" ? svc.SyncCompetition(id) : svc.SyncSeries(id); // competitionstore-ok: se ovan
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adressen kunde inte räknas om efter publiceringen av {Ids}", string.Join(", ", targets.Select(t => t.Id)));
            }
        }
    }

    /// <summary>
    /// Namn som skulle ta över en adress vägras när noden sparas:
    /// <list type="bullet">
    /// <item>direkt under startsidan: adressernas toppsegment (<c>sm</c>, <c>ssm</c> … <c>tavlingar</c>)</item>
    /// <item>en klubb: <c>klubbar</c>, sortsegmenten (<c>tavlingar</c>, <c>handelser</c>, <c>serier</c>), ett årtal,
    /// och namnet på en sida som redan ligger direkt under kretsen — klubbens adress är <c>/{krets}/{klubb}/</c>
    /// (<see cref="HpskSite.Routing.ClubShortUrls"/>)</item>
    /// <item>en sida direkt under en krets: sortsegmenten, ett årtal, och namnet på en av kretsens klubbar</item>
    /// </list>
    /// </summary>
    public class PageAddressReservedNames : INotificationHandler<ContentSavingNotification>
    {
        private readonly IContentService _content;
        private readonly IShortStringHelper _shortStrings;

        public PageAddressReservedNames(IContentService content, IShortStringHelper shortStrings)
        {
            _content = content;
            _shortStrings = shortStrings;
        }

        public void Handle(ContentSavingNotification notification)
        {
            foreach (var e in notification.SavedEntities)
            {
                if (e.ParentId <= 0) continue;
                var segment = _shortStrings.CleanStringForUrlSegment(e.Name ?? "");
                if (string.IsNullOrEmpty(segment)) continue;
                var problem = Problem(e, segment);
                if (problem == null) continue;
                notification.CancelOperation(new EventMessage("Adress", problem, EventMessageType.Error));
                return;
            }
        }

        private string? Problem(Umbraco.Cms.Core.Models.IContent e, string segment)
        {
            var parent = _content.GetById(e.ParentId);
            if (parent == null) return null;

            if (parent.ParentId == Umbraco.Cms.Core.Constants.System.Root
                && PageAddressRules.ReservedTopSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return $"Namnet \"{e.Name}\" är reserverat för tävlingarnas adresser och kan inte användas direkt under startsidan.";

            if (e.ContentType.Alias == "club" && parent.ContentType.Alias == "clubsPage")
            {
                if (PageAddressRules.IsReservedOwnerChildSegment(segment))
                    return $"Klubben kan inte heta \"{e.Name}\": ordet används i klubbens och kretsens adresser.";
                var region = _content.GetById(parent.ParentId);
                if (region?.ContentType.Alias == "regionalPage"
                    && ChildSegments(region.Id).Any(c => c.Id != parent.Id && c.Id != e.Id && SameSegment(c, segment)))
                    return $"Klubben kan inte heta \"{e.Name}\": kretsen har redan en sida med den adressen.";
                return null;
            }

            if (parent.ContentType.Alias == "regionalPage" && e.ContentType.Alias != "clubsPage")
            {
                if (PageAddressRules.IsReservedOwnerChildSegment(segment))
                    return $"Sidan kan inte heta \"{e.Name}\" direkt under kretsen: ordet används i kretsens adresser.";
                var directory = ChildSegments(parent.Id).FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                if (directory != null && ChildSegments(directory.Id).Any(c => c.Id != e.Id && SameSegment(c, segment)))
                    return $"Sidan kan inte heta \"{e.Name}\" direkt under kretsen: en av kretsens klubbar har den adressen.";
            }
            return null;
        }

        private IEnumerable<Umbraco.Cms.Core.Models.IContent> ChildSegments(int parentId)
            => _content.GetPagedChildren(parentId, 0, 2000, out _);

        private bool SameSegment(Umbraco.Cms.Core.Models.IContent c, string segment)
            => string.Equals(_shortStrings.CleanStringForUrlSegment(c.Name ?? ""), segment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Vid start: (1) händelseadresser som den gamla koden hann skriva i <c>ClubEventUrl</c> mellan migreringen
    /// och deployen kopieras till <c>PageAddress</c>, (2) varje publicerad tävling och serie får sin adress.
    /// Båda är idempotenta. Utan (2) har en tävling ingen ny adress förrän den publiceras nästa gång — den visas
    /// då på sin gamla, så ingenting går sönder under tiden.
    /// </summary>
    public class PageAddressSeedHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
        public const string EventCopyDoneKey = "PageAddress:EventCopyDone";
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly ILogger<PageAddressSeedHostedService> _logger;

        public PageAddressSeedHostedService(IServiceScopeFactory scopeFactory, IUmbracoContextFactory contextFactory,
            ILogger<PageAddressSeedHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _contextFactory = contextFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                if (!TableExists(sp))
                {
                    _logger.LogCritical("ADRESSERNA ÄR TRASIGA: dbo.PageAddress saknas. Händelsernas sidor och tävlingarnas nya adresser "
                        + "svarar inte. Kör Migrations/create-page-address-table.sql (idempotent).");
                    return;
                }
                // ⚠️ Bara EN gång: efter första starten skriver ingen ClubEventUrl längre, och en ny kopiering
                // hade skrivit tillbaka de gamla adresserna över de nya.
                var settings = sp.GetRequiredService<HpskSite.Services.SiteSettings.SiteSettingService>();
                var copied = 0;
                if (settings.GetFresh(EventCopyDoneKey, "0") != "1")
                {
                    copied = CopyEventAddresses(sp);
                    settings.Set(EventCopyDoneKey, "1", null);
                }
                using var umbracoContext = _contextFactory.EnsureUmbracoContext();
                var results = sp.GetRequiredService<CompetitionAddressService>().SyncAll();
                sp.GetRequiredService<PageAddressService>().Invalidate();
                var changed = results.Count(r => r.Changed);
                var problems = results.Where(r => r.Problem != null && !r.Problem.StartsWith("ingen värd")).ToList();
                _logger.LogInformation("Adresserna: {Copied} händelseadresser kopierade, {Changed} av {Count} tävlingar och serier fick ny adress, {NoHost} utan värd.",
                    copied, changed, results.Count, results.Count(r => r.Problem?.StartsWith("ingen värd") == true));
                foreach (var p in problems)
                    _logger.LogWarning("Adressen för {Kind} {Id}: {Problem}", p.Kind, p.Id, p.Problem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adresserna kunde inte sättas vid start.");
            }
        }

        private static bool TableExists(IServiceProvider sp)
        {
            using var s = sp.GetRequiredService<IScopeProvider>().CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.PageAddress', 'U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        private static int CopyEventAddresses(IServiceProvider sp)
        {
            using var s = sp.GetRequiredService<IScopeProvider>().CreateScope();
            var db = s.Database;
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.ClubEventUrl', 'U') IS NULL THEN 0 ELSE 1 END") == 0)
            {
                s.Complete();
                return 0;
            }
            // Rader som saknas på adressen. En aktuell rad blir bara aktuell om händelsen inte redan har en.
            var n = db.Execute(@"SET QUOTED_IDENTIFIER ON;
INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent, CreatedDate)
SELECT u.OwnerType, u.OwnerId, u.UrlYear, u.Slug, 'event', u.EventId,
       CASE WHEN u.IsCurrent = 1 AND NOT EXISTS (SELECT 1 FROM dbo.PageAddress c WHERE c.TargetKind = 'event' AND c.TargetId = u.EventId AND c.IsCurrent = 1) THEN 1 ELSE 0 END,
       u.CreatedDate
FROM dbo.ClubEventUrl u
WHERE NOT EXISTS (SELECT 1 FROM dbo.PageAddress p
                  WHERE p.OwnerType = u.OwnerType AND p.OwnerId = u.OwnerId AND p.Slug = u.Slug
                    AND (p.UrlYear = u.UrlYear OR (p.UrlYear IS NULL AND u.UrlYear IS NULL)))");
            // Har den gamla koden bytt en händelses aktuella adress efter migreringen följer bytet med.
            n += db.Execute(@"SET QUOTED_IDENTIFIER ON;
UPDATE p SET IsCurrent = CASE WHEN p.OwnerType = u.OwnerType AND p.OwnerId = u.OwnerId AND p.Slug = u.Slug
                                   AND (p.UrlYear = u.UrlYear OR (p.UrlYear IS NULL AND u.UrlYear IS NULL)) THEN 1 ELSE 0 END
FROM dbo.PageAddress p
JOIN dbo.ClubEventUrl u ON u.EventId = p.TargetId AND u.IsCurrent = 1
WHERE p.TargetKind = 'event'
  AND NOT EXISTS (SELECT 1 FROM dbo.PageAddress c WHERE c.TargetKind = 'event' AND c.TargetId = u.EventId AND c.IsCurrent = 1
                    AND c.OwnerType = u.OwnerType AND c.OwnerId = u.OwnerId AND c.Slug = u.Slug
                    AND (c.UrlYear = u.UrlYear OR (c.UrlYear IS NULL AND u.UrlYear IS NULL)))");
            s.Complete();
            return n;
        }
    }
}
