-- Migration: Backfill ShooterStatistics from historical data with Rolling Window
-- Date: 2025-12-19
-- Purpose: Populate ShooterStatistics table with aggregated RAW data from existing results
--          Using a rolling window of the most recent N matches per member/weapon class

-- NOTE: This script assumes:
-- 1. ShooterStatistics table already exists (run CreateShooterStatisticsTable.sql first)
-- 2. TrainingScores table has: MemberId, SeriesScores (JSON), TotalScore, TrainingMatchId, TrainingDate
-- 3. PrecisionResultEntry table has: MemberId, CompetitionId, SeriesNumber, ShootingClass, Shots (JSON)

-- CONFIGURATION: Rolling window size (matches appsettings.json HandicapSettings.RollingWindowMatchCount)
DECLARE @RollingWindowMatchCount INT = 10;

-- Clear existing data (start fresh)
DELETE FROM ShooterStatistics WHERE Discipline = 'Precision';
PRINT 'Cleared existing Precision statistics';
GO

-- =============================================
-- Rolling Window Backfill
-- =============================================
-- Combines all match sources, ranks by date per member/weapon, takes top N

DECLARE @RollingWindowMatchCount INT = 10;

;WITH AllMatches AS (
    -- Training matches
    SELECT
        ts.MemberId,
        tm.WeaponClass,
        'TrainingMatch' AS Source,
        ts.TrainingMatchId AS MatchId,
        tm.CompletedDate AS MatchDate,
        CASE
            WHEN ts.SeriesScores IS NOT NULL AND ISJSON(ts.SeriesScores) = 1
            THEN (SELECT COUNT(*) FROM OPENJSON(ts.SeriesScores))
            ELSE 0
        END AS SeriesCount,
        ts.TotalScore AS TotalPoints
    FROM TrainingScores ts
    INNER JOIN TrainingMatches tm ON ts.TrainingMatchId = tm.Id
    WHERE ts.TrainingMatchId IS NOT NULL
      AND tm.WeaponClass IN ('A', 'B', 'C', 'R', 'M', 'L')
      AND tm.Status = 'Completed'
      AND ts.SeriesScores IS NOT NULL
      AND ISJSON(ts.SeriesScores) = 1

    UNION ALL

    -- Self-entered training
    SELECT
        ts.MemberId,
        ts.WeaponClass,
        'SelfEntered' AS Source,
        ts.Id AS MatchId,
        ts.TrainingDate AS MatchDate,
        CASE
            WHEN ts.SeriesScores IS NOT NULL AND ISJSON(ts.SeriesScores) = 1
            THEN CASE
                WHEN JSON_VALUE(ts.SeriesScores, '$[0].seriesCount') IS NOT NULL
                THEN CAST(JSON_VALUE(ts.SeriesScores, '$[0].seriesCount') AS INT)
                ELSE (SELECT COUNT(*) FROM OPENJSON(ts.SeriesScores))
            END
            ELSE 0
        END AS SeriesCount,
        ts.TotalScore AS TotalPoints
    FROM TrainingScores ts
    WHERE ts.TrainingMatchId IS NULL
      AND ts.WeaponClass IN ('A', 'B', 'C', 'R', 'M', 'L')
      AND ts.SeriesScores IS NOT NULL
      AND ISJSON(ts.SeriesScores) = 1

    UNION ALL

    -- Competitions: first calculate per-series scores using CROSS APPLY, then aggregate
    SELECT
        css.MemberId,
        css.WeaponClass,
        'Competition' AS Source,
        css.CompetitionId AS MatchId,
        MIN(css.EnteredAt) AS MatchDate,
        COUNT(*) AS SeriesCount,
        SUM(css.SeriesTotal) AS TotalPoints
    FROM (
        SELECT
            pre.MemberId,
            LEFT(pre.ShootingClass, 1) AS WeaponClass,
            pre.CompetitionId,
            pre.EnteredAt,
            ShotScores.SeriesTotal
        FROM PrecisionResultEntry pre
        CROSS APPLY (
            SELECT SUM(
                CASE
                    WHEN UPPER(value) = 'X' THEN 10
                    WHEN TRY_CAST(value AS INT) IS NOT NULL THEN CAST(value AS INT)
                    ELSE 0
                END
            ) AS SeriesTotal
            FROM OPENJSON(pre.Shots)
        ) AS ShotScores
        WHERE pre.MemberId IS NOT NULL
          AND pre.ShootingClass IS NOT NULL
          AND LEN(pre.ShootingClass) > 0
          AND LEFT(pre.ShootingClass, 1) IN ('A', 'B', 'C', 'R', 'M', 'L')
          AND pre.Shots IS NOT NULL
          AND ISJSON(pre.Shots) = 1
    ) css
    GROUP BY css.MemberId, css.WeaponClass, css.CompetitionId
),
RankedMatches AS (
    -- Rank matches by date per member/weapon class (most recent first)
    SELECT
        MemberId,
        WeaponClass,
        Source,
        MatchId,
        MatchDate,
        SeriesCount,
        TotalPoints,
        ROW_NUMBER() OVER (PARTITION BY MemberId, WeaponClass ORDER BY MatchDate DESC) AS MatchRank
    FROM AllMatches
),
RecentMatches AS (
    -- Only include the most recent N matches per member/weapon class
    SELECT *
    FROM RankedMatches
    WHERE MatchRank <= @RollingWindowMatchCount
),
Aggregated AS (
    -- Aggregate the recent matches
    SELECT
        MemberId,
        WeaponClass,
        COUNT(*) AS CompletedMatches,
        SUM(SeriesCount) AS TotalSeriesCount,
        SUM(TotalPoints) AS TotalSeriesPoints
    FROM RecentMatches
    GROUP BY MemberId, WeaponClass
)

