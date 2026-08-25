using HpskSite.CompetitionTypes.Precision.Controllers;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// Fältskytte / MagnumFält keep their start units — patrols — in SQL, not in a content node.
    /// </summary>
    public sealed class FaltskytteStartListCoverageSource : IStartListCoverageSource
    {
        private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        {
            "Faltskytte", "MagnumFalt"
        };

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly UmbracoStartListRepository _repository;
        private readonly ILogger<FaltskytteStartListCoverageSource> _logger;

        public FaltskytteStartListCoverageSource(
            IUmbracoDatabaseFactory databaseFactory,
            UmbracoStartListRepository repository,
            ILogger<FaltskytteStartListCoverageSource> logger)
        {
            _databaseFactory = databaseFactory;
            _repository = repository;
            _logger = logger;
        }

        public bool Supports(string? competitionType) =>
            !string.IsNullOrWhiteSpace(competitionType) && Types.Contains(competitionType.Trim());

        public async Task<StartListCoverageResult> BuildAsync(IContent competition)
        {
            var placedRows = new List<CoverageBuilder.PlacedRow>();
            var patrolCount = 0;

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                patrolCount = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM FaltskyttePatrol WHERE CompetitionId = @0", competition.Id);

                // A DNS member still occupies a place on the list — they were placed and then
                // withdrew. Excluding them here would present a withdrawal as a planning gap.
                var rows = await db.FetchAsync<PatrolAssignment>(
                    @"SELECT pm.MemberId, pm.ShootingClass, pm.MemberName, pm.ClubName
                      FROM FaltskyttePatrolMember pm
                      INNER JOIN FaltskyttePatrol p ON pm.PatrolId = p.Id
                      WHERE p.CompetitionId = @0", competition.Id);

                foreach (var row in rows)
                    if (row.MemberId > 0)
                        placedRows.Add(new CoverageBuilder.PlacedRow(
                            row.MemberId, row.MemberName ?? "", row.ClubName ?? "",
                            row.ShootingClass ?? "", CoverageKeys.WeaponGroupOf(row.ShootingClass)));
            }
            catch (Exception ex)
            {
                // An un-migrated environment must degrade to "cannot tell", never to a report
                // claiming every shooter is unplaced.
                _logger.LogError(ex, "Could not read Fältskytte patrols for coverage on competition {CompetitionId}", competition.Id);
                return new StartListCoverageResult { Supported = false, UnitLabel = "patrull" };
            }

            var registrations = await _repository.GetCompetitionRegistrations(competition.Id);

            // Collapse to one required start per (member, weapon group) — a patrol walks the whole
            // course once, so C1 and C2 are the same start.
            var required = registrations
                .Where(r => r.MemberId > 0 && !string.IsNullOrWhiteSpace(r.MemberClass))
                .GroupBy(r => CoverageKeys.For(r.MemberId, CoverageKeys.WeaponGroupOf(r.MemberClass)))
                .Select(g => g.First())
                .ToList();

            return CoverageBuilder.Build(required
                .Select(r => new CoverageBuilder.Row(
                    r.MemberId, r.MemberName ?? "", r.MemberClub ?? "", r.MemberClass ?? "",
                    KeyClass: CoverageKeys.WeaponGroupOf(r.MemberClass)))
                .ToList(),
                placedRows, patrolCount > 0, "patrull");
        }

        private sealed class PatrolAssignment
        {
            public int MemberId { get; set; }
            public string ShootingClass { get; set; } = "";
            public string? MemberName { get; set; }
            public string? ClubName { get; set; }
        }
    }
}
