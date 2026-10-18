using HpskSite.Services.Addresses;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Klubbens korta adress (Stefans beslut 2026-10-18): <c>/halland/haaplinge-goass/</c> i stället för trädets
    /// <c>/halland/klubbar/haaplinge-goass/</c>. Klubbkatalogen <c>/halland/klubbar/</c> står kvar.
    ///
    /// <para><b>Trädet ändras inte.</b> Klubben ligger kvar under kretsens <c>clubsPage</c>; det är bara adressen
    /// som hoppar över katalogens segment. Utåt bygger <see cref="ClubShortUrlProvider"/> den korta adressen
    /// (för klubben och allt under den), inåt översätter <see cref="ClubShortUrlContentFinder"/> den till
    /// trädets adress, och den långa formen ger 301 till den korta.</para>
    ///
    /// <para>⚠️ Klubben delar därmed namnrymd med kretsens egna sidor (<c>/halland/{x}/</c>).
    /// <see cref="PageAddressReservedNames"/> vägrar ett klubbnamn som krockar med en sida direkt under kretsen,
    /// med ett sortsegment (<c>tavlingar</c>, <c>handelser</c>, <c>serier</c>), med <c>klubbar</c> och med ett årtal.</para>
    /// </summary>
    public static class ClubShortUrls
    {
        /// <summary>Kortadressen för en klubb eller en nod under en klubb, eller null.</summary>
        public static string? ShortUrlOf(IPublishedContent content)
        {
            var below = new List<string>();
            var node = content;
            // Under en klubb ligger bara klubbens gamla händelsenoder, så två steg räcker med marginal.
            for (var depth = 0; node != null && node.ContentType.Alias != "club"; depth++)
            {
                if (depth >= 3 || node.ContentType.Alias is "regionalPage" or "clubsPage") return null;
                below.Add(node.UrlSegment ?? "");
                node = node.Parent;
            }
            if (node == null || string.IsNullOrEmpty(node.UrlSegment)) return null;
            var directory = node.Parent;
            var region = directory?.Parent;
            if (directory?.ContentType.Alias != "clubsPage" || region?.ContentType.Alias != "regionalPage") return null;
            var regionUrl = region.Url();
            if (string.IsNullOrEmpty(regionUrl) || regionUrl == "#") return null;
            below.Reverse();
            var tail = below.Count == 0 ? "" : string.Join('/', below) + "/";
            return (regionUrl.EndsWith('/') ? regionUrl : regionUrl + "/") + node.UrlSegment + "/" + tail;
        }

        /// <summary>Är noden en klubb eller ligger den under en?</summary>
        public static bool IsClubOrBelow(IPublishedContent? node)
        {
            for (var depth = 0; node != null && depth < 4; depth++, node = node.Parent)
                if (node.ContentType.Alias == "club") return true;
            return false;
        }

        /// <summary>Kretsens klubbkatalog (<c>clubsPage</c>), eller null.</summary>
        public static IPublishedContent? DirectoryOf(IPublishedContent? region)
            => region?.ContentType.Alias == "regionalPage"
                ? region.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage")
                : null;
    }

    /// <summary>
    /// Uppslag på adress i den publicerade cachen — trädets adresser, inklusive klubbens korta form.
    /// Ett ställe för det, så att adresserna i <c>CompetitionPagesController</c>, händelsernas gamla
    /// trädadresser och klubbsidorna löses upp likadant.
    /// </summary>
    public class SiteRouteLookup
    {
        private readonly IDocumentUrlService _urls;
        private readonly IDefaultCultureAccessor _culture;

        public SiteRouteLookup(IDocumentUrlService urls, IDefaultCultureAccessor culture)
        {
            _urls = urls;
            _culture = culture;
        }

        /// <summary>Noden på trädets adress (<c>"/halland/klubbar/x/"</c>), eller null.</summary>
        public IPublishedContent? TreeRoute(IPublishedContentCache cache, string route)
        {
            var trimmed = route.Length > 1 ? route.TrimEnd('/') : route;
            var key = _urls.GetDocumentKeyByRoute(trimmed, _culture.DefaultCulture, null, false)
                      ?? (trimmed == route ? null : _urls.GetDocumentKeyByRoute(route, _culture.DefaultCulture, null, false));
            return key.HasValue ? cache.GetById(key.Value) : null;
        }

        public IPublishedContent? TreeRoute(IPublishedContentCache cache, IEnumerable<string> segments)
            => TreeRoute(cache, "/" + string.Join('/', segments) + "/");

        /// <summary>
        /// Noden bakom en adress i klubbens KORTA form (<c>/halland/haaplinge-goass/…</c>): kretsen, dess klubbkatalog,
        /// sedan resten. Null när första segmentet inte är en krets eller adressen inte leder till en klubb.
        /// </summary>
        public IPublishedContent? ShortClubRoute(IPublishedContentCache cache, string[] segments)
        {
            if (segments.Length < 2) return null;
            var region = TreeRoute(cache, new[] { segments[0] });
            var directory = ClubShortUrls.DirectoryOf(region);
            if (directory == null || string.IsNullOrEmpty(directory.UrlSegment)) return null;
            if (string.Equals(segments[1], directory.UrlSegment, StringComparison.OrdinalIgnoreCase)) return null;
            var node = TreeRoute(cache, new[] { segments[0], directory.UrlSegment }.Concat(segments[1..]));
            return ClubShortUrls.IsClubOrBelow(node) ? node : null;
        }

        /// <summary>
        /// Ägaren (klubb eller krets) bakom ägarens segment — kretsen (<c>[halland]</c>), klubben i kort form
        /// (<c>[halland, haaplinge-goass]</c>) eller i trädets form (<c>[halland, klubbar, haaplinge-goass]</c>).
        /// </summary>
        public IPublishedContent? Owner(IPublishedContentCache cache, string[] ownerSegments)
        {
            if (ownerSegments.Length == 0) return null;
            var direct = TreeRoute(cache, ownerSegments);
            if (direct?.ContentType.Alias is "club" or "regionalPage") return direct;
            if (ownerSegments.Length != 2) return null;
            var club = ShortClubRoute(cache, ownerSegments);
            return club?.ContentType.Alias == "club" ? club : null;
        }
    }

    /// <summary>Utåt: klubbens och dess sidors korta adress (se <see cref="ClubShortUrls"/>).</summary>
    public class ClubShortUrlProvider : IUrlProvider
    {
        public const string ProviderAlias = "Hpsk.ClubShortUrlProvider";
        public string Alias => ProviderAlias;

        private readonly ILogger<ClubShortUrlProvider> _logger;

        public ClubShortUrlProvider(ILogger<ClubShortUrlProvider> logger) => _logger = logger;

        public UrlInfo? GetUrl(IPublishedContent content, UrlMode mode, string? culture, Uri current)
        {
            try
            {
                if (content == null) return null;
                var alias = content.ContentType.Alias;
                if (alias != "club" && alias != "clubSimpleEvent") return null;
                var url = ClubShortUrls.ShortUrlOf(content);
                if (url == null) return null;
                if (mode == UrlMode.Absolute && current != null) url = new Uri(current, url).ToString();
                return UrlInfo.AsUrl(url, ProviderAlias, culture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ClubShortUrlProvider: adressen för {Id} kunde inte byggas — trädets adress gäller", content?.Id);
                return null;
            }
        }

        public IEnumerable<UrlInfo> GetOtherUrls(int id, Uri current) => Enumerable.Empty<UrlInfo>();

        public Task<UrlInfo?> GetPreviewUrlAsync(IContent content, string? culture, string? segment)
            => Task.FromResult<UrlInfo?>(null);
    }

    /// <summary>
    /// Inåt: <c>/halland/haaplinge-goass/…</c> visar klubbens nod (och det som ligger under den), och den långa
    /// formen <c>/halland/klubbar/haaplinge-goass/…</c> ger 301 till den korta. Före Umbracos egen upplösning,
    /// så att den långa formen aldrig hinner serveras — men EFTER <see cref="ClubEventLegacyUrlFinder"/>: en
    /// händelse som flyttat till tabellen har kvar sin nod tills avpubliceringen, och den får inte visas.
    /// </summary>
    public class ClubShortUrlContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly SiteRouteLookup _routes;
        private readonly ILogger<ClubShortUrlContentFinder> _logger;

        public ClubShortUrlContentFinder(IUmbracoContextAccessor ctx, SiteRouteLookup routes, ILogger<ClubShortUrlContentFinder> logger)
        {
            _ctx = ctx;
            _routes = routes;
            _logger = logger;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
                var cache = uc.Content;

                var region = _routes.TreeRoute(cache, new[] { seg[0] });
                var directory = ClubShortUrls.DirectoryOf(region);
                if (directory == null || string.IsNullOrEmpty(directory.UrlSegment)) return Task.FromResult(false);

                // Den långa formen → 301 till den korta. Katalogen själv (/halland/klubbar/) står kvar.
                if (string.Equals(seg[1], directory.UrlSegment, StringComparison.OrdinalIgnoreCase))
                {
                    if (seg.Length < 3) return Task.FromResult(false);
                    var club = _routes.TreeRoute(cache, seg[..3]);
                    if (club?.ContentType.Alias != "club") return Task.FromResult(false);
                    // ⚠️ Bara en adress som leder till en nod skrivs om. Umbracos egna omdirigeringsrader
                    // (umbracoRedirectUrl) är lagrade på trädets — alltså den långa — formen; skrevs adressen om
                    // innan de hann läsas blev en gammal händelselänk 404 (sett på kopian 2026-10-18).
                    var target = seg.Length == 3 ? club : _routes.TreeRoute(cache, seg);
                    if (target == null) return Task.FromResult(false);
                    var shortUrl = ClubShortUrls.ShortUrlOf(target);
                    if (shortUrl == null) return Task.FromResult(false);
                    request.SetRedirectPermanent(shortUrl + request.Uri.Query);
                    return Task.FromResult(true);
                }

                // En sida direkt under kretsen (eller under en av dess sidor) har företräde — den har trädets adress.
                if (_routes.TreeRoute(cache, seg) != null) return Task.FromResult(false);

                var node = _routes.ShortClubRoute(cache, seg);
                if (node == null) return Task.FromResult(false);
                request.SetPublishedContent(node);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ClubShortUrlContentFinder: {Path} kunde inte avgöras", request.Uri.AbsolutePath);
                return Task.FromResult(false);
            }
        }
    }
}
