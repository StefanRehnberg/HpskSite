using HpskSite.Services.Hosting;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;

// Umbraco.Extensions med flit: vyerna importerar den namnrymden (_ViewImports), och SiteRoot() ska stå där
// Root() stod.
namespace Umbraco.Extensions
{
    /// <summary>
    /// <b>Sajtens rot (startsidan), oberoende av vilken nod sidan har som modell</b> (C3-planen §5c, K4).
    /// <c>Root()</c> går via navigeringstjänsten på modellens nyckel — en virtuell tävlingsnod (eller en routad sida
    /// med en annan modell) är okänd där. Faller tillbaka på <c>Root()</c> när den publicerade cachen inte går att nå.
    /// </summary>
    public static class SiteRootExtensions
    {
        public static IPublishedContent SiteRoot(this IPublishedContent content)
        {
            var accessor = StaticServiceProvider.Instance.GetService(typeof(IUmbracoContextAccessor)) as IUmbracoContextAccessor;
            if (accessor != null && accessor.TryGetUmbracoContext(out var uc) && uc.Content != null)
            {
                var root = uc.Content.GetAtRoot().FirstOrDefault();
                if (root != null) return root;
            }
            return content.Root();
        }
    }
}
