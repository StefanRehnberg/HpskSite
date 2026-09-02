using HpskSite.Models.Configuration;
using HpskSite.Services.Firearms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registrerar vapenregistrets kryptolager: rotnyckelringen, valvet, skyddet resten av koden
    /// använder, och startkontrollen som larmar om nyckeln saknas.
    ///
    /// <para>Egen komposer i stället för ännu en rad i <c>AdminServicesComposer</c> (som är över 250
    /// rader): det här är ett självständigt lager med en egen konfigurationssektion, och den som
    /// letar efter var krypteringen kopplas in ska hitta den på en gång.</para>
    /// </summary>
    public class FirearmComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.Configure<FirearmCryptoOptions>(
                builder.Config.GetSection(FirearmMasterKeyRing.ConfigSection));

            // Singleton: nycklarna kommer ur konfigurationen och ändras inte under en process.
            // base64-avkodningen och valideringen ska göras en gång, inte per anrop.
            builder.Services.AddSingleton<FirearmMasterKeyRing>();

            // Scoped: läser databasen via den scopade IScopeProvider.
            builder.Services.AddScoped<FirearmVaultService>();
            builder.Services.AddScoped<FirearmProtector>();

            // Behörigheten. Scoped — IMemberManager är scopad, och grinden frågar efter den
            // inloggade medlemmen.
            builder.Services.AddScoped<FirearmAuthorizationService>();
            builder.Services.AddScoped<FirearmAccessLogService>();

            // Registret. Enda vägen till vapendata — grinden, läsloggen och den tvåstegs
            // skrivningen bor i tjänsten, inte i controllern.
            builder.Services.AddScoped<FirearmService>();
            builder.Services.AddScoped<FirearmUsageService>();
            builder.Services.AddScoped<ForeningsintygRequestService>();

            // Kedjans punkt 6: bokning av lånevapen. Ett rent lager ovanpå registret.
            builder.Services.AddScoped<FirearmBookingService>();

            // Klubbens lanevapenregler (horisont, externa lan, per-handelse-flaggan). Egen tjanst
            // sa bokningen och granssnittet laser SAMMA svar -- tva raknande ytor glider isar.
            builder.Services.AddScoped<LoanWeaponClubRules>();

            builder.Services.AddHostedService<FirearmKeyGuardHostedService>();

            // Licenspåminnelser: 90 / 30 / förfallen. Claim-then-send, se tjänsten.
            builder.Services.AddHostedService<FirearmReminderHostedService>();
        }
    }
}
