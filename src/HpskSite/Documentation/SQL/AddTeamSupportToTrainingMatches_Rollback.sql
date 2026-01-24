-- ============================================
-- Training Match Team Support - ROLLBACK Script
-- Run this script to undo the team support migration
-- WARNING: This will delete all team data!
-- ============================================

PRINT 'WARNING: This will delete all team assignments and team definitions!'
PRINT 'Starting rollback...'

-- Step 1: Remove foreign key and index from TrainingMatchParticipants
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TrainingMatchParticipants_Teams')
BEGIN
    ALTER TABLE TrainingMatchParticipants DROP CONSTRAINT FK_TrainingMatchParticipants_Teams
    PRINT 'Dropped FK_TrainingMatchParticipants_Teams constraint'
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TrainingMatchParticipants_TeamId' AND object_id = OBJECT_ID('TrainingMatchParticipants'))
BEGIN
    DROP INDEX IX_TrainingMatchParticipants_TeamId ON TrainingMatchParticipants
    PRINT 'Dropped IX_TrainingMatchParticipants_TeamId index'
END

-- Step 2: Remove TeamId column from TrainingMatchParticipants
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatchParticipants') AND name = 'TeamId')
BEGIN
    ALTER TABLE TrainingMatchParticipants DROP COLUMN TeamId
    PRINT 'Dropped TeamId column from TrainingMatchParticipants'
END

-- Step 3: Drop TrainingMatchTeams table
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrainingMatchTeams')
BEGIN
    DROP TABLE TrainingMatchTeams
    PRINT 'Dropped TrainingMatchTeams table'
END

-- Step 4: Remove MaxShootersPerTeam column from TrainingMatches
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'MaxShootersPerTeam')
BEGIN
    ALTER TABLE TrainingMatches DROP COLUMN MaxShootersPerTeam
    PRINT 'Dropped MaxShootersPerTeam column from TrainingMatches'
END

-- Step 5: Remove IsTeamMatch column from TrainingMatches (with its default constraint)
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'IsTeamMatch')
BEGIN
    -- Drop the default constraint first
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_TrainingMatches_IsTeamMatch')
    BEGIN
        ALTER TABLE TrainingMatches DROP CONSTRAINT DF_TrainingMatches_IsTeamMatch
        PRINT 'Dropped DF_TrainingMatches_IsTeamMatch default constraint'
    END

    ALTER TABLE TrainingMatches DROP COLUMN IsTeamMatch
    PRINT 'Dropped IsTeamMatch column from TrainingMatches'
END

PRINT ''
PRINT '=== Rollback Complete ==='
