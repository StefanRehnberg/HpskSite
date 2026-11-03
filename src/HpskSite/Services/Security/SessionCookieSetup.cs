using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Säkerhetsplanen 2026-11-02 (P5a): flaggorna på medlemmens inloggningskaka och på antiforgery-kakan — EN plats som både
    /// <c>Program.cs</c> och testet (<c>SessionCookieSetupTests</c>) använder, så testet läser de FAKTISKA alternativen.
    /// Kaknamn och livslängd rörs inte (ett nytt namn loggar ut alla medlemmar).
    /// </summary>
    public static class SessionCookieSetup
    {
        public static IServiceCollection AddSessionCookieHardening(this IServiceCollection services)
        {
            // Medlemmens kaka är schemat "Identity.Application" (inte "Umbraco.Members").
            services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme,
                o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
            services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
            return services;
        }
    }
}
