-- Add Discipline column to TrainingScores table
-- Allows tagging training/competition entries as Precision or Milsnabb
-- Default 'Precision' for backward compatibility with existing data

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'TrainingScores' AND COLUMN_NAME = 'Discipline'
)
BEGIN
    ALTER TABLE TrainingScores
    ADD Discipline NVARCHAR(50) NOT NULL DEFAULT 'Precision';

    PRINT 'Added Discipline column to TrainingScores table';
END
ELSE
BEGIN
    PRINT 'Discipline column already exists on TrainingScores table';
END
