-- Migration: Add Handicap fields to Training Match tables
-- Date: 2025-12-18
-- Purpose: Enable handicap system overlay for training matches

-- 1. Add HasHandicap column to TrainingMatches table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'HasHandicap')
BEGIN
    ALTER TABLE TrainingMatches ADD HasHandicap BIT NOT NULL DEFAULT 0
    PRINT 'Added HasHandicap column to TrainingMatches table'
END
ELSE
BEGIN
    PRINT 'HasHandicap column already exists on TrainingMatches'
END
GO

-- 2. Add FrozenHandicapPerSeries column to TrainingMatchParticipants table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatchParticipants') AND name = 'FrozenHandicapPerSeries')
BEGIN
    ALTER TABLE TrainingMatchParticipants ADD FrozenHandicapPerSeries DECIMAL(5,2) NULL
    PRINT 'Added FrozenHandicapPerSeries column to TrainingMatchParticipants table'
END
ELSE
BEGIN
    PRINT 'FrozenHandicapPerSeries column already exists on TrainingMatchParticipants'
END
GO

-- 3. Add FrozenIsProvisional column to TrainingMatchParticipants table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatchParticipants') AND name = 'FrozenIsProvisional')
BEGIN
    ALTER TABLE TrainingMatchParticipants ADD FrozenIsProvisional BIT NULL
    PRINT 'Added FrozenIsProvisional column to TrainingMatchParticipants table'
END
ELSE
BEGIN
    PRINT 'FrozenIsProvisional column already exists on TrainingMatchParticipants'
END
GO

-- Verify the changes
SELECT 'TrainingMatches columns:' AS Info;
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TrainingMatches'
ORDER BY ORDINAL_POSITION;

SELECT 'TrainingMatchParticipants columns:' AS Info;
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TrainingMatchParticipants'
ORDER BY ORDINAL_POSITION;
