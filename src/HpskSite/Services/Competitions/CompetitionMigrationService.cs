using System.Text.Json;
using HpskSite.Models.Competitions;
using HpskSite.Services.Addresses;
using HpskSite.Services.Hosting;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Flytten av tävlingarna och serierna från noder till tabellerna</b> (C3-planen §5b). Samma ordning och
    /// samma regler som startlistornas och resultatlistornas flytt:
    /// <list type="number">
    /// <item><see cref="Preview"/> — torrkörning, skriver ingenting.</item>
    /// <item><see cref="SetWriteFreeze"/>(true) — alla skrivningar till tävlingarna och serierna vägras.</item>
    /// <item><see cref="Apply"/> — en rad per nod, nodens id bevaras, idempotent; varje nod arkiveras.</item>
    /// <item><see cref="Verify"/> — fält för fält mellan nod och rad.</item>
    /// <item><see cref="Switch"/>("table") — vägras om jämförelsen inte är ren, och tills sidorna kan visas utan
    /// noder (<see cref="PagesReadyForTable"/>).</item>
    /// <item><see cref="SetWriteFreeze"/>(false).</item>
    /// </list>
    ///
    /// <para><b>⚠️ Id:n bevaras</b> — anmälningar, resultat, startlistor, liggaren, adresserna och länkarna bär dem.
    /// Identiteten sätts sedan över VARJE id som någon pekar på (<see cref="CompetitionIdSources"/>,
    /// <see cref="SeriesIdSources"/>; LAG: id-reglerna, regel 3).</para>
    ///
    /// <para><b>⚠️ En publicerad nod flyttas i sitt PUBLICERADE läge</b> — det sajten visat och sajtens redigering
    /// skrivit. Ett utkast före det kommer bara ur backoffice och har aldrig synts (KM C 5361: utkastet saknade
    /// klubben och hade blivit öppet för alla). Torrkörningen namnger dem med fälten som skiljer; utkastet finns kvar
    /// i arkivet. Opublicerade noder flyttas i sitt sparade läge.</para>
    ///
    /// <para><b>Opublicerade och papperskorgens noder flyttas som RADERADE rader</b> (<c>DeletedUtc</c> satt):
    /// tabellen har inget publiceringsläge, och en opublicerad tävling hade annars blivit synlig. De syns inte,
    /// men finns kvar och går att lägga tillbaka. Torrkörningen namnger de opublicerade.</para>
    ///
    /// <para><b>Arkivet</b> (<c>CompetitionNodeArchive</c>): varje nods alla egenskaper, utkast och publicerat, som
    /// JSON — även de som inte är fält i posten. Det är det som finns kvar när noderna tas bort (§5e).</para>
    ///
    /// <para><b>Återställning</b> är <see cref="Switch"/>("node") — noderna ligger orörda kvar. Den vägras när tabellen
    /// har rader som inte finns som noder (då vore de borta), utom med <c>force</c>.</para>
    /// </summary>
    public class CompetitionMigrationService
    {
        public const string ArchiveTable = "dbo.CompetitionNodeArchive";

        /// <summary>
        /// ⚠️ Tabelläget visar sidorna ur noderna tills den virtuella noden finns (§5c) och de sista nodberoendena är
        /// borta (§5d). Byts till sant i §5d — förut vägrar <see cref="Switch"/> att slå om till tabellen.
        /// </summary>
        public const bool PagesReadyForTable = false;

        private readonly NodeCompetitionStore _nodeCompetitions;
        private readonly NodeCompetitionSeriesStore _nodeSeries;
        private readonly SqlCompetitionStore _sqlCompetitions;
        private readonly SqlCompetitionSeriesStore _sqlSeries;
        private readonly SiteSettingService _settings;
        private readonly IScopeProvider _scopeProvider;
        private readonly IShortStringHelper _shortStrings;
        private readonly PageAddressService _addresses;
        private readonly ILogger<CompetitionMigrationService> _logger;

        public CompetitionMigrationService(NodeCompetitionStore nodeCompetitions, NodeCompetitionSeriesStore nodeSeries,
            SqlCompetitionStore sqlCompetitions, SqlCompetitionSeriesStore sqlSeries, SiteSettingService settings,
            IScopeProvider scopeProvider, IShortStringHelper shortStrings, PageAddressService addresses,
            ILogger<CompetitionMigrationService> logger)
        {
            _nodeCompetitions = nodeCompetitions;
            _nodeSeries = nodeSeries;
            _sqlCompetitions = sqlCompetitions;
            _sqlSeries = sqlSeries;
            _settings = settings;
            _scopeProvider = scopeProvider;
            _shortStrings = shortStrings;
            _addresses = addresses;
            _logger = logger;
        }

        public string CurrentStore => _settings.Get(CompetitionStoreSettings.StoreKey, CompetitionStoreSettings.StoreNode);
        public bool IsWriteFrozen => CompetitionStoreSettings.IsWriteFrozen(_settings);
        private bool NodesCurrent => !CompetitionStoreSettings.UsesTable(_settings);

        // ── Noderna ────────────────────────────────────────────────────────────────────────

        public sealed class NodeState
        {
            public int Id { get; set; }
            public string Alias { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Published { get; set; }
            public bool Edited { get; set; }
            public bool Trashed { get; set; }
        }

        /// <summary>Alla tävlings- och serienoder, även opublicerade och i papperskorgen, i id-ordning.</summary>
        public List<NodeState> AllNodes()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<NodeState>(@"
SELECT n.id AS Id, ct.alias AS Alias, n.text AS Name,
       CAST(ISNULL(d.published, 0) AS BIT) AS Published, CAST(ISNULL(d.edited, 0) AS BIT) AS Edited,
       CAST(n.trashed AS BIT) AS Trashed
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias IN (@0, @1)
LEFT JOIN umbracoDocument d ON d.nodeId = n.id
ORDER BY n.id", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias);
        }

        private static bool IsSeries(NodeState n) => n.Alias == CompetitionSeriesFieldTable.NodeAlias;

        /// <summary>Raderad i tabellen = i papperskorgen eller aldrig publicerad.</summary>
        private static bool BecomesDeleted(NodeState n) => n.Trashed || !n.Published;

        // ── Torrkörningen ──────────────────────────────────────────────────────────────────

        public sealed class PreviewResult
        {
            public string Store { get; init; } = "";
            public bool WriteFrozen { get; init; }
            public bool TableExists { get; init; }
            public int Competitions { get; init; }
            public int Series { get; init; }
            public int Published { get; init; }
            /// <summary>Opublicerade (ej i papperskorgen) — flyttas som raderade.</summary>
            public List<string> Unpublished { get; init; } = new();
            public int Trashed { get; init; }
            /// <summary>Publicerade med sparade ändringar som inte publicerats — efter flytten gäller utkastet.</summary>
            public List<string> DraftAhead { get; init; } = new();
            /// <summary>Värden som inte överlever läsregeln.</summary>
            public List<string> Unreadable { get; init; } = new();
            /// <summary>Publicerade tävlingar/serier utan adress — de har ingen sida i tabelläget.</summary>
            public List<string> WithoutAddress { get; init; } = new();
            public int RowsAlreadyInTable { get; init; }
            public List<string> Notes { get; init; } = new();
            public List<string> UnpublishBlockers { get; init; } = new();
            public List<string> MissingSchema => TableExists ? new List<string>() : new List<string> { "Competition (med §5-kolumnerna)", "CompetitionNodeArchive" };
        }

        public PreviewResult Preview()
        {
            var exists = TablesExist();
            var nodes = AllNodes();
            var unpublished = new List<string>();
            var draftAhead = new List<string>();
            var unreadable = new List<string>();
            var withoutAddress = new List<string>();

            foreach (var n in nodes)
            {
                var label = $"{(IsSeries(n) ? "Serie" : "Tävling")} {n.Id} {n.Name}";
                if (!n.Trashed && !n.Published) unpublished.Add(label);
                if (n.Trashed) continue;

                var node = IsSeries(n) ? ReadSeries(n.Id)?.Node : ReadCompetition(n.Id)?.Node;
                if (node == null) { unreadable.Add($"{label}: går inte att läsa"); continue; }

                foreach (var (alias, shape) in FieldShapes(IsSeries(n)))
                {
                    if (!node.HasProperty(alias)) continue;
                    var why = CompetitionFieldValues.LossReason(shape, node.GetValue(alias));
                    if (why != null) unreadable.Add($"{label}: {alias} — {why}");
                }

                if (n.Published && n.Edited)
                {
                    var differs = FieldShapes(IsSeries(n))
                        .Where(f => node.HasProperty(f.Alias))
                        .Where(f => !Equals(CompetitionFieldValues.Read(f.Shape, node.GetValue(f.Alias)),
                                            CompetitionFieldValues.Read(f.Shape, node.GetValue(f.Alias, published: true))))
                        .Select(f => f.Alias).ToList();
                    if (node.Name != node.PublishName) differs.Insert(0, "namnet");
                    if (differs.Count > 0) draftAhead.Add($"{label}: {string.Join(", ", differs)}");
                }

                if (n.Published && _addresses.Current(IsSeries(n) ? PageAddressKinds.Series : PageAddressKinds.Competition, n.Id) == null)
                    withoutAddress.Add(label);
            }

            int inTable = 0;
            if (exists)
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                inTable = scope.Database.ExecuteScalar<int>($"SELECT (SELECT COUNT(*) FROM {SqlCompetitionStore.Table}) + (SELECT COUNT(*) FROM {SqlCompetitionSeriesStore.Table})");
            }

            var notes = new List<string>();
            if (unpublished.Count > 0) notes.Add($"{unpublished.Count} opublicerade flyttas som raderade (syns inte, finns kvar).");
            if (draftAhead.Count > 0) notes.Add($"{draftAhead.Count} har sparade ändringar som inte publicerats — det publicerade flyttas, utkastet sparas bara i arkivet.");
            if (withoutAddress.Count > 0) notes.Add($"{withoutAddress.Count} publicerade saknar adress — de har ingen sida i tabelläget förrän en värd sätts.");
            if (unreadable.Count > 0) notes.Add($"{unreadable.Count} värden går inte att läsa och blir standardvärdet.");

            return new PreviewResult
            {
                Store = CurrentStore,
                WriteFrozen = IsWriteFrozen,
                TableExists = exists,
                Competitions = nodes.Count(n => !IsSeries(n)),
                Series = nodes.Count(IsSeries),
                Published = nodes.Count(n => n.Published && !n.Trashed),
                Unpublished = unpublished,
                Trashed = nodes.Count(n => n.Trashed),
                DraftAhead = draftAhead,
                Unreadable = unreadable,
                WithoutAddress = withoutAddress,
                RowsAlreadyInTable = inTable,
                UnpublishBlockers = exists ? UnpublishBlockers() : new List<string>(),
                Notes = notes,
            };
        }

        private static IEnumerable<(string Alias, CompetitionFieldShape Shape)> FieldShapes(bool series) =>
            series
                ? CompetitionSeriesFieldTable.Fields.Select(f => (f.Alias, f.Shape))
                : CompetitionFieldTable.Fields.Select(f => (f.Alias, f.Shape));

        private (CompetitionInfo Record, IContent Node)? ReadCompetition(int id) => _nodeCompetitions.ReadForMigration(id);
        private (CompetitionSeriesInfo Record, IContent Node)? ReadSeries(int id) => _nodeSeries.ReadForMigration(id);

        public bool TablesExist()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.Competition')") != null
                && db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.CompetitionSeries')") != null
                && db.ExecuteScalar<int?>($"SELECT OBJECT_ID('{ArchiveTable}')") != null
                && db.ExecuteScalar<int?>("SELECT COL_LENGTH('dbo.Competition', 'DeletedUtc')") != null
                && db.ExecuteScalar<int?>("SELECT COL_LENGTH('dbo.CompetitionSeries', 'DeletedUtc')") != null;
        }

        public void SetWriteFreeze(bool on, int? byMemberId)
            => _settings.Set(CompetitionStoreSettings.WriteFreezeKey, on ? "1" : "0", byMemberId);

        // ── Flytten ────────────────────────────────────────────────────────────────────────

        public sealed class ApplyResult
        {
            public bool Ok { get; init; }
            public string Message { get; init; } = "";
            public int Inserted { get; init; }
            public int Skipped { get; init; }
            public int Failed { get; init; }
            public List<string> Errors { get; init; } = new();
            public int CompetitionReseed { get; init; }
            public int SeriesReseed { get; init; }
        }

        /// <summary>
        /// Skriver varje nod till sin tabell, serierna först, en nod per transaktion (raden och arkivet tillsammans).
        /// Kräver frysning och att noderna fortfarande gäller. Idempotent: en rad vars id redan finns hoppas över.
        /// </summary>
        public ApplyResult Apply()
        {
            if (!IsWriteFrozen)
                return new ApplyResult { Message = "Frys skrivningarna till tävlingarna först (Competitions:WriteFreeze)." };
            if (!NodesCurrent)
                return new ApplyResult { Message = "Tabellagringen gäller redan — flytten körs bara från noderna." };
            if (!TablesExist())
                return new ApplyResult { Message = "Tabellerna saknas eller har den gamla formen: kör Migrations/create-competition-tables.sql." };

            int inserted = 0, skipped = 0, failed = 0;
            var errors = new List<string>();
            var nowUtc = DateTime.UtcNow;
            foreach (var n in AllNodes().OrderBy(n => IsSeries(n) ? 0 : 1).ThenBy(n => n.Id))
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope();
                    var db = scope.Database;
                    var table = IsSeries(n) ? SqlCompetitionSeriesStore.Table : SqlCompetitionStore.Table;
                    if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE Id = @0", n.Id) > 0)
                    {
                        scope.Complete();
                        skipped++;
                        continue;
                    }

                    IContent node;
                    Dictionary<string, object?> values;
                    if (IsSeries(n))
                    {
                        var read = ReadSeries(n.Id) ?? throw new InvalidOperationException("noden gick inte att läsa");
                        node = read.Node;
                        values = Columns(read.Record, CompetitionSeriesFieldTable.Fields, Meta(read.Record.Id, read.Record.Key, read.Record.Name,
                            read.Record.CreateDate, read.Record.UpdateDate, read.Record.SortOrder, read.Record.ParentId, read.Record.ParentKind, read.Record.UrlSegment));
                    }
                    else
                    {
                        var read = ReadCompetition(n.Id) ?? throw new InvalidOperationException("noden gick inte att läsa");
                        node = read.Node;
                        values = Columns(read.Record, CompetitionFieldTable.Fields, Meta(read.Record.Id, read.Record.Key, read.Record.Name,
                            read.Record.CreateDate, read.Record.UpdateDate, read.Record.SortOrder, read.Record.ParentId, read.Record.ParentKind, read.Record.UrlSegment));
                        values["SeriesId"] = read.Record.SeriesId;
                    }
                    if (string.IsNullOrWhiteSpace(values["UrlSegment"] as string))
                        values["UrlSegment"] = (node.Name ?? "").ToUrlSegment(_shortStrings) ?? "";
                    if (BecomesDeleted(n)) values["DeletedUtc"] = nowUtc;

                    Insert(db, table, values);
                    Archive(db, n, node, nowUtc);
                    scope.Complete();
                    inserted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{(IsSeries(n) ? "Serie" : "Tävling")} {n.Id} {n.Name}: {ex.Message}");
                    _logger.LogError(ex, "Flytten av {Alias} {Id} misslyckades — rullad tillbaka", n.Alias, n.Id);
                }
            }

            var (compSeed, seriesSeed) = Reseed();
            _logger.LogWarning("Tävlingarnas flytt: {Inserted} skrivna, {Skipped} fanns redan, {Failed} misslyckades, RESEED {Comp}/{Series}",
                inserted, skipped, failed, compSeed, seriesSeed);
            return new ApplyResult
            {
                Ok = failed == 0,
                Message = failed == 0 ? "Flytten är klar." : $"{failed} noder kunde inte flyttas — se felen. Kör om när de är åtgärdade.",
                Inserted = inserted, Skipped = skipped, Failed = failed, Errors = errors,
                CompetitionReseed = compSeed, SeriesReseed = seriesSeed,
            };
        }

        private static Dictionary<string, object?> Meta(int id, Guid key, string name, DateTime created, DateTime updated, int sort,
            int parentId, string parentKind, string urlSegment) => new()
        {
            ["Id"] = id, ["Key"] = key, ["Name"] = string.IsNullOrWhiteSpace(name) ? "(utan namn)" : name,
            ["CreateDate"] = created, ["UpdateDate"] = updated, ["SortOrder"] = sort,
            ["LegacyParentId"] = parentId, ["LegacyParentKind"] = parentKind ?? "", ["UrlSegment"] = urlSegment ?? "",
        };

        private static Dictionary<string, object?> Columns<T>(T record, IReadOnlyList<CompetitionFieldDef<T>> fields, Dictionary<string, object?> meta)
        {
            foreach (var f in fields) meta[f.Property] = f.Get(record);
            return meta;
        }

        private static void Insert(IUmbracoDatabase db, string table, Dictionary<string, object?> values)
        {
            var cols = values.Keys.ToList();
            db.Execute($"SET IDENTITY_INSERT {table} ON");
            try
            {
                db.Execute($"INSERT INTO {table} ({string.Join(", ", cols.Select(c => "[" + c + "]"))}) VALUES ({string.Join(", ", cols.Select((_, i) => "@" + i))})",
                    cols.Select(c => SqlStoreCore<CompetitionInfo>.ToDb(values[c])).ToArray());
            }
            finally
            {
                db.Execute($"SET IDENTITY_INSERT {table} OFF");
            }
        }

        private static readonly JsonSerializerOptions ArchiveJson = new() { WriteIndented = false };

        /// <summary>Nodens alla egenskaper, utkast och publicerat. Skrivs en gång — en arkiverad nod rörs inte igen.</summary>
        private static void Archive(IUmbracoDatabase db, NodeState n, IContent node, DateTime nowUtc)
        {
            if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {ArchiveTable} WHERE NodeId = @0", n.Id) > 0) return;
            var draft = node.Properties.ToDictionary(p => p.Alias, p => ArchiveValue(p.GetValue()));
            var published = n.Published ? node.Properties.ToDictionary(p => p.Alias, p => ArchiveValue(p.GetValue(published: true))) : null;
            db.Execute($@"INSERT INTO {ArchiveTable}
    (NodeId, Alias, Name, PublishName, ParentId, Path, Published, Edited, Trashed, CreateDate, UpdateDate, DraftValues, PublishedValues, ArchivedUtc)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13)",
                n.Id, n.Alias, node.Name ?? "", (object?)node.PublishName ?? DBNull.Value, node.ParentId, node.Path ?? "",
                n.Published, n.Edited, n.Trashed, node.CreateDate, node.UpdateDate,
                JsonSerializer.Serialize(draft, ArchiveJson),
                published == null ? DBNull.Value : JsonSerializer.Serialize(published, ArchiveJson),
                nowUtc);
        }

        private static object? ArchiveValue(object? v) => v switch
        {
            null => null,
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss"),
            string or bool or int or long or decimal or double => v,
            _ => v.ToString(),
        };

        // ── Id-golvet ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Allt som bär ett tävlings-id. Heltalskolumner som heter <c>*CompetitionId</c> läses ur schemat vid varje
        /// körning, så en ny tabell kommer med av sig själv; övriga referenser står här. Läggs en ny referens till
        /// som inte heter så: lägg den här.
        /// </summary>
        internal static readonly IdentityFloor.Source[] CompetitionIdSources =
        {
            IdentityFloor.NodesOfType(CompetitionFieldTable.NodeAlias),
            IdentityFloor.ColumnsLike("%CompetitionId"),
            IdentityFloor.ColumnsLike("TargetId", "TargetKind", PageAddressKinds.Competition, CompetitionFieldTable.NodeAlias),
            // Liggaren: anmälningsavgifter, lagavgifter, fakturor till klubbar och projekt bär TÄVLINGENS id.
            // (legacy-invoice bär fakturans nod-id och hör inte hit.)
            IdentityFloor.ColumnsLike("SourceId", "SourceType", Models.Ledger.LedgerSourceType.CompetitionRegistration,
                Models.Ledger.LedgerSourceType.TeamFee, Models.Ledger.LedgerSourceType.CompetitionInvoice,
                Models.Ledger.LedgerProjectSource.Competition),
            // Vapnets användning (FirearmUsage) bär aktivitetens sort.
            IdentityFloor.ColumnsLike("SourceId", "SourceKind", Models.MemberActivityEntry.SourceKindCompetition),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'competitionId'"),
        };

        /// <summary>Allt som bär ett serie-id.</summary>
        internal static readonly IdentityFloor.Source[] SeriesIdSources =
        {
            IdentityFloor.NodesOfType(CompetitionSeriesFieldTable.NodeAlias),
            new("dbo.Competition", "SELECT MAX(SeriesId) FROM dbo.Competition"),
            IdentityFloor.ColumnsLike("TargetId", "TargetKind", PageAddressKinds.Series, CompetitionSeriesFieldTable.NodeAlias),
            IdentityFloor.ColumnsLike("SourceId", "SourceType", Models.Ledger.LedgerProjectSource.Series),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'seriesId'"),
        };

        private (int Competition, int Series) Reseed()
        {
            using var scope = _scopeProvider.CreateScope();
            var c = IdentityFloor.Reseed(scope.Database, SqlCompetitionStore.Table, CompetitionIdSources);
            var s = IdentityFloor.Reseed(scope.Database, SqlCompetitionSeriesStore.Table, SeriesIdSources);
            scope.Complete();
            return (c, s);
        }

        // ── Jämförelsen ────────────────────────────────────────────────────────────────────

        public sealed class VerifyResult
        {
            public int Compared { get; init; }
            public int Mismatches => Problems.Count;
            public List<string> Problems { get; init; } = new();
        }

        /// <summary>Jämför varje nod med sin rad, fält för fält, och raderingsläget. Läser bara.</summary>
        public VerifyResult Verify()
        {
            var problems = new List<string>();
            var nodes = AllNodes();
            foreach (var n in nodes)
            {
                var label = $"{(IsSeries(n) ? "Serie" : "Tävling")} {n.Id}";
                if (IsSeries(n))
                {
                    var a = ReadSeries(n.Id);
                    var b = _sqlSeries.GetForMigration(n.Id);
                    if (a == null || b == null) { problems.Add($"{label}: {(a == null ? "noden" : "raden")} saknas"); continue; }
                    Compare(label, n, a.Value.Record, b.Value.Record, b.Value.Deleted, CompetitionSeriesFieldTable.Fields, problems);
                    Same(problems, label, "Name", a.Value.Record.Name, b.Value.Record.Name);
                }
                else
                {
                    var a = ReadCompetition(n.Id);
                    var b = _sqlCompetitions.GetForMigration(n.Id);
                    if (a == null || b == null) { problems.Add($"{label}: {(a == null ? "noden" : "raden")} saknas"); continue; }
                    Compare(label, n, a.Value.Record, b.Value.Record, b.Value.Deleted, CompetitionFieldTable.Fields, problems);
                    Same(problems, label, "Name", a.Value.Record.Name, b.Value.Record.Name);
                    Same(problems, label, "SeriesId", a.Value.Record.SeriesId, b.Value.Record.SeriesId);
                    Same(problems, label, "Key", a.Value.Record.Key, b.Value.Record.Key);
                }
            }
            var extra = TableBornIds(nodes);
            if (extra.Count > 0)
                problems.Add($"{extra.Count} rader i tabellerna saknar nod: {string.Join(", ", extra.Take(20))}");
            return new VerifyResult { Compared = nodes.Count, Problems = problems };
        }

        private static void Compare<T>(string label, NodeState n, T node, T row, bool deleted, IReadOnlyList<CompetitionFieldDef<T>> fields, List<string> problems)
        {
            foreach (var f in fields) Same(problems, label, f.Alias, f.Get(node), f.Get(row));
            if (deleted != BecomesDeleted(n))
                problems.Add($"{label}: raderad i tabellen = {deleted}, väntat {BecomesDeleted(n)}");
        }

        private static void Same(List<string> problems, string label, string field, object? a, object? b)
        {
            if (Equals(a, b)) return;
            if (a is DateTime da && b is DateTime db && Math.Abs((da - db).TotalMilliseconds) < 1) return;
            problems.Add($"{label}: {field} skiljer (nod={Short(a)} rad={Short(b)})");
        }

        /// <summary>Raderna i tabellerna som inte finns som noder — skapade i tabellen.</summary>
        private List<string> TableBornIds(List<NodeState> nodes)
        {
            var competitionIds = nodes.Where(n => !IsSeries(n)).Select(n => n.Id).ToHashSet();
            var seriesIds = nodes.Where(IsSeries).Select(n => n.Id).ToHashSet();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<int>($"SELECT Id FROM {SqlCompetitionStore.Table}").Where(id => !competitionIds.Contains(id)).Select(id => "tävling " + id)
                .Concat(db.Fetch<int>($"SELECT Id FROM {SqlCompetitionSeriesStore.Table}").Where(id => !seriesIds.Contains(id)).Select(id => "serie " + id))
                .ToList();
        }

        // ── Växeln och tömningen ───────────────────────────────────────────────────────────

        /// <summary>
        /// Byter lagring. Till tabellen bara under frysning, med ren jämförelse och när sidorna kan visas utan noder.
        /// Tillbaka till noderna vägras när tabellen har rader utan nod (de vore borta) — utom med <paramref name="force"/>.
        /// </summary>
        public (bool Ok, string Message, List<string> Problems) Switch(string store, bool force, int? byMemberId)
        {
            if (string.Equals(store, CompetitionStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
            {
                var born = TablesExist() ? TableBornIds(AllNodes()) : new List<string>();
                if (born.Count > 0 && !force)
                    return (false, $"{born.Count} tävlingar och serier finns bara i tabellen och skulle försvinna. Rätta framåt i stället.", born.Take(50).ToList());
                _settings.Set(CompetitionStoreSettings.StoreKey, CompetitionStoreSettings.StoreNode, byMemberId);
                return (true, "Noderna gäller igen.", new List<string>());
            }
            if (!string.Equals(store, CompetitionStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                return (false, "Okänd lagring.", new List<string>());
            if (!PagesReadyForTable)
                return (false, "Tävlingssidorna kan ännu inte visas utan noderna (C3-planen §5c–5d). Bytet görs när de kan.", new List<string>());
            if (!IsWriteFrozen)
                return (false, "Frys skrivningarna till tävlingarna först.", new List<string>());
            var verify = Verify();
            if (verify.Mismatches > 0)
                return (false, $"{verify.Mismatches} avvikelser mellan noderna och tabellerna — växeln slås inte om.", verify.Problems.Take(50).ToList());
            _settings.Set(CompetitionStoreSettings.StoreKey, CompetitionStoreSettings.StoreTable, byMemberId);
            return (true, $"Tabellerna gäller nu ({verify.Compared} tävlingar och serier jämförda).", new List<string>());
        }

        /// <summary>
        /// Avpublicerar högst <paramref name="batch"/> tävlings- och serienoder (§5e). Bara när tabellen gäller.
        /// Raderar ingenting — noderna och deras barn (gamla fakturor) ligger kvar i trädet.
        /// </summary>
        /// <summary>
        /// Det som måste vara klart innan tävlingsnoderna får avpubliceras (C3-planen §5 punkt 5.5). Tom = fritt.
        /// <para><b>⚠️⚠️ De gamla fakturamapparna ligger under tävlingsnoderna.</b> Umbraco 17:s publicerade cache
        /// döljer allt under en avpublicerad förälder (<c>HasPublishedAncestorPath</c>): mappen finns kvar i
        /// databasen men <c>LegacyInvoiceHubs.PublishedHub</c> svarar null — Swish-betalningen av en gammal faktura
        /// och kvittona slutar fungera (provat på kopian 2026-10-18, <c>c3/c3-s5e-invoice-hazard.mjs</c>). Mapparna
        /// flyttas till arkivet först (F1, <see cref="HpskSite.Services.LegacyInvoiceHubArchiveService"/> — Underhåll →
        /// Tävlingar och serier → Fakturamapparna till arkivet).</para>
        /// <para>Barnen som bor kvar i trädet — anmälningar, startlistor och resultatlistor — och händelserna ska
        /// stå på sina tabeller.</para>
        /// </summary>
        public List<string> UnpublishBlockers()
        {
            var blockers = new List<string>();
            if (NodesCurrent) blockers.Add("Tävlingarna står på noderna — byt till tabellerna först.");
            if (!string.Equals(_settings.Get(HpskSite.Services.Registrations.RegistrationStoreSettings.StoreKey, "node"), "table", StringComparison.OrdinalIgnoreCase))
                blockers.Add("Anmälningarna står inte på tabellen.");
            if (!HpskSite.Services.StartLists.StartListStoreSettings.UsesTable(_settings))
                blockers.Add("Startlistorna står inte på tabellen.");
            if (!HpskSite.Services.Results.CompetitionResultStoreSettings.UsesTable(_settings))
                blockers.Add("Resultatlistorna står inte på tabellen.");
            if (!HpskSite.Services.Events.ClubEventStoreSettings.UsesTable(_settings))
                blockers.Add("Händelserna står inte på tabellen.");

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var hubs = scope.Database.Fetch<int>(@"
SELECT h.id FROM umbracoNode h
JOIN umbracoContent c ON c.nodeId = h.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
JOIN umbracoContent pc ON pc.nodeId = h.parentId
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
WHERE ct.alias = @0 AND pct.alias = @1 AND h.trashed = 0
ORDER BY h.id", LegacyInvoiceHubs.HubAlias, CompetitionFieldTable.NodeAlias);
            if (hubs.Count > 0)
                blockers.Add($"{hubs.Count} gamla fakturamappar ligger under tävlingsnoder — flytta dem till arkivet först (knappen Fakturamapparna till arkivet). " +
                    $"Avpublicerade tävlingar döljer dem i den publicerade cachen. Mappar: {string.Join(", ", hubs.Take(10))}{(hubs.Count > 10 ? " …" : "")}.");
            return blockers;
        }

        public (int Unpublished, int Remaining, string Message) UnpublishNodes(int batch, IContentService contentService, bool copyOnlyIgnoreBlockers = false)
        {
            var blockers = UnpublishBlockers();
            if (copyOnlyIgnoreBlockers) blockers.RemoveAll(b => !b.StartsWith("Tävlingarna står på noderna"));
            if (blockers.Count > 0)
                return (0, 0, "Avpubliceringen vägras: " + string.Join(" ", blockers));
            batch = Math.Clamp(batch, 1, 200);
            List<int> published;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                published = scope.Database.Fetch<int>(@"
SELECT n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
JOIN umbracoDocument d ON d.nodeId = n.id
WHERE ct.alias IN (@0, @1) AND d.published = 1 AND n.trashed = 0
ORDER BY n.id", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias);
            }
            int done = 0;
            foreach (var id in published.Take(batch))
            {
                try
                {
                    var node = contentService.GetById(id);
                    if (node != null && node.Published) { contentService.Unpublish(node); done++; }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tävlingsnod {Id} kunde inte avpubliceras", id);
                }
            }
            var remaining = Math.Max(0, published.Count - done);
            return (done, remaining, remaining == 0 ? "Alla tävlings- och serienoder är avpublicerade." : $"{remaining} kvar — kör igen.");
        }

        /// <summary>Tömmer tabellerna och arkivet inför ett nytt försök. Bara när noderna gäller. Identiteten går aldrig bakåt.</summary>
        public (bool Ok, string Message) ClearTables(int? byMemberId)
        {
            if (!NodesCurrent) return (false, "Tabellerna gäller — gå tillbaka till noderna först.");
            var born = TableBornIds(AllNodes());
            if (born.Count > 0) return (false, $"{born.Count} rader finns bara i tabellen och tas inte bort: {string.Join(", ", born.Take(10))}.");
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.ExecuteScalar<int>($"SELECT (SELECT COUNT(*) FROM {SqlCompetitionStore.Table}) + (SELECT COUNT(*) FROM {SqlCompetitionSeriesStore.Table})");
            db.Execute($"DELETE FROM {SqlCompetitionStore.Table}; DELETE FROM {SqlCompetitionSeriesStore.Table}; DELETE FROM {ArchiveTable};");
            IdentityFloor.Reseed(db, SqlCompetitionStore.Table, CompetitionIdSources);
            IdentityFloor.Reseed(db, SqlCompetitionSeriesStore.Table, SeriesIdSources);
            scope.Complete();
            _logger.LogWarning("Tävlingarnas tabeller tömda ({Rows} rader) av medlem {MemberId}", rows, byMemberId);
            return (true, $"Tabellerna tömda ({rows} rader). Flytten kan köras igen.");
        }

        private static string Short(object? v)
        {
            var s = v?.ToString() ?? "null";
            return s.Length > 40 ? s[..40] + $"… ({s.Length} tecken)" : s;
        }
    }
}
