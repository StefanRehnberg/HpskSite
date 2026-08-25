using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.StartListCleanup
{
    /// <summary>
    /// Fältskytte / MagnumFält — the start unit is a patrol row in SQL, so cleanup is a targeted
    /// DELETE rather than a content-node rewrite. Nothing needs re-publishing: the patrol list is
    /// read live from the tables.
    /// </summary>
    public sealed class FaltskytteStartListCleanupSource : IStartListCleanupSource
    {
        private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            "Faltskytte", "MagnumFalt"
        };

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<FaltskytteStartListCleanupSource> _logger;

        public FaltskytteStartListCleanupSource(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<FaltskytteStartListCleanupSource> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        public bool Supports(string? competitionType) =>
            !string.IsNullOrWhiteSpace(competitionType) && Types.Contains(competitionType.Trim());

        public async Task<List<StartListPlacement>> DescribePlacementsAsync(IContent competition, int memberId)
        {
            var found = new List<StartListPlacement>();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var rows = await db.FetchAsync<PatrolPlacementRow>(
                    @"SELECT p.PatrolNumber, p.StartTime, p.Label, pm.Position, pm.ShootingClass
                      FROM FaltskyttePatrolMember pm
                      INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id
                      WHERE p.CompetitionId = @0 AND pm.MemberId = @1
                      ORDER BY p.PatrolNumber, pm.Position", competition.Id, memberId);

                foreach (var r in rows)
                {
                    var label = string.IsNullOrWhiteSpace(r.Label) ? "" : $" ({r.Label})";
                    found.Add(new StartListPlacement
                    {
                        ListName = "Patrullista",
                        Where = $"Patrull {r.PatrolNumber}{label}, plats {r.Position}",
                        StartTime = r.StartTime?.ToString("HH:mm") ?? "",
                        ShootingClass = r.ShootingClass ?? "",
                        // The patrol list is published as a whole (faltskyttePatrolsPublished), not
                        // per patrol, and the page reads live — so there is nothing to re-publish.
                        IsPublished = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read Fältskytte patrol placement for member {MemberId}", memberId);
            }
            return found;
        }

        public async Task<CleanupOutcome> CleanupAsync(IContent competition, int memberId, string? onlyShootingClass = null)
        {
            // A patrol is per WEAPON GROUP, so a scoped removal narrows to the group — clearing
            // "C1" must not also pull the shooter out of their A patrol.
            var onlyGroup = string.IsNullOrWhiteSpace(onlyShootingClass)
                ? null
                : StartListCoverage.CoverageKeys.WeaponGroupOf(onlyShootingClass);

            var warnings = new List<string>();
            var freed = 0;
            var deleted = 0;

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                // Scoped through the patrol join so a member id can never reach another
                // competition's patrols. LEFT(ShootingClass,1) is the same weapon-group test the
                // assign path uses.
                freed = onlyGroup == null
                    ? await db.ExecuteAsync(
                        @"DELETE pm FROM FaltskyttePatrolMember pm
                          INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id
                          WHERE p.CompetitionId = @0 AND pm.MemberId = @1", competition.Id, memberId)
                    : await db.ExecuteAsync(
                        @"DELETE pm FROM FaltskyttePatrolMember pm
                          INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id
                          WHERE p.CompetitionId = @0 AND pm.MemberId = @1
                            AND LEFT(pm.ShootingClass, 1) = @2", competition.Id, memberId, onlyGroup);

                // Empty patrols are deliberately LEFT in place. The patrol number is printed on
                // station cards and referred to all day; deleting patrol 4 because its last shooter
                // withdrew would renumber the field around the organiser.
                var groupFilter = onlyGroup == null ? "" : " AND LEFT(ShootingClass, 1) = @2";
                var args = onlyGroup == null
                    ? new object[] { competition.Id, memberId }
                    : new object[] { competition.Id, memberId, onlyGroup };

                deleted += await db.ExecuteAsync(
                    $"DELETE FROM FaltskytteResultEntry WHERE CompetitionId=@0 AND MemberId=@1{groupFilter}", args);
                try
                {
                    deleted += await db.ExecuteAsync(
                        $"DELETE FROM FaltskytteShootOffEntry WHERE CompetitionId=@0 AND MemberId=@1{groupFilter}", args);
                }
                catch (Exception ex)
                {
                    // Its migration may not have run; the patrol + result cleanup above is the
                    // valuable half and must not be lost to this.
                    _logger.LogWarning(ex, "Cleanup: Fältskytte shoot-off delete failed (table may be missing)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fältskytte cleanup failed for competition {CompetitionId} member {MemberId}", competition.Id, memberId);
                warnings.Add("Skytten kunde inte tas bort ur patrullen automatiskt — kontrollera patrullistan.");
            }

            return new CleanupOutcome
            {
                SlotsFreed = freed,
                ResultRowsDeleted = deleted,
                Warnings = warnings
            };
        }

        private sealed class PatrolPlacementRow
        {
            public int PatrolNumber { get; set; }
            public DateTime? StartTime { get; set; }
            public string? Label { get; set; }
            public int Position { get; set; }
            public string? ShootingClass { get; set; }
        }
    }
}
