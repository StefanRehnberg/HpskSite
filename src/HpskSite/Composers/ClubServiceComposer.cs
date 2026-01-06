using HpskSite.Services;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Composer to register the ClubService for dependency injection.
    /// </summary>
    public class ClubServiceComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register ClubService as singleton since it's stateless and can be reused
            builder.Services.AddSingleton<ClubService>();
        }
    }
}
