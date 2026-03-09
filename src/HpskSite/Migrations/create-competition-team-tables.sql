-- Create CompetitionTeam and CompetitionTeamMember tables
-- Teams compete based on the sum of their members' individual results.
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-03-09

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CompetitionTeam')
BEGIN
    CREATE TABLE [CompetitionTeam] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [CompetitionId] INT NOT NULL,
        [TeamName] NVARCHAR(100) NOT NULL,
        [TeamClass] NVARCHAR(50) NOT NULL,
        [ClubId] INT NOT NULL,
        [CreatedBy] INT NOT NULL,
        [CreatedAt] DATETIME NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_CompetitionTeam] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UX_CompetitionTeam_Name] UNIQUE ([CompetitionId], [TeamName])
    );

    CREATE NONCLUSTERED INDEX [IX_CompetitionTeam_CompetitionId]
    ON [CompetitionTeam] ([CompetitionId]);

    CREATE NONCLUSTERED INDEX [IX_CompetitionTeam_ClubId]
    ON [CompetitionTeam] ([ClubId]);

    PRINT 'CompetitionTeam table created successfully.';
END
ELSE
BEGIN
    PRINT 'CompetitionTeam table already exists. Skipping.';
END

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CompetitionTeamMember')
BEGIN
    CREATE TABLE [CompetitionTeamMember] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [TeamId] INT NOT NULL,
        [MemberId] INT NOT NULL,
        [IsSpare] BIT NOT NULL DEFAULT 0,
        [JoinedAt] DATETIME NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_CompetitionTeamMember] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_CompetitionTeamMember_Team] FOREIGN KEY ([TeamId]) REFERENCES [CompetitionTeam]([Id]) ON DELETE CASCADE,
        CONSTRAINT [UX_CompetitionTeamMember] UNIQUE ([TeamId], [MemberId])
    );

    CREATE NONCLUSTERED INDEX [IX_CompetitionTeamMember_TeamId]
    ON [CompetitionTeamMember] ([TeamId]);

    CREATE NONCLUSTERED INDEX [IX_CompetitionTeamMember_MemberId]
    ON [CompetitionTeamMember] ([MemberId]);

    PRINT 'CompetitionTeamMember table created successfully.';
END
ELSE
BEGIN
    PRINT 'CompetitionTeamMember table already exists. Skipping.';
END
