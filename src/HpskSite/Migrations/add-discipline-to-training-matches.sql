-- Add Discipline column to TrainingMatches (defaults to 'Precision' for existing data)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'Discipline')
BEGIN
    ALTER TABLE TrainingMatches ADD Discipline NVARCHAR(50) NOT NULL DEFAULT 'Precision';
END
