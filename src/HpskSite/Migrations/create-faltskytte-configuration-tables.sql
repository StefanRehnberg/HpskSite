-- Create FaltskytteConfiguration + FaltskytteConfigurationCollaborator tables
-- for standalone Fältskytte station configurations that can be reused across
-- competitions and shared with collaborators.
--
-- FaltskytteConfiguration holds a named station-set (1..N stations) as a JSON
-- blob (same shape as the existing inline competition.stationConfig property).
-- FaltskytteConfigurationCollaborator is a member-list (no roles) — anyone in
-- the list can view + edit regardless of Visibility.
--
-- Visibility levels (string enum):
--   'Private'  — owner + collaborators only
--   'Club'     — visible to club admins / Skjutledare in OwnerClubId
--   'Region'   — visible to regional admins in the owner club's region
--   'Public'   — visible to all authenticated users
--
-- SecretUntil overrides Visibility: until that timestamp passes, only owner +
-- collaborators can see the config. Used while a competition is being built
-- so it doesn't leak to potential competitors.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-24

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'FaltskytteConfiguration')
BEGIN
    CREATE TABLE [FaltskytteConfiguration] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [Name] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [OwnerMemberId] INT NOT NULL,
        [OwnerClubId] INT NULL,
        [Visibility] NVARCHAR(20) NOT NULL DEFAULT 'Private',
        [SecretUntil] DATETIME NULL,
        [JsonBlob] NVARCHAR(MAX) NOT NULL,
        [CreatedDate] DATETIME NOT NULL DEFAULT GETDATE(),
        [ModifiedDate] DATETIME NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_FaltskytteConfiguration] PRIMARY KEY CLUSTERED ([Id])
    );

    -- Owner lookup (most common access path: "show me my configs").
    CREATE NONCLUSTERED INDEX [IX_FaltskytteConfiguration_OwnerMemberId]
    ON [FaltskytteConfiguration] ([OwnerMemberId]);

    -- Club lookup for the Klubb tab on the listing page.
    CREATE NONCLUSTERED INDEX [IX_FaltskytteConfiguration_OwnerClubId]
    ON [FaltskytteConfiguration] ([OwnerClubId]) WHERE [OwnerClubId] IS NOT NULL;

    PRINT 'FaltskytteConfiguration table created successfully.';
END
ELSE
BEGIN
    PRINT 'FaltskytteConfiguration table already exists. Skipping.';
END

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'FaltskytteConfigurationCollaborator')
BEGIN
    CREATE TABLE [FaltskytteConfigurationCollaborator] (
        [ConfigId] INT NOT NULL,
        [MemberId] INT NOT NULL,
        [AddedDate] DATETIME NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_FaltskytteConfigurationCollaborator] PRIMARY KEY CLUSTERED ([ConfigId], [MemberId]),
        CONSTRAINT [FK_FaltskytteConfigurationCollaborator_Config] FOREIGN KEY ([ConfigId])
            REFERENCES [FaltskytteConfiguration] ([Id]) ON DELETE CASCADE
    );

    -- Member lookup for "configs I'm a collaborator on" tab.
    CREATE NONCLUSTERED INDEX [IX_FaltskytteConfigurationCollaborator_MemberId]
    ON [FaltskytteConfigurationCollaborator] ([MemberId]);

    PRINT 'FaltskytteConfigurationCollaborator table created successfully.';
END
ELSE
BEGIN
    PRINT 'FaltskytteConfigurationCollaborator table already exists. Skipping.';
END
