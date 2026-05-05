using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.Migrations
{
    /// <summary>
    /// DTO for the DeviceRegistrations table — stores FCM device tokens and notification
    /// preferences per member. Schema lives in Migrations/create-device-registrations-table.sql
    /// (run manually in SSMS for new databases). Kept under the HpskSite.Migrations namespace
    /// for backwards compatibility with existing call sites in PushNotificationService.
    /// </summary>
    [TableName("DeviceRegistrations")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class DeviceRegistrationDto
    {
        /// <summary>Primary key</summary>
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>Member ID this device belongs to</summary>
        [Column("MemberId")]
        public int MemberId { get; set; }

        /// <summary>FCM device token</summary>
        [Column("DeviceToken")]
        [MaxLength(500)]
        public string DeviceToken { get; set; } = string.Empty;

        /// <summary>Device platform: "Android" or "iOS"</summary>
        [Column("Platform")]
        [MaxLength(20)]
        public string Platform { get; set; } = string.Empty;

        /// <summary>Notification preference: "OpenMatchesOnly" or "All"</summary>
        [Column("NotificationPreference")]
        [MaxLength(20)]
        public string NotificationPreference { get; set; } = "OpenMatchesOnly";

        /// <summary>Whether notifications are enabled for this device</summary>
        [Column("NotificationsEnabled")]
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>When the device was registered (UTC)</summary>
        [Column("CreatedDateUtc")]
        public DateTime CreatedDateUtc { get; set; }

        /// <summary>When the registration was last updated (UTC)</summary>
        [Column("UpdatedDateUtc")]
        public DateTime UpdatedDateUtc { get; set; }
    }
}
