using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// One medal won at one of our own competitions, ready to be written to the ledger.
    /// </summary>
    public record OnSiteMedal(int MemberId, string? ShootingClass, string MedalType);

    /// <summary>
    /// Materializes Standard medals won at our OWN competitions into the StandardMedalAward
    /// ledger when results become official. On-site medals are auto-Verified (the published
    /// result page is the proof). Re-publish is idempotent: medals are upserted by
    /// (CompetitionId, MemberId, Discipline, ShootingClass), and medals that disappear on a
    /// recompute are removed — except any already consumed by a Guldmedalj application.
    /// </summary>
    public class StandardMedalMaterializationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<StandardMedalMaterializationService> _logger;

        public StandardMedalMaterializationService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<StandardMedalMaterializationService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Reconcile the OnSite awards for a competition with the set of medals just computed.
        /// </summary>
        public async Task UpsertOnSiteMedalsAsync(
            int competitionId,
            string discipline,
            int year,
            string? competitionName,
            DateTime? competitionDate,
            IEnumerable<OnSiteMedal> medals)
        {
            using var db = _databaseFactory.CreateDatabase();

            var existing = await db.FetchAsync<StandardMedalAward>(
                "WHERE CompetitionId = @0 AND Source = @1", competitionId, StandardMedals.SourceOnSite);

            var incoming = (medals ?? Enumerable.Empty<OnSiteMedal>())
                .Where(m => m.MemberId > 0 && StandardMedals.IsMedal(m.MedalType))
                .ToList();

            var keptIds = new HashSet<int>();
            int inserted = 0, updated = 0, removed = 0;

            foreach (var m in incoming)
            {
                var match = existing.FirstOrDefault(e =>
                    e.MemberId == m.MemberId &&
                    string.Equals(e.Discipline, discipline, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.ShootingClass ?? "", m.ShootingClass ?? "", StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    keptIds.Add(match.Id);
                    // Locked into a Gold application — its points back a reserved 50, so never mutate
                    // it (keep it in keptIds so the delete sweep below also leaves it alone).
                    if (match.GoldApplicationId.HasValue)
                        continue;
                    // Refresh medal type / descriptive fields; never touch Status or GoldApplicationId.
                    bool changed =
                        match.MedalType != m.MedalType ||
                        match.Year != year ||
                        match.CompetitionName != competitionName ||
                        match.CompetitionDate != competitionDate;
                    if (changed)
                    {
                        match.MedalType = m.MedalType;
                        match.Points = StandardMedals.PointsFor(m.MedalType);
                        match.Year = year;
                        match.CompetitionName = competitionName;
                        match.CompetitionDate = competitionDate;
                        match.UpdatedAt = DateTime.Now;
                        await db.UpdateAsync(match);
                        updated++;
                    }
                }
                else
                {
                    var award = new StandardMedalAward
                    {
                        MemberId = m.MemberId,
                        Year = year,
                        Discipline = discipline,
                        MedalType = m.MedalType,
                        Points = StandardMedals.PointsFor(m.MedalType),
                        Source = StandardMedals.SourceOnSite,
                        CompetitionId = competitionId,
                        CompetitionName = competitionName,
                        CompetitionDate = competitionDate,
                        ShootingClass = m.ShootingClass,
                        ProofType = StandardMedals.ProofOnSite,
                        Status = StandardMedals.StatusVerified, // our own result page is the proof
                        EnteredByMemberId = 0,                  // system/auto materialization
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    await db.InsertAsync(award);
                    keptIds.Add(award.Id);
                    inserted++;
                }
            }

            // Medals that no longer exist on recompute — drop them, unless locked into a Gold app.
            foreach (var e in existing)
            {
                if (!keptIds.Contains(e.Id) && !e.GoldApplicationId.HasValue)
                {
                    await db.ExecuteAsync("DELETE FROM StandardMedalAward WHERE Id = @0", e.Id);
                    removed++;
                }
            }

            _logger.LogInformation(
                "Materialized standard medals for competition {CompetitionId} ({Discipline}): +{Inserted} ~{Updated} -{Removed}",
                competitionId, discipline, inserted, updated, removed);
        }

        /// <summary>
        /// True if any OnSite award already exists for this competition. Gates the one-time lazy
        /// backfill of competitions that went official before this ledger existed (see
        /// CompetitionResultsController.GetResultsList) so the upsert runs only when nothing is stored yet.
        /// </summary>
        public async Task<bool> HasOnSiteAwardsAsync(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var counts = await db.FetchAsync<int>(
                "SELECT COUNT(*) FROM StandardMedalAward WHERE CompetitionId = @0 AND Source = @1",
                competitionId, StandardMedals.SourceOnSite);
            return counts.FirstOrDefault() > 0;
        }

        /// <summary>
        /// Remove all OnSite awards for a competition (used when results are un-published / set
        /// back to preliminary). Awards consumed by a Guldmedalj application are left intact.
        /// </summary>
        public async Task RemoveOnSiteForCompetitionAsync(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var affected = await db.ExecuteAsync(
                @"DELETE FROM StandardMedalAward
                   WHERE CompetitionId = @0 AND Source = @1 AND GoldApplicationId IS NULL",
                competitionId, StandardMedals.SourceOnSite);
            if (affected > 0)
                _logger.LogInformation(
                    "Removed {Count} on-site standard medals for un-published competition {CompetitionId}",
                    affected, competitionId);
        }
    }
}
