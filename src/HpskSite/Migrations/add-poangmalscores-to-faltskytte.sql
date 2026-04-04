-- Add PoangmalScores column to FaltskytteResultEntry
-- Stores individual poångmål scores as JSON array (e.g. [24,20])
-- Run manually in SSMS
-- 2026-04-03

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('FaltskytteResultEntry') AND name = 'PoangmalScores')
BEGIN
    ALTER TABLE FaltskytteResultEntry ADD PoangmalScores NVARCHAR(200) NULL;
END
GO
