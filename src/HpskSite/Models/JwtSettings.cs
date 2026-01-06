namespace HpskSite.Models
{
    /// <summary>
    /// JWT configuration settings
    /// </summary>
    public class JwtSettings
    {
        /// <summary>
        /// Section name in appsettings.json
        /// </summary>
        public const string SectionName = "JwtSettings";

        /// <summary>
        /// Secret key for signing tokens (minimum 32 characters)
        /// </summary>
        public string Secret { get; set; } = string.Empty;

        /// <summary>
        /// Token issuer (typically your site URL)
        /// </summary>
        public string Issuer { get; set; } = "HpskSite";

        /// <summary>
        /// Token audience (typically "HpskMobile" for the mobile app)
        /// </summary>
        public string Audience { get; set; } = "HpskMobile";

        /// <summary>
        /// Access token expiration in minutes (default: 60)
        /// </summary>
        public int AccessTokenExpirationMinutes { get; set; } = 60;

        /// <summary>
        /// Refresh token expiration in days (default: 30)
        /// </summary>
        public int RefreshTokenExpirationDays { get; set; } = 30;

        /// <summary>
        /// Validate the settings
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Secret)
                && Secret.Length >= 32
                && !string.IsNullOrWhiteSpace(Issuer)
                && !string.IsNullOrWhiteSpace(Audience)
                && AccessTokenExpirationMinutes > 0
                && RefreshTokenExpirationDays > 0;
        }
    }
}
