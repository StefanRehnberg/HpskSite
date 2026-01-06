-- Add missing columns to TrainingMatchJoinRequests table
-- Run this script in SSMS against your HpskSite database

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'TrainingMatchJoinRequests') AND name = 'MemberName')
BEGIN
    ALTER TABLE TrainingMatchJoinRequests ADD MemberName NVARCHAR(200) NULL;
    PRINT 'Added MemberName column';
END

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'TrainingMatchJoinRequests') AND name = 'MemberProfilePictureUrl')
BEGIN
    ALTER TABLE TrainingMatchJoinRequests ADD MemberProfilePictureUrl NVARCHAR(500) NULL;
    PRINT 'Added MemberProfilePictureUrl column';
END

-- Verify
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TrainingMatchJoinRequests'
ORDER BY ORDINAL_POSITION;
