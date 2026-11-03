using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HpskSite
{
    /// <summary>
    /// Hur ofta inloggningskakans säkerhetsstämpel prövas mot databasen.
    ///
    /// Stämpeln byts när medlemmen byter lösenord, stängs av, låses eller förlorar en roll
    /// (<see cref="HpskSite.Services.Security.SessionRevocationHandler"/>) — men en öppen session märker det först vid nästa prövning.
    /// Intervallet är därför det som avgör hur länge en avstängd medlem kan vara kvar.
    ///
    /// <para>Förvalet är 30 dagar (ett medvetet gammalt beslut: ASP.NET:s förval 30 minuter ansågs ha loggat ut medlemmar i onödan).
    /// <c>Security:Session:StampValidationMinutes</c> ändrar det utan ny kod — sätt ett kort värde (t.ex. 15) först när en A/B i dev
    /// har visat att en vanlig session överlever intervallet och att en avstängd medlem tappar sin.</para>
    /// </summary>
    public class MemberSecurityStampValidatorOptions : IConfigureOptions<SecurityStampValidatorOptions>
    {
        public const int DefaultMinutes = 30 * 24 * 60;
        private readonly IConfiguration _configuration;

        public MemberSecurityStampValidatorOptions(IConfiguration configuration) => _configuration = configuration;

        public static TimeSpan IntervalFrom(string? configured)
        {
            if (int.TryParse(configured, out var minutes) && minutes >= 1 && minutes <= DefaultMinutes)
                return TimeSpan.FromMinutes(minutes);
            return TimeSpan.FromMinutes(DefaultMinutes);
        }

        public void Configure(SecurityStampValidatorOptions options) =>
            options.ValidationInterval = IntervalFrom(_configuration["Security:Session:StampValidationMinutes"]);
    }
}
