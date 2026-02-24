-- Add LastActivityDate column to TrainingMatches
-- Used for inactivity reminders (30 min) and auto-close (6h)
-- Run manually in SSMS against the Umbraco database

IF COL_LENGTH('TrainingMatches', 'LastActivityDate') IS NULL
BEGIN
    ALTER TABLE TrainingMatches ADD LastActivityDate DATETIME NULL;
END
GO

-- Backfill existing rows with CreatedDate
UPDATE TrainingMatches SET LastActivityDate = CreatedDate WHERE LastActivityDate IS NULL;
GO