-- Insert into ShooterStatistics
INSERT INTO ShooterStatistics (MemberId, Discipline, WeaponClass, CompletedMatches, TotalSeriesCount, TotalSeriesPoints, LastCalculated)
SELECT
    MemberId,
    'Precision' AS Discipline,
    WeaponClass,
    CompletedMatches,
    TotalSeriesCount,
    TotalSeriesPoints,
    GETDATE() AS LastCalculated
FROM Aggregated
WHERE CompletedMatches > 0;

PRINT 'Backfilled ShooterStatistics with rolling window (most recent ' + CAST(@RollingWindowMatchCount AS VARCHAR) + ' matches per member/weapon)';
GO

-- =============================================
-- Display Summary
-- =============================================
SELECT 'ShooterStatistics Summary by WeaponClass (Rolling Window):' AS Info;
SELECT
    WeaponClass,
    COUNT(*) AS ShooterCount,
    SUM(CompletedMatches) AS TotalMatches,
    AVG(CompletedMatches) AS AvgMatchesPerShooter,
    SUM(TotalSeriesCount) AS TotalSeries,
    CAST(AVG(AveragePerSeries) AS DECIMAL(5,2)) AS AvgSeriesScore
FROM ShooterStatistics
WHERE Discipline = 'Precision'
GROUP BY WeaponClass
ORDER BY WeaponClass;

-- Display sample data
SELECT 'Sample data (top 10 shooters by series count):' AS Info;
SELECT TOP 10
    MemberId,
    WeaponClass,
    CompletedMatches,
    TotalSeriesCount,
    CAST(TotalSeriesPoints AS INT) AS TotalSeriesPoints,
    AveragePerSeries,
    LastCalculated
FROM ShooterStatistics
WHERE Discipline = 'Precision'
ORDER BY TotalSeriesCount DESC;

-- Sanity check: Average should be between 0 and 50 (max per series)
SELECT 'Sanity Check - Shooters with unusual averages (should be 0-50):' AS Info;
SELECT
    MemberId,
    WeaponClass,
    CompletedMatches,
    TotalSeriesCount,
    CAST(TotalSeriesPoints AS INT) AS TotalSeriesPoints,
    AveragePerSeries
FROM ShooterStatistics
WHERE Discipline = 'Precision'
  AND (AveragePerSeries < 0 OR AveragePerSeries > 50)
ORDER BY AveragePerSeries DESC;
