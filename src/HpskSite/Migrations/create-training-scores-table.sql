-- TrainingScores: per-member training session log (self-entered scores).
-- Replaces TrainingScoresMigrationComposer + CreateTrainingScoresTable.cs.
--
-- Note: subsequent additions to this table (Discipline, IsCompetition, WeaponClass)
-- live in the per-feature .sql files in this directory. Run them in order on a fresh DB.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.TrainingScores', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TrainingScores] (
        [Id]            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TrainingScores PRIMARY KEY,
        [MemberId]      INT           NOT NULL,
        [TrainingDate]  DATETIME      NOT NULL,
        [ShootingClass] NVARCHAR(50)  NOT NULL,
        [SeriesScores]  NVARCHAR(MAX) NOT NULL,    -- JSON array of per-series scores
        [TotalScore]    INT           NOT NULL,
        [XCount]        INT           NOT NULL CONSTRAINT DF_TrainingScores_XCount DEFAULT 0,
        [Notes]         NVARCHAR(1000) NULL,
        [CreatedAt]     DATETIME      NOT NULL,
        [UpdatedAt]     DATETIME      NOT NULL
    );

    CREATE INDEX IX_TrainingScores_MemberId      ON [dbo].[TrainingScores] (MemberId);
    CREATE INDEX IX_TrainingScores_TrainingDate  ON [dbo].[TrainingScores] (TrainingDate);
    CREATE INDEX IX_TrainingScores_ShootingClass ON [dbo].[TrainingScores] (ShootingClass);
END
GO
