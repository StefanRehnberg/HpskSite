-- Add TargetsPerFigure column to FieldTarget table
-- Default 1 (single target per figure, the normal case)
-- Multi-target figures (e.g. triple silhouette) set this to 2, 3, etc.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('FieldTarget') AND name = 'TargetsPerFigure'
)
BEGIN
    ALTER TABLE FieldTarget ADD TargetsPerFigure INT NOT NULL DEFAULT 1;
    PRINT 'Added TargetsPerFigure column to FieldTarget';
END
ELSE
BEGIN
    PRINT 'TargetsPerFigure column already exists';
END
