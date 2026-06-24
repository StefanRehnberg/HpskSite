using HpskSite.Services.Notifications;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>Registers the browser Web Push service.</summary>
    public class WebPushComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<WebPushService>();
        }
    }
}
