using HpskSite.Middleware;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers the VisitorTrackingMiddleware to run after Umbraco middleware so the
    /// request path is fully resolved and the response status is final.
    /// </summary>
    public class VisitorTrackingComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.Configure<UmbracoPipelineOptions>(options =>
            {
                options.AddFilter(new UmbracoPipelineFilter(
                    "VisitorTracking",
                    postPipeline: app => app.UseMiddleware<VisitorTrackingMiddleware>()
                ));
            });
        }
    }
}
