using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services
{
    /// <summary>
    /// Composer to register InvoiceAdminService in dependency injection container
    /// Pattern follows AdminServicesComposer.cs
    /// </summary>
    public class InvoiceAdminServiceComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register as singleton - service is stateless and can be reused
            builder.Services.AddSingleton<InvoiceAdminService>();

            // Consolidated invoices ("samlingsfaktura"). Scoped, not singleton: it writes content via
            // IContentService and depends on PaymentService, which is itself request-scoped.
            builder.Services.AddScoped<ConsolidatedInvoiceService>();
        }
    }
}
