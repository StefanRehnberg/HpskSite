using Microsoft.Extensions.Logging;
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
            //
            // This MUST run BEFORE ContentFinderByRedirectUrl. Umbraco's URL tracker writes a row
            // into umbracoRedirectUrl every time a node's URL changes, and because
            // CompetitionUrlProvider derives the URL from properties (clubId / regionalFederation /
            // competitionScope / competitionDate) rather than from tree position, reverting a
            // property edit makes a stored "old" URL identical to the live one again. If the
            // redirect finder gets there first it 301s to _publishedUrlProvider.GetUrl(node) — the
            // very URL that was requested — and the browser dies with ERR_TOO_MANY_REDIRECTS.
            // Resolving real content first makes a stale row unreachable instead of fatal.
            try
            {
                builder.ContentFinders().InsertBefore<ContentFinderByRedirectUrl, CompetitionUrlContentFinder>();
            }
            catch (InvalidOperationException ex)
            {
                // ContentFinderByRedirectUrl isn't in the collection (URL tracking disabled, or it
                // moved later in the startup pipeline — same class of gotcha as the UrlProviders
                // note below). Append so the site still boots: competition URLs keep resolving,
                // only the stale-redirect hardening is lost. Logged rather than swallowed so the
                // degradation is visible instead of silent.
                builder.BuilderLoggerFactory
                    .CreateLogger<CompetitionContentFinderComposer>()
                    .LogWarning(ex,
                        "Could not place CompetitionUrlContentFinder before ContentFinderByRedirectUrl — " +
                        "appended instead. A stale umbracoRedirectUrl row can now self-redirect a live " +
                        "competition URL (ERR_TOO_MANY_REDIRECTS).");

                builder.ContentFinders().Append<CompetitionUrlContentFinder>();
            }

            // URL provider that produces the new shapes. Insert<T>() with default index 0
            // puts us at the front of the collection so we run before DefaultUrlProvider.
            // (We can't use InsertBefore<DefaultUrlProvider> here — DefaultUrlProvider is
            // added later in the startup pipeline and isn't in the collection yet at
            // composer time.)
            builder.UrlProviders().Insert<CompetitionUrlProvider>();
        }
    }
}
