-- ============================================
-- Training Match Team Support - Database Migration
-- Run this script in SSMS against your database
-- ============================================

-- Step 1: Add IsTeamMatch column to TrainingMatches table
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'IsTeamMatch')
BEGIN
    ALTER TABLE TrainingMatches ADD IsTeamMatch BIT NOT NULL CONSTRAINT DF_TrainingMatches_IsTeamMatch DEFAULT 0
    PRINT 'Added IsTeamMatch column to TrainingMatches'
END
ELSE
BEGIN
    PRINT 'IsTeamMatch column already exists'
END

-- Step 2: Add MaxShootersPerTeam column to TrainingMatches table
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'MaxShootersPerTeam')
BEGIN
    ALTER TABLE TrainingMatches ADD MaxShootersPerTeam INT NULL
    PRINT 'Added MaxShootersPerTeam column to TrainingMatches'
END
ELSE
BEGIN
    PRINT 'MaxShootersPerTeam column already exists'
END

-- Step 3: Create TrainingMatchTeams table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrainingMatchTeams')
BEGIN
    CREATE TABLE TrainingMatchTeams (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TrainingMatchId INT NOT NULL,
        TeamNumber INT NOT NULL,
        TeamName NVARCHAR(100) NOT NULL,
        ClubId INT NULL,
        DisplayOrder INT NOT NULL CONSTRAINT DF_TrainingMatchTeams_DisplayOrder DEFAULT 0,

        CONSTRAINT FK_TrainingMatchTeams_TrainingMatches
            FOREIGN KEY (TrainingMatchId)
            REFERENCES TrainingMatches(Id)
            ON DELETE CASCADE,

        CONSTRAINT UQ_TrainingMatchTeams_MatchTeamNumber
            UNIQUE (TrainingMatchId, TeamNumber)
    )

    CREATE INDEX IX_TrainingMatchTeams_TrainingMatchId
        ON TrainingMatchTeams(TrainingMatchId)

    PRINT 'Created TrainingMatchTeams table'
END
ELSE
BEGIN
    PRINT 'TrainingMatchTeams table already exists'
END

-- Step 4: Add TeamId column to TrainingMatchParticipants
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatchParticipants') AND name = 'TeamId')
BEGIN
    ALTER TABLE TrainingMatchParticipants ADD TeamId INT NULL

    ALTER TABLE TrainingMatchParticipants
        ADD CONSTRAINT FK_TrainingMatchParticipants_Teams
        FOREIGN KEY (TeamId)
        REFERENCES TrainingMatchTeams(Id)
        ON DELETE SET NULL

    CREATE INDEX IX_TrainingMatchParticipants_TeamId
        ON TrainingMatchParticipants(TeamId)

    PRINT 'Added TeamId column to TrainingMatchParticipants'
END
ELSE
BEGIN
    PRINT 'TeamId column already exists'
END

-- ============================================
-- Verification queries
-- ============================================
PRINT ''
PRINT '=== Verification ==='

-- Check TrainingMatches columns
SELECT
    'TrainingMatches' AS TableName,
    c.name AS ColumnName,
    t.name AS DataType,
    c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('TrainingMatches')
AND c.name IN ('IsTeamMatch', 'MaxShootersPerTeam')
ORDER BY c.name

-- Check TrainingMatchTeams table exists
SELECT
    'TrainingMatchTeams' AS TableName,
    COUNT(*) AS ColumnCount
FROM sys.columns
WHERE object_id = OBJECT_ID('TrainingMatchTeams')

-- Check TrainingMatchParticipants TeamId column
SELECT
    'TrainingMatchParticipants' AS TableName,
    c.name AS ColumnName,
    t.name AS DataType,
    c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('TrainingMatchParticipants')
AND c.name = 'TeamId'

PRINT ''
PRINT '=== Migration Complete ==='
