using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Manual klubb-/kretsmästare CRUD. Champions are typed in by admins (no auto
    /// computation from competition results — many clubs don't enter results in pistol.nu).
    /// "Reigning" champion = highest Year per (Level, ScopeId, Discipline, ChampionType, ClassCode).
    /// </summary>
    public class CompetitionChampionsService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<CompetitionChampionsService> _logger;

        public CompetitionChampionsService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<CompetitionChampionsService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the reigning champion entry for each (Discipline, ChampionType, ClassCode)
        /// in the given scope — that is, the row with the highest Year for each key.
        /// </summary>
        public async Task<List<CompetitionChampion>> GetReigningForScopeAsync(string level, string scopeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var all = await db.FetchAsync<CompetitionChampion>(
                @"WHERE Level = @0 AND ScopeId = @1 ORDER BY Year DESC, Id DESC",
                level, scopeId);
            return all
                .GroupBy(c => new { c.Discipline, c.ChampionType, c.ClassCode })
                .Select(g => g.First())   // already date-desc ordered
                .ToList();
        }

        /// <summary>
        /// All champion entries for a scope across all years and classes — used by the
        /// admin tab to surface complete history for backfilling and review.
        /// </summary>
        public async Task<List<CompetitionChampion>> GetAllForScopeAsync(string level, string scopeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionChampion>(
                @"WHERE Level = @0 AND ScopeId = @1 ORDER BY Discipline, ChampionType, ClassCode, Year DESC, Id DESC",
                level, scopeId);
        }

        public async Task<List<CompetitionChampion>> GetHistoryAsync(
            string level, string scopeId, string discipline, string championType, string classCode)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionChampion>(
                @"WHERE Level = @0 AND ScopeId = @1 AND Discipline = @2
                    AND ChampionType = @3 AND ClassCode = @4
                  ORDER BY Year DESC, Id DESC",
                level, scopeId, discipline, championType, classCode);
        }

        public async Task<List<CompetitionChampion>> GetForMemberAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionChampion>(
                "WHERE HolderMemberId = @0 ORDER BY Year DESC, Id DESC", memberId);
        }

        public async Task<CompetitionChampion?> GetByIdAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultByIdAsync<CompetitionChampion>(id);
        }

        // ── Writes ────────────────────────────────────────────────────

        public async Task<(bool Success, int Id, string? Message)> CreateAsync(
            CreateChampionRequest req, int actingMemberId)
        {
            if (req == null) return (false, 0, "Ogiltig begäran.");
            if (req.Year < 1900 || req.Year > 2200) return (false, 0, "Ogiltigt år.");

            if (!RecordClassRegistry.IsValid(req.Discipline, req.ChampionType, req.ClassCode))
                return (false, 0, $"Klassen {req.ClassCode} finns inte för {RecordDisciplines.DisplayName(req.Discipline)} {RecordTypes.DisplayName(req.ChampionType)}.");

            var maxScore = RecordClassRegistry.GetMaxScore(req.Discipline, req.ChampionType);
            if (req.TotalScore < 0 || req.TotalScore > maxScore)
                return (false, 0, $"Poäng {req.TotalScore} är utanför giltigt intervall [0, {maxScore}].");

            if (string.IsNullOrWhiteSpace(req.HolderName))
                return (false, 0, "Skytt eller lagnamn måste anges.");

            using var db = _databaseFactory.CreateDatabase();

            // Check for duplicate (unique business key) — return a friendly message rather
            // than letting the unique index throw.
            var existing = await db.SingleOrDefaultAsync<CompetitionChampion>(
                @"WHERE Level = @0 AND ScopeId = @1 AND Year = @2
                    AND Discipline = @3 AND ChampionType = @4 AND ClassCode = @5",
                req.Level, req.ScopeId, req.Year, req.Discipline, req.ChampionType, req.ClassCode);
            if (existing != null)
            {
                return (false, 0, $"Det finns redan en mästare registrerad för {RecordClassRegistry.GetClassDisplayName(req.ClassCode)} {req.Year}. Ta bort den befintliga först om du vill ändra.");
            }

            var entry = new CompetitionChampion
            {
                Level = req.Level,
                ScopeId = req.ScopeId,
                Year = req.Year,
                Discipline = req.Discipline,
                ChampionType = req.ChampionType,
                ClassCode = req.ClassCode,
                TotalScore = req.TotalScore,
                CompetitionName = req.CompetitionName,
                CompetitionDate = req.CompetitionDate,
                HolderMemberId = req.HolderMemberId,
                HolderName = req.HolderName,
                TeamName = req.TeamName,
                TeamMembersJson = req.TeamMembersJson,
                Notes = req.Notes,
                EnteredByMemberId = actingMemberId,
                EnteredAt = DateTime.UtcNow
            };
            var newId = Convert.ToInt32(await db.InsertAsync(entry));

            _logger.LogInformation(
                "Created champion {Id} ({Level}/{ScopeId}/{Year}/{Discipline}/{ChampionType}/{ClassCode}) by member {ActingMemberId}",
                newId, req.Level, req.ScopeId, req.Year, req.Discipline, req.ChampionType, req.ClassCode, actingMemberId);

            return (true, newId, null);
        }

        public async Task<(bool Success, string? Message)> DeleteAsync(int championId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var rows = await db.ExecuteAsync("DELETE FROM CompetitionChampions WHERE Id = @0", championId);
            if (rows == 0) return (false, "Mästaren hittades inte.");
            _logger.LogInformation("Deleted champion {ChampionId}", championId);
            return (true, null);
        }
    }

    public class CreateChampionRequest
    {
        public string Level { get; set; } = "";
        public string ScopeId { get; set; } = "";
        public int Year { get; set; }
        public string Discipline { get; set; } = "";
        public string ChampionType { get; set; } = "";
        public string ClassCode { get; set; } = "";
        public int TotalScore { get; set; }
        public string? CompetitionName { get; set; }
        public DateTime? CompetitionDate { get; set; }
        public int? HolderMemberId { get; set; }
        public string HolderName { get; set; } = "";
        public string? TeamName { get; set; }
        public string? TeamMembersJson { get; set; }
        public string? Notes { get; set; }
    }
}
