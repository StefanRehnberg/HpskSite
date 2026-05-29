using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Shared.Models;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using NPoco;
using System.Text.Json;

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
        private readonly FaltskytteStatsService _faltskytteStatsService;
        private readonly StandardMedalLedgerService _medalLedger;
        private readonly StandardMedalProofStorage _proofStorage;

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
            IShooterStatisticsService statisticsService,
            FaltskytteStatsService faltskytteStatsService,
            StandardMedalLedgerService medalLedger,
            StandardMedalProofStorage proofStorage)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _unifiedResultsService = unifiedResultsService;
            _statisticsService = statisticsService;
            _faltskytteStatsService = faltskytteStatsService;
            _medalLedger = medalLedger;
            _proofStorage = proofStorage;
        }

        /// <summary>
        /// Keep the Standardmedalj ledger in sync with a self-entered competition result.
        /// Creates / updates / removes the linked StandardMedalAward as the medal on the
        /// TrainingScores row changes. Awards already consumed by a Guldmedalj application are
        /// left untouched (their points are locked into a submitted application).
        /// </summary>
        private async Task SyncSelfReportedMedalAsync(int trainingScoreId, TrainingScoreEntry entry)
        {
            var existing = await _medalLedger.GetByTrainingScoreAsync(trainingScoreId);
            bool hasMedal = entry.IsCompetition && StandardMedals.IsMedal(entry.CompetitionStdMedal);

            if (!hasMedal)
            {
                // Medal removed/never present — drop the linked award (unless locked into a Gold app).
                if (existing != null && !existing.GoldApplicationId.HasValue)
                {
                    var proofToDelete = existing.ProofFileRef;
                    var (deleted, _) = await _medalLedger.DeleteAwardAsync(existing.Id);
                    // Award is gone now; delete its file only if no other award still references it.
                    if (deleted && !string.IsNullOrEmpty(proofToDelete)
                        && await _medalLedger.CountAwardsUsingProofAsync(proofToDelete) == 0)
                        _proofStorage.Delete(proofToDelete);
                }
                return;
            }

            if (existing == null)
            {
                await _medalLedger.InsertAwardAsync(new StandardMedalAward
                {
                    MemberId = entry.MemberId,
                    Year = entry.TrainingDate.Year,
                    Discipline = string.IsNullOrWhiteSpace(entry.Discipline) ? StandardMedals.Precision : entry.Discipline,
                    MedalType = entry.CompetitionStdMedal!,
                    Source = StandardMedals.SourceSelfReported,
                    CompetitionName = entry.CompetitionName,
                    CompetitionDate = entry.TrainingDate,
                    Location = entry.CompetitionLocation,
                    ShootingClass = entry.CompetitionShootingClass,
                    ProofType = string.IsNullOrEmpty(entry.MedalProofFileRef)
                        ? StandardMedals.ProofAttestation
                        : StandardMedals.ProofFile,
                    ProofFileRef = entry.MedalProofFileRef,
                    Status = StandardMedals.StatusReported,
                    TrainingScoreId = trainingScoreId,
                    EnteredByMemberId = entry.MemberId
                });
                return;
            }

            // Existing award — refresh descriptive fields. Don't touch a locked (consumed) award.
            if (existing.GoldApplicationId.HasValue)
                return;

            // Detect changes that affect what gets reported, before overwriting, so we can
            // re-open verification when a member edits an already-verified medal.
            var newMedal = entry.CompetitionStdMedal!;
            var newYear = entry.TrainingDate.Year;
            bool materialChange =
                existing.MedalType != newMedal ||
                existing.Year != newYear ||
                !string.Equals(existing.ShootingClass ?? "", entry.CompetitionShootingClass ?? "", StringComparison.OrdinalIgnoreCase);

            existing.Year = newYear;
            existing.Discipline = string.IsNullOrWhiteSpace(entry.Discipline) ? StandardMedals.Precision : entry.Discipline;
            existing.MedalType = newMedal;
            existing.CompetitionDate = entry.TrainingDate;
            existing.ShootingClass = entry.CompetitionShootingClass;
            // Name/location aren't loaded into the edit form, so only overwrite when the caller
            // actually supplied them — otherwise keep what was entered at creation.
            if (!string.IsNullOrWhiteSpace(entry.CompetitionName)) existing.CompetitionName = entry.CompetitionName;
            if (!string.IsNullOrWhiteSpace(entry.CompetitionLocation)) existing.Location = entry.CompetitionLocation;

            // Replace/reuse the proof reference only when the caller supplied a different one;
            // otherwise keep what's there. Delete the old file only if no other award uses it.
            if (!string.IsNullOrEmpty(entry.MedalProofFileRef) && entry.MedalProofFileRef != existing.ProofFileRef)
            {
                var oldRef = existing.ProofFileRef;
                existing.ProofFileRef = entry.MedalProofFileRef;
                existing.ProofType = StandardMedals.ProofFile;
                if (!string.IsNullOrEmpty(oldRef)
                    && await _medalLedger.CountAwardsUsingProofAsync(oldRef, existing.Id) == 0)
                    _proofStorage.Delete(oldRef);
            }

            // A member edit re-opens verification: a rejected medal returns to reported, and a
            // material change to an already-verified medal must be re-checked by the club before
            // it's reported to SPSF.
            if (existing.Status == StandardMedals.StatusRejected ||
                (existing.Status == StandardMedals.StatusVerified && materialChange))
            {
                existing.Status = StandardMedals.StatusReported;
                existing.VerifiedByMemberId = null;
                existing.VerifiedAt = null;
            }

            await _medalLedger.UpdateAwardAsync(existing);
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
                int newTrainingScoreId;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    var newId = db.Insert("TrainingScores", "Id", true, new
                    {
                        MemberId = entry.MemberId,
                        TrainingDate = entry.TrainingDate,
                        WeaponClass = entry.WeaponClass, // Save weapon class (A, B, C, R, P)
                        Discipline = entry.Discipline ?? "Precision", // Discipline (Precision, Milsnabb)
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
                    newTrainingScoreId = Convert.ToInt32(newId);
                }

                // Record any self-reported standard medal in the Standardmedalj ledger.
                await SyncSelfReportedMedalAsync(newTrainingScoreId, entry);

                // Update shooter statistics for handicap calculation
                if (!string.IsNullOrEmpty(entry.WeaponClass) && entry.SeriesCount > 0)
                {
                    await _statisticsService.UpdateAfterMatchAsync(
                        entry.MemberId,
                        entry.WeaponClass,
                        entry.SeriesCount,
                        entry.TotalScore,
                        entry.Discipline ?? "Precision");
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
                        Discipline = (string?)r.Discipline ?? "Precision",
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
        public async Task<IActionResult> GetDashboardStatistics(string competitionType = "Precision")
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

                // Fältskytte uses a fundamentally different data shape (träff/figurer,
                // no series). Build a dedicated dashboard payload and return early.
                if (competitionType == "Faltskytte")
                {
                    var faltDashboard = await _faltskytteStatsService.BuildMemberDashboardAsync(member.Id);
                    return Json(new { success = true, data = faltDashboard });
                }

                // Get all results — use UnifiedResultsService for Precision only.
                // For non-Precision disciplines, load directly from discipline-specific sources
                // to avoid loading + discarding all Precision data.
                List<HpskSite.Shared.Models.UnifiedResultEntry> allResults;
                if (competitionType == "Precision")
                {
                    allResults = _unifiedResultsService.GetMemberResults(member.Id);
                }
                else
                {
                    allResults = LoadDisciplineResults(member.Id, competitionType);
                }

                // Separate training and competition results
                // TrainingMatch = from mobile app training matches, Training = manually entered training
                var trainingResults = allResults.Where(r => r.SourceType == "Training" || r.SourceType == "TrainingMatch").ToList();
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
                        isCompetition = r.SourceType == "Competition" || r.SourceType == "Official",
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
                    .GroupBy(r => new { r.WeaponClass, IsCompetition = r.SourceType == "Competition" || r.SourceType == "Official" })
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
                // Milsnabb uses 12 series for all weapon classes; Precision uses 6/7/10 (L: 6/8/12)
                int[] GetSeriesCountsForWeapon(string wc, string discipline) {
                    if (discipline == "MagnumPrecision")
                        return new[] { 6 };
                    if (discipline == "Milsnabb" || discipline == "NationellHelmatch")
                        return new[] { 12 };
                    // Duell and Precision use same series counts
                    return wc == "L" ? new[] { 6, 8, 12 } : new[] { 6, 7, 10 };
                }

                var personalBestsBySeriesCount = allResults
                    .Where(r => {
                        var seriesCounts = GetSeriesCountsForWeapon(r.WeaponClass, competitionType);
                        return seriesCounts.Contains(r.SeriesCount);
                    })
                    .GroupBy(r => new { r.WeaponClass, r.SeriesCount, IsCompetition = r.SourceType == "Competition" || r.SourceType == "Official" })
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

                // Medal statistics — the Standardmedalj ledger is the canonical source (replaces the
                // old TrainingScores+result-doc tally). Returns total (non-rejected) + verified split.
                var medalStatsByYear = await _medalLedger.GetMedalStatsByYearAsync(member.Id, competitionType, availableYears);
                // Default medalStats is for current year (or most recent year with data)
                var currentYear = DateTime.Now.Year;
                var medalStats = medalStatsByYear.ContainsKey(currentYear)
                    ? (object)medalStatsByYear[currentYear]
                    : (availableYears.Any() && medalStatsByYear.ContainsKey(availableYears.First())
                        ? medalStatsByYear[availableYears.First()]
                        : new MedalYearStats());

                var stats = new
                {
                    // Discipline context
                    competitionType,

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
                    personalBests = new[] { "A", "B", "C", "R", "M", "L" }.Select(wc => new
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

            var weaponClasses = new[] { "A", "B", "C", "R", "M", "L" };
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
                          SET TrainingDate = @0, WeaponClass = @1, Discipline = @2, IsCompetition = @3,
                              CompetitionPlace = @4, CompetitionShootingClass = @5, CompetitionStdMedal = @6,
                              SeriesScores = @7, TotalScore = @8, XCount = @9, Notes = @10, UpdatedAt = @11
                          WHERE Id = @12",
                        entry.TrainingDate,
                        entry.WeaponClass,
                        entry.Discipline ?? "Precision",
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

                    // Keep the Standardmedalj ledger in sync with the edited result.
                    entry.MemberId = member.Id;
                    await SyncSelfReportedMedalAsync(entry.Id, entry);

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

                    // Remove any linked Standardmedalj award (unless locked into a Gold application).
                    var linkedAward = await _medalLedger.GetByTrainingScoreAsync(id);
                    if (linkedAward != null && !linkedAward.GoldApplicationId.HasValue)
                    {
                        var proofToDelete = linkedAward.ProofFileRef;
                        var (deleted, _) = await _medalLedger.DeleteAwardAsync(linkedAward.Id);
                        // Delete the file only if no other award still references it.
                        if (deleted && !string.IsNullOrEmpty(proofToDelete)
                            && await _medalLedger.CountAwardsUsingProofAsync(proofToDelete) == 0)
                            _proofStorage.Delete(proofToDelete);
                    }

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

        #region Discipline result loading

        /// <summary>
        /// Load results for a non-Precision discipline directly from discipline-specific sources,
        /// avoiding the overhead of loading all Precision data from UnifiedResultsService.
        /// </summary>
        private List<HpskSite.Shared.Models.UnifiedResultEntry> LoadDisciplineResults(int memberId, string discipline)
        {
            var results = new List<HpskSite.Shared.Models.UnifiedResultEntry>();
            var resultTable = discipline switch { "Milsnabb" => "MilsnabbResultEntry", "Duell" => "DuellResultEntry", "NationellHelmatch" => "NationellHelmatchResultEntry", "MagnumPrecision" => "MagnumPrecisionResultEntry", _ => "PrecisionResultEntry" };
            int defaultSeriesCount = discipline switch { "Milsnabb" or "NationellHelmatch" => 12, "MagnumPrecision" => 6, _ => 0 };

            using (var db = _databaseFactory.CreateDatabase())
            {
                // Source 1: TrainingScores filtered by discipline
                var trainingScores = db.Fetch<dynamic>(@"
                    SELECT Id, TrainingDate, WeaponClass, SeriesScores, TotalScore, XCount, IsCompetition, TrainingMatchId
                    FROM TrainingScores
                    WHERE MemberId = @0 AND Discipline = @1
                    ORDER BY TrainingDate DESC",
                    memberId, discipline);

                foreach (var score in trainingScores)
                {
                    int seriesCount = 0;
                    try
                    {
                        var seriesJson = score.SeriesScores?.ToString();
                        if (!string.IsNullOrEmpty(seriesJson))
                        {
                            var trainingSeries = System.Text.Json.JsonSerializer.Deserialize<List<HpskSite.Shared.Models.TrainingSeries>>(seriesJson);
                            if (trainingSeries != null && trainingSeries.Count > 0)
                            {
                                if (trainingSeries.Count == 1 && trainingSeries[0].EntryMethod == "TotalOnly" && trainingSeries[0].SeriesCount.HasValue)
                                    seriesCount = trainingSeries[0].SeriesCount.Value;
                                else
                                    seriesCount = trainingSeries.Count;
                            }
                        }
                    }
                    catch { }
                    if (seriesCount == 0 && defaultSeriesCount > 0) seriesCount = defaultSeriesCount;

                    int totalScore = (int)(score.TotalScore ?? 0);
                    bool isCompetition = (bool)(score.IsCompetition ?? false);
                    int? trainingMatchId = (int?)score.TrainingMatchId;
                    string sourceType = isCompetition ? "Competition" : (trainingMatchId != null ? "TrainingMatch" : "Training");

                    results.Add(new HpskSite.Shared.Models.UnifiedResultEntry
                    {
                        Id = (int)score.Id,
                        Date = score.TrainingDate,
                        SourceType = sourceType,
                        WeaponClass = score.WeaponClass?.ToString() ?? "",
                        TotalScore = totalScore,
                        XCount = score.XCount ?? 0,
                        SeriesCount = seriesCount,
                        AverageScore = seriesCount > 0 ? Math.Round((double)totalScore / seriesCount, 1) : 0,
                        CanEdit = true,
                        CanDelete = true
                    });
                }

                // Source 2: Competition results from discipline-specific table
                var competitionResults = db.Fetch<dynamic>($@"
                    SELECT CompetitionId, ShootingClass, Shots, SeriesNumber, EnteredAt
                    FROM {resultTable}
                    WHERE MemberId = @0
                    ORDER BY EnteredAt DESC",
                    memberId);

                // Group by competition + shooting class, batch-load competition names
                var competitionIds = competitionResults.Select(r => (int)r.CompetitionId).Distinct().ToList();
                var competitionNames = new Dictionary<int, string>();
                var competitionDates = new Dictionary<int, DateTime>();
                if (competitionIds.Any())
                {
                    var competitions = _contentService.GetByIds(competitionIds);
                    foreach (var comp in competitions)
                    {
                        competitionNames[comp.Id] = comp.Name ?? $"Tävling #{comp.Id}";
                        var compDate = comp.GetValue<DateTime>("competitionDate");
                        if (compDate != default) competitionDates[comp.Id] = compDate;
                    }
                }

                var byCompetition = competitionResults.GroupBy(r => (int)r.CompetitionId);
                foreach (var group in byCompetition)
                {
                    int competitionId = group.Key;
                    string weaponClass = "";
                    int totalScore = 0;
                    int xCount = 0;
                    int seriesCount = group.Count();

                    foreach (var entry in group)
                    {
                        string shootingClass = entry.ShootingClass?.ToString() ?? "";
                        if (string.IsNullOrEmpty(weaponClass))
                            weaponClass = ShootingClasses.GetWeaponClassCode(shootingClass);

                        try
                        {
                            var shotsJson = entry.Shots?.ToString();
                            if (!string.IsNullOrEmpty(shotsJson))
                            {
                                var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson);
                                if (shots != null)
                                {
                                    foreach (var shot in shots)
                                    {
                                        if (shot.Equals("X", StringComparison.OrdinalIgnoreCase)) { totalScore += 10; xCount++; }
                                        else if (int.TryParse(shot, out int v)) totalScore += v;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    competitionNames.TryGetValue(competitionId, out var compName);
                    competitionDates.TryGetValue(competitionId, out var compDate);
                    if (compDate == default) compDate = group.Max(e => (DateTime)e.EnteredAt);

                    results.Add(new HpskSite.Shared.Models.UnifiedResultEntry
                    {
                        Id = competitionId,
                        Date = compDate,
                        SourceType = "Official",
                        WeaponClass = weaponClass,
                        TotalScore = totalScore,
                        XCount = xCount,
                        SeriesCount = seriesCount,
                        AverageScore = seriesCount > 0 ? Math.Round((double)totalScore / seriesCount, 1) : 0,
                        CompetitionName = compName,
                        CompetitionId = competitionId,
                        CanEdit = false,
                        CanDelete = false
                    });
                }
            }

            return results.OrderByDescending(r => r.Date).ToList();
        }

        #endregion

        #region Fältskytte (dashboard + self-entry CRUD)


        /// <summary>
        /// Record a self-entered Fältskytte result (external comp). Persists in
        /// TrainingScores with Discipline='Faltskytte' and IsCompetition=1. The
        /// Fältskytte-specific fields (mode/stationCount/hits/figures/...) are
        /// packed into the SeriesScores JSON column.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordFaltskytteScore([FromBody] FaltskytteScoreRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null) return Json(new { success = false, message = "Medlem hittades inte." });

                var error = ValidateFaltskytteRequest(request);
                if (error != null) return Json(new { success = false, message = error });

                var weaponClass = ShootingClasses.GetWeaponClassCode(request.ShootingClass);
                if (string.IsNullOrEmpty(weaponClass))
                    return Json(new { success = false, message = "Okänd skytteklass." });

                var payloadJson = JsonSerializer.Serialize(new FaltskytteExternalPayload
                {
                    CompetitionName = request.CompetitionName ?? "",
                    Mode = request.Mode,
                    StationCount = request.StationCount,
                    Hits = request.Hits,
                    Figures = request.Figures,
                    FiguresMax = request.FiguresMax,
                    Placement = request.Placement,
                    Participants = request.Participants
                });

                using var db = _databaseFactory.CreateDatabase();
                db.Insert("TrainingScores", "Id", true, new
                {
                    MemberId = member.Id,
                    TrainingDate = request.Date,
                    WeaponClass = weaponClass,
                    Discipline = "Faltskytte",
                    IsCompetition = true,
                    CompetitionPlace = request.Placement,            // int? — placement, not name
                    CompetitionShootingClass = request.ShootingClass,
                    CompetitionStdMedal = request.StandardMedal ?? "",
                    SeriesScores = payloadJson,
                    TotalScore = request.Hits, // best-effort scalar for any legacy aggregation
                    XCount = 0,
                    Notes = request.Notes ?? string.Empty,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte spara: " + ex.Message });
            }
        }

        /// <summary>Update a self-entered Fältskytte row. Owner-only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateFaltskytteScore([FromBody] FaltskytteScoreRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null) return Json(new { success = false, message = "Medlem hittades inte." });

                if (!request.Id.HasValue) return Json(new { success = false, message = "Id saknas." });

                var error = ValidateFaltskytteRequest(request);
                if (error != null) return Json(new { success = false, message = error });

                var weaponClass = ShootingClasses.GetWeaponClassCode(request.ShootingClass);
                if (string.IsNullOrEmpty(weaponClass))
                    return Json(new { success = false, message = "Okänd skytteklass." });

                using var db = _databaseFactory.CreateDatabase();
                var existing = db.SingleOrDefault<dynamic>("SELECT MemberId, Discipline FROM TrainingScores WHERE Id = @0", request.Id.Value);
                if (existing == null) return Json(new { success = false, message = "Resultatet finns inte." });
                if ((int)existing.MemberId != member.Id) return Json(new { success = false, message = "Du kan bara redigera dina egna resultat." });
                if (((string?)existing.Discipline) != "Faltskytte") return Json(new { success = false, message = "Endast Fältskytte-rader kan uppdateras via denna endpoint." });

                var payloadJson = JsonSerializer.Serialize(new FaltskytteExternalPayload
                {
                    CompetitionName = request.CompetitionName ?? "",
                    Mode = request.Mode,
                    StationCount = request.StationCount,
                    Hits = request.Hits,
                    Figures = request.Figures,
                    FiguresMax = request.FiguresMax,
                    Placement = request.Placement,
                    Participants = request.Participants
                });

                db.Execute(@"UPDATE TrainingScores
                              SET TrainingDate = @0, WeaponClass = @1, CompetitionPlace = @2,
                                  CompetitionShootingClass = @3, CompetitionStdMedal = @4,
                                  SeriesScores = @5, TotalScore = @6, Notes = @7, UpdatedAt = @8
                              WHERE Id = @9",
                    request.Date,
                    weaponClass,
                    (object?)request.Placement ?? DBNull.Value,   // int? — placement, not name
                    request.ShootingClass,
                    request.StandardMedal ?? "",
                    payloadJson,
                    request.Hits,
                    request.Notes ?? string.Empty,
                    DateTime.Now,
                    request.Id.Value);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte uppdatera: " + ex.Message });
            }
        }

        /// <summary>Delete a self-entered Fältskytte row. Owner-only.</summary>
        [HttpDelete]
        public async Task<IActionResult> DeleteFaltskytteScore(int id)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null) return Json(new { success = false, message = "Medlem hittades inte." });

                using var db = _databaseFactory.CreateDatabase();
                var existing = db.SingleOrDefault<dynamic>("SELECT MemberId, Discipline FROM TrainingScores WHERE Id = @0", id);
                if (existing == null) return Json(new { success = false, message = "Resultatet finns inte." });
                if ((int)existing.MemberId != member.Id) return Json(new { success = false, message = "Du kan bara ta bort dina egna resultat." });
                if (((string?)existing.Discipline) != "Faltskytte") return Json(new { success = false, message = "Endast Fältskytte-rader kan tas bort via denna endpoint." });

                db.Execute("DELETE FROM TrainingScores WHERE Id = @0", id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte ta bort: " + ex.Message });
            }
        }

        private static string? ValidateFaltskytteRequest(FaltskytteScoreRequest r)
        {
            if (r.Date == default) return "Datum saknas.";
            if (string.IsNullOrWhiteSpace(r.ShootingClass)) return "Skytteklass saknas.";
            if (r.Mode != "Normalfalt" && r.Mode != "Poangfalt") return "Ogiltig fälttyp.";
            if (r.StationCount < 1 || r.StationCount > 16) return "Antal stationer måste vara mellan 1 och 16.";
            var maxHits = r.StationCount * 6;
            if (r.Hits < 0 || r.Hits > maxHits) return $"Antal träff måste vara 0–{maxHits}.";
            if (r.FiguresMax < 1) return "Totalt antal figurer måste vara minst 1.";
            if (r.Figures < 0 || r.Figures > r.FiguresMax) return $"Träffade figurer måste vara 0–{r.FiguresMax}.";
            if (r.Placement.HasValue || r.Participants.HasValue)
            {
                if (!(r.Placement.HasValue && r.Participants.HasValue))
                    return "Ange både placering och totalt antal deltagare, eller lämna båda tomma.";
                if (r.Placement.Value < 1) return "Placering måste vara minst 1.";
                if (r.Participants.Value < r.Placement.Value) return "Totalt antal deltagare kan inte vara mindre än placering.";
            }
            return null;
        }

        public class FaltskytteScoreRequest
        {
            public int? Id { get; set; }
            public DateTime Date { get; set; }
            public string? CompetitionName { get; set; }
            public string ShootingClass { get; set; } = "";
            public string Mode { get; set; } = "Normalfalt"; // "Normalfalt" | "Poangfalt"
            public int StationCount { get; set; }
            public int Hits { get; set; }
            public int Figures { get; set; }
            public int FiguresMax { get; set; }
            public int? Placement { get; set; }
            public int? Participants { get; set; }
            public string? StandardMedal { get; set; } // "S" | "B" | null
            public string? Notes { get; set; }
        }

        #endregion
    }
}
