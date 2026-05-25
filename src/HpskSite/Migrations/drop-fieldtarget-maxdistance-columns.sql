-- Drop FieldTarget.MaxDistance{A,R,B,C} columns.
--
-- These were per-figure overrides that duplicated the SHB table values.
-- SizeGroup now drives max-distance lookups via the SHB diagram in JS,
-- so the per-figure values are dead. Drop the columns to keep the schema
-- aligned with what the app actually uses.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-25

IF COL_LENGTH('FieldTarget', 'MaxDistanceC') IS NOT NULL
BEGIN
    ALTER TABLE [FieldTarget] DROP COLUMN [MaxDistanceC];
    PRINT 'Dropped MaxDistanceC';
END
IF COL_LENGTH('FieldTarget', 'MaxDistanceB') IS NOT NULL
BEGIN
    ALTER TABLE [FieldTarget] DROP COLUMN [MaxDistanceB];
    PRINT 'Dropped MaxDistanceB';
END
IF COL_LENGTH('FieldTarget', 'MaxDistanceA') IS NOT NULL
BEGIN
    ALTER TABLE [FieldTarget] DROP COLUMN [MaxDistanceA];
    PRINT 'Dropped MaxDistanceA';
END
IF COL_LENGTH('FieldTarget', 'MaxDistanceR') IS NOT NULL
BEGIN
    ALTER TABLE [FieldTarget] DROP COLUMN [MaxDistanceR];
    PRINT 'Dropped MaxDistanceR';
END
