-- MarkenSeries: validated single-series märke evidence (SHB kap 5).
--
-- Two kinds, one table:
--   SeriesType = 'Precision'  → a "Guldserie" (5 shots, shot-by-shot; Total/Threshold/Qualifies)
--   SeriesType = 'Speed'      → a "Snabbserie" (tillämpning; Target + ClaimedLevel, no shots)
--
-- A shooter submits a series → Status 'Pending' → a board member / Skjutledare (scoped to ClubId)
-- verifies it in-app or via QR. Only Verified + Qualifying rows count toward a Guldfodring. The
-- precision part of a Guldfodring is also satisfied by qualifying series from hosted pistol.nu
-- competitions (read live from PrecisionResultEntry — not stored here).
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.MarkenSeries', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MarkenSeries] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [ClubId]               INT           NOT NULL,   -- club chosen for validation (scopes queue/QR)
        [BadgeFamily]          NVARCHAR(40)  NOT NULL CONSTRAINT DF_MarkenSeries_Family DEFAULT 'Pistolskytte',
        [SeriesType]           NVARCHAR(20)  NOT NULL,    -- 'Precision' | 'Speed'
        [Year]                 INT           NOT NULL,
        [SeriesDate]           DATETIME      NOT NULL,
        [WeaponGroup]          NVARCHAR(4)   NOT NULL,    -- 'A' | 'B' | 'C' | 'R'
        [ClaimedLevel]         NVARCHAR(20)  NOT NULL CONSTRAINT DF_MarkenSeries_Level DEFAULT 'Guld',

        -- Precision-only
        [Shots]                NVARCHAR(100) NOT NULL CONSTRAINT DF_MarkenSeries_Shots DEFAULT '[]',
        [Total]                INT           NOT NULL CONSTRAINT DF_MarkenSeries_Total DEFAULT 0,
        [Threshold]            INT           NOT NULL CONSTRAINT DF_MarkenSeries_Thr DEFAULT 0,
        [Qualifies]            BIT           NOT NULL CONSTRAINT DF_MarkenSeries_Qual DEFAULT 0,

        -- Speed-only
        [Target]               NVARCHAR(40)  NULL,

        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_MarkenSeries_Status DEFAULT 'Pending',
        [ValidatedByMemberId]  INT           NULL,
        [ValidatedDate]        DATETIME      NULL,
        [PhotoFileRef]         NVARCHAR(400) NULL,
        [Notes]                NVARCHAR(MAX) NULL,

        [EnteredByMemberId]    INT           NOT NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MarkenSeries_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MarkenSeries_UpdatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_MarkenSeries_MemberYear ON [dbo].[MarkenSeries] (MemberId, [Year]);
    CREATE INDEX IX_MarkenSeries_Queue      ON [dbo].[MarkenSeries] (ClubId, Status);
END
GO
