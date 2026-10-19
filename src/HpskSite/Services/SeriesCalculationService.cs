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

        /// <summary>Nycklarna den här instansen cachat — så att cachesignalen kan tömma alla (skalplanen D4).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CachedKeys = new();

        /// <summary>Tömmer den här instansens serieresultat (anropas av cachesignalen).</summary>
        public static void ClearLocal(Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
        {
            foreach (var key in CachedKeys.Keys) { cache.Remove(key); CachedKeys.TryRemove(key, out _); }
        }

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _seriesStore;

        public SeriesCalculationService(
            IContentService contentService,
            IEnumerable<ISeriesScoreSource> scoreSources,
            IMemoryCache cache,
            ILogger<SeriesCalculationService> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Competitions.ICompetitionSeriesStore seriesStore)
        {
            _competitions = competitions;
            _seriesStore = seriesStore;
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

            // C3-planen §5d: serien och omgångarna ur lagringarna (det sparade läget, som content service gav).
            var series = _seriesStore.GetSaved(seriesContentId);
            if (series == null)
            {
                _logger.LogWarning("Series not found or wrong type: {SeriesId}", seriesContentId);
                return null;
            }

            var strategyId = series.SeriesCalculationStrategy ?? "";
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
            var configJson = series.SeriesCalculationConfig ?? "{}";
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
            // Barnen ur trädet (seriens struktur), varje omgång ur tävlingslagringen i SPARAT läge, som
            // content service gav. seriesSortOrder har aldrig funnits på doktypen (CompetitionFieldTable),
            // så ordningen är datumet.
            var childCompetitions = _competitions.ListAllInSeries(seriesContentId)
                .OrderBy(c => c.CompetitionDate ?? DateTime.MinValue)
                .ToList();

            var competitions = childCompetitions
                .Select(c => new SeriesCompetitionInfo
                {
                    CompetitionId = c.Id,
                    Name = c.CompetitionName ?? c.Name,
                    Date = c.CompetitionDate ?? DateTime.MinValue
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
            var competitionType = childCompetitions[0].CompetitionType ?? "";
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
                SeriesName = series.DisplayName,
                Competitions = competitions,
                Parameters = parameters,
                CompetitionResults = scores.ByCompetition
            };

            var result = strategy.Calculate(context);
            result.ScoreLabel = scores.ScoreLabel;
            result.SecondaryLabel = scores.SecondaryLabel;

            // Cache the result
            _cache.Set(cacheKey, result, CacheDuration);
            CachedKeys[cacheKey] = 0;
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
                // C3-planen §5d: serien ur lagringen, inte nodens förälder.
                var seriesId = _competitions.GetSaved(competitionId)?.SeriesId;
                if (seriesId is > 0)
                {
                    var cacheKey = CacheKeyPrefix + seriesId.Value;
                    _cache.Remove(cacheKey);
                    HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SeriesResults);
                    _logger.LogDebug("Invalidated series results cache for series {SeriesId} (competition {CompId} changed)",
                        seriesId.Value, competitionId);
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
            HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SeriesResults);
            _logger.LogDebug("Invalidated series results cache for series {SeriesId}", seriesId);
        }
    }
}
