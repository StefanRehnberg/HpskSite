-- Märken (marksmanship proficiency badges, SHB kap 5) ledger.
--
-- Phase 1: Pistolskyttemärket — base valörer (Brons/Silver/Guld, Guld carries a national
-- registration number) + the yearly Guldfodringar (two-part upholding) that drive the
-- årtalsmärke ladder. Distinct from Standardmedaljer (competition-placement medals).
--
-- MemberBadge              — awarded badges (system of record).
-- MemberBadgeQualification — yearly two-part Guldfodring; årtalsmärke level is derived in code
--                            from COUNT(Fulfilled + Verified) per (member, family).
--
-- Run manually in SSMS against the Umbraco database (the Umbraco migration composer is
-- unreliable in this project — see CLAUDE.md).

IF OBJECT_ID('dbo.MemberBadge', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MemberBadge] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [BadgeFamily]          NVARCHAR(40)  NOT NULL,   -- 'Pistolskytte' (Phase 1)
        [Level]                NVARCHAR(80)  NOT NULL,   -- 'Brons' | 'Silver' | 'Guld' | <årtalsmärke step>
        [LevelOrdinal]         INT           NOT NULL CONSTRAINT DF_MemberBadge_LevelOrd DEFAULT 0,
        [Discipline]           NVARCHAR(30)  NULL,
        [AchievedYear]         INT           NOT NULL,
        [AchievedDate]         DATETIME      NULL,
        [SignedOffByMemberId]  INT           NULL,
        [SignedOffDate]        DATETIME      NULL,
        [UniqueNumber]         NVARCHAR(40)  NULL,        -- ONLY Pistolskyttemärket Guld
        [Source]               NVARCHAR(20)  NOT NULL,    -- 'SelfReported' | 'OnSite' | 'Admin'
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_MemberBadge_Status DEFAULT 'Reported',
        [ProofFileRef]         NVARCHAR(400) NULL,
        [Notes]                NVARCHAR(MAX) NULL,
        [EnteredByMemberId]    INT           NOT NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MemberBadge_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MemberBadge_UpdatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_MemberBadge_Member        ON [dbo].[MemberBadge] (MemberId, BadgeFamily);
    CREATE INDEX IX_MemberBadge_MemberStatus  ON [dbo].[MemberBadge] (MemberId, Status);

    -- One base valör per (member, family, level). Årtalsmärken aren't constrained here
    -- (they're normally derived, not stored).
    CREATE UNIQUE INDEX UX_MemberBadge_BaseLevel
        ON [dbo].[MemberBadge] (MemberId, BadgeFamily, [Level])
        WHERE LevelOrdinal >= 1 AND LevelOrdinal <= 3;
END
GO

IF OBJECT_ID('dbo.MemberBadgeQualification', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MemberBadgeQualification] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [BadgeFamily]          NVARCHAR(40)  NOT NULL,
        [Year]                 INT           NOT NULL,

        -- Part 1 — precision
        [Part1Met]             BIT           NOT NULL CONSTRAINT DF_MBQ_Part1Met DEFAULT 0,
        [Part1Source]          NVARCHAR(20)  NULL,        -- 'TrainingScore' | 'Competition' | 'StandardMedal' | 'ManualAttest'
        [Part1Date]            DATETIME      NULL,
        [Part1RefId]           INT           NULL,        -- TrainingScores row id when auto-detected
        [Part1Note]            NVARCHAR(400) NULL,

        -- Part 2 — speed / tillämpning
        [Part2Met]             BIT           NOT NULL CONSTRAINT DF_MBQ_Part2Met DEFAULT 0,
        [Part2Source]          NVARCHAR(20)  NULL,
        [Part2Date]            DATETIME      NULL,
        [Part2RefId]           INT           NULL,
        [Part2Note]            NVARCHAR(400) NULL,

        [Fulfilled]            BIT           NOT NULL CONSTRAINT DF_MBQ_Fulfilled DEFAULT 0,

        [SignedOffByMemberId]  INT           NULL,
        [SignedOffDate]        DATETIME      NULL,
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_MBQ_Status DEFAULT 'Reported',

        [ProofFileRef]         NVARCHAR(400) NULL,
        [Notes]                NVARCHAR(MAX) NULL,

        [EnteredByMemberId]    INT           NOT NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MBQ_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MBQ_UpdatedAt DEFAULT GETDATE()
    );

    -- One qualification row per (member, family, year).
    CREATE UNIQUE INDEX UX_MBQ_MemberFamilyYear
        ON [dbo].[MemberBadgeQualification] (MemberId, BadgeFamily, [Year]);

    CREATE INDEX IX_MBQ_MemberStatus
        ON [dbo].[MemberBadgeQualification] (MemberId, Status);
END
GO
