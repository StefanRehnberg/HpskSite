-- Add MaxSeriesCount column to TrainingMatches.
-- Replaces AddMaxSeriesCountToTrainingMatchesComposer + AddMaxSeriesCountToTrainingMatches.cs.
-- Run manually in SSMS against the Umbraco database.

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrainingMatches')
   AND NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'TrainingMatches') AND name = 'MaxSeriesCount')
BEGIN
    ALTER TABLE [dbo].[TrainingMatches] ADD [MaxSeriesCount] INT NULL;
END
GO
