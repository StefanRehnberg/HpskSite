-- DeviceRegistrations: FCM device tokens and notification preferences for the mobile app.
-- The DTO mapping (Models/DeviceRegistration.cs → DeviceRegistrationDto) is the source
-- of truth for the column shapes; this script builds the matching table.
-- Replaces DeviceRegistrationsMigrationComposer + CreateDeviceRegistrationsTable.cs.
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.DeviceRegistrations', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[DeviceRegistrations] (
        [Id]                     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeviceRegistrations PRIMARY KEY,
        [MemberId]               INT           NOT NULL,
        [DeviceToken]            NVARCHAR(500) NOT NULL,
        [Platform]               NVARCHAR(20)  NOT NULL,    -- "Android" | "iOS"
        [NotificationPreference] NVARCHAR(20)  NOT NULL CONSTRAINT DF_DeviceRegistrations_NotificationPreference DEFAULT 'OpenMatchesOnly',
        [NotificationsEnabled]   BIT           NOT NULL CONSTRAINT DF_DeviceRegistrations_NotificationsEnabled DEFAULT 1,
        [CreatedDateUtc]         DATETIME      NOT NULL,
        [UpdatedDateUtc]         DATETIME      NOT NULL
    );

    CREATE INDEX IX_DeviceRegistrations_MemberId
        ON [dbo].[DeviceRegistrations] (MemberId);
END
GO
