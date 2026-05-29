-- Backfill the Standardmedalj ledger from existing self-entered competition results.
--
-- Members have, for some time, been able to flag a Silver/Brons standard medal on an
-- external competition result in "Min sida" (TrainingScores.CompetitionStdMedal). Those
-- pre-date the ledger, so seed StandardMedalAward from them as SelfReported / Reported
-- (Attestation — no proof file was ever captured for these). Idempotent: the NOT EXISTS
-- guard plus the UX_StdMedalAward_TrainingScore unique index prevent duplicates on re-run.
--
-- Run AFTER create-standard-medal-tables.sql, manually in SSMS.

INSERT INTO [dbo].[StandardMedalAward]
    (MemberId, [Year], Discipline, MedalType, Points, Source,
     CompetitionDate, ShootingClass, ProofType, Status,
     TrainingScoreId, EnteredByMemberId, CreatedAt, UpdatedAt)
SELECT
    ts.MemberId,
    YEAR(ts.TrainingDate),
    ISNULL(NULLIF(LTRIM(RTRIM(ts.Discipline)), ''), 'Precision'),
    ts.CompetitionStdMedal,
    CASE ts.CompetitionStdMedal WHEN 'S' THEN 2 WHEN 'B' THEN 1 ELSE 0 END,
    'SelfReported',
    ts.TrainingDate,
    ts.CompetitionShootingClass,
    'Attestation',
    'Reported',
    ts.Id,
    ts.MemberId,
    GETDATE(),
    GETDATE()
FROM [dbo].[TrainingScores] ts
WHERE ts.IsCompetition = 1
  AND ts.CompetitionStdMedal IN ('S', 'B')
  AND NOT EXISTS (
        SELECT 1 FROM [dbo].[StandardMedalAward] a
        WHERE a.TrainingScoreId = ts.Id
  );
GO
