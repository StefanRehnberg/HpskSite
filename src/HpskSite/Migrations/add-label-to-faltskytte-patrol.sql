-- =============================================================================
-- Migration: add-label-to-faltskytte-patrol
-- Date: 2026-05-20
-- Purpose: Adds an optional freeform Label per patrol so admins can append a
--          short identifier to the patrol number (e.g. "Lördag fm", "Söndag",
--          "Final") in multi-day competitions. Renders as "Patrull 3 — Söndag"
--          across admin and public views.
-- Idempotent: re-running is a no-op.
-- Run manually in SSMS against the Umbraco DB.
-- =============================================================================

PRINT 'Adding Label column to FaltskyttePatrol...';

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.FaltskyttePatrol')
      AND name = 'Label'
)
BEGIN
    ALTER TABLE FaltskyttePatrol ADD Label NVARCHAR(200) NULL;
    PRINT 'Column added.';
END
ELSE
BEGIN
    PRINT 'Column already exists, no change.';
END
