namespace HpskSite.Models.Configuration
{
    /// <summary>
    /// Configuration options for member activity tracking
    /// </summary>
    public class MemberActivityOptions
    {
        /// <summary>
        /// Number of minutes to wait before updating lastActiveDate again
        /// Default: 5 minutes (good balance between accuracy and database load)
        /// </summary>
        public int ThrottleMinutes { get; set; } = 5;

        /// <summary>
        /// Enable or disable activity tracking
        /// Default: true
        /// </summary>
        public bool EnableTracking { get; set; } = true;
    }
}
