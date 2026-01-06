namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Request model for registering a device for push notifications
    /// </summary>
    public class RegisterDeviceRequest
    {
        /// <summary>
        /// FCM device token
        /// </summary>
        public string DeviceToken { get; set; } = string.Empty;

        /// <summary>
        /// Device platform: "Android" or "iOS"
        /// </summary>
        public string Platform { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for unregistering a device from push notifications
    /// </summary>
    public class UnregisterDeviceRequest
    {
        /// <summary>
        /// FCM device token to unregister
        /// </summary>
        public string DeviceToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for updating notification preferences
    /// </summary>
    public class UpdateNotificationPreferencesRequest
    {
        /// <summary>
        /// Whether push notifications are enabled
        /// </summary>
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>
        /// Notification preference: "OpenMatchesOnly" or "All"
        /// </summary>
        public string NotificationPreference { get; set; } = "OpenMatchesOnly";
    }

    /// <summary>
    /// Response model for notification preferences
    /// </summary>
    public class NotificationPreferencesResponse
    {
        /// <summary>
        /// Whether push notifications are enabled
        /// </summary>
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>
        /// Notification preference: "OpenMatchesOnly" or "All"
        /// </summary>
        public string NotificationPreference { get; set; } = "OpenMatchesOnly";
    }
}
