-- PrecisionResultEntry + PrecisionResultEntrySession.
--
-- Identity-based result storage: results are keyed by (CompetitionId, MemberId,
-- SeriesNumber), so start lists can be regenerated without losing scores and late
-- registrations work without data loss. TeamNumber and Position are informational
-- (the shooter's position at time of entry).
--
-- Replaces RefactorPrecisionResultsComposer + RefactorPrecisionResultsToIdentityBased.cs.
-- Run manually in SSMS against the Umbraco database.

-- 1. PrecisionResultEntry
IF OBJECT_ID('dbo.PrecisionResultEntry', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PrecisionResultEntry] (
        [Id]             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrecisionResultEntry PRIMARY KEY,
        [CompetitionId]  INT          NOT NULL,
        [SeriesNumber]   INT          NOT NULL,
        [MemberId]       INT          NOT NULL,    -- IDENTITY FIELD — primary lookup
        [TeamNumber]     INT          NOT NULL,    -- INFORMATIONAL — position at time of entry
        [Position]       INT          NOT NULL,    -- INFORMATIONAL — position at time of entry
        [ShootingClass]  NVARCHAR(50) NOT NULL,
        [Shots]          NVARCHAR(50) NOT NULL,    -- JSON: ["X","10","9","8","7"]
        [EnteredBy]      INT          NOT NULL,    -- range officer MemberId
        [EnteredAt]      DATETIME     NOT NULL,
        [LastModified]   DATETIME     NOT NULL
    );

    -- Unique: one result per shooter per series, regardless of position.
    CREATE UNIQUE NONCLUSTERED INDEX UX_PrecisionResultEntry_CompetitionMemberSeries
        ON [dbo].[PrecisionResultEntry] (CompetitionId, MemberId, SeriesNumber);

    -- Common lookup indexes.
    CREATE NONCLUSTERED INDEX IX_PrecisionResultEntry_CompetitionId
        ON [dbo].[PrecisionResultEntry] (CompetitionId);

    CREATE NONCLUSTERED INDEX IX_PrecisionResultEntry_MemberId
        ON [dbo].[PrecisionResultEntry] (MemberId);

    CREATE NONCLUSTERED INDEX IX_PrecisionResultEntry_ShootingClass
        ON [dbo].[PrecisionResultEntry] (ShootingClass);
END
GO

-- 2. PrecisionResultEntrySession (session locking for concurrent result entry)
IF OBJECT_ID('dbo.PrecisionResultEntrySession', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PrecisionResultEntrySession] (
        [Id]              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrecisionResultEntrySession PRIMARY KEY,
        [CompetitionId]   INT      NOT NULL,
        [Position]        INT      NOT NULL,
        [SeriesNumber]    INT      NOT NULL,
        [RangeOfficerId]  INT      NOT NULL,
        [SessionStart]    DATETIME NOT NULL,
        [LastActivity]    DATETIME NOT NULL,
        [IsActive]        BIT      NOT NULL CONSTRAINT DF_PrecisionResultEntrySession_IsActive DEFAULT 1
    );

    CREATE NONCLUSTERED INDEX IX_PrecisionResultEntrySession_Competition
        ON [dbo].[PrecisionResultEntrySession] (CompetitionId, Position, SeriesNumber);
END
GO
