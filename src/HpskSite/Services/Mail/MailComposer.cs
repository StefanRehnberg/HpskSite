using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Registrerar svarslagret: vem ett svar ska gå till, och var medlemmens svar landar.
    ///
    /// <para>Egen komposer i stället för ännu en rad i <c>AdminServicesComposer</c> (som är över 250
    /// rader): det här är ett självständigt lager, och den som letar efter var svarsadressen avgörs
    /// ska hitta den på en gång.</para>
    ///
    /// <para><b>⚠️ INGET REGISTRERAS I <c>EmailService</c>.</b> Den är singleton och känner bara
    /// konfigurationen. Att injicera klubb- och medlemsuppslag där hade dragit in halva domänen i
    /// mejllagret och riskerat en DI-cykel, eftersom flera av de tjänsterna själva mejlar.</para>
    /// </summary>
    public class MailComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Scoped: läser klubbnoder, medlemsregistret och innehållstjänsten, som alla är scopade.
            builder.Services.AddScoped<ReplyContactResolver>();

            // Scoped: läser och skriver databasen via den scopade IScopeProvider.
            builder.Services.AddScoped<MailReplyService>();

            // Singleton: nyckelringen och konfigurationen ändras inte under en process, och
            // skyddet är trådsäkert. Samma livstid som EmailService, så den kan användas från
            // bakgrundstrådar utan ett scope.
            builder.Services.AddSingleton<MailReplyLinkService>();

            // ⚠️ Startkontrollen är inte pynt. En okörd migrering gör varje redan utskickad
            // svarslänk till en återvändsgränd, tyst — det hände i prod 2026-09-09.
            builder.Services.AddHostedService<MailReplySchemaGuardHostedService>();
        }
    }
}
