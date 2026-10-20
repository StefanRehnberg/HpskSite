using HpskSite.Models.StartLists;
using NPoco;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// Startlistorna i tabellen <c>CompetitionStartList</c> (skalplanen fas C2). Väljs av
    /// <see cref="SwitchingStartListStore"/> när växeln <c>StartLists:Store</c> står på <c>table</c>.
    ///
    /// <para><b>Inget Umbraco-lås, ingen publicering.</b> En sparad lista är en rad i en transaktion.
    /// Det publika läget avgörs av <see cref="StartListRecord.IsOfficial"/>, som alla publika läsare redan
    /// frågar efter; <see cref="ListPublished"/> är därför samma sak som <see cref="List"/>.
    /// <see cref="StartListPublish"/> ignoreras.</para>
    ///
    /// <para><b>⚠️ Id:n — LAG: id-reglerna (CLAUDE.md).</b> Migrerade rader bär nodens id (IDENTITY_INSERT);
    /// identiteten fortsätter över varje startliste-id någon pekar på (<c>IdentityFloor</c> med
    /// <c>TableIdSources.StartLists</c>, vid flytt, tömning och varje start). Id:n är positiva;
    /// "ingen lista" är 0. Att samma tal kan finnas som en nod av en annan sort är ofarligt — en lista
    /// slås bara upp här.</para>
    ///
    /// <para><b>Namnet är unikt inom tävlingen</b>, som syskonnoder i Umbraco: adressens segment härleds ur
    /// namnet och finnarna matchar listan på segmentet. Ett namn som redan finns får " (1)", " (2)" …</para>
    ///
    /// <para>Använder <see cref="IScopeProvider"/>, aldrig en egen anslutning: lagringen anropas ibland
    /// inifrån någon annans scope, och en egen anslutning där låser.</para>
    /// </summary>
    public class SqlStartListStore : IStartListStore
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IShortStringHelper _shortStrings;
        private readonly ILogger<SqlStartListStore> _logger;

        public SqlStartListStore(IScopeProvider scopeProvider, IShortStringHelper shortStrings, ILogger<SqlStartListStore> logger)
        {
            _scopeProvider = scopeProvider;
            _shortStrings = shortStrings;
            _logger = logger;
        }

        public const string Table = "dbo.CompetitionStartList";

        private const string Order = "ORDER BY LegacyHub, SortOrder, Id";

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<StartListRecord> List(int competitionId, StartListKind? kind = null)
        {
            if (competitionId <= 0) return Array.Empty<StartListRecord>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = kind == null
                ? scope.Database.Fetch<Row>($"SELECT * FROM {Table} WHERE CompetitionId = @0 {Order}", competitionId)
                : scope.Database.Fetch<Row>($"SELECT * FROM {Table} WHERE CompetitionId = @0 AND Kind = @1 {Order}", competitionId, (byte)kind.Value);
            return rows.Select(ToRecord).ToList();
        }

        public IReadOnlyList<StartListRecord> ListPublished(int competitionId, StartListKind? kind = null) => List(competitionId, kind);

        public StartListRecord? Get(int startListId)
        {
            if (startListId == 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.FirstOrDefault<Row>($"SELECT * FROM {Table} WHERE Id = @0", startListId);
            return row is null ? null : ToRecord(row);
        }

        public StartListRecord? Primary(int competitionId)
        {
            var qual = List(competitionId, StartListKind.Qualifying);
            return qual.FirstOrDefault(r => !r.LegacyHub)
                ?? qual.Where(r => r.LegacyHub).OrderByDescending(r => r.GeneratedDate ?? DateTime.MinValue).FirstOrDefault();
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public StartListRecord Create(int competitionId, StartListKind kind, string name, Action<StartListRecord> init,
            StartListPublish publish = StartListPublish.Always)
        {
            if (competitionId <= 0) throw new ArgumentOutOfRangeException(nameof(competitionId));

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            // Tävlingens rader låses medan namnet väljs, så två samtidiga skapanden inte tar samma namn.
            var siblings = db.Fetch<Row>($"SELECT Id, Name, SortOrder FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0", competitionId);

            var record = new StartListRecord
            {
                CompetitionId = competitionId,
                Kind = kind,
                Name = string.IsNullOrWhiteSpace(name) ? StartListNodes.QualifyingName : name.Trim(),
            };
            init(record);
            record.Kind = kind;
            record.CompetitionId = competitionId;
            record.Name = UniqueName(string.IsNullOrWhiteSpace(record.Name) ? StartListNodes.QualifyingName : record.Name.Trim(),
                exceptId: 0, siblings);
            record.UrlSegment = Segment(record.Name);
            record.SortOrder = siblings.Count == 0 ? 0 : siblings.Max(s => s.SortOrder) + 1;

            var now = DateTime.Now;
            var id = db.ExecuteScalar<int>($@"
INSERT INTO {Table}
    (CompetitionId, Kind, Name, UrlSegment, ConfigurationData, StartListContent, IsOfficial, GeneratedDate,
     GeneratedBy, TeamFormat, Notes, QualifyingResultsSnapshot, PerClassConfigData, TotalFinalists,
     MaxShootersPerTeam, QualificationStartListId, LegacyHub, SortOrder, CreatedDate, UpdatedDate)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15, 0, @16, @17, @17);
SELECT CAST(SCOPE_IDENTITY() AS INT);",
                competitionId, (byte)kind, record.Name, record.UrlSegment,
                Null(record.ConfigurationData), Null(record.StartListContent), record.IsOfficial, Truncate(record.GeneratedDate),
                Null(record.GeneratedBy), Null(record.TeamFormat), Null(record.Notes), Null(record.QualifyingResultsSnapshot),
                Null(record.PerClassConfigData), record.TotalFinalists, record.MaxShootersPerTeam, record.QualificationStartListId,
                record.SortOrder, now);
            scope.Complete();

            record.Id = id;
            record.LegacyHub = false;
            record.IsPublished = true;
            record.GeneratedDate = Truncate(record.GeneratedDate);
            return record;
        }

        public StartListRecord? Update(int startListId, Action<StartListRecord> change, StartListPublish publish = StartListPublish.Always)
        {
            if (startListId == 0) return null;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var row = db.FirstOrDefault<Row>($"SELECT * FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0", startListId);
            if (row is null) { scope.Complete(); return null; }

            var before = ToRecord(row);
            var after = before.Clone();
            change(after);
            // Identiteten ägs av lagringen — en ändring får aldrig flytta listan till en annan tävling eller sort.
            after.Id = before.Id;
            after.CompetitionId = before.CompetitionId;
            after.Kind = before.Kind;
            after.LegacyHub = before.LegacyHub;
            after.SortOrder = before.SortOrder;

            if (string.IsNullOrWhiteSpace(after.Name)) after.Name = before.Name;
            if (after.Name != before.Name)
            {
                var siblings = db.Fetch<Row>($"SELECT Id, Name, SortOrder FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE CompetitionId = @0", before.CompetitionId);
                after.Name = UniqueName(after.Name.Trim(), exceptId: before.Id, siblings);
                after.UrlSegment = Segment(after.Name);
            }
            else after.UrlSegment = before.UrlSegment;
            after.GeneratedDate = Truncate(after.GeneratedDate);

            if (!Differs(before, after))
            {
                scope.Complete();
                return before;
            }

            db.Execute($@"
UPDATE {Table} SET
    Name = @1, UrlSegment = @2, ConfigurationData = @3, StartListContent = @4, IsOfficial = @5, GeneratedDate = @6,
    GeneratedBy = @7, TeamFormat = @8, Notes = @9, QualifyingResultsSnapshot = @10, PerClassConfigData = @11,
    TotalFinalists = @12, MaxShootersPerTeam = @13, QualificationStartListId = @14, UpdatedDate = @15
WHERE Id = @0",
                before.Id, after.Name, after.UrlSegment, Null(after.ConfigurationData), Null(after.StartListContent), after.IsOfficial,
                after.GeneratedDate, Null(after.GeneratedBy), Null(after.TeamFormat), Null(after.Notes),
                Null(after.QualifyingResultsSnapshot), Null(after.PerClassConfigData), after.TotalFinalists,
                after.MaxShootersPerTeam, after.QualificationStartListId, DateTime.Now);
            scope.Complete();
            after.IsPublished = true;
            return after;
        }

        public bool Delete(int startListId)
        {
            if (startListId == 0) return false;
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute($"DELETE FROM {Table} WHERE Id = @0", startListId);
            scope.Complete();
            return n > 0;
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────────────

        private string Segment(string name) => NodeStartListStore.Segment(_shortStrings, name);

        /// <summary>
        /// Ett namn som inte krockar med en syskonlista — på SEGMENTET, eftersom det är segmentet adressen och
        /// finnarna går på. Samma form som Umbracos "Namn (1)".
        /// </summary>
        internal string UniqueName(string name, int exceptId, IEnumerable<Row> siblings)
        {
            var taken = new HashSet<string>(
                siblings.Where(s => s.Id != exceptId).Select(s => Segment(s.Name ?? "")),
                StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(Segment(name))) return name;
            for (var i = 1; i < 1000; i++)
            {
                var candidate = $"{name} ({i})";
                if (!taken.Contains(Segment(candidate))) return candidate;
            }
            return $"{name} ({Guid.NewGuid():N})";
        }

        /// <summary>Raden som den ligger i tabellen. NPoco mappar på kolumnnamnen.</summary>
        [TableName("CompetitionStartList")]
        [PrimaryKey("Id")]
        public class Row
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public byte Kind { get; set; }
            public string? Name { get; set; }
            public string? UrlSegment { get; set; }
            public string? ConfigurationData { get; set; }
            public string? StartListContent { get; set; }
            public bool IsOfficial { get; set; }
            public DateTime? GeneratedDate { get; set; }
            public string? GeneratedBy { get; set; }
            public string? TeamFormat { get; set; }
            public string? Notes { get; set; }
            public string? QualifyingResultsSnapshot { get; set; }
            public string? PerClassConfigData { get; set; }
            public int TotalFinalists { get; set; }
            public int MaxShootersPerTeam { get; set; }
            public int QualificationStartListId { get; set; }
            public bool LegacyHub { get; set; }
            public int SortOrder { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime UpdatedDate { get; set; }
        }

        internal static StartListRecord ToRecord(Row r) => new()
        {
            Id = r.Id,
            CompetitionId = r.CompetitionId,
            Kind = r.Kind == 1 ? StartListKind.Finals : StartListKind.Qualifying,
            Name = r.Name ?? "",
            UrlSegment = r.UrlSegment ?? "",
            ConfigurationData = r.ConfigurationData ?? "",
            StartListContent = r.StartListContent ?? "",
            IsOfficial = r.IsOfficial,
            GeneratedDate = r.GeneratedDate,
            GeneratedBy = r.GeneratedBy ?? "",
            TeamFormat = r.TeamFormat ?? "",
            Notes = r.Notes ?? "",
            QualifyingResultsSnapshot = r.QualifyingResultsSnapshot ?? "",
            PerClassConfigData = r.PerClassConfigData ?? "",
            TotalFinalists = r.TotalFinalists,
            MaxShootersPerTeam = r.MaxShootersPerTeam,
            QualificationStartListId = r.QualificationStartListId,
            LegacyHub = r.LegacyHub,
            SortOrder = r.SortOrder,
            IsPublished = true,
        };

        /// <summary>Om något sparat fält skiljer sig.</summary>
        internal static bool Differs(StartListRecord a, StartListRecord b) =>
            (a.Name ?? "") != (b.Name ?? "")
            || (a.ConfigurationData ?? "") != (b.ConfigurationData ?? "")
            || (a.StartListContent ?? "") != (b.StartListContent ?? "")
            || a.IsOfficial != b.IsOfficial
            || a.GeneratedDate != b.GeneratedDate
            || (a.GeneratedBy ?? "") != (b.GeneratedBy ?? "")
            || (a.TeamFormat ?? "") != (b.TeamFormat ?? "")
            || (a.Notes ?? "") != (b.Notes ?? "")
            || (a.QualifyingResultsSnapshot ?? "") != (b.QualifyingResultsSnapshot ?? "")
            || (a.PerClassConfigData ?? "") != (b.PerClassConfigData ?? "")
            || a.TotalFinalists != b.TotalFinalists
            || a.MaxShootersPerTeam != b.MaxShootersPerTeam
            || a.QualificationStartListId != b.QualificationStartListId;

        /// <summary>Tabellen lagrar sekunder (DATETIME2(0)); jämförelsen och flytten använder samma upplösning.</summary>
        internal static DateTime? Truncate(DateTime? d) =>
            d is DateTime v ? new DateTime(v.Ticks - v.Ticks % TimeSpan.TicksPerSecond, v.Kind) : null;

        private static string? Null(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
