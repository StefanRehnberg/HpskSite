-- Create SpringskytteResultEntry table
-- Completely different schema from other competition types:
-- - Time-based scoring (sprint time + penalty minutes)
-- - One row per shooter (not per series)
-- - Age/gender classes instead of shooter classes
-- - Weapon classes A (cardboard/zones) and C (falling targets)
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-03-07

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SpringskytteResultEntry')
BEGIN
    CREATE TABLE [SpringskytteResultEntry] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [CompetitionId] INT NOT NULL,
        [MemberId] INT NOT NULL,
        [WeaponClass] NVARCHAR(10) NOT NULL,          -- 'A' or 'C'
        [AgeGenderClass] NVARCHAR(20) NOT NULL,        -- 'D 15', 'H 21', 'D 50', etc.
        [StartOrder] INT NOT NULL DEFAULT 0,           -- Position in start list
        [StartTime] NVARCHAR(20) NULL,                 -- Scheduled start time 'HH:mm:ss'
        [SprintTimeSeconds] DECIMAL(10,2) NULL,        -- Running time in seconds
        [Shots] NVARCHAR(MAX) NOT NULL DEFAULT '[]',   -- JSON: Class C: [["H","H","B","H","H"],...] Class A: [["0","1","0","3",...],...]
        [ShootingScore] INT NULL,                      -- Total penalty points from shooting
        [PenaltyMultiplier] INT NOT NULL DEFAULT 1,    -- Points per miss/zone: 1 (normal) or 2 (markestagning for class C)
        [TotalTimeSeconds] DECIMAL(10,2) NULL,         -- SprintTimeSeconds + (ShootingScore * PenaltyMultiplier * 60)
        [Status] NVARCHAR(10) NULL,                    -- NULL=normal, 'DNS'=Did Not Start, 'DNF'=Did Not Finish
        [EnteredBy] INT NOT NULL,                      -- MemberId of result officer
        [EnteredAt] DATETIME NOT NULL,
        [LastModified] DATETIME NOT NULL,
        CONSTRAINT [PK_SpringskytteResultEntry] PRIMARY KEY CLUSTERED ([Id])
    );

    -- One result per shooter per weapon class per competition
    CREATE UNIQUE INDEX [UX_SpringskytteResultEntry_CompetitionMemberWeapon]
    ON [SpringskytteResultEntry] ([CompetitionId], [MemberId], [WeaponClass]);

    -- Index for competition lookups (leaderboard/results page)
    CREATE NONCLUSTERED INDEX [IX_SpringskytteResultEntry_CompetitionId]
    ON [SpringskytteResultEntry] ([CompetitionId]);

    -- Index for member lookups (personal results)
    CREATE NONCLUSTERED INDEX [IX_SpringskytteResultEntry_MemberId]
    ON [SpringskytteResultEntry] ([MemberId]);

    PRINT 'SpringskytteResultEntry table created successfully.';
END
ELSE
BEGIN
    PRINT 'SpringskytteResultEntry table already exists. Skipping.';
END
