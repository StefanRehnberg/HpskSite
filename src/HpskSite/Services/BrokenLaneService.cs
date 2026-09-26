using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Trasiga banor (skjutplatser ur funktion) per tävling — tabellen CompetitionBrokenLane.
    ///
    /// Per TÄVLING, inte per startlista: grundomgång och final skjuts på samma banor, och en bana
    /// som går sönder gäller alla skjutlag. En lagad bana tas bort ur listan och blir då ledig igen.
    ///
    /// Läsningen degraderar till "inga trasiga banor" om tabellen saknas, så ett omigrerat läge
    /// beter sig som före funktionen. Skrivningen VÄGRAR och namnger migreringen i stället för att
    /// låtsas att banan markerades.
    /// </summary>
    public class BrokenLaneService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<BrokenLaneService> _logger;

        public const string MigrationFile = "create-competition-broken-lane-table.sql";

        public BrokenLaneService(IUmbracoDatabaseFactory databaseFactory, ILogger<BrokenLaneService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        public HashSet<int> Get(int competitionId)
        {
            if (competitionId <= 0) return new HashSet<int>();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.Fetch<int>(
                    "SELECT LaneNumber FROM dbo.CompetitionBrokenLane WHERE CompetitionId = @0", competitionId)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Kunde inte läsa CompetitionBrokenLane för tävling {CompetitionId} — har {Migration} körts?",
                    competitionId, MigrationFile);
                return new HashSet<int>();
            }
        }

        /// <summary>
        /// Ersätter tävlingens trasiga banor med <paramref name="lanes"/>. En tom lista = alla banor
        /// är hela igen.
        /// </summary>
        public (bool Ok, string? Error) Set(int competitionId, IEnumerable<int> lanes, string? markedBy)
        {
            var wanted = (lanes ?? Enumerable.Empty<int>()).Where(l => l > 0).Distinct().OrderBy(l => l).ToList();
            if (wanted.Any(l => l > 500))
                return (false, "En bana kan inte ha högre nummer än 500.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                using var tx = db.GetTransaction();
                db.Execute("DELETE FROM dbo.CompetitionBrokenLane WHERE CompetitionId = @0", competitionId);
                foreach (var lane in wanted)
                {
                    db.Execute(
                        "INSERT INTO dbo.CompetitionBrokenLane (CompetitionId, LaneNumber, MarkedBy) VALUES (@0, @1, @2)",
                        competitionId, lane, markedBy);
                }
                tx.Complete();
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte spara trasiga banor för tävling {CompetitionId}", competitionId);
                return (false, $"Trasiga banor kunde inte sparas. Har migreringen {MigrationFile} körts?");
            }
        }
    }
}
