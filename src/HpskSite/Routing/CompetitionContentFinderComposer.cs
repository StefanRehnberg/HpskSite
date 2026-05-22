using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Routing;

namespace HpskSite.Routing
{
    /// <summary>
    /// Registers competition URL routing:
    ///   1. CompetitionContentFinder        — handles legacy /competitions/{id}/ direct-link
    ///   2. CompetitionUrlContentFinder     — parses the year/region/club/(series)/comp shape
    ///                                        plus SM/Landsdel shape, with optional child segment
    ///   3. CompetitionUrlProvider          — renders the same shapes when Url() is called
    /// </summary>
    public class CompetitionContentFinderComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // ID-based fallback finder first (only matches /competitions/{numericId}/ — 2 segments).
            builder.ContentFinders().Append<CompetitionContentFinder>();

            // Hierarchical finder for the new URL shapes (year + region/club + comp + optional child).
            builder.ContentFinders().Append<CompetitionUrlContentFinder>();

            // URL provider that produces the new shapes. Insert<T>() with default index 0
            // puts us at the front of the collection so we run before DefaultUrlProvider.
            // (We can't use InsertBefore<DefaultUrlProvider> here — DefaultUrlProvider is
            // added later in the startup pipeline and isn't in the collection yet at
            // composer time.)
            builder.UrlProviders().Insert<CompetitionUrlProvider>();
        }
    }
}
