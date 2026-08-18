using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using HpskSite.CompetitionTypes.Common.SeriesCalculation;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources;

namespace HpskSite.Services
{
    public class SeriesCalculationService
    {
        private readonly IContentService _contentService;
        private readonly IEnumerable<ISeriesScoreSource> _scoreSources;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SeriesCalculationService> _logger;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string CacheKeyPrefix = "SeriesResults_";

        public SeriesCalculationService(
            IContentService contentService,
            IEnumerable<ISeriesScoreSource> scoreSources,
            IMemoryCache cache,
            ILogger<SeriesCalculationService> logger)
        {
            _contentService = contentService;
            _scoreSources = scoreSources;
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
            var childCompetitions = _contentService.GetPagedChildren(seriesContentId, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competition")
                .OrderBy(c => c.GetValue<int?>("seriesSortOrder") ?? int.MaxValue)
                .ThenBy(c => c.GetValue<DateTime>("competitionDate"))
                .ToList();

            var competitions = childCompetitions
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

            // All competitions in a series are the same discipline; the first one names it.
            var competitionType = childCompetitions[0].GetValue<string>("competitionType") ?? "";
            var scoreSource = _scoreSources.FirstOrDefault(s => s.Supports(competitionType));
            if (scoreSource == null)
            {
                _logger.LogInformation(
                    "Series {SeriesId} is of competition type '{CompetitionType}', which has no series score source",
                    seriesContentId, competitionType);
                return new SeriesResultData
                {
                    StrategyId = strategy.Id,
                    StrategyName = strategy.Name,
                    CalculatedAt = DateTime.UtcNow,
                    Competitions = competitions,
                    Sections = new List<SeriesResultSection>(),
                    UnsupportedMessage = $"Serieberäkning stöds inte för tävlingstypen {competitionType}."
                };
            }

            var competitionIds = competitions.Select(c => c.CompetitionId).ToList();
            var scores = await scoreSource.FetchAsync(competitionIds, competitionType);

            // Build context and calculate
            var context = new SeriesCalculationContext
            {
                SeriesId = seriesContentId,
                SeriesName = series.GetValue<string>("seriesName") ?? series.Name,
                Competitions = competitions,
                Parameters = parameters,
                CompetitionResults = scores.ByCompetition
            };

            var result = strategy.Calculate(context);
            result.ScoreLabel = scores.ScoreLabel;
            result.SecondaryLabel = scores.SecondaryLabel;

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
    }
}
