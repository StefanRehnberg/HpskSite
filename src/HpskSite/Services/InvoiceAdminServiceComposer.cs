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
            // Scoped (var singleton): den läser tävlingen genom ICompetitionStore, som är scoped (C3-planen S3).
            // Skanningens cache ligger i AppCaches, så ingenting förloras mellan förfrågningarna.
            builder.Services.AddScoped<InvoiceAdminService>();

            // Consolidated invoices ("samlingsfaktura"). Scoped, not singleton: it writes content via
            // IContentService and depends on PaymentService, which is itself request-scoped.
            builder.Services.AddScoped<ConsolidatedInvoiceService>();
        }
    }
}
