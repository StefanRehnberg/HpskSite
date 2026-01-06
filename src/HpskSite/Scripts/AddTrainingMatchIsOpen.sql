-- Migration: Add IsOpen column to TrainingMatches
-- Date: 2025-12-17
-- Purpose: Allow matches to be marked as private (join only via link/QR code)

-- Add IsOpen column (default true for existing matches - preserves current behavior)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('TrainingMatches') AND name = 'IsOpen')
BEGIN
    ALTER TABLE TrainingMatches ADD IsOpen BIT NOT NULL DEFAULT 1
    PRINT 'Added IsOpen column to TrainingMatches table'
END
ELSE
BEGIN
    PRINT 'IsOpen column already exists'
END
