-- MarkenStormastarEntry: championship results logged toward the Stormästarmärket (SHB 5.3) —
-- career inteckningspoäng (Tabell 2). The shooter enters scope/deltagarantal/placering; points are
-- computed at entry. A functionary validates each (same queue/QR as MarkenSeries / MarkenCompetitionResult).
-- Only Verified rows count toward the 30-point eligibility threshold. The award itself is a manual
-- club→SPSF nomination — this table accumulates and documents the merits.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.MarkenStormastarEntry', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MarkenStormastarEntry] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]             INT           NOT NULL,
        [ClubId]               INT           NOT NULL,   -- club chosen for validation (scopes queue/QR)
        [Year]                 INT           NOT NULL,
        [Scope]                NVARCHAR(20)  NOT NULL,    -- 'Krets' | 'Landsdel' | 'Svenskt'
        [Participants]         INT           NOT NULL,    -- antal deltagare i vapengruppen/klassen
        [Place]                INT           NOT NULL,    -- placering (1 = winner)
        [Points]               INT           NOT NULL,    -- inteckningspoäng (Tabell 2), computed at entry
        [Discipline]           NVARCHAR(40)  NULL,        -- optional label for the meritförteckning
        [CompetitionName]      NVARCHAR(300) NULL,
        [Notes]                NVARCHAR(MAX) NULL,
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_MSE_Status DEFAULT 'Pending',
        [ValidatedByMemberId]  INT           NULL,
        [ValidatedDate]        DATETIME      NULL,
        [ProofFileRef]         NVARCHAR(400) NULL,
        [EnteredByMemberId]    INT           NOT NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MSE_CreatedAt DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_MSE_UpdatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_MSE_Member ON [dbo].[MarkenStormastarEntry] (MemberId, Status);
    CREATE INDEX IX_MSE_Queue  ON [dbo].[MarkenStormastarEntry] (ClubId, Status);
END
GO
