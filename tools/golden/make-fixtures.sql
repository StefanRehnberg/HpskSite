/* make-fixtures.sql -- golden-master fixtures (rehearsal database ONLY).
   Clones every competition in $(SRC) four times: scopes "Svenskt Masterskap", "Landsdelsmasterskap",
   "Kretsmasterskap" and "Klubbmasterskap" (all with the real a-umlaut). Clones are named "... [GOLDEN SM|LDM|KM|KLM]", have SeriesId NULL,
   and are recorded in dbo.GoldenFixture. Originals are never touched. Re-runnable: old clones are removed first.
   Run: sqlcmd -S "localhost\SQLEXPRESS" -E -C -I -b -d Umbraco_rehearsal -v SRC="1,2,3" -i make-fixtures.sql
   File is ASCII on purpose (non-ASCII is built with NCHAR). */
SET NOCOUNT ON; SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON;
IF DB_NAME() <> 'Umbraco_rehearsal' BEGIN RAISERROR('Refusing: not Umbraco_rehearsal', 16, 1); RETURN; END
GO
IF OBJECT_ID('dbo.GoldenFixture') IS NULL
    CREATE TABLE dbo.GoldenFixture (CloneId INT NOT NULL PRIMARY KEY, SourceId INT NOT NULL, Scope NVARCHAR(60) NOT NULL);
GO
BEGIN TRAN;

DECLARE @copyTables TABLE (t sysname);
INSERT @copyTables VALUES
 ('CompetitionParticipantStatus'),('CompetitionTeam'),('CompetitionShootOffEntry'),('DuellResultEntry'),
 ('FaltskytteResultEntry'),('FaltskytteShootOffEntry'),('MagnumPrecisionResultEntry'),('MilsnabbResultEntry'),
 ('NationellHelmatchResultEntry'),('PrecisionResultEntry'),('SportpistolResultEntry'),('StandardpistolResultEntry');

-- 1. remove earlier clones (children first)
DECLARE @old TABLE (id INT);
INSERT @old SELECT CloneId FROM dbo.GoldenFixture;
DECLARE @t sysname, @sql NVARCHAR(MAX);
IF EXISTS (SELECT 1 FROM @old)
BEGIN
    DELETE rc FROM dbo.CompetitionRegistrationClass rc JOIN dbo.CompetitionRegistration r ON r.Id = rc.RegistrationId WHERE r.CompetitionId IN (SELECT id FROM @old);
    DELETE FROM dbo.CompetitionRegistration WHERE CompetitionId IN (SELECT id FROM @old);
    DECLARE d CURSOR LOCAL FAST_FORWARD FOR SELECT t FROM @copyTables;
    OPEN d; FETCH NEXT FROM d INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'DELETE FROM dbo.' + QUOTENAME(@t) + N' WHERE CompetitionId IN (SELECT CloneId FROM dbo.GoldenFixture)';
        EXEC sp_executesql @sql;
        FETCH NEXT FROM d INTO @t;
    END
    CLOSE d; DEALLOCATE d;
    DELETE FROM dbo.CompetitionResultList WHERE CompetitionId IN (SELECT CloneId FROM dbo.GoldenFixture);
    DELETE FROM dbo.CompetitionStartList WHERE CompetitionId IN (SELECT CloneId FROM dbo.GoldenFixture);
    DELETE FROM dbo.Competition WHERE Id IN (SELECT CloneId FROM dbo.GoldenFixture);
    DELETE FROM dbo.GoldenFixture;
END

