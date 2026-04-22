-- =============================================================================
-- Migration: aopt-split-v1
-- Date: 2026-04-22
-- Purpose: Promote "A_opt" from a single A-class variant to its own weapon class
--          with three levels (A_opt_1, A_opt_2, A_opt_3).
--          Existing rows storing "A_opt" are rewritten to "A_opt_2" (middle level);
--          admins can reclassify per shooter afterward.
-- Idempotent: re-running is a no-op once values are migrated.
-- Run manually in SSMS against the Umbraco DB.
-- =============================================================================

PRINT 'Starting A_opt split migration (v1)...';

-- Result entry tables (one row per series per shooter; multiple per discipline)
UPDATE PrecisionResultEntry        SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'PrecisionResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

UPDATE MilsnabbResultEntry         SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'MilsnabbResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

UPDATE DuellResultEntry            SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'DuellResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

UPDATE NationellHelmatchResultEntry SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'NationellHelmatchResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

UPDATE MagnumPrecisionResultEntry  SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'MagnumPrecisionResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

UPDATE FaltskytteResultEntry       SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'FaltskytteResultEntry: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

-- Patrol assignments (Fältskytte) carry the shooting class for per-weapon-group routing
UPDATE FaltskyttePatrolMember      SET ShootingClass = 'A_opt_2' WHERE ShootingClass = 'A_opt';
PRINT 'FaltskyttePatrolMember: ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' row(s) updated';

PRINT 'A_opt split migration complete.';
