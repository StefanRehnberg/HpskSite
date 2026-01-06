using System.ComponentModel.DataAnnotations;
using NPoco;
using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to create the DeviceRegistrations table for push notifications
    /// </summary>
    public class CreateDeviceRegistrationsTable : AsyncMigrationBase
    {
        public CreateDeviceRegistrationsTable(IMigrationContext context)
            : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            if (!TableExists("DeviceRegistrations"))
            {
                Create.Table<DeviceRegistrationDto>().Do();

                // Create index on MemberId for efficient lookups
                Create.Index("IX_DeviceRegistrations_MemberId")
                    .OnTable("DeviceRegistrations")
                    .OnColumn("MemberId")
                    .Ascending()
                    .Do();
            }
        }
    }

    /// <summary>
    /// DTO for DeviceRegistrations table - stores FCM device tokens and notification preferences
    /// </summary>
    [TableName("DeviceRegistrations")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class DeviceRegistrationDto
    {
        /// <summary>
        /// Primary key
        /// </summary>
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// Member ID this device belongs to
        /// </summary>
        [Column("MemberId")]
        public int MemberId { get; set; }

        /// <summary>
        /// FCM device token
        /// </summary>
        [Column("DeviceToken")]
        [MaxLength(500)]
        public string DeviceToken { get; set; } = string.Empty;

        /// <summary>
        /// Device platform: "Android" or "iOS"
        /// </summary>
        [Column("Platform")]
        [MaxLength(20)]
        public string Platform { get; set; } = string.Empty;

        /// <summary>
        /// Notification preference: "OpenMatchesOnly" or "All"
        /// </summary>
        [Column("NotificationPreference")]
        [MaxLength(20)]
        public string NotificationPreference { get; set; } = "OpenMatchesOnly";

        /// <summary>
        /// Whether notifications are enabled for this device
        /// </summary>
        [Column("NotificationsEnabled")]
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>
        /// When the device was registered (UTC)
        /// </summary>
        [Column("CreatedDateUtc")]
        public DateTime CreatedDateUtc { get; set; }

        /// <summary>
        /// When the registration was last updated (UTC)
        /// </summary>
        [Column("UpdatedDateUtc")]
        public DateTime UpdatedDateUtc { get; set; }
    }
}
