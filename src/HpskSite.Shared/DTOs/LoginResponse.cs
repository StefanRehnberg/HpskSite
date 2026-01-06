namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Response model for successful login
    /// </summary>
    public class LoginResponse
    {
        /// <summary>
        /// JWT access token
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Refresh token for obtaining new access tokens
        /// </summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>
        /// Access token expiration time (UTC)
        /// </summary>
        public DateTime AccessTokenExpires { get; set; }

        /// <summary>
        /// Refresh token expiration time (UTC)
        /// </summary>
        public DateTime RefreshTokenExpires { get; set; }

        /// <summary>
        /// User information
        /// </summary>
        public UserInfo User { get; set; } = new UserInfo();
    }

    /// <summary>
    /// Basic user information returned after login
    /// </summary>
    public class UserInfo
    {
        /// <summary>
        /// Member ID
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Member's email
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Member's first name
        /// </summary>
        public string FirstName { get; set; } = string.Empty;

        /// <summary>
        /// Member's last name
        /// </summary>
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// Full display name
        /// </summary>
        public string DisplayName => $"{FirstName} {LastName}".Trim();

        /// <summary>
        /// Profile picture URL (if any)
        /// </summary>
        public string? ProfilePictureUrl { get; set; }

        /// <summary>
        /// Whether the user is a site administrator
        /// </summary>
        public bool IsAdmin { get; set; }

        /// <summary>
        /// Club IDs the user is admin for
        /// </summary>
        public List<int> AdminClubIds { get; set; } = new List<int>();
    }

    /// <summary>
    /// Request model for refreshing tokens
    /// </summary>
    public class RefreshTokenRequest
    {
        /// <summary>
        /// The refresh token
        /// </summary>
        public string RefreshToken { get; set; } = string.Empty;
    }
}
