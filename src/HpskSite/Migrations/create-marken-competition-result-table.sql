-- MarkenCompetitionResult: self-reported external competition results submitted as evidence toward
-- a competition-driven märke (Precision / Fält / Milsnabb / Nationell helmatch).
--
-- The shooter enters competition + total; a functionary validates it (same queue/QR as MarkenSeries).
-- Hosted pistol.nu results are NOT stored here — they're harvested live from the discipline result
-- tables. Only Verified rows count toward a märke.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.MarkenCompetitionResult', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MarkenCompetitionResult] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [ClubId]               INT           NOT NULL,   -- club chosen for validation (scopes queue/QR)
        [BadgeFamily]          NVARCHAR(40)  NOT NULL,    -- 'Precision' | 'Falt' | 'Milsnabb' | 'NationellHelmatch'
        [Year]                 INT           NOT NULL,
        [CompetitionDate]      DATETIME      NOT NULL,
        [CompetitionName]      NVARCHAR(300) NOT NULL,
        [Location]             NVARCHAR(200) NULL,
        [WeaponGroup]          NVARCHAR(4)   NOT NULL,    -- 'A' | 'B' | 'C' | 'R'
        [Dim]                  INT           NOT NULL CONSTRAINT DF_MCR_Dim DEFAULT 0,  -- series/station count; 0 = n/a
        [Total]                INT           NOT NULL,    -- points (precision-shape) or hits (Fält)
        [ReachedLevel]         NVARCHAR(20)  NULL,        -- 'Brons' | 'Silver' | 'Guld' | NULL
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_MCR_Status DEFAULT 'Pending',
        [ValidatedByMemberId]  INT           NULL,
        [ValidatedDate]        DATETIME      NULL,
        [ProofFileRef]         NVARCHAR(400) NULL,
        [Notes]                NVARCHAR(MAX) NULL,
        [EnteredByMemberId]    INT           NOT NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MCR_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MCR_UpdatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_MCR_MemberFamilyYear ON [dbo].[MarkenCompetitionResult] (MemberId, BadgeFamily, [Year]);
    CREATE INDEX IX_MCR_Queue            ON [dbo].[MarkenCompetitionResult] (ClubId, Status);
END
GO
