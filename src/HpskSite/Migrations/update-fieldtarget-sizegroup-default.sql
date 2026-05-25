-- Bump the FieldTarget.SizeGroup default + existing zero rows to 15.
--
-- Group 0 is a phantom calculation reference in SHB (the "one step bigger"
-- reference used by the stödhand offset rule) — no real catalog figures
-- live there. We treat group 15 as the "Ej grupperad" bucket instead so
-- the chip row in the Figurkatalog doesn't need to surface group 0.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-25

-- 1. Existing rows with SizeGroup = 0 become 15 ("Ej grupperad")
UPDATE [FieldTarget] SET [SizeGroup] = 15 WHERE [SizeGroup] = 0;

-- 2. Drop the existing DEFAULT 0 constraint and add a DEFAULT 15 one.
--    SQL Server names default constraints automatically; look it up by column.
DECLARE @constraintName NVARCHAR(200);
SELECT @constraintName = dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON c.default_object_id = dc.object_id
WHERE c.object_id = OBJECT_ID('FieldTarget') AND c.name = 'SizeGroup';

IF @constraintName IS NOT NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = 'ALTER TABLE [FieldTarget] DROP CONSTRAINT [' + @constraintName + ']';
    EXEC sp_executesql @sql;
    PRINT 'Dropped existing default constraint ' + @constraintName;
END

ALTER TABLE [FieldTarget] ADD CONSTRAINT [DF_FieldTarget_SizeGroup] DEFAULT 15 FOR [SizeGroup];
PRINT 'Added DF_FieldTarget_SizeGroup with default 15';
