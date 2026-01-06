using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Services;
using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Controllers.Api
{
    /// <summary>
    /// API controller for statistics and dashboard data (Mobile app)
    /// </summary>
    [Route("api/stats")]
    [ApiController]
    [Authorize(AuthenticationSchemes = "JwtBearer")]
    public class StatsApiController : ControllerBase
    {
        private readonly JwtTokenService _jwtTokenService;
        private readonly UnifiedResultsService _unifiedResultsService;
        private readonly IMemberService _memberService;
        private readonly IShooterStatisticsService _statisticsService;
        private readonly IHandicapCalculator _handicapCalculator;

        public StatsApiController(
            JwtTokenService jwtTokenService,
            UnifiedResultsService unifiedResultsService,
            IMemberService memberService,
            IShooterStatisticsService statisticsService,
            IHandicapCalculator handicapCalculator)
        {
            _jwtTokenService = jwtTokenService;
            _unifiedResultsService = unifiedResultsService;
            _memberService = memberService;
            _statisticsService = statisticsService;
            _handicapCalculator = handicapCalculator;
        }

        /// <summary>
        /// Get current member ID from JWT token
        /// </summary>
        private int? GetCurrentMemberId()
        {
            return _jwtTokenService.GetMemberIdFromClaims(User);
        }

        /// <summary>
        /// Get dashboard statistics for current user
        /// </summary>
        [HttpGet("dashboard")]
        public IActionResult GetDashboardStatistics([FromQuery] int? year = null)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<DashboardStatistics>.Error("Ej inloggad"));
            }

            var selectedYear = year ?? DateTime.Now.Year;

            // Get all results for the member
            var allResults = _unifiedResultsService.GetMemberResults(memberId.Value);
            var yearResults = allResults.Where(r => r.Date.Year == selectedYear).ToList();

            // Calculate statistics
            var trainingSessions = yearResults.Count(r => r.SourceType == "Training");
            var competitionSessions = yearResults.Count(r => r.SourceType == "Competition" || r.SourceType == "Official");

            // 30-day average
            var thirtyDaysAgo = DateTime.Now.AddDays(-30);
            var recentResults = allResults.Where(r => r.Date >= thirtyDaysAgo).ToList();
            var thirtyDayAverage = recentResults.Any() ? recentResults.Average(r => r.AverageScore) : 0;

            // Overall average for the year
            var overallAverage = yearResults.Any() ? yearResults.Average(r => r.AverageScore) : 0;

            // Best scores
            var bestSeriesScore = yearResults.Any() ? yearResults.Max(r => r.SeriesCount > 0 ? r.TotalScore / r.SeriesCount : 0) : 0;
            var bestMatchScore = yearResults.Any() ? yearResults.Max(r => r.TotalScore) : 0;

            // Weapon class breakdown
            var weaponClassStats = yearResults
                .GroupBy(r => r.WeaponClass.Length > 0 ? r.WeaponClass[0].ToString() : "?")
                .Select(g => new WeaponClassStat
                {
                    WeaponClass = g.Key,
                    TotalSessions = g.Count(),
                    Average = g.Average(r => r.AverageScore),
                    BestScore = g.Max(r => r.TotalScore)
                })
                .ToList();

            // Recent activity (last 10)
            var recentActivity = allResults.Take(10).Select(r => new ActivityEntry
            {
                Date = r.Date,
                WeaponClass = r.WeaponClass,
                Score = r.TotalScore,
                XCount = r.XCount,
                SeriesCount = r.SeriesCount,
                IsCompetition = r.SourceType != "Training",
                SourceType = r.SourceType
            }).ToList();

            var dashboard = new DashboardStatistics
            {
                Year = selectedYear,
                TotalSessions = trainingSessions + competitionSessions,
                TrainingSessions = trainingSessions,
                CompetitionSessions = competitionSessions,
                ThirtyDayAverage = Math.Round(thirtyDayAverage, 1),
                OverallAverage = Math.Round(overallAverage, 1),
                BestSeriesScore = bestSeriesScore,
                BestMatchScore = bestMatchScore,
                WeaponClassStats = weaponClassStats,
                RecentActivity = recentActivity
            };

            return Ok(ApiResponse<DashboardStatistics>.Ok(dashboard));
        }

        /// <summary>
        /// Get personal bests by weapon class
        /// </summary>
        [HttpGet("personal-bests")]
        public IActionResult GetPersonalBests()
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<List<PersonalBestsByClass>>.Error("Ej inloggad"));
            }

            // Get all results for the member
            var allResults = _unifiedResultsService.GetMemberResults(memberId.Value);

            // Group by weapon class and find bests
            var personalBests = allResults
                .GroupBy(r => r.WeaponClass.Length > 0 ? r.WeaponClass[0].ToString() : "?")
                .Select(g => new PersonalBestsByClass
                {
                    WeaponClass = g.Key,
                    Bests = g
                        .GroupBy(r => r.SeriesCount)
                        .Select(sg => new PersonalBest
                        {
                            MemberId = memberId.Value,
                            WeaponClass = g.Key,
                            SeriesCount = sg.Key,
                            BestScore = sg.Max(r => r.TotalScore),
                            XCount = sg.OrderByDescending(r => r.TotalScore).First().XCount,
                            AchievedDate = sg.OrderByDescending(r => r.TotalScore).First().Date,
                            IsCompetition = sg.OrderByDescending(r => r.TotalScore).First().SourceType != "Training"
                        })
                        .OrderBy(b => b.SeriesCount)
                        .ToList()
                })
                .ToList();

            return Ok(ApiResponse<List<PersonalBestsByClass>>.Ok(personalBests));
        }

        /// <summary>
        /// Get results with pagination
        /// </summary>
        [HttpGet("results")]
        public IActionResult GetResults(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? weaponClass = null,
            [FromQuery] string? sourceType = null)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<PagedResponse<UnifiedResultEntry>>.Error("Ej inloggad"));
            }

            var skip = (page - 1) * pageSize;

            // Get all results and filter
            var allResults = _unifiedResultsService.GetMemberResults(memberId.Value);

            // Apply filters
            if (!string.IsNullOrEmpty(weaponClass))
            {
                allResults = allResults.Where(r => r.WeaponClass.StartsWith(weaponClass, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrEmpty(sourceType))
            {
                allResults = allResults.Where(r => r.SourceType.Equals(sourceType, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var totalCount = allResults.Count;
            var items = allResults.Skip(skip).Take(pageSize).ToList();

            var response = new PagedResponse<UnifiedResultEntry>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };

            return Ok(ApiResponse<PagedResponse<UnifiedResultEntry>>.Ok(response));
        }

        /// <summary>
        /// Get chart data for progress over time
        /// </summary>
        [HttpGet("progress-chart")]
        public IActionResult GetProgressChart(
            [FromQuery] int? year = null,
            [FromQuery] string? weaponClass = null)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<ProgressChartData>.Error("Ej inloggad"));
            }

            var selectedYear = year ?? DateTime.Now.Year;

            // Get all results for the year
            var allResults = _unifiedResultsService.GetMemberResults(memberId.Value)
                .Where(r => r.Date.Year == selectedYear)
                .OrderBy(r => r.Date)
                .ToList();

            if (!string.IsNullOrEmpty(weaponClass))
            {
                allResults = allResults.Where(r => r.WeaponClass.StartsWith(weaponClass, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var chartData = new ProgressChartData
            {
                Year = selectedYear,
                DataPoints = allResults.Select(r => new ChartDataPoint
                {
                    Date = r.Date,
                    Score = r.AverageScore,
                    XCount = r.XCount,
                    Label = r.WeaponClass
                }).ToList()
            };

            return Ok(ApiResponse<ProgressChartData>.Ok(chartData));
        }

        /// <summary>
        /// Get handicap profile for current user
        /// </summary>
        [HttpGet("handicap")]
        public async Task<IActionResult> GetHandicapProfile()
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<HandicapProfile>.Error("Ej inloggad"));
            }

            try
            {
                var member = _memberService.GetById(memberId.Value);
                if (member == null)
                {
                    return NotFound(ApiResponse<HandicapProfile>.Error("Medlem hittades inte"));
                }

                // Get shooter class from member profile
                var shooterClass = member.GetValue<string>("precisionShooterClass") ?? "";

                // Get all statistics for this member
                var allStats = await _statisticsService.GetAllStatisticsAsync(memberId.Value);

                // Calculate handicap for each weapon class
                var weaponClassProfiles = new List<WeaponClassHandicap>();
                foreach (var stats in allStats)
                {
                    var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);

                    weaponClassProfiles.Add(new WeaponClassHandicap
                    {
                        WeaponClass = stats.WeaponClass,
                        WeaponClassName = GetWeaponClassName(stats.WeaponClass),
                        HandicapPerSeries = profile.HandicapPerSeries,
                        IsProvisional = profile.IsProvisional,
                        CompletedMatches = profile.CompletedMatches,
                        RequiredMatches = _handicapCalculator.Settings.RequiredMatches,
                        EffectiveAverage = profile.EffectiveAverage,
                        ActualAverage = profile.ActualAverage,
                        ReferenceScore = _handicapCalculator.Settings.ReferenceSeriesScore
                    });
                }

                var handicapProfile = new HandicapProfile
                {
                    ShooterClass = shooterClass,
                    WeaponClasses = weaponClassProfiles
                };

                return Ok(ApiResponse<HandicapProfile>.Ok(handicapProfile));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<HandicapProfile>.Error($"Fel vid hämtning av handicap: {ex.Message}"));
            }
        }

        private string GetWeaponClassName(string weaponClass)
        {
            return weaponClass switch
            {
                "A" => "Tjänstevapen",
                "B" => "Kal. 32-45",
                "C" => "Kal. 22",
                "R" => "Revolver",
                "M" => "Magnum",
                "L" => "Luftpistol",
                _ => weaponClass
            };
        }
    }
}
