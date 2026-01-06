-- Training Match: Add StartDate and JoinRequests table
-- Run this script in SSMS against your HpskSite database

-- 1. Add StartDate column to TrainingMatches table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'TrainingMatches') AND name = 'StartDate')
BEGIN
    ALTER TABLE TrainingMatches ADD StartDate DATETIME NULL;
    PRINT 'Added StartDate column to TrainingMatches';

    -- Set existing matches to have StartDate = CreatedDate (immediate start)
    UPDATE TrainingMatches SET StartDate = CreatedDate WHERE StartDate IS NULL;
    PRINT 'Updated existing matches with StartDate = CreatedDate';
END
ELSE
BEGIN
    PRINT 'StartDate column already exists';
END
GO

-- 2. Create TrainingMatchJoinRequests table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TrainingMatchJoinRequests')
BEGIN
    CREATE TABLE TrainingMatchJoinRequests (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TrainingMatchId INT NOT NULL,
        MemberId INT NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
        RequestDate DATETIME NOT NULL DEFAULT GETDATE(),
        ResponseDate DATETIME NULL,
        ResponseByMemberId INT NULL,
        Notes NVARCHAR(500) NULL,

        CONSTRAINT UQ_TrainingMatchJoinRequests_Match_Member
            UNIQUE (TrainingMatchId, MemberId),

        CONSTRAINT FK_TrainingMatchJoinRequests_TrainingMatches
            FOREIGN KEY (TrainingMatchId) REFERENCES TrainingMatches(Id) ON DELETE CASCADE
    );

    -- Create index for faster lookups
    CREATE INDEX IX_TrainingMatchJoinRequests_TrainingMatchId
        ON TrainingMatchJoinRequests(TrainingMatchId);

    CREATE INDEX IX_TrainingMatchJoinRequests_MemberId
        ON TrainingMatchJoinRequests(MemberId);

    CREATE INDEX IX_TrainingMatchJoinRequests_Status
        ON TrainingMatchJoinRequests(Status);

    PRINT 'Created TrainingMatchJoinRequests table with indexes';
END
ELSE
BEGIN
    PRINT 'TrainingMatchJoinRequests table already exists';
END
GO

-- Verify the changes
SELECT 'TrainingMatches columns:' AS Info;
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TrainingMatches'
ORDER BY ORDINAL_POSITION;

SELECT 'TrainingMatchJoinRequests columns:' AS Info;
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TrainingMatchJoinRequests'
ORDER BY ORDINAL_POSITION;
