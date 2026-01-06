using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HpskSite
{
    /// <summary>
    /// Configures the Security Stamp Validator for member authentication.
    ///
    /// By default, ASP.NET Core Identity validates the security stamp every 30 minutes.
    /// This means even if your cookie is valid for 30 days, users get logged out every 30 minutes
    /// when the validator detects a security stamp mismatch.
    ///
    /// This configuration extends the validation interval to match the cookie timeout,
    /// preventing unexpected logouts for members who have "Remember Me" checked.
    ///
    /// Note: The security stamp is updated when:
    /// - Password is changed
    /// - External login is added/removed
    /// - AllowConcurrentLogins is false and user logs in elsewhere (invalidates other sessions)
    /// </summary>
    public class MemberSecurityStampValidatorOptions : IConfigureOptions<SecurityStampValidatorOptions>
    {
        public void Configure(SecurityStampValidatorOptions options)
        {
            // Set validation interval to 30 days (matching cookie timeout)
            // This means the security stamp is only validated every 30 days
            // Users will still be logged out immediately if their password changes
            // or if AllowConcurrentLogins=false and they log in elsewhere
            options.ValidationInterval = TimeSpan.FromDays(30);
        }
    }
}
