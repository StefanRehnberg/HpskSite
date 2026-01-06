namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Request model for user login via API
    /// </summary>
    public class LoginRequest
    {
        /// <summary>
        /// User's email address
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// User's password
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Whether to remember the user (longer refresh token expiry)
        /// </summary>
        public bool RememberMe { get; set; } = true;
    }
}
