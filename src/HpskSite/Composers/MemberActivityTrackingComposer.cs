using HpskSite.Middleware;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers the MemberActivityTrackingMiddleware to run after all Umbraco middleware (PostPipeline)
    /// This ensures authentication is fully configured and IMemberManager can access the member context
    /// </summary>
    public class MemberActivityTrackingComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.Configure<UmbracoPipelineOptions>(options =>
            {
                options.AddFilter(new UmbracoPipelineFilter(
                    "MemberActivityTracking",
                    postPipeline: app => app.UseMiddleware<MemberActivityTrackingMiddleware>()
                ));
            });
        }
    }
}
