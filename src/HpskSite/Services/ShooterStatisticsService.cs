using HpskSite.Models;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Interface for shooter statistics service.
    /// Manages RAW performance statistics for handicap calculation.
    /// </summary>
    public interface IShooterStatisticsService
    {
        /// <summary>
        /// Get statistics for a shooter in a specific weapon class.
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <param name="weaponClass">Weapon class (A, B, C, etc.)</param>
        /// <returns>Statistics or null if none exist</returns>
        Task<ShooterStatistics?> GetStatisticsAsync(int memberId, string weaponClass);

        /// <summary>
        /// Update statistics after a match is completed.
        /// With rolling window, this triggers a full recalculation from history.
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <param name="weaponClass">Weapon class</param>
        /// <param name="seriesCount">Number of series in the match (ignored with rolling window)</param>
        /// <param name="rawTotalPoints">Total RAW points scored (ignored with rolling window)</param>
        Task UpdateAfterMatchAsync(int memberId, string weaponClass, int seriesCount, decimal rawTotalPoints);

        /// <summary>
        /// Recalculate all statistics from historical data using rolling window.
        /// Only the most recent N matches are included.
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <param name="weaponClass">Weapon class</param>
        Task RecalculateFromHistoryAsync(int memberId, string weaponClass);

        /// <summary>
        /// Get all statistics for a member across all weapon classes.
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <returns>List of statistics for each weapon class</returns>
        Task<List<ShooterStatistics>> GetAllStatisticsAsync(int memberId);
    }

    /// <summary>
    /// Service for managing shooter statistics.
    /// Statistics are based on RAW scores only - never handicap-adjusted.
    /// Uses a rolling window to only include the most recent matches.
    /// </summary>
    public class ShooterStatisticsService : IShooterStatisticsService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<ShooterStatisticsService> _logger;
        private readonly HandicapSettings _settings;

        private const string DISCIPLINE = "Precision";

        public ShooterStatisticsService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<ShooterStatisticsService> logger,
            IOptions<HandicapSettings> settings)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
            _settings = settings.Value;
        }

        /// <summary>
        /// Get statistics for a shooter in a specific weapon class.
        /// </summary>
        public async Task<ShooterStatistics?> GetStatisticsAsync(int memberId, string weaponClass)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var db = _databaseFactory.CreateDatabase();

                    var result = db.SingleOrDefault<dynamic>(
                        @"SELECT Id, MemberId, Discipline, WeaponClass, CompletedMatches,
                          TotalSeriesCount, TotalSeriesPoints, AveragePerSeries, LastCalculated
                          FROM ShooterStatistics
                          WHERE MemberId = @0 AND WeaponClass = @1 AND Discipline = @2",
                        memberId, weaponClass, DISCIPLINE);

                    if (result == null)
                    {
                        return null;
                    }

                    return new ShooterStatistics
                    {
                        Id = (int)result.Id,
                        MemberId = (int)result.MemberId,
                        Discipline = (string)result.Discipline,
                        WeaponClass = (string)result.WeaponClass,
                        CompletedMatches = (int)result.CompletedMatches,
                        TotalSeriesCount = (int)result.TotalSeriesCount,
                        TotalSeriesPoints = (decimal)result.TotalSeriesPoints,
                        AveragePerSeries = (decimal)result.AveragePerSeries,
                        LastCalculated = (DateTime)result.LastCalculated
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting statistics for member {MemberId}, weapon {WeaponClass}",
                        memberId, weaponClass);
                    return null;
                }
            });
        }

        /// <summary>
        /// Get all statistics for a member across all weapon classes.
        /// </summary>
        public async Task<List<ShooterStatistics>> GetAllStatisticsAsync(int memberId)
        {
            return await Task.Run(() =>
            {
                var stats = new List<ShooterStatistics>();

                try
                {
                    using var db = _databaseFactory.CreateDatabase();

                    var results = db.Fetch<dynamic>(
                        @"SELECT Id, MemberId, Discipline, WeaponClass, CompletedMatches,
                          TotalSeriesCount, TotalSeriesPoints, AveragePerSeries, LastCalculated
                          FROM ShooterStatistics
                          WHERE MemberId = @0 AND Discipline = @1
                          ORDER BY WeaponClass",
                        memberId, DISCIPLINE);

                    foreach (var result in results)
                    {
                        stats.Add(new ShooterStatistics
                        {
                            Id = (int)result.Id,
                            MemberId = (int)result.MemberId,
                            Discipline = (string)result.Discipline,
                            WeaponClass = (string)result.WeaponClass,
                            CompletedMatches = (int)result.CompletedMatches,
                            TotalSeriesCount = (int)result.TotalSeriesCount,
                            TotalSeriesPoints = (decimal)result.TotalSeriesPoints,
                            AveragePerSeries = (decimal)result.AveragePerSeries,
                            LastCalculated = (DateTime)result.LastCalculated
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting all statistics for member {MemberId}", memberId);
                }

                return stats;
            });
        }

        /// <summary>
        /// Update statistics after a match is completed.
        /// With rolling window, this triggers a full recalculation from history.
        /// </summary>
        public async Task UpdateAfterMatchAsync(int memberId, string weaponClass, int seriesCount, decimal rawTotalPoints)
        {
            // With rolling window, we must recalculate from source data
            // to ensure only the most recent N matches are included
            await RecalculateFromHistoryAsync(memberId, weaponClass);
        }

        /// <summary>
        /// Recalculate all statistics from historical data using rolling window.
        /// Only the most recent N matches (from all sources combined) are included.
        /// </summary>
        public async Task RecalculateFromHistoryAsync(int memberId, string weaponClass)
        {
            await Task.Run(() =>
            {
                try
                {
                    using var db = _databaseFactory.CreateDatabase();

                    int windowSize = _settings.RollingWindowMatchCount;

                    // Use a CTE to combine all match sources, then take the most recent N
                    // This ensures we use the N most recent matches regardless of source
                    var result = db.SingleOrDefault<dynamic>(
                        @";WITH AllMatches AS (
                            -- Training matches
                            SELECT
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
                            WHERE ts.MemberId = @0
                              AND tm.WeaponClass = @1
                              AND tm.Status = 'Completed'
                              AND ts.TrainingMatchId IS NOT NULL

                            UNION ALL

                            -- Self-entered training
                            SELECT
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
                            WHERE ts.MemberId = @0
                              AND ts.WeaponClass = @1
                              AND ts.TrainingMatchId IS NULL
                              AND ts.SeriesScores IS NOT NULL
                              AND ISJSON(ts.SeriesScores) = 1
                        ),
                        -- Competition results: first calculate per-series scores using CROSS APPLY
                        CompetitionSeriesScores AS (
                            SELECT
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
                            WHERE pre.MemberId = @0
                              AND LEFT(pre.ShootingClass, 1) = @1
                              AND pre.Shots IS NOT NULL
                              AND ISJSON(pre.Shots) = 1
                        ),
                        -- Then aggregate per competition
                        CompetitionMatches AS (
                            SELECT
                                'Competition' AS Source,
                                CompetitionId AS MatchId,
                                MIN(EnteredAt) AS MatchDate,
                                COUNT(*) AS SeriesCount,
                                SUM(SeriesTotal) AS TotalPoints
                            FROM CompetitionSeriesScores
                            GROUP BY CompetitionId
                        ),
                        AllMatchesCombined AS (
                            SELECT * FROM AllMatches
                            UNION ALL
                            SELECT * FROM CompetitionMatches
                        ),
                        RecentMatches AS (
                            SELECT TOP (@2) *
                            FROM AllMatchesCombined
                            ORDER BY MatchDate DESC
                        )
                        SELECT
                            COUNT(*) AS MatchCount,
                            COALESCE(SUM(SeriesCount), 0) AS SeriesCount,
                            COALESCE(SUM(TotalPoints), 0) AS TotalPoints
                        FROM RecentMatches",
                        memberId, weaponClass, windowSize);

                    int totalMatches = result?.MatchCount ?? 0;
                    int totalSeries = result?.SeriesCount ?? 0;
                    decimal totalPoints = result?.TotalPoints ?? 0m;

                    // Update or insert statistics
                    var existing = db.SingleOrDefault<dynamic>(
                        @"SELECT Id FROM ShooterStatistics
                          WHERE MemberId = @0 AND WeaponClass = @1 AND Discipline = @2",
                        memberId, weaponClass, DISCIPLINE);

                    if (existing != null)
                    {
                        db.Execute(
                            @"UPDATE ShooterStatistics
                              SET CompletedMatches = @0,
                                  TotalSeriesCount = @1,
                                  TotalSeriesPoints = @2,
                                  LastCalculated = @3
                              WHERE Id = @4",
                            totalMatches, totalSeries, totalPoints, DateTime.Now, (int)existing.Id);
                    }
                    else if (totalMatches > 0)
                    {
                        db.Insert("ShooterStatistics", "Id", true, new
                        {
                            MemberId = memberId,
                            Discipline = DISCIPLINE,
                            WeaponClass = weaponClass,
                            CompletedMatches = totalMatches,
                            TotalSeriesCount = totalSeries,
                            TotalSeriesPoints = totalPoints,
                            LastCalculated = DateTime.Now
                        });
                    }

                    _logger.LogInformation(
                        "Recalculated statistics for member {MemberId} ({WeaponClass}): {Matches} matches (window: {Window}), {Series} series, {Points} points",
                        memberId, weaponClass, totalMatches, windowSize, totalSeries, totalPoints);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error recalculating statistics for member {MemberId}, weapon {WeaponClass}",
                        memberId, weaponClass);
                    throw;
                }
            });
        }
    }
}
