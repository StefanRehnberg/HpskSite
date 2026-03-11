IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('CompetitionTeam') AND name = 'IsRelay')
BEGIN
    ALTER TABLE [CompetitionTeam] ADD [IsRelay] BIT NOT NULL DEFAULT 0;
END
