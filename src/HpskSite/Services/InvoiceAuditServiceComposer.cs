using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services
{
    /// <summary>
    /// Registers InvoiceAuditService in the DI container. Singleton — the service is
    /// stateless and short-lived database scopes are created per call.
    /// </summary>
    public class InvoiceAuditServiceComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<InvoiceAuditService>();
        }
    }
}
