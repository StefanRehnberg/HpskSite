using HpskSite.Services;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Composer to register target photo services for dependency injection.
    /// </summary>
    public class TargetPhotoServicesComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register ImageResizeService as singleton (stateless, reads config once)
            builder.Services.AddSingleton<ImageResizeService>();

            // Register the cleanup background service
            builder.Services.AddHostedService<TargetPhotoCleanupService>();
        }
    }
}
