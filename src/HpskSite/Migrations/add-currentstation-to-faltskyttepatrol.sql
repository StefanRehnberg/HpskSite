-- =============================================================================
-- Migration: add-currentstation-to-faltskyttepatrol
-- Date: 2026-05-09
-- Purpose: Per-patrol "current station" cursor for the new Fältskytte
--          self-service result entry mode. Each scan of /station?c=X&s=N by a
--          shooter in the patrol advances the cursor; staff scans never touch
--          it. Score writes from non-staff are only allowed at the patrol's
--          current station — older stations become read-only for shooters.
-- Idempotent: re-running is a no-op.
-- Run manually in SSMS against the Umbraco DB.
-- =============================================================================

PRINT 'Adding CurrentStation column to FaltskyttePatrol...';

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.FaltskyttePatrol')
      AND name = 'CurrentStation'
)
BEGIN
    ALTER TABLE FaltskyttePatrol ADD CurrentStation INT NULL;
    PRINT 'Column added.';
END
ELSE
BEGIN
    PRINT 'Column already exists, no change.';
END
