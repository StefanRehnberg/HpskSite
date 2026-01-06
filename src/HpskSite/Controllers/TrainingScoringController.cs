using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Shared.Models;
using HpskSite.Services;
using NPoco;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Controller for managing training score entries
    /// Members can log their own training sessions and track personal bests
    /// </summary>
    public class TrainingScoringController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly UnifiedResultsService _unifiedResultsService;
        private readonly IShooterStatisticsService _statisticsService;

        public TrainingScoringController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            UnifiedResultsService unifiedResultsService,
            IShooterStatisticsService statisticsService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _unifiedResultsService = unifiedResultsService;
            _statisticsService = statisticsService;
        }

        #region Public Endpoints (Member Access)

        /// <summary>
        /// Record a new training score (self-service for members)
        /// POST /umbraco/surface/TrainingScoring/RecordTrainingScore
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordTrainingScore([FromBody] TrainingScoreEntry entry)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in to record training scores" });
            }

            try
            {
                // Get member ID
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Set member ID and timestamps
                entry.MemberId = member.Id;
                entry.CreatedAt = DateTime.Now;
                entry.UpdatedAt = DateTime.Now;

                // Validate the entry
                if (!entry.IsValid(out string errorMessage))
                {
                    return Json(new { success = false, message = errorMessage });
                }

                // Calculate totals
                entry.CalculateTotals();

                // Save to database
                using (var db = _databaseFactory.CreateDatabase())
                {
                    db.Insert("TrainingScores", "Id", true, new
                    {
                        MemberId = entry.MemberId,
                        TrainingDate = entry.TrainingDate,
                        WeaponClass = entry.WeaponClass, // Save weapon class (A, B, C, R, P)
                        IsCompetition = entry.IsCompetition, // External competition flag
                        CompetitionPlace = entry.CompetitionPlace, // Competition placement
                        CompetitionShootingClass = entry.CompetitionShootingClass, // Competition shooting class
                        CompetitionStdMedal = entry.CompetitionStdMedal, // Competition standard medal
                        SeriesScores = entry.SerializeSeries(),
                        TotalScore = entry.TotalScore,
                        XCount = entry.XCount,
                        Notes = entry.Notes ?? string.Empty,
                        CreatedAt = entry.CreatedAt,
                        UpdatedAt = entry.UpdatedAt
                    });
                }

                // Update shooter statistics for handicap calculation
                if (!string.IsNullOrEmpty(entry.WeaponClass) && entry.SeriesCount > 0)
                {
                    await _statisticsService.UpdateAfterMatchAsync(
                        entry.MemberId,
                        entry.WeaponClass,
                        entry.SeriesCount,
                        entry.TotalScore);
                }

                return Json(new
                {
                    success = true,
                    message = entry.IsCompetition ? "Competition result recorded successfully" : "Training score recorded successfully",
                    entry = new
                    {
                        entry.MemberName,
                        entry.TrainingDate,
                        entry.WeaponClass,
                        entry.IsCompetition,
                        entry.TotalScore,
                        entry.XCount,
                        entry.SeriesCount
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error recording training score: " + ex.Message });
            }
        }

        /// <summary>
        /// Get current member's training scores
        /// GET /umbraco/surface/TrainingScoring/GetMyTrainingScores
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyTrainingScores(int? limit = null, int? skip = null)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    var sql = db.SqlContext.Sql()
                        .Select("*")
                        .From("TrainingScores")
                        .Where("MemberId = @0", member.Id)
                        .OrderByDescending("TrainingDate");

                    if (skip.HasValue)
                    {
                        sql = sql.Append($"OFFSET {skip.Value} ROWS");
                    }

                    if (limit.HasValue)
                    {
                        sql = sql.Append($"FETCH NEXT {limit.Value} ROWS ONLY");
                    }

                    var records = db.Fetch<dynamic>(sql);

                    var scores = records.Select(r => new TrainingScoreEntry
                    {
                        Id = r.Id,
                        MemberId = r.MemberId,
                        MemberName = currentMember.Name,
                        TrainingDate = r.TrainingDate,
                        WeaponClass = r.WeaponClass,
                        IsCompetition = r.IsCompetition,
                        CompetitionPlace = r.CompetitionPlace,
                        CompetitionShootingClass = r.CompetitionShootingClass,
                        CompetitionStdMedal = r.CompetitionStdMedal,
                        TotalScore = r.TotalScore,
                        XCount = r.XCount,
                        Notes = r.Notes,
                        CreatedAt = r.CreatedAt,
                        UpdatedAt = r.UpdatedAt
                    }).ToList();

                    // Deserialize series for each entry
                    foreach (var score in scores)
                    {
                        var record = records.First(r => r.Id == score.Id);
                        score.DeserializeSeries(record.SeriesScores);
                    }

                    return Json(new
                    {
                        success = true,
                        scores = scores,
                        total = records.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading training scores: " + ex.Message });
            }
        }

        /// <summary>
        /// Get all results for current member (training, competition, and official)
        /// GET /umbraco/surface/TrainingScoring/GetMyResults
        /// This replaces GetMyTrainingScores with unified data from all sources
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyResults(int? limit = null, int? skip = null)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get unified results from all sources
                var results = _unifiedResultsService.GetMemberResults(member.Id, limit, skip);

                return Json(new
                {
                    success = true,
                    results = results,
                    total = results.Count
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading results: " + ex.Message });
            }
        }

        /// <summary>
        /// Get personal bests for current member
        /// GET /umbraco/surface/TrainingScoring/GetPersonalBests
        /// Parameters:
        /// - weaponClass: Filter by weapon class (optional)
        /// - includeCompetitions: Include competition results (default: true)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPersonalBests(string? weaponClass = null, bool includeCompetitions = true)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get all training scores for this member
                    var sql = db.SqlContext.Sql()
                        .Select("*")
                        .From("TrainingScores")
                        .Where("MemberId = @0", member.Id);

                    if (!string.IsNullOrWhiteSpace(weaponClass))
                    {
                        sql = sql.Where("WeaponClass = @0", weaponClass);
                    }

                    if (!includeCompetitions)
                    {
                        sql = sql.Where("IsCompetition = 0");
                    }

                    var records = db.Fetch<dynamic>(sql);

                    // Calculate personal bests by weapon class, series count, and competition status
                    var personalBests = new List<PersonalBest>();

                    foreach (var record in records)
                    {
                        var entry = new TrainingScoreEntry
                        {
                            Id = record.Id,
                            TrainingDate = record.TrainingDate,
                            WeaponClass = record.WeaponClass,
                            IsCompetition = record.IsCompetition,
                            TotalScore = record.TotalScore,
                            XCount = record.XCount
                        };
                        entry.DeserializeSeries(record.SeriesScores);

                        // Check if this is a personal best for this class/series count/competition combo
                        var existing = personalBests.FirstOrDefault(pb =>
                            pb.WeaponClass == entry.WeaponClass &&
                            pb.SeriesCount == entry.SeriesCount &&
                            pb.IsCompetition == entry.IsCompetition);

                        if (existing == null)
                        {
                            personalBests.Add(new PersonalBest
                            {
                                MemberId = member.Id,
                                WeaponClass = entry.WeaponClass,
                                IsCompetition = entry.IsCompetition,
                                SeriesCount = entry.SeriesCount,
                                BestScore = entry.TotalScore,
                                XCount = entry.XCount,
                                AchievedDate = entry.TrainingDate,
                                TrainingScoreId = entry.Id
                            });
                        }
                        else if (entry.TotalScore > existing.BestScore ||
                                (entry.TotalScore == existing.BestScore && entry.XCount > existing.XCount))
                        {
                            existing.BestScore = entry.TotalScore;
                            existing.XCount = entry.XCount;
                            existing.AchievedDate = entry.TrainingDate;
                            existing.TrainingScoreId = entry.Id;
                        }
                    }

                    // Group by weapon class
                    var groupedBests = personalBests
                        .GroupBy(pb => pb.WeaponClass)
                        .Select(g => new PersonalBestsByClass
                        {
                            WeaponClass = g.Key,
                            Bests = g.OrderBy(pb => pb.SeriesCount).ToList()
                        })
                        .ToList();

                    return Json(new
                    {
                        success = true,
                        personalBests = groupedBests
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error calculating personal bests: " + ex.Message });
            }
        }

        /// <summary>
        /// Get dashboard statistics for current member
        /// GET /umbraco/surface/TrainingScoringController/GetDashboardStatistics
        /// Returns comprehensive statistics for the dashboard view
        /// Combines training scores, competition scores from TrainingScores table, and official competition results
        /// Provides SEPARATE statistics for training vs competition
        /// All statistics use SERIES AVERAGE (Medelresultat) for meaningful comparisons
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDashboardStatistics()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Not authenticated" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get all results from unified service (all 3 data sources)
                var allResults = _unifiedResultsService.GetMemberResults(member.Id);

                // Separate training and competition results
                var trainingResults = allResults.Where(r => r.SourceType == "Training").ToList();
                var competitionResults = allResults.Where(r => r.SourceType == "Competition" || r.SourceType == "Official").ToList();

                // Calculate TRAINING statistics
                var trainingStats = CalculateStatistics(trainingResults, "Training");

                // Calculate COMPETITION statistics
                var competitionStats = CalculateStatistics(competitionResults, "Competition");

                // Calculate COMBINED statistics for charts and overall metrics
                var totalSessions = allResults.Count;
                var totalTrainingSessions = trainingResults.Count;
                // Count total competition ENTRIES (not distinct competitions)
                // Each weapon class in same competition is a separate entry
                var totalCompetitions = competitionResults.Count;

                var overallAverage = allResults.Any() ? allResults.Average(r => r.AverageScore) : 0;

                // Calculate trend (last 30 days vs previous 30 days)
                var recentDate = DateTime.Now.AddDays(-30);
                var previousDate = DateTime.Now.AddDays(-60);

                var recentResults = allResults.Where(r => r.Date >= recentDate).ToList();
                var previousResults = allResults.Where(r => r.Date >= previousDate && r.Date < recentDate).ToList();

                var recentAverage = recentResults.Any() ? recentResults.Average(r => r.AverageScore) : 0;
                var previousAverage = previousResults.Any() ? previousResults.Average(r => r.AverageScore) : 0;

                // Calculate 30-day average breakdown by weapon class
                var recentAverageByClass = recentResults
                    .GroupBy(r => r.WeaponClass)
                    .Select(g => new
                    {
                        weaponClass = g.Key,
                        average = Math.Round(g.Average(r => r.AverageScore), 1)
                    })
                    .OrderBy(x => x.weaponClass)
                    .ToList();

                // Generate individual entry data for all results (filtered by year on frontend)
                // This allows the chart to show every training session and competition entry
                var monthlyData = allResults
                    .Select(r => new
                    {
                        date = r.Date,
                        year = r.Date.Year,
                        month = r.Date.Month,
                        day = r.Date.Day,
                        weaponClass = r.WeaponClass,
                        isCompetition = r.SourceType != "Training",
                        averageScore = r.AverageScore,
                        totalScore = r.TotalScore,
                        seriesCount = r.SeriesCount,
                        competitionName = r.CompetitionName,
                        id = r.Id
                    })
                    .OrderBy(x => x.date)
                    .ToList();

                // Generate weapon class distribution
                var weaponClassData = allResults
                    .GroupBy(r => new { r.WeaponClass, IsCompetition = r.SourceType != "Training" })
                    .Select(g => new
                    {
                        weaponClass = g.Key.WeaponClass,
                        isCompetition = g.Key.IsCompetition,
                        averageScore = g.Average(r => r.AverageScore),
                        sessionCount = g.Count()
                    })
                    .OrderBy(x => x.weaponClass)
                    .ToList();

                // Calculate combined personal bests (for backward compatibility)
                var personalBestsByClass = allResults
                    .GroupBy(r => r.WeaponClass)
                    .ToDictionary(g => g.Key, g => Math.Round(g.Max(r => r.AverageScore), 1));

                var weaponClasses = new[] { "A", "B", "C", "R", "M", "L" };
                var personalBests = weaponClasses.Select(wc => new
                {
                    weaponClass = wc,
                    bestAverage = personalBestsByClass.ContainsKey(wc) ? (double?)personalBestsByClass[wc] : null
                }).ToList();

                // Calculate personal bests by series count (total score grouped by weapon class, series count, and training/competition)
                var standardSeriesCounts = new[] { 6, 7, 10 };
                var lWeaponSeriesCounts = new[] { 6, 8, 12 };

                var personalBestsBySeriesCount = allResults
                    .Where(r => {
                        var seriesCounts = r.WeaponClass == "L" ? lWeaponSeriesCounts : standardSeriesCounts;
                        return seriesCounts.Contains(r.SeriesCount);
                    })
                    .GroupBy(r => new { r.WeaponClass, r.SeriesCount, IsCompetition = r.SourceType != "Training" })
                    .Select(g => new
                    {
                        weaponClass = g.Key.WeaponClass,
                        seriesCount = g.Key.SeriesCount,
                        isCompetition = g.Key.IsCompetition,
                        bestTotalScore = g.Max(r => r.TotalScore)
                    })
                    .OrderBy(x => x.weaponClass)
                    .ThenBy(x => x.seriesCount)
                    .ToList();

                // Calculate available years
                var allDates = allResults.Select(r => r.Date.Year).Distinct().OrderByDescending(y => y).ToList();
                var availableYears = allDates.Any() ? allDates : new List<int> { DateTime.Now.Year };

                // Calculate medal statistics for all available years
                var medalStatsByYear = new Dictionary<int, object>();
                foreach (var year in availableYears)
                {
                    medalStatsByYear[year] = GetMemberMedalStats(member.Id, year);
                }
                // Default medalStats is for current year (or most recent year with data)
                var currentYear = DateTime.Now.Year;
                var medalStats = medalStatsByYear.ContainsKey(currentYear)
                    ? medalStatsByYear[currentYear]
                    : (availableYears.Any() ? medalStatsByYear[availableYears.First()] : new { silverCount = 0, bronzeCount = 0, totalPoints = 0 });

                var stats = new
                {
                    // Overall metrics
                    totalSessions,
                    totalTrainingSessions,
                    totalCompetitions,
                    overallAverage = Math.Round(overallAverage, 1),
                    recentAverage = Math.Round(recentAverage, 1),
                    recentAverageByClass,
                    previousAverage = Math.Round(previousAverage, 1),

                    // Chart data
                    monthlyData,
                    weaponClassData,
                    availableYears,

                    // Combined personal bests (for backward compatibility)
                    personalBests,

                    // Personal bests by series count (total scores)
                    personalBestsBySeriesCount,

                    // Separate statistics for training vs competition
                    trainingStats,
                    competitionStats,

                    // Medal statistics
                    medalStats,
                    medalStatsByYear
                };

                return Json(new { success = true, data = stats });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading dashboard statistics: " + ex.Message });
            }
        }

        /// <summary>
        /// Calculate statistics for a specific result type (training or competition)
        /// </summary>
        private object CalculateStatistics(List<UnifiedResultEntry> results, string type)
        {
            if (!results.Any())
            {
                var emptyStats = new
                {
                    totalSessions = 0,
                    overallAverage = 0.0,
                    recentAverage = 0.0,
                    recentAverageByClass = new List<object>(),
                    personalBests = new[] { "A", "B", "C", "R", "L" }.Select(wc => new
                    {
                        weaponClass = wc,
                        bestAverage = (double?)null
                    }).ToList()
                };
                return emptyStats;
            }

            var totalSessions = results.Count;
            var overallAverage = results.Average(r => r.AverageScore);

            // Calculate recent average (last 30 days)
            var recentDate = DateTime.Now.AddDays(-30);
            var recentResults = results.Where(r => r.Date >= recentDate).ToList();
            var recentAverage = recentResults.Any() ? recentResults.Average(r => r.AverageScore) : 0;

            // Recent average by class
            var recentAverageByClass = recentResults
                .GroupBy(r => r.WeaponClass)
                .Select(g => new
                {
                    weaponClass = g.Key,
                    average = Math.Round(g.Average(r => r.AverageScore), 1)
                })
                .OrderBy(x => x.weaponClass)
                .ToList();

            // Personal bests by class
            var personalBestsByClass = results
                .GroupBy(r => r.WeaponClass)
                .ToDictionary(g => g.Key, g => Math.Round(g.Max(r => r.AverageScore), 1));

            var weaponClasses = new[] { "A", "B", "C", "R", "L" };
            var personalBests = weaponClasses.Select(wc => new
            {
                weaponClass = wc,
                bestAverage = personalBestsByClass.ContainsKey(wc) ? (double?)personalBestsByClass[wc] : null
            }).ToList();

            return new
            {
                totalSessions,
                overallAverage = Math.Round(overallAverage, 1),
                recentAverage = Math.Round(recentAverage, 1),
                recentAverageByClass,
                personalBests
            };
        }

        /// <summary>
        /// Update an existing training score
        /// PUT /umbraco/surface/TrainingScoring/UpdateTrainingScore
        /// </summary>
        [HttpPut]
        public async Task<IActionResult> UpdateTrainingScore([FromBody] TrainingScoreEntry entry)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Validate the entry
                if (!entry.IsValid(out string errorMessage))
                {
                    return Json(new { success = false, message = errorMessage });
                }

                // Calculate totals
                entry.CalculateTotals();

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Verify ownership
                    var existing = db.Single<dynamic>("SELECT * FROM TrainingScores WHERE Id = @0", entry.Id);
                    if (existing == null)
                    {
                        return Json(new { success = false, message = "Training score not found" });
                    }

                    if (existing.MemberId != member.Id)
                    {
                        return Json(new { success = false, message = "You can only edit your own training scores" });
                    }

                    // Track weapon classes for statistics recalculation
                    string? oldWeaponClass = existing.WeaponClass as string;
                    string? newWeaponClass = entry.WeaponClass;

                    // Update the record
                    db.Execute(
                        @"UPDATE TrainingScores
                          SET TrainingDate = @0, WeaponClass = @1, IsCompetition = @2,
                              CompetitionPlace = @3, CompetitionShootingClass = @4, CompetitionStdMedal = @5,
                              SeriesScores = @6, TotalScore = @7, XCount = @8, Notes = @9, UpdatedAt = @10
                          WHERE Id = @11",
                        entry.TrainingDate,
                        entry.WeaponClass,
                        entry.IsCompetition,
                        entry.CompetitionPlace,
                        entry.CompetitionShootingClass,
                        entry.CompetitionStdMedal,
                        entry.SerializeSeries(),
                        entry.TotalScore,
                        entry.XCount,
                        entry.Notes ?? string.Empty,
                        DateTime.Now,
                        entry.Id);

                    // Recalculate statistics for affected weapon classes
                    if (!string.IsNullOrEmpty(newWeaponClass))
                    {
                        await _statisticsService.RecalculateFromHistoryAsync(member.Id, newWeaponClass);
                    }
                    if (!string.IsNullOrEmpty(oldWeaponClass) && oldWeaponClass != newWeaponClass)
                    {
                        await _statisticsService.RecalculateFromHistoryAsync(member.Id, oldWeaponClass);
                    }

                    return Json(new { success = true, message = "Training score updated successfully" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error updating training score: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a training score
        /// DELETE /umbraco/surface/TrainingScoring/DeleteTrainingScore
        /// </summary>
        [HttpDelete]
        public async Task<IActionResult> DeleteTrainingScore(int id)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "You must be logged in" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Verify ownership
                    var existing = db.SingleOrDefault<dynamic>("SELECT * FROM TrainingScores WHERE Id = @0", id);
                    if (existing == null)
                    {
                        return Json(new { success = false, message = "Training score not found" });
                    }

                    if (existing.MemberId != member.Id)
                    {
                        return Json(new { success = false, message = "You can only delete your own training scores" });
                    }

                    // Track weapon class for statistics recalculation
                    string? weaponClass = existing.WeaponClass as string;

                    // Delete the record
                    db.Execute("DELETE FROM TrainingScores WHERE Id = @0", id);

                    // Recalculate statistics for the affected weapon class
                    if (!string.IsNullOrEmpty(weaponClass))
                    {
                        await _statisticsService.RecalculateFromHistoryAsync(member.Id, weaponClass);
                    }

                    return Json(new { success = true, message = "Training score deleted successfully" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting training score: " + ex.Message });
            }
        }

        #endregion

        #region Medal Statistics

        /// <summary>
        /// Get medal statistics for a member for a specific year.
        /// Sources: TrainingScores (external competitions) and Competition Results documents.
        /// </summary>
        private object GetMemberMedalStats(int memberId, int year)
        {
            int silverCount = 0;
            int bronzeCount = 0;

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Source 1: TrainingScores table - external competitions with medals
                    var trainingMedals = db.Fetch<dynamic>(@"
                        SELECT CompetitionStdMedal, COUNT(*) as MedalCount
                        FROM TrainingScores
                        WHERE MemberId = @0
                          AND IsCompetition = 1
                          AND CompetitionStdMedal IS NOT NULL
                          AND CompetitionStdMedal != ''
                          AND YEAR(TrainingDate) = @1
                        GROUP BY CompetitionStdMedal",
                        memberId, year);

                    foreach (var medal in trainingMedals)
                    {
                        string medalType = medal.CompetitionStdMedal?.ToString()?.ToUpper() ?? "";
                        int count = (int)(medal.MedalCount ?? 0);

                        if (medalType == "S")
                            silverCount += count;
                        else if (medalType == "B")
                            bronzeCount += count;
                    }

                    // Source 2: Competition Results - get competitions the member participated in
                    var competitionIds = db.Fetch<int>(@"
                        SELECT DISTINCT CompetitionId
                        FROM PrecisionResultEntry
                        WHERE MemberId = @0",
                        memberId);

                    // Batch load competitions for year filtering
                    var competitionIdsForYear = GetCompetitionIdsForYear(competitionIds, year);

                    foreach (var competitionId in competitionIdsForYear)
                    {
                        // Find the specific result page named "Resultat" (official results)
                        var resultPage = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                            .FirstOrDefault(n => n.ContentType.Alias == "competitionResult" && n.Name == "Resultat");

                        if (resultPage == null) continue;

                        var resultDataJson = resultPage.GetValue<string>("resultData");
                        if (string.IsNullOrEmpty(resultDataJson)) continue;

                        try
                        {
                            var finalResults = Newtonsoft.Json.JsonConvert.DeserializeObject<HpskSite.CompetitionTypes.Precision.Models.PrecisionFinalResults>(resultDataJson);
                            if (finalResults?.ClassGroups == null) continue;

                            foreach (var classGroup in finalResults.ClassGroups)
                            {
                                var shooter = classGroup.Shooters?.FirstOrDefault(s => s.MemberId == memberId);
                                if (shooter != null && !string.IsNullOrEmpty(shooter.StandardMedal))
                                {
                                    if (shooter.StandardMedal.ToUpper() == "S")
                                        silverCount++;
                                    else if (shooter.StandardMedal.ToUpper() == "B")
                                        bronzeCount++;
                                }
                            }
                        }
                        catch
                        {
                            // Skip invalid JSON
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MedalStats] ERROR: {ex.Message}");
            }

            // Calculate total points: Silver = 2, Bronze = 1
            int totalPoints = (silverCount * 2) + (bronzeCount * 1);

            return new
            {
                silverCount,
                bronzeCount,
                totalPoints
            };
        }

        /// <summary>
        /// Filter competition IDs by year (optimization - reduces content service calls)
        /// </summary>
        private List<int> GetCompetitionIdsForYear(List<int> competitionIds, int year)
        {
            var result = new List<int>();

            if (!competitionIds.Any()) return result;

            var competitions = _contentService.GetByIds(competitionIds);

            foreach (var competition in competitions)
            {
                var competitionDate = competition.GetValue<DateTime>("competitionDate");
                if (competitionDate.Year == year)
                {
                    result.Add(competition.Id);
                }
            }

            return result;
        }

        #endregion
    }
}
