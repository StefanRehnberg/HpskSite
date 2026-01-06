using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services
{
    /// <summary>
    /// Composer to register EmailService with dependency injection
    /// </summary>
    public class EmailServiceComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<EmailService>();
        }
    }
}
