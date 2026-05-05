-- Adds team support to training matches:
--   * IsTeamMatch + MaxShootersPerTeam columns on TrainingMatches
--   * New TrainingMatchTeams table (with FK to TrainingMatches, unique (Match, Team))
--   * TeamId column on TrainingMatchParticipants (with FK to TrainingMatchTeams)
-- Replaces AddTeamSupportToTrainingMatchesComposer + AddTeamSupportToTrainingMatches.cs.
-- Run manually in SSMS against the Umbraco database.

-- 1. New columns on TrainingMatches
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrainingMatches')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'TrainingMatches') AND name = 'IsTeamMatch')
    BEGIN
        ALTER TABLE [dbo].[TrainingMatches]
            ADD [IsTeamMatch] BIT NOT NULL CONSTRAINT DF_TrainingMatches_IsTeamMatch DEFAULT 0;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'TrainingMatches') AND name = 'MaxShootersPerTeam')
    BEGIN
        ALTER TABLE [dbo].[TrainingMatches] ADD [MaxShootersPerTeam] INT NULL;
    END
END
GO

-- 2. TrainingMatchTeams table
IF OBJECT_ID('dbo.TrainingMatchTeams', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TrainingMatchTeams] (
        [Id]                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrainingMatchTeams PRIMARY KEY,
        [TrainingMatchId]   INT           NOT NULL,
        [TeamNumber]        INT           NOT NULL,
        [TeamName]          NVARCHAR(100) NOT NULL,
        [ClubId]            INT           NULL,
        [DisplayOrder]      INT           NOT NULL CONSTRAINT DF_TrainingMatchTeams_DisplayOrder DEFAULT 0,
        CONSTRAINT FK_TrainingMatchTeams_TrainingMatches
            FOREIGN KEY (TrainingMatchId) REFERENCES [dbo].[TrainingMatches]([Id]) ON DELETE CASCADE
    );

    -- Unique (TrainingMatchId, TeamNumber)
    CREATE UNIQUE INDEX IX_TrainingMatchTeams_MatchTeam
        ON [dbo].[TrainingMatchTeams] (TrainingMatchId, TeamNumber);
END
GO

-- 3. TeamId column on TrainingMatchParticipants + FK to TrainingMatchTeams
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrainingMatchParticipants')
   AND NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'TrainingMatchParticipants') AND name = 'TeamId')
BEGIN
    ALTER TABLE [dbo].[TrainingMatchParticipants] ADD [TeamId] INT NULL;

    -- FK with no cascade (team deletion is handled separately).
    ALTER TABLE [dbo].[TrainingMatchParticipants]
        ADD CONSTRAINT FK_TrainingMatchParticipants_Teams
            FOREIGN KEY (TeamId) REFERENCES [dbo].[TrainingMatchTeams]([Id]);
END
GO
