using HpskSite.Models.Results;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// Resultatlistorna i tabellen <c>CompetitionResultList</c> (skalplanen fas C1). Väljs av
    /// <see cref="SwitchingCompetitionResultStore"/> när växeln <c>Results:Store</c> står på <c>table</c>.
    ///
    /// <para><b>Inget Umbraco-lås, ingen publicering.</b> En omräkning är en rad i en transaktion — det
    /// globala innehållslåset (-333) som varje nodsparning köar i är borta.</para>
    ///
    /// <para><b>⚠️ Id:n.</b> Migrerade rader bär nodens id (IDENTITY_INSERT), så en referens till
    /// resultatnodens id pekar fortfarande rätt.</para>
    ///
    /// <para><see cref="Update"/> läser raden med <c>UPDLOCK, HOLDLOCK</c> i samma transaktion som den
    /// skriver, så två samtidiga skrivningar till samma lista köar i stället för att den ena skriver
    /// tillbaka ett gammalt värde. Unikt index på tävlingen gör en andra rad omöjlig.</para>
    ///
    /// <para>Använder <see cref="IScopeProvider"/>, aldrig en egen anslutning: lagringen anropas ibland
    /// inifrån någon annans scope, och en egen anslutning där låser.</para>
    /// </summary>
    public class SqlCompetitionResultStore : ICompetitionResultStore
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<SqlCompetitionResultStore> _logger;

        public SqlCompetitionResultStore(IScopeProvider scopeProvider, ILogger<SqlCompetitionResultStore> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public const string Table = "dbo.CompetitionResultList";

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public CompetitionResultRecord? Get(int competitionId)
        {
            if (competitionId <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.FirstOrDefault<Row>($"SELECT * FROM {Table} WHERE CompetitionId = @0", competitionId);
            return row is null ? null : ToRecord(row);
        }

        public IReadOnlyDictionary<int, CompetitionResultRecord> GetMany(IReadOnlyCollection<int> competitionIds)
        {
            var result = new Dictionary<int, CompetitionResultRecord>();
            var ids = competitionIds?.Where(i => i > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return result;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // ⚠️ IN (@0) tar slut kring 2100 parametrar och gör det tyst — i bitar om 1000.
            foreach (var chunk in ids.Chunk(1000))
            {
                foreach (var row in scope.Database.Fetch<Row>($"SELECT * FROM {Table} WHERE CompetitionId IN (@0)", chunk))
                    result[row.CompetitionId] = ToRecord(row);
            }
            return result;
        }

        public IReadOnlyDictionary<int, bool> OfficialByCompetition(IReadOnlyCollection<int> competitionIds)
        {
            var result = new Dictionary<int, bool>();
            var ids = competitionIds?.Where(i => i > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return result;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // ⚠️ IN (@0) tar slut kring 2100 parametrar och gör det tyst — i bitar om 1000.
            foreach (var chunk in ids.Chunk(1000))
                foreach (var row in scope.Database.Fetch<OfficialRow>($"SELECT CompetitionId, IsOfficial FROM {Table} WHERE CompetitionId IN (@0)", chunk))
                    result[row.CompetitionId] = row.IsOfficial;
            return result;
        }

        private sealed class OfficialRow
        {
            public int CompetitionId { get; set; }
            public bool IsOfficial { get; set; }
        }

        public bool CanStore(CompetitionResultField field) => true;

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public CompetitionResultRecord Ensure(int competitionId) =>
            Update(competitionId, _ => { }, createIfMissing: true)
            ?? throw new InvalidOperationException($"Resultatlistan för tävling {competitionId} kunde inte skapas.");

        public CompetitionResultRecord? Update(int competitionId, Action<CompetitionResultRecord> change, bool createIfMissing = false)
        {
            if (competitionId <= 0) throw new ArgumentOutOfRangeException(nameof(competitionId));

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var row = db.FirstOrDefault<Row>($"SELECT * FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0", competitionId);
            bool created = false;
            if (row is null)
            {
                if (!createIfMissing) { scope.Complete(); return null; }
                var now = DateTime.Now;
                var id = db.ExecuteScalar<int>($@"
INSERT INTO {Table} (CompetitionId, ResultType, IsOfficial, SubIsOfficial, LastUpdated, CreatedDate, UpdatedDate)
VALUES (@0, @1, 0, 0, @2, @2, @2);
SELECT CAST(SCOPE_IDENTITY() AS INT);", competitionId, CompetitionResultFields.DefaultResultType, now);
                row = db.First<Row>($"SELECT * FROM {Table} WHERE Id = @0", id);
                created = true;
            }

            var before = ToRecord(row);
            var after = before.Clone();
            change(after);

            if (!created && !Differs(before, after))
            {
                scope.Complete();
                return before;
            }

            db.Execute($@"
UPDATE {Table} SET
    ResultType = @1, ResultData = @2, IsOfficial = @3, SubIsOfficial = @4,
    MergeConfig = @5, SubMergeConfig = @6, ClassNameOverrides = @7, SubClassNameOverrides = @8,
    ShootOffConfig = @9, SubShootOffConfig = @10, HonoraryAwardConfig = @11,
    LastUpdated = @12, UpdatedDate = @13
WHERE Id = @0",
                row.Id,
                string.IsNullOrWhiteSpace(after.ResultType) ? CompetitionResultFields.DefaultResultType : after.ResultType,
                Null(after.ResultData), after.IsOfficial, after.SubIsOfficial,
                Null(after.MergeConfig), Null(after.SubMergeConfig),
                Null(after.ClassNameOverrides), Null(after.SubClassNameOverrides),
                Null(after.ShootOffConfig), Null(after.SubShootOffConfig),
                Null(after.HonoraryAwardConfig),
                after.LastUpdated, DateTime.Now);
            scope.Complete();

            after.Id = row.Id;
            after.CompetitionId = competitionId;
            after.IsPublished = true;
            return after;
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────────────

        /// <summary>Raden som den ligger i tabellen. NPoco mappar på kolumnnamnen.</summary>
        [TableName("CompetitionResultList")]
        [PrimaryKey("Id")]
        public class Row
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public string? ResultType { get; set; }
            public string? ResultData { get; set; }
            public bool IsOfficial { get; set; }
            public bool SubIsOfficial { get; set; }
            public string? MergeConfig { get; set; }
            public string? SubMergeConfig { get; set; }
            public string? ClassNameOverrides { get; set; }
            public string? SubClassNameOverrides { get; set; }
            public string? ShootOffConfig { get; set; }
            public string? SubShootOffConfig { get; set; }
            public string? HonoraryAwardConfig { get; set; }
            public DateTime? LastUpdated { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime UpdatedDate { get; set; }
        }

        internal static CompetitionResultRecord ToRecord(Row r) => new()
        {
            Id = r.Id,
            CompetitionId = r.CompetitionId,
            ResultType = string.IsNullOrWhiteSpace(r.ResultType) ? CompetitionResultFields.DefaultResultType : r.ResultType!,
            ResultData = r.ResultData ?? "",
            IsOfficial = r.IsOfficial,
            SubIsOfficial = r.SubIsOfficial,
            MergeConfig = r.MergeConfig ?? "",
            SubMergeConfig = r.SubMergeConfig ?? "",
            ClassNameOverrides = r.ClassNameOverrides ?? "",
            SubClassNameOverrides = r.SubClassNameOverrides ?? "",
            ShootOffConfig = r.ShootOffConfig ?? "",
            SubShootOffConfig = r.SubShootOffConfig ?? "",
            HonoraryAwardConfig = r.HonoraryAwardConfig ?? "",
            LastUpdated = r.LastUpdated,
            IsPublished = true,
        };

        /// <summary>Om något sparat fält skiljer sig. Lika regel som nodlagringens diff.</summary>
        internal static bool Differs(CompetitionResultRecord a, CompetitionResultRecord b) =>
            a.ResultType != b.ResultType
            || (a.ResultData ?? "") != (b.ResultData ?? "")
            || a.IsOfficial != b.IsOfficial
            || a.SubIsOfficial != b.SubIsOfficial
            || (a.MergeConfig ?? "") != (b.MergeConfig ?? "")
            || (a.SubMergeConfig ?? "") != (b.SubMergeConfig ?? "")
            || (a.ClassNameOverrides ?? "") != (b.ClassNameOverrides ?? "")
            || (a.SubClassNameOverrides ?? "") != (b.SubClassNameOverrides ?? "")
            || (a.ShootOffConfig ?? "") != (b.ShootOffConfig ?? "")
            || (a.SubShootOffConfig ?? "") != (b.SubShootOffConfig ?? "")
            || (a.HonoraryAwardConfig ?? "") != (b.HonoraryAwardConfig ?? "")
            || a.LastUpdated != b.LastUpdated;

        private static string? Null(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
