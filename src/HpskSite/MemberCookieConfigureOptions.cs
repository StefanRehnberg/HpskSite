using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;

namespace HpskSite
{
    /// <summary>
    /// Configures authentication cookies specifically for frontend members
    /// This ensures member cookies are separate from backoffice cookies and respect RememberMe
    /// </summary>
    public class MemberCookieConfigureOptions : IConfigureNamedOptions<CookieAuthenticationOptions>
    {
        public void Configure(string? name, CookieAuthenticationOptions options)
        {
            // Only configure if this is the member authentication scheme
            // Umbraco v16 uses "Umbraco.Members" as the authentication scheme name
            if (name == "Umbraco.Members")
            {
                // 30-day cookie expiration when "Remember Me" is checked
                options.ExpireTimeSpan = TimeSpan.FromDays(30);

                // Sliding expiration: cookie refreshes halfway through its lifetime
                // This means if user visits within 15 days, cookie extends another 30 days
                options.SlidingExpiration = true;

                // Security settings
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Explicit cookie name for clarity
                options.Cookie.Name = ".HpskSite.Member.Auth";

                // Mark as essential (GDPR compliance)
                options.Cookie.IsEssential = true;
            }
        }

        public void Configure(CookieAuthenticationOptions options)
        {
            // Default implementation - not used for named options
            Configure(Options.DefaultName, options);
        }
    }
}
