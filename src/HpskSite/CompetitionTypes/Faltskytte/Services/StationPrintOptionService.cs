using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>Stationskortets utskriftsläge (fas 3b). Lagrade strängar — byt aldrig en befintlig.</summary>
    public static class StationPrintMode
    {
        public const string Full = "Full";
        public const string Kompakt = "Kompakt";
        public static bool IsValid(string? mode) => mode is Full or Kompakt;
    }

    [TableName("FaltskytteStationPrintOption")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class FaltskytteStationPrintOption
    {
        public int Id { get; set; }
        public int ConfigId { get; set; }
        public int StationNumber { get; set; }
        public string Mode { get; set; } = StationPrintMode.Kompakt;
        public DateTime UpdatedAt { get; set; }
        public int? UpdatedByMemberId { get; set; }
    }

    /// <summary>
    /// Full eller Kompakt per station, i en egen tabell utanför konfigurationens JsonBlob (en ändring ska varken
    /// ogiltigförklara godkännandet eller röra versionen). Ingen rad = Full. Läsningen faller tillbaka på Full om
    /// tabellen inte går att läsa (loggas) — kortet ska aldrig utebli för att ett utskriftsval saknas.
    /// </summary>
    public class StationPrintOptionService
    {
        private readonly IScopeProvider _scopes;
        private readonly ILogger<StationPrintOptionService> _logger;

        public StationPrintOptionService(IScopeProvider scopes, ILogger<StationPrintOptionService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        /// <summary>Stationerna i konfigurationen som skrivs ut Kompakt.</summary>
        public HashSet<int> CompactStations(int configId)
        {
            if (configId <= 0) return new HashSet<int>();
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                return s.Database.Fetch<int>("SELECT StationNumber FROM FaltskytteStationPrintOption WHERE ConfigId = @0 AND Mode = @1",
                    configId, StationPrintMode.Kompakt).ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa stationskortens utskriftsläge för konfiguration {ConfigId}; skriver ut Full.", configId);
                return new HashSet<int>();
            }
        }

        /// <summary>Sätter läget. Full tar bort raden. Kastar vid databasfel (användaren ska få veta att valet inte sparades).</summary>
        public void Set(int configId, int station, string mode, int? memberId)
        {
            if (configId <= 0 || station <= 0) throw new ArgumentException("Konfiguration och station krävs.");
            if (!StationPrintMode.IsValid(mode)) throw new ArgumentException("Okänt läge.");
            using var s = _scopes.CreateScope(autoComplete: true);
            if (mode == StationPrintMode.Full)
            {
                s.Database.Execute("DELETE FROM FaltskytteStationPrintOption WHERE ConfigId = @0 AND StationNumber = @1", configId, station);
                return;
            }
            s.Database.Execute(@"
IF EXISTS (SELECT 1 FROM FaltskytteStationPrintOption WITH (UPDLOCK, HOLDLOCK) WHERE ConfigId = @0 AND StationNumber = @1)
    UPDATE FaltskytteStationPrintOption SET Mode = @2, UpdatedAt = @3, UpdatedByMemberId = @4 WHERE ConfigId = @0 AND StationNumber = @1
ELSE
    INSERT INTO FaltskytteStationPrintOption (ConfigId, StationNumber, Mode, UpdatedAt, UpdatedByMemberId) VALUES (@0, @1, @2, @3, @4)",
                configId, station, mode, DateTime.Now, memberId);
        }
    }
}
