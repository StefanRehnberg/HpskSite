-- CompetitionChampions: Klubb- och kretsmästare per år och klass.
-- Manually entered by club/regional admins. The "reigning" champion for a class is
-- whichever entry has the highest Year in the same (Level, ScopeId, Discipline,
-- ChampionType, ClassCode) group.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.CompetitionChampions', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CompetitionChampions] (
        [Id]                    INT IDENTITY(1,1) PRIMARY KEY,
        [Level]                 NVARCHAR(20)  NOT NULL,    -- 'Club' | 'Region'
        [ScopeId]               NVARCHAR(50)  NOT NULL,    -- clubId (string) or regionCode
        [Year]                  INT           NOT NULL,    -- e.g. 2026
        [Discipline]            NVARCHAR(30)  NOT NULL,    -- 'Precision' | 'MagnumPrecision' | 'Milsnabb'
        [ChampionType]          NVARCHAR(20)  NOT NULL,    -- 'Individual' | 'Team'
        [ClassCode]             NVARCHAR(30)  NOT NULL,    -- 'A' | 'B' | 'C_Dam' | 'M1' | 'R' etc — matches RecordClassRegistry
        [TotalScore]            INT           NOT NULL,
        [CompetitionName]       NVARCHAR(200) NULL,
        [CompetitionDate]       DATETIME      NULL,
        [HolderMemberId]        INT           NULL,
        [HolderName]            NVARCHAR(200) NOT NULL,
        [TeamName]              NVARCHAR(200) NULL,
        [TeamMembersJson]       NVARCHAR(MAX) NULL,
        [Notes]                 NVARCHAR(MAX) NULL,
        [EnteredByMemberId]     INT           NOT NULL,
        [EnteredAt]             DATETIME      NOT NULL CONSTRAINT DF_CompetitionChampions_EnteredAt DEFAULT GETDATE()
    );

    -- Business rule: at most one entry per scope/year/discipline/type/class.
    CREATE UNIQUE INDEX UX_CompetitionChampions_Key
        ON [dbo].[CompetitionChampions] (Level, ScopeId, Year, Discipline, ChampionType, ClassCode);

    CREATE INDEX IX_CompetitionChampions_Scope
        ON [dbo].[CompetitionChampions] (Level, ScopeId, Year DESC);

    CREATE INDEX IX_CompetitionChampions_Holder
        ON [dbo].[CompetitionChampions] (HolderMemberId)
        WHERE HolderMemberId IS NOT NULL;
END
GO