-- 2. clone
DECLARE @compCols NVARCHAR(MAX), @compSel NVARCHAR(MAX);
SELECT @compCols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
       @compSel  = STRING_AGG(CAST(CASE c.name
                      WHEN 'Key' THEN 'NEWID()'
                      WHEN 'Name' THEN 'LEFT([Name],150)+@suffix'
                      WHEN 'UrlSegment' THEN 'LEFT([UrlSegment],150)+@useg'
                      WHEN 'CompetitionScope' THEN '@scope'
                      WHEN 'SeriesId' THEN 'NULL'
                      ELSE QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('dbo.Competition') AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';

DECLARE @regCols NVARCHAR(MAX), @regSel NVARCHAR(MAX);
SELECT @regCols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
       @regSel  = STRING_AGG(CAST(CASE c.name WHEN 'CompetitionId' THEN '@clone' ELSE 's.' + QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('dbo.CompetitionRegistration') AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';

DECLARE @clsCols NVARCHAR(MAX), @clsSel NVARCHAR(MAX);
SELECT @clsCols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
       @clsSel  = STRING_AGG(CAST(CASE c.name WHEN 'RegistrationId' THEN 'm.newId' ELSE 'c.' + QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('dbo.CompetitionRegistrationClass') AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';

DECLARE @scopes TABLE (code NVARCHAR(10), scope NVARCHAR(60));
INSERT @scopes VALUES
 (N'SM',  N'Svenskt M' + NCHAR(228) + N'sterskap'),
 (N'LDM', N'Landsdelsm' + NCHAR(228) + N'sterskap'),
 (N'KM',  N'Kretsm' + NCHAR(228) + N'sterskap'),
 (N'KLM', N'Klubbm' + NCHAR(228) + N'sterskap');

CREATE TABLE #map (oldId INT, newId INT);
DECLARE @src INT, @clone INT, @code NVARCHAR(10), @scope NVARCHAR(60), @suffix NVARCHAR(40), @useg NVARCHAR(40);

DECLARE s CURSOR LOCAL FAST_FORWARD FOR
    SELECT CAST(value AS INT), sc.code, sc.scope
    FROM STRING_SPLIT('$(SRC)', ',') x CROSS JOIN @scopes sc
    WHERE EXISTS (SELECT 1 FROM dbo.Competition WHERE Id = CAST(x.value AS INT))
    ORDER BY CAST(value AS INT), sc.code;
OPEN s; FETCH NEXT FROM s INTO @src, @code, @scope;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @suffix = N' [GOLDEN ' + @code + N']';
    SET @useg   = N'-golden-' + LOWER(@code);

    SET @sql = N'INSERT dbo.Competition (' + @compCols + N') SELECT ' + @compSel + N' FROM dbo.Competition WHERE Id = @src; SET @newId = SCOPE_IDENTITY();';
    EXEC sp_executesql @sql, N'@src INT, @suffix NVARCHAR(40), @useg NVARCHAR(40), @scope NVARCHAR(60), @newId INT OUTPUT', @src, @suffix, @useg, @scope, @clone OUTPUT;
    INSERT dbo.GoldenFixture (CloneId, SourceId, Scope) VALUES (@clone, @src, @scope);

    TRUNCATE TABLE #map;
    SET @sql = N'MERGE dbo.CompetitionRegistration t USING (SELECT * FROM dbo.CompetitionRegistration WHERE CompetitionId = @src) s ON 1 = 0 '
             + N'WHEN NOT MATCHED THEN INSERT (' + @regCols + N') VALUES (' + @regSel + N') OUTPUT s.Id, inserted.Id INTO #map(oldId, newId);';
    EXEC sp_executesql @sql, N'@src INT, @clone INT', @src, @clone;

    SET @sql = N'INSERT dbo.CompetitionRegistrationClass (' + @clsCols + N') SELECT ' + @clsSel
             + N' FROM dbo.CompetitionRegistrationClass c JOIN #map m ON m.oldId = c.RegistrationId';
    EXEC sp_executesql @sql;

    DECLARE ct CURSOR LOCAL FAST_FORWARD FOR SELECT t FROM @copyTables;
    OPEN ct; FETCH NEXT FROM ct INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @cols NVARCHAR(MAX), @sel NVARCHAR(MAX);
        SELECT @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
               @sel  = STRING_AGG(CAST(CASE c.name WHEN 'CompetitionId' THEN '@clone' ELSE QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID('dbo.' + @t) AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';
        SET @sql = N'INSERT dbo.' + QUOTENAME(@t) + N' (' + @cols + N') SELECT ' + @sel + N' FROM dbo.' + QUOTENAME(@t) + N' WHERE CompetitionId = @src';
        EXEC sp_executesql @sql, N'@src INT, @clone INT', @src, @clone;
        FETCH NEXT FROM ct INTO @t;
    END
    CLOSE ct; DEALLOCATE ct;

    -- result list row: carries MergeConfig / ShootOffConfig / ClassNameOverrides, so merges and shoot-offs are exercised under every scope
    DECLARE @rlCols NVARCHAR(MAX), @rlSel NVARCHAR(MAX);
    SELECT @rlCols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
           @rlSel  = STRING_AGG(CAST(CASE c.name WHEN 'CompetitionId' THEN '@clone' ELSE QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('dbo.CompetitionResultList') AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';
    SET @sql = N'INSERT dbo.CompetitionResultList (' + @rlCols + N') SELECT ' + @rlSel + N' FROM dbo.CompetitionResultList WHERE CompetitionId = @src';
    EXEC sp_executesql @sql, N'@src INT, @clone INT', @src, @clone;

    -- start lists (qualifying first, then finals with QualificationStartListId remapped to the cloned qualifying list).
    -- Needed so the finals path (finalist stars, contenders) is exercised on clones of championship competitions.
    DECLARE @slCols NVARCHAR(MAX), @slSelQ NVARCHAR(MAX), @slSelF NVARCHAR(MAX);
    SELECT @slCols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), ','),
           @slSelQ = STRING_AGG(CAST(CASE c.name WHEN 'CompetitionId' THEN '@clone' ELSE 's.' + QUOTENAME(c.name) END AS NVARCHAR(MAX)), ','),
           @slSelF = STRING_AGG(CAST(CASE c.name WHEN 'CompetitionId' THEN '@clone'
                                     WHEN 'QualificationStartListId' THEN 'ISNULL(m.newId, s.QualificationStartListId)'
                                     ELSE 's.' + QUOTENAME(c.name) END AS NVARCHAR(MAX)), ',')
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('dbo.CompetitionStartList') AND c.is_identity = 0 AND c.is_computed = 0 AND TYPE_NAME(c.user_type_id) <> 'timestamp';
    CREATE TABLE #slmap (oldId INT, newId INT);
    SET @sql = N'MERGE dbo.CompetitionStartList t USING (SELECT * FROM dbo.CompetitionStartList WHERE CompetitionId = @src AND Kind <> 1) s ON 1 = 0 '
             + N'WHEN NOT MATCHED THEN INSERT (' + @slCols + N') VALUES (' + @slSelQ + N') OUTPUT s.Id, inserted.Id INTO #slmap(oldId, newId);';
    EXEC sp_executesql @sql, N'@src INT, @clone INT', @src, @clone;
    SET @sql = N'INSERT dbo.CompetitionStartList (' + @slCols + N') SELECT ' + @slSelF
             + N' FROM dbo.CompetitionStartList s LEFT JOIN #slmap m ON m.oldId = s.QualificationStartListId WHERE s.CompetitionId = @src AND s.Kind = 1';
    EXEC sp_executesql @sql, N'@src INT, @clone INT', @src, @clone;
    DROP TABLE #slmap;

    FETCH NEXT FROM s INTO @src, @code, @scope;
END
CLOSE s; DEALLOCATE s;
DROP TABLE #map;

SELECT 'clones' AS k, COUNT(*) AS n FROM dbo.GoldenFixture;
COMMIT;
SELECT @@TRANCOUNT AS trancount;
