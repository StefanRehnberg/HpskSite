using HpskSite.Services.Ledger;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registrerar verifikationsliggarens tjänster.
    ///
    /// <para><b>Startkontrollen registreras i SAMMA omgång som tabellerna skapades</b>, inte
    /// efteråt. Lärdomen är betald två gånger: både e-postkvittona och ekonomifrågorna deployades
    /// utan sin guard, renderade perfekt i prod och föll tyst vid varje sparning.</para>
    ///
    /// <para><see cref="LedgerNumberAllocator"/> är tillståndslös och registreras som singleton —
    /// den håller ingen egen databas utan tar anroparens <c>IDatabase</c>, just för att numret ska
    /// delas ut i samma transaktion som verifikationen skrivs.</para>
    /// </summary>
    public class LedgerComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<LedgerNumberAllocator>();
            builder.Services.AddSingleton<LedgerSchemaInspector>();
            builder.Services.AddScoped<LedgerSetupService>();
            builder.Services.AddScoped<LedgerPostingService>();
            builder.Services.AddScoped<LedgerProjectService>();
            builder.Services.AddScoped<LedgerDraftService>();
            builder.Services.AddScoped<LedgerIssuerResolver>();
            builder.Services.AddScoped<LedgerPaymentService>();
            builder.Services.AddScoped<LedgerOverviewService>();
            builder.Services.AddScoped<LedgerBudgetService>();
            builder.Services.AddScoped<LedgerAccessService>();
            builder.Services.AddScoped<LedgerChartService>();
            builder.Services.AddScoped<LedgerManualPostingService>();
            builder.Services.AddScoped<LedgerMembershipFeeBridge>();
            builder.Services.AddScoped<LedgerSandboxService>();
            builder.Services.AddScoped<LedgerBankImportService>();
            builder.Services.AddScoped<LedgerClosingService>();
            builder.Services.AddScoped<LedgerSieExportService>();
            builder.Services.AddScoped<LedgerReceivableExportService>();
            builder.Services.AddHostedService<LedgerSchemaGuardHostedService>();
        }
    }
}
