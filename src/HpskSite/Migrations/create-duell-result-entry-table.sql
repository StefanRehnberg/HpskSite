-- Create DuellResultEntry table (separate from PrecisionResultEntry)
-- Identical schema to PrecisionResultEntry but for Duell competition results.
-- This prevents Duell scores from mixing into Precision statistics,
-- handicap calculations, and personal bests.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-03-06

-- Create table only if it doesn't exist
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'DuellResultEntry')
BEGIN
    CREATE TABLE [DuellResultEntry] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [CompetitionId] INT NOT NULL,
        [SeriesNumber] INT NOT NULL,
        [MemberId] INT NOT NULL,
        [TeamNumber] INT NOT NULL,
        [Position] INT NOT NULL,
        [ShootingClass] NVARCHAR(50) NOT NULL,
        [Shots] NVARCHAR(50) NOT NULL,     -- JSON: ["X","10","9","8","7"]
        [EnteredBy] INT NOT NULL,           -- Range officer MemberId
        [EnteredAt] DATETIME NOT NULL,
        [LastModified] DATETIME NOT NULL,
        CONSTRAINT [PK_DuellResultEntry] PRIMARY KEY CLUSTERED ([Id])
    );

    -- Unique index: one result per shooter per class per series
    CREATE UNIQUE INDEX [UX_DuellResultEntry_CompetitionMemberClassSeries]
    ON [DuellResultEntry] ([CompetitionId], [MemberId], [ShootingClass], [SeriesNumber]);

    -- Index for competition lookups
    CREATE NONCLUSTERED INDEX [IX_DuellResultEntry_CompetitionId]
    ON [DuellResultEntry] ([CompetitionId]);

    -- Index for member lookups (personal results)
    CREATE NONCLUSTERED INDEX [IX_DuellResultEntry_MemberId]
    ON [DuellResultEntry] ([MemberId]);

    -- Index for shooting class (results grouping)
    CREATE NONCLUSTERED INDEX [IX_DuellResultEntry_ShootingClass]
    ON [DuellResultEntry] ([ShootingClass]);

    PRINT 'DuellResultEntry table created successfully.';
END
ELSE
BEGIN
    PRINT 'DuellResultEntry table already exists. Skipping.';
END
