using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Routing
{
    /// <summary>
    /// Composer to register the CompetitionContentFinder
    /// </summary>
    public class CompetitionContentFinderComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register the content finder to handle /competitions/{id}/ URLs
            builder.ContentFinders().Append<CompetitionContentFinder>();
        }
    }
}
