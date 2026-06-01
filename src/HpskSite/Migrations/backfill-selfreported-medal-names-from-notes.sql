-- Backfill missing competition names on self-reported Standard medals.
--
-- Shooters type the competition name into the result's description ("Beskrivning"),
-- which lands in TrainingScores.Notes and is shown as the "Namn" column on Min sida.
-- The dedicated "Tävlingens namn" field (StandardMedalAward.CompetitionName) was usually
-- left blank, so the club admin's verify modal showed "–" even though the shooter sees a
-- name on Min sida. Going forward SyncSelfReportedMedalAsync falls back to the description;
-- this script repairs the awards created before that fix.
--
-- Only touches self-reported awards linked to a TrainingScores row that still have no name.
-- Idempotent. Run manually in SSMS after deploying the code change.

UPDATE a
SET a.CompetitionName = LTRIM(RTRIM(ts.Notes)),
    a.UpdatedAt = GETDATE()
FROM [dbo].[StandardMedalAward] a
JOIN [dbo].[TrainingScores] ts ON ts.Id = a.TrainingScoreId
WHERE a.Source = 'SelfReported'
  AND (a.CompetitionName IS NULL OR LTRIM(RTRIM(a.CompetitionName)) = '')
  AND ts.Notes IS NOT NULL
  AND LTRIM(RTRIM(ts.Notes)) <> '';
GO
