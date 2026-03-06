-- Create MilsnabbResultEntry table (separate from PrecisionResultEntry)
-- Identical schema to PrecisionResultEntry but for Milsnabb competition results.
-- This prevents Milsnabb scores from mixing into Precision statistics,
-- handicap calculations, and personal bests.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-03-05

-- Create table only if it doesn't exist
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'MilsnabbResultEntry')
BEGIN
    CREATE TABLE [MilsnabbResultEntry] (
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
        CONSTRAINT [PK_MilsnabbResultEntry] PRIMARY KEY CLUSTERED ([Id])
    );

    -- Unique index: one result per shooter per class per series
    CREATE UNIQUE INDEX [UX_MilsnabbResultEntry_CompetitionMemberClassSeries]
    ON [MilsnabbResultEntry] ([CompetitionId], [MemberId], [ShootingClass], [SeriesNumber]);

    -- Index for competition lookups
    CREATE NONCLUSTERED INDEX [IX_MilsnabbResultEntry_CompetitionId]
    ON [MilsnabbResultEntry] ([CompetitionId]);

    -- Index for member lookups (personal results)
    CREATE NONCLUSTERED INDEX [IX_MilsnabbResultEntry_MemberId]
    ON [MilsnabbResultEntry] ([MemberId]);

    -- Index for shooting class (results grouping)
    CREATE NONCLUSTERED INDEX [IX_MilsnabbResultEntry_ShootingClass]
    ON [MilsnabbResultEntry] ([ShootingClass]);

    PRINT 'MilsnabbResultEntry table created successfully.';
END
ELSE
BEGIN
    PRINT 'MilsnabbResultEntry table already exists. Skipping.';
END
