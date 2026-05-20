-- FaltskytteShootOffEntry: per-shooter, per-round shoot-off (Särskjutning) results
-- for Fältskytte (Normal + Poäng) and Magnumfält. One row per (CompetitionId,
-- MemberId, ShootingClass, Round). Multiple rounds resolve tied medal positions
-- progressively — shooters who become uniquely separated stop shooting.
--
-- Hits/Figures/HitDistribution are nullable to accommodate Magnumfält shoot-offs
-- (which use poängmål-only scoring with one hit per figure max).
--
-- Run manually in SSMS against the Umbraco database.
-- 2026-05-20

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FaltskytteShootOffEntry')
BEGIN
    CREATE TABLE FaltskytteShootOffEntry (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CompetitionId INT NOT NULL,
        MemberId INT NOT NULL,
        ShootingClass NVARCHAR(20) NOT NULL,
        Round INT NOT NULL,

        -- Normal / Poäng use Hits + Figures (with optional poängmål as tiebreaker).
        -- Magnumfält leaves these NULL — Magnum's round score is the poängmål sum.
        Hits INT NULL,
        Figures INT NULL,
        HitDistribution NVARCHAR(200) NULL,   -- JSON e.g. ["3","2","1"]

        -- Poängmål: aggregate (sum) and per-figure scores.
        -- Normal/Poäng: tiebreaker after Hits/Figures.
        -- Magnumfält: this IS the round score.
        TiebreakerScore INT NULL,
        PoangmalScores NVARCHAR(200) NULL,    -- JSON e.g. [5,4,0,3,5,2]

        EnteredBy INT NOT NULL,
        EnteredAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        LastModified DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_FaltskytteShootOff_Identity
            UNIQUE (CompetitionId, MemberId, ShootingClass, Round)
    );

    CREATE INDEX IX_FaltskytteShootOff_Competition
        ON FaltskytteShootOffEntry (CompetitionId);

    CREATE INDEX IX_FaltskytteShootOff_Member
        ON FaltskytteShootOffEntry (CompetitionId, MemberId);

    PRINT 'FaltskytteShootOffEntry table created.';
END
ELSE
BEGIN
    PRINT 'FaltskytteShootOffEntry table already exists. Skipping.';
END
GO
