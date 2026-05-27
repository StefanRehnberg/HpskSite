-- =============================================================================
-- Migration: add-departedat-to-faltskyttepatrol
-- Date: 2026-05-27
-- Purpose: Adds DepartedAt per patrol so the starter can "tick off" patrols as
--          they're sent out from the start line. Drives the /patrullista send-off
--          screen: "next" = lowest patrol number with DepartedAt = NULL. Stamped
--          (UTC) when a staff member presses "Skicka iväg"; cleared on "Ångra".
-- Idempotent: re-running is a no-op.
-- Run manually in SSMS against the Umbraco DB *before* deploying the code
-- (FaltskyttePatrol queries select this column once the model property exists).
-- =============================================================================

PRINT 'Adding DepartedAt column to FaltskyttePatrol...';

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.FaltskyttePatrol')
      AND name = 'DepartedAt'
)
BEGIN
    ALTER TABLE FaltskyttePatrol ADD DepartedAt DATETIME NULL;
    PRINT 'Column added.';
END
ELSE
BEGIN
    PRINT 'Column already exists, no change.';
END
