-- =============================================================================
-- Migration: widen-faltskytte-patrol-weapongroup
-- Date: 2026-05-09
-- Purpose: Widen FaltskyttePatrol.WeaponGroup from NVARCHAR(10) to NVARCHAR(50).
--          Original sizing fit "A+R", "A+M", etc., but combinations including
--          "A_Opt" (5 chars) overflow once 3+ classes share a patrol — e.g.
--          "A+A_Opt+C+R" is 11 chars. Reported as a patrol-generation crash
--          ("String or binary data would be truncated") on competitionId 3936.
-- Idempotent: re-running is a no-op (ALTER COLUMN to the same width).
-- Run manually in SSMS against the Umbraco DB.
-- =============================================================================

PRINT 'Widening FaltskyttePatrol.WeaponGroup to NVARCHAR(50)...';

ALTER TABLE FaltskyttePatrol
    ALTER COLUMN WeaponGroup NVARCHAR(50) NULL;

PRINT 'Done.';
