namespace HpskSite.Models
{
    /// <summary>
    /// Model for password reset request (first step - email submission)
    /// </summary>
    public class PasswordResetRequest
    {
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// Model for password reset confirmation (second step - token + new password)
    /// </summary>
    public class PasswordResetConfirm
    {
        public string Email { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}
