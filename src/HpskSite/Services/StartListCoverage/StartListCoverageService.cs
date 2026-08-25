using Umbraco.Cms.Core.Models;

namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// Picks the coverage reader for a competition's discipline.
    ///
    /// Springskytte is deliberately NOT here: it has had `Springskytte/GetStartListCoverage` since
    /// 2026-08-05, including stafett-team coverage that no other discipline has a concept of.
    /// Re-implementing it to fit this seam would risk changing behaviour on the one surface that
    /// already works. The client picks the endpoint by discipline; fold Springskytte in only if its
    /// own endpoint is being touched anyway.
    /// </summary>
    public sealed class StartListCoverageService
    {
        private readonly IEnumerable<IStartListCoverageSource> _sources;

        public StartListCoverageService(IEnumerable<IStartListCoverageSource> sources) => _sources = sources;

        public async Task<StartListCoverageResult> BuildAsync(IContent competition)
        {
            var type = competition.GetValue<string>("competitionType");

            var source = _sources.FirstOrDefault(s => s.Supports(type));
            if (source == null)
                return new StartListCoverageResult { Supported = false };

            return await source.BuildAsync(competition);
        }
    }
}
