using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 17 tog bort <c>IPublishedContentCache.GetAtRoot()</c> och <c>GetByRoute()</c>. De här
    /// tilläggen gör samma sak genom Umbracos egna tjänster, så att de 57 anropen kunde stå kvar.
    ///
    /// ⚠️ Det här är en BRYGGA, inte ett mönster att bygga vidare på. C3-planen (K4) säger att ingen ny
    /// kod navigerar trädet: rot, hubb, klubb och krets ska slås upp på ett ställe (<c>ISiteNodes</c>),
    /// och sidorna blir controllers i S7. Använd inte de här i ny kod.
    ///
    /// Tjänsterna hämtas ur <see cref="StaticServiceProvider"/>, eftersom ett tillägg inte kan få dem
    /// injicerade. Båda är singletons i Umbraco och läser ur minnet.
    /// </summary>
    public static class PublishedCacheNavigationExtensions
    {
        /// <summary>
        /// De publicerade rotnoderna i sorteringsordning — samma svar som Umbraco 16:s <c>GetAtRoot()</c>.
        /// En rotnod som inte är publicerad saknas, som förut.
        /// </summary>
        public static IEnumerable<IPublishedContent> GetAtRoot(this IPublishedContentCache cache)
        {
            var navigation = StaticServiceProvider.Instance.GetRequiredService<IDocumentNavigationQueryService>();
            if (!navigation.TryGetRootKeys(out var keys)) return Enumerable.Empty<IPublishedContent>();
            return keys.Select(cache.GetById).Where(c => c != null).Cast<IPublishedContent>().ToList();
        }

        /// <summary>
        /// Den publicerade noden på en adress (<c>"/halland/klubbar/x/"</c>), eller null — samma svar som
        /// Umbraco 16:s <c>GetByRoute()</c>. Uppslaget görs av Umbracos adresstjänst, så domäner och
        /// "göm översta noden" gäller som för vanliga sidor.
        /// </summary>
        public static IPublishedContent? GetByRoute(this IPublishedContentCache cache, string route)
        {
            var urls = StaticServiceProvider.Instance.GetRequiredService<IDocumentUrlService>();
            var culture = StaticServiceProvider.Instance.GetRequiredService<IDefaultCultureAccessor>().DefaultCulture;
            var trimmed = route.Length > 1 ? route.TrimEnd('/') : route;
            var key = urls.GetDocumentKeyByRoute(trimmed, culture, null, false)
                      ?? (trimmed == route ? null : urls.GetDocumentKeyByRoute(route, culture, null, false));
            return key.HasValue ? cache.GetById(key.Value) : null;
        }
    }
}
