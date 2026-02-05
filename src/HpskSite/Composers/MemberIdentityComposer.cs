using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Configures member identity settings including lockout policies.
    ///
    /// Default ASP.NET Identity lockout settings are quite strict:
    /// - MaxFailedAccessAttempts: 5
    /// - DefaultLockoutTimeSpan: 5 minutes
    ///
    /// These settings can cause legitimate users to be locked out easily,
    /// especially new users who might forget their password after registration.
    ///
    /// This composer configures more lenient settings while still providing
    /// protection against brute-force attacks.
    /// </summary>
    public class MemberIdentityComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Configure member identity options for more lenient lockout policy
            builder.Services.Configure<IdentityOptions>(options =>
            {
                // Lockout settings - more lenient than defaults
                options.Lockout.MaxFailedAccessAttempts = 10;  // Default: 5
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);  // Default: 5 minutes
                options.Lockout.AllowedForNewUsers = true;  // Default: true - keep protection but with higher threshold

                // Password settings (keep reasonable but not overly strict)
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 6;  // Minimum 6 characters

                // User settings
                options.User.RequireUniqueEmail = true;
            });
        }
    }
}
