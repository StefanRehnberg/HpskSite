-- CompetitionRecords: Klubb- och kretsrekord för Precision, Magnumprecision och
-- Militär snabbmatch. Records are entered manually by club/regional admins.
-- One row per record entry; the IsCurrent flag identifies the active record per
-- (Level, ScopeId, Discipline, RecordType, ClassCode), and ReplacedByRecordId
-- chains the history. When a record is removed, the most recent prior row is
-- re-promoted to current.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.CompetitionRecords', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CompetitionRecords] (
        [Id]                    INT IDENTITY(1,1) PRIMARY KEY,
        [Level]                 NVARCHAR(20)  NOT NULL,    -- 'Club' | 'Region'
        [ScopeId]               NVARCHAR(50)  NOT NULL,    -- clubId (as string) or regionCode
        [Discipline]            NVARCHAR(30)  NOT NULL,    -- 'Precision' | 'MagnumPrecision' | 'Milsnabb'
        [RecordType]            NVARCHAR(20)  NOT NULL,    -- 'Individual' | 'Team'
        [ClassCode]             NVARCHAR(30)  NOT NULL,    -- 'A' | 'B' | 'C' | 'C_Dam' | 'C_Jun' | 'C_VetY' | 'C_VetA' | 'C_Vet' | 'R' | 'M1'..'M7'
        [TotalScore]            INT           NOT NULL,
        [SeriesCount]           INT           NOT NULL,
        [RecordDate]            DATETIME      NOT NULL,
        [CompetitionName]       NVARCHAR(200) NULL,
        [HolderMemberId]        INT           NULL,
        [HolderName]            NVARCHAR(200) NOT NULL,
        [TeamName]              NVARCHAR(200) NULL,
        [TeamMembersJson]       NVARCHAR(MAX) NULL,
        [Notes]                 NVARCHAR(MAX) NULL,
        [IsCurrent]             BIT           NOT NULL CONSTRAINT DF_CompetitionRecords_IsCurrent DEFAULT 1,
        [ReplacedByRecordId]    INT           NULL,
        [EnteredByMemberId]     INT           NOT NULL,
        [EnteredAt]             DATETIME      NOT NULL CONSTRAINT DF_CompetitionRecords_EnteredAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_CompetitionRecords_Scope
        ON [dbo].[CompetitionRecords] (Level, ScopeId, IsCurrent);

    CREATE INDEX IX_CompetitionRecords_Holder
        ON [dbo].[CompetitionRecords] (HolderMemberId, IsCurrent)
        WHERE HolderMemberId IS NOT NULL;

    CREATE INDEX IX_CompetitionRecords_LookupKey
        ON [dbo].[CompetitionRecords] (Level, ScopeId, Discipline, RecordType, ClassCode, IsCurrent);
END
GO
