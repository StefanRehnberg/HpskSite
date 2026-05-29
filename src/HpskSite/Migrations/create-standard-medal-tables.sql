-- Standardmedaljer (Standard Medals) ledger.
--
-- StandardMedalAward is the durable system of record for every Standard medal
-- (Silver = 2 p, Brons = 1 p) won by a member. Sources:
--   'OnSite'        — materialized from our own competitions when results go official
--   'SelfReported'  — entered by the member on "Min sida" for external competitions
--   'AdminEntered'  — entered on the member's behalf by a club admin
--
-- The SAME awards aggregate two different ways:
--   * Riksmästarklass (klass 3) qualification: points PER DISCIPLINE, previous year,
--     threshold 3 p. (Computed in code — not stored here.)
--   * Guldmedalj: points pooled across ALL disciplines, lifetime, 50 p per medal,
--     consumed by an approved StandardMedalGoldApplication.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.StandardMedalAward', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StandardMedalAward] (
        [Id]                  INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]            INT           NOT NULL,
        [Year]                INT           NOT NULL,    -- season = calendar year of the competition
        [Discipline]          NVARCHAR(30)  NOT NULL,    -- 'Precision' | 'MagnumPrecision' | 'Milsnabb' | 'Faltskytte' | 'MagnumFalt' | 'NationellHelmatch' | 'Duell'
        [MedalType]           NVARCHAR(2)   NOT NULL,    -- 'S' (Silver) | 'B' (Brons)
        [Points]              INT           NOT NULL,    -- 2 | 1  (denormalized from MedalType for safety)
        [Source]              NVARCHAR(20)  NOT NULL,    -- 'OnSite' | 'SelfReported' | 'AdminEntered'

        -- On-site link (Source = 'OnSite') — our own result page is the proof.
        [CompetitionId]       INT           NULL,

        -- External / self-reported descriptive fields.
        [CompetitionName]     NVARCHAR(300) NULL,
        [CompetitionDate]     DATETIME      NULL,
        [Location]            NVARCHAR(200) NULL,
        [ShootingClass]       NVARCHAR(40)  NULL,

        -- Proof of the medal. ProofType: 'File' | 'OnSite' | 'Attestation' | NULL.
        -- ProofFileRef is an opaque relative reference resolved by an authorized endpoint
        -- (NOT a public media URL — these are personal data bundled into Gold applications).
        [ProofType]           NVARCHAR(20)  NULL,
        [ProofFileRef]        NVARCHAR(400) NULL,

        -- Quality gate before anything is reported to SPSF.
        [Status]              NVARCHAR(20)  NOT NULL CONSTRAINT DF_StdMedalAward_Status DEFAULT 'Reported', -- 'Reported' | 'Verified' | 'Rejected'

        -- Set when an approved Gold application consumes this award's points.
        [GoldApplicationId]   INT           NULL,

        -- Link back to the self-entered TrainingScores row, so edits/deletes stay in sync.
        [TrainingScoreId]     INT           NULL,

        [VerifiedByMemberId]  INT           NULL,
        [VerifiedAt]          DATETIME      NULL,
        [EnteredByMemberId]   INT           NOT NULL,
        [CreatedAt]           DATETIME      NOT NULL CONSTRAINT DF_StdMedalAward_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]           DATETIME      NOT NULL CONSTRAINT DF_StdMedalAward_UpdatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_StdMedalAward_MemberYear
        ON [dbo].[StandardMedalAward] (MemberId, [Year]);

    CREATE INDEX IX_StdMedalAward_MemberStatus
        ON [dbo].[StandardMedalAward] (MemberId, Status);

    CREATE INDEX IX_StdMedalAward_Gold
        ON [dbo].[StandardMedalAward] (GoldApplicationId)
        WHERE GoldApplicationId IS NOT NULL;

    -- Idempotent on-site materialization: one medal per (competition, member, discipline, class).
    CREATE UNIQUE INDEX UX_StdMedalAward_OnSite
        ON [dbo].[StandardMedalAward] (CompetitionId, MemberId, Discipline, ShootingClass)
        WHERE Source = 'OnSite' AND CompetitionId IS NOT NULL;

    -- Avoid duplicate self-entry medals from the same TrainingScores row.
    CREATE UNIQUE INDEX UX_StdMedalAward_TrainingScore
        ON [dbo].[StandardMedalAward] (TrainingScoreId)
        WHERE TrainingScoreId IS NOT NULL;
END
GO

-- Guldmedalj-ansökan. Each approved application consumes 50 points pooled across ALL
-- disciplines. AwardIdsJson snapshots the awards that justify those 50 points so the
-- club can attach the matching result lists to the SPSF application. Accounting is
-- derived: available points = SUM(member award points) - SUM(approved PointsConsumed).

IF OBJECT_ID('dbo.StandardMedalGoldApplication', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StandardMedalGoldApplication] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [ClubId]               INT           NOT NULL,   -- member's primary club at application time
        [SequenceNumber]       INT           NOT NULL,   -- 1st gold, 2nd gold, ... per member
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_StdMedalGold_Status DEFAULT 'Draft', -- 'Draft' | 'Applied' | 'Approved' | 'Rejected'
        [PointsConsumed]       INT           NOT NULL CONSTRAINT DF_StdMedalGold_Points DEFAULT 50,
        [AwardIdsJson]         NVARCHAR(MAX) NULL,        -- snapshot of StandardMedalAward.Id list forming the 50 p
        [Notes]                NVARCHAR(MAX) NULL,
        [AppliedByMemberId]    INT           NOT NULL,
        [AppliedAt]            DATETIME      NULL,
        [ApprovedByMemberId]   INT           NULL,
        [ApprovedAt]           DATETIME      NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_StdMedalGold_CreatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_StdMedalGold_Member
        ON [dbo].[StandardMedalGoldApplication] (MemberId, Status);

    CREATE INDEX IX_StdMedalGold_Club
        ON [dbo].[StandardMedalGoldApplication] (ClubId, Status);
END
GO
