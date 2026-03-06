using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.CompetitionTypes.Common.SeriesCalculation;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Milsnabb.Models;

namespace HpskSite.Services
{
    public class SeriesCalculationService
    {
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SeriesCalculationService> _logger;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string CacheKeyPrefix = "SeriesResults_";

        public SeriesCalculationService(
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            IContentService contentService,
            IMemberService memberService,
            ClubService clubService,
            IMemoryCache cache,
            ILogger<SeriesCalculationService> logger)
        {
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _contentService = contentService;
            _memberService = memberService;
            _clubService = clubService;
            _cache = cache;
            _logger = logger;
        }

        public async Task<SeriesResultData?> CalculateSeriesResults(int seriesContentId)
        {
            var cacheKey = CacheKeyPrefix + seriesContentId;
            if (_cache.TryGetValue(cacheKey, out SeriesResultData? cached) && cached != null)
            {
                _logger.LogDebug("Returning cached series results for {SeriesId}", seriesContentId);
                return cached;
            }

            var series = _contentService.GetById(seriesContentId);
            if (series == null || series.ContentType.Alias != "competitionSeries")
            {
                _logger.LogWarning("Series not found or wrong type: {SeriesId}", seriesContentId);
                return null;
            }

            var strategyId = series.GetValue<string>("seriesCalculationStrategy") ?? "";
            if (string.IsNullOrEmpty(strategyId))
            {
                _logger.LogDebug("No calculation strategy configured for series {SeriesId}", seriesContentId);
                return null;
            }

            var strategy = SeriesCalculationRegistry.GetById(strategyId);
            if (strategy == null)
            {
                _logger.LogWarning("Unknown calculation strategy '{StrategyId}' for series {SeriesId}", strategyId, seriesContentId);
                return null;
            }

            // Parse strategy config
            var configJson = series.GetValue<string>("seriesCalculationConfig") ?? "{}";
            var parameters = new Dictionary<string, object>();
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
                if (parsed != null)
                {
                    foreach (var kvp in parsed)
                    {
                        parameters[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse strategy config for series {SeriesId}", seriesContentId);
            }

            // Get child competitions ordered by sortOrder then date
            var competitions = _contentService.GetPagedChildren(seriesContentId, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competition")
                .OrderBy(c => c.GetValue<int?>("seriesSortOrder") ?? int.MaxValue)
                .ThenBy(c => c.GetValue<DateTime>("competitionDate"))
                .Select(c => new SeriesCompetitionInfo
                {
                    CompetitionId = c.Id,
                    Name = c.GetValue<string>("competitionName") ?? c.Name,
                    Date = c.GetValue<DateTime>("competitionDate")
                })
                .ToList();

            if (!competitions.Any())
            {
                _logger.LogDebug("No competitions in series {SeriesId}", seriesContentId);
                return new SeriesResultData
                {
                    StrategyId = strategy.Id,
                    StrategyName = strategy.Name,
                    CalculatedAt = DateTime.UtcNow,
                    Competitions = competitions,
                    Sections = new List<SeriesResultSection>()
                };
            }

            // Batch-fetch all results for all competitions
            var competitionIds = competitions.Select(c => c.CompetitionId).ToList();
            var allResults = await FetchResultsForCompetitions(competitionIds);

            // Build shooter lookup (MemberId -> Name, Club, ClubId)
            var allMemberIds = allResults.Values
                .SelectMany(r => r)
                .Select(r => r.MemberId)
                .Distinct()
                .ToList();

            var shooterLookup = BuildShooterLookup(allMemberIds);

            // Aggregate results by competition: competitionId -> list of ShooterCompetitionScore
            var competitionResults = new Dictionary<int, List<ShooterCompetitionScore>>();
            foreach (var compId in competitionIds)
            {
                if (!allResults.TryGetValue(compId, out var results))
                {
                    competitionResults[compId] = new List<ShooterCompetitionScore>();
                    continue;
                }

                // Group by (MemberId, ShootingClass) and calculate totals
                var scores = results
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g =>
                    {
                        var totalScore = g.Sum(r => CalculateTotalFromShots(r.Shots));
                        var xCount = g.Sum(r => CalculateXCountFromShots(r.Shots));
                        var (name, club, clubId) = shooterLookup.TryGetValue(g.Key.MemberId, out var info)
                            ? info
                            : ("Okänd skytt", "Okänd klubb", 0);

                        return new ShooterCompetitionScore
                        {
                            MemberId = g.Key.MemberId,
                            Name = name,
                            Club = club,
                            ClubId = clubId,
                            ShootingClass = g.Key.ShootingClass,
                            TotalScore = totalScore,
                            XCount = xCount
                        };
                    })
                    .ToList();

                competitionResults[compId] = scores;
            }

            // Build context and calculate
            var context = new SeriesCalculationContext
            {
                SeriesId = seriesContentId,
                SeriesName = series.GetValue<string>("seriesName") ?? series.Name,
                Competitions = competitions,
                Parameters = parameters,
                CompetitionResults = competitionResults
            };

            var result = strategy.Calculate(context);

            // Cache the result
            _cache.Set(cacheKey, result, CacheDuration);
            _logger.LogDebug("Cached series results for {SeriesId}", seriesContentId);

            return result;
        }

        /// <summary>
        /// Invalidates the cached series results for a specific competition.
        /// Finds the parent series (if any) and evicts its cache entry.
        /// Call this when competition results are saved or deleted.
        /// </summary>
        public void InvalidateCacheForCompetition(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return;

                var parent = _contentService.GetById(competition.ParentId);
                if (parent != null && parent.ContentType.Alias == "competitionSeries")
                {
                    var cacheKey = CacheKeyPrefix + parent.Id;
                    _cache.Remove(cacheKey);
                    _logger.LogDebug("Invalidated series results cache for series {SeriesId} (competition {CompId} changed)",
                        parent.Id, competitionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate series cache for competition {CompId}", competitionId);
            }
        }

        /// <summary>
        /// Invalidates the cached series results for a specific series.
        /// </summary>
        public void InvalidateCacheForSeries(int seriesId)
        {
            _cache.Remove(CacheKeyPrefix + seriesId);
            _logger.LogDebug("Invalidated series results cache for series {SeriesId}", seriesId);
        }

        private async Task<Dictionary<int, List<PrecisionResultEntry>>> FetchResultsForCompetitions(List<int> competitionIds)
        {
            var result = new Dictionary<int, List<PrecisionResultEntry>>();
            if (!competitionIds.Any()) return result;

            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                // Determine table based on competition type (all competitions in a series should be same type)
                var firstComp = _contentService.GetById(competitionIds.First());
                var compType = firstComp?.GetValue<string>("competitionType") ?? "Precision";
                var tableName = compType switch
                {
                    "Milsnabb" => "MilsnabbResultEntry",
                    "Duell" => "DuellResultEntry",
                    "NationellHelmatch" => "NationellHelmatchResultEntry",
                    _ => "PrecisionResultEntry"
                };

                // Build parameterized IN clause
                var paramNames = competitionIds.Select((id, i) => $"@{i}").ToArray();
                var inClause = string.Join(",", paramNames);
                var sql = $"SELECT * FROM [{tableName}] WHERE CompetitionId IN ({inClause}) ORDER BY CompetitionId, MemberId, SeriesNumber";

                var allResults = await db.FetchAsync<PrecisionResultEntry>(sql, competitionIds.Cast<object>().ToArray());

                foreach (var group in allResults.GroupBy(r => r.CompetitionId))
                {
                    result[group.Key] = group.ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching results for competitions {CompetitionIds}", string.Join(",", competitionIds));
            }

            return result;
        }

        private Dictionary<int, (string Name, string Club, int ClubId)> BuildShooterLookup(List<int> memberIds)
        {
            var lookup = new Dictionary<int, (string Name, string Club, int ClubId)>();

            foreach (var memberId in memberIds)
            {
                try
                {
                    var member = _memberService.GetById(memberId);
                    if (member != null)
                    {
                        var name = member.Name ?? "Okänd";
                        var clubId = member.GetValue<int>("primaryClubId");
                        var clubName = clubId > 0
                            ? (_clubService.GetClubNameById(clubId) ?? "Okänd klubb")
                            : "Okänd klubb";
                        lookup[memberId] = (name, clubName, clubId);
                    }
                    else
                    {
                        lookup[memberId] = ("Okänd skytt", "Okänd klubb", 0);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to look up member {MemberId}", memberId);
                    lookup[memberId] = ("Okänd skytt", "Okänd klubb", 0);
                }
            }

            return lookup;
        }

        private static int CalculateTotalFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? Array.Empty<string>();
                return shots.Sum(shot => shot.ToUpper() == "X" ? 10 : (int.TryParse(shot, out int value) ? value : 0));
            }
            catch
            {
                return 0;
            }
        }

        private static int CalculateXCountFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? Array.Empty<string>();
                return shots.Count(shot => shot.ToUpper() == "X");
            }
            catch
            {
                return 0;
            }
        }
    }
}
