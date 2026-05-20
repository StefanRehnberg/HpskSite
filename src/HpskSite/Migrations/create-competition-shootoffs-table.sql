-- Create CompetitionShootOffEntry table for Särskjutning (shoot-off) handling
-- in championship competitions across all precision-family disciplines
-- (Precision, Duell, Milsnabb, MagnumPrecision, NationellHelmatch).
--
-- Identity-based: keyed by (CompetitionId, MemberId, ShootingClass, Round, SeriesNumber)
-- so start-list or class regeneration never orphans entered shoot-off scores.
--
-- One row per series. A "round" is typically a single 5-shot series. Round 2+
-- is only created when round 1 left tied shooters undecided.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-19

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CompetitionShootOffEntry')
BEGIN
    CREATE TABLE [CompetitionShootOffEntry] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [CompetitionId] INT NOT NULL,
        [MemberId] INT NOT NULL,
        [ShootingClass] NVARCHAR(50) NOT NULL,
        [Round] INT NOT NULL,
        [SeriesNumber] INT NOT NULL DEFAULT 1,
        [Shots] NVARCHAR(50) NOT NULL,     -- JSON: ["X","10","9","8","7"]
        [EnteredBy] INT NOT NULL,           -- Range officer MemberId
        [EnteredAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [LastModified] DATETIME NOT NULL DEFAULT GETDATE(),
        CONSTRAINT [PK_CompetitionShootOffEntry] PRIMARY KEY CLUSTERED ([Id])
    );

    -- Unique index: one row per (competition, shooter, class, round, series)
    CREATE UNIQUE INDEX [UX_CompetitionShootOffEntry_Identity]
    ON [CompetitionShootOffEntry] ([CompetitionId], [MemberId], [ShootingClass], [Round], [SeriesNumber]);

    -- Lookup by competition (most common access pattern)
    CREATE NONCLUSTERED INDEX [IX_CompetitionShootOffEntry_CompetitionId]
    ON [CompetitionShootOffEntry] ([CompetitionId]);

    -- Lookup by member (personal-history surfaces, if ever needed)
    CREATE NONCLUSTERED INDEX [IX_CompetitionShootOffEntry_MemberId]
    ON [CompetitionShootOffEntry] ([MemberId]);

    PRINT 'CompetitionShootOffEntry table created successfully.';
END
ELSE
BEGIN
    PRINT 'CompetitionShootOffEntry table already exists. Skipping.';
END
