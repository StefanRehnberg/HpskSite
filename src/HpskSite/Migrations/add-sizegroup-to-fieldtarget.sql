-- Add SizeGroup column to FieldTarget table
-- Integer 0-15 used to derive max distance buckets for automatic
-- shoot-time suggestions on Fältskytte stations (SHB rules).
-- Default 0 (unset) so existing rows remain valid.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('FieldTarget') AND name = 'SizeGroup'
)
BEGIN
    ALTER TABLE FieldTarget ADD SizeGroup INT NOT NULL DEFAULT 0;
    PRINT 'Added SizeGroup column to FieldTarget';
END
ELSE
BEGIN
    PRINT 'SizeGroup column already exists';
END
