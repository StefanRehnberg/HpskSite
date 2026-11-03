using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Configures member identity settings (lockout threshold + password policy).
    ///
    /// Lockout threshold: we raise MaxFailedAccessAttempts from 5 to 10 so legitimate members
    /// (who often forget a password after registration) aren't locked out too easily.
    ///
    /// IMPORTANT — lockout DURATION is NOT controlled here for members. DefaultLockoutTimeSpan
    /// below only governs ASP.NET Identity / backoffice users. Umbraco *members* take their
    /// lockout duration from Umbraco:CMS:Security:MemberDefaultLockoutTimeInMinutes, which
    /// defaults to 43200 (30 days) and is NOT overridden in appsettings. So a locked-out member
    /// stays locked ~30 days until they reset their password (which clears the lockout) or an
    /// admin unlocks them — it does NOT auto-expire after 5 minutes. This is intentional (decided
    /// 2026-06-15); the login page + lockout email guide members to the password-reset recovery
    /// path. If you ever want members to self-heal faster, set MemberDefaultLockoutTimeInMinutes
    /// in appsettings rather than touching DefaultLockoutTimeSpan here.
    /// </summary>
    public class MemberIdentityComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.Configure<IdentityOptions>(options =>
            {
                // Number of failed attempts before lockout (applies to members). Default: 5.
                options.Lockout.MaxFailedAccessAttempts = 10;
                // NOTE: member lockout duration is NOT set here — see class summary
                // (members use Security:MemberDefaultLockoutTimeInMinutes, 30 days by default).
                // (Raden "DefaultLockoutTimeSpan = 5 min" togs bort 2026-11-03: den gällde inte medlemmar och var vilseledande;
                // ASP.NET:s eget förval är samma 5 minuter för backoffice.)
                options.Lockout.AllowedForNewUsers = true;

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
