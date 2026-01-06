-- Migration: Create ShooterStatistics table for Handicap System
-- Date: 2025-12-18
-- Purpose: Track RAW performance statistics per member/weapon class for handicap calculation

-- Create ShooterStatistics table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ShooterStatistics')
BEGIN
    CREATE TABLE ShooterStatistics (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        MemberId INT NOT NULL,
        Discipline NVARCHAR(50) NOT NULL,        -- 'Precision'
        WeaponClass NVARCHAR(10) NOT NULL,       -- 'A', 'B', 'C', 'R', 'P', 'M', 'L'
        CompletedMatches INT NOT NULL DEFAULT 0,
        TotalSeriesCount INT NOT NULL DEFAULT 0,
        TotalSeriesPoints DECIMAL(10,2) NOT NULL DEFAULT 0,  -- RAW points only
        AveragePerSeries AS (
            CASE WHEN TotalSeriesCount > 0
            THEN CAST(TotalSeriesPoints / TotalSeriesCount AS DECIMAL(5,2))
            ELSE 0 END
        ) PERSISTED,
        LastCalculated DATETIME NOT NULL DEFAULT GETDATE(),

        -- Ensure one record per member/discipline/weapon combination
        CONSTRAINT UQ_ShooterStatistics_Member_Discipline_Weapon
            UNIQUE (MemberId, Discipline, WeaponClass)
    );

    -- Create indexes for faster lookups
    CREATE INDEX IX_ShooterStatistics_MemberId
        ON ShooterStatistics(MemberId);

    CREATE INDEX IX_ShooterStatistics_WeaponClass
        ON ShooterStatistics(WeaponClass);

    CREATE INDEX IX_ShooterStatistics_Discipline
        ON ShooterStatistics(Discipline);

    PRINT 'Created ShooterStatistics table with indexes';
END
ELSE
BEGIN
    PRINT 'ShooterStatistics table already exists';
END
GO

-- Verify the table structure
SELECT 'ShooterStatistics columns:' AS Info;
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'ShooterStatistics'
ORDER BY ORDINAL_POSITION;
