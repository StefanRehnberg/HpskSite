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
            // Projektgrupperna och projektet ur källan (första kronan). Resolvern anropas inifrån
            // LedgerPostingService.Post — den enda vägen in i liggaren.
            builder.Services.AddScoped<LedgerProjectGroupService>();
            builder.Services.AddScoped<LedgerSourceProjectResolver>();
            builder.Services.AddScoped<LedgerDraftService>();
            builder.Services.AddScoped<LedgerIssuerResolver>();
            builder.Services.AddScoped<LedgerPaymentService>();
            builder.Services.AddScoped<LedgerOverviewService>();
            builder.Services.AddScoped<LedgerBudgetService>();
            builder.Services.AddScoped<LedgerAccessService>();
            builder.Services.AddScoped<LedgerChartService>();
            builder.Services.AddScoped<LedgerManualPostingService>();
            builder.Services.AddScoped<LedgerOpeningBalanceService>();
            builder.Services.AddScoped<LedgerMembershipFeeBridge>();
            builder.Services.AddScoped<LedgerSandboxService>();
            builder.Services.AddScoped<LedgerBankImportService>();
            builder.Services.AddScoped<LedgerClosingService>();
            builder.Services.AddScoped<LedgerSieExportService>();
            builder.Services.AddScoped<LedgerReceivableExportService>();
            builder.Services.AddScoped<LedgerSieImportService>();

            // Verifikationslistan, huvudboken och underlagen — revisionens ryggrad.
            // ⚠️ Lagringen är statslös och singleton; tjänsterna tar en databas per request.
            builder.Services.AddSingleton<LedgerAttachmentStorage>();
            builder.Services.AddScoped<LedgerAttachmentService>();
            builder.Services.AddScoped<LedgerJournalService>();
            builder.Services.AddScoped<LedgerAuditorService>();
            builder.Services.AddScoped<LedgerWriteGrantService>();
            builder.Services.AddScoped<LedgerVatReturnService>();
            builder.Services.AddScoped<LedgerAssetService>();
            builder.Services.AddScoped<LedgerExpenseService>();

            // Tävlingsavgifterna i liggaren (P3/P4) — ersätter den gamla fakturamodellen per tävling.
            // Växeln läses ALLTID via CompetitionPaymentModelService; ingen yta gissar ur fakturorna.
            builder.Services.AddScoped<HpskSite.Services.CompetitionFees.CompetitionPaymentModelService>();
            builder.Services.AddScoped<HpskSite.Services.CompetitionFees.CompetitionFeeService>();
            builder.Services.AddScoped<HpskSite.Services.CompetitionFees.LedgerChargeService>();
            builder.Services.AddScoped<HpskSite.Services.CompetitionFees.LedgerLegacyInvoiceBridge>();
            builder.Services.AddHostedService<LedgerSchemaGuardHostedService>();
        }
    }
}
