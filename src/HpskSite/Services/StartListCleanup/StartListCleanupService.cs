using Umbraco.Cms.Core.Models;

namespace HpskSite.Services.StartListCleanup
{
    /// <summary>
    /// Picks the cleanup source for a competition's discipline.
    ///
    /// Springskytte is deliberately absent: it already does this from the client
    /// (`Springskytte/CleanupDeletedRegistration`, plus its own membership warning) and it works.
    /// With no source registered for it this service no-ops there, so the existing path keeps
    /// running untouched. Do not add a Springskytte source without removing the client call —
    /// the two would both run and the second would report "0 freed", which reads like a failure.
    /// </summary>
    public sealed class StartListCleanupService
    {
        private readonly IEnumerable<IStartListCleanupSource> _sources;
        private readonly ILogger<StartListCleanupService> _logger;

        public StartListCleanupService(IEnumerable<IStartListCleanupSource> sources, ILogger<StartListCleanupService> logger)
        {
            _sources = sources;
            _logger = logger;
        }

        private IStartListCleanupSource? SourceFor(IContent competition) =>
            _sources.FirstOrDefault(s => s.Supports(competition.GetValue<string>("competitionType")));

        public async Task<List<StartListPlacement>> DescribePlacementsAsync(IContent competition, int memberId)
        {
            var source = SourceFor(competition);
            if (source == null) return new List<StartListPlacement>();
            try { return await source.DescribePlacementsAsync(competition, memberId); }
            catch (Exception ex)
            {
                // A failed warning must never block a deletion the operator is entitled to make.
                _logger.LogWarning(ex, "Could not describe start-list placement for member {MemberId} on competition {CompetitionId}", memberId, competition.Id);
                return new List<StartListPlacement>();
            }
        }

        /// <summary>
        /// Runs AFTER the registration is deleted. Best-effort by design: the registration is
        /// already gone, so throwing here would report a failed deletion that actually succeeded and
        /// invite the operator to try again.
        /// </summary>
        public async Task<CleanupOutcome> CleanupAsync(IContent competition, int memberId, string? onlyShootingClass = null)
        {
            var source = SourceFor(competition);
            if (source == null) return new CleanupOutcome { Supported = false };
            try { return await source.CleanupAsync(competition, memberId, onlyShootingClass); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Start-list cleanup failed for member {MemberId} on competition {CompetitionId}", memberId, competition.Id);
                return new CleanupOutcome
                {
                    Warnings = { "Startlistan kunde inte städas automatiskt — kontrollera den manuellt." }
                };
            }
        }
    }
}
