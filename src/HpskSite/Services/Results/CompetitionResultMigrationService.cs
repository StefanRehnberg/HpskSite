using HpskSite.Models.Results;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// <b>Flytten av resultatlistorna från noder till tabellen</b> (skalplanen fas C1). Samma ordning och
    /// samma regler som anmälningarnas flytt (<c>RegistrationMigrationService</c>):
    /// <list type="number">
    /// <item><see cref="Preview"/> — torrkörning, skriver ingenting.</item>
    /// <item><see cref="SetWriteFreeze"/>(true) — alla skrivningar till resultatlistorna vägras.</item>
    /// <item><see cref="Apply"/> — en rad per tävling, nodens id bevaras, idempotent.</item>
    /// <item><see cref="Verify"/> — fält för fält mellan nod och rad.</item>
    /// <item><see cref="Switch"/>("table") — vägras om jämförelsen inte är ren.</item>
    /// <item><see cref="SetWriteFreeze"/>(false).</item>
    /// <item><see cref="UnpublishNodes"/> — i portioner, efter att skrivningarna släppts. Adressen
    /// <c>/resultat/</c> fungerar ändå: tävlingssidans adressuppslag visar listan ur tabellen när noden
    /// inte längre är publicerad.</item>
    /// </list>
    ///
    /// <para><b>Återställning</b> är <see cref="Switch"/>("node") — noderna ligger orörda kvar. Det gäller
    /// bara tills nya skrivningar gjorts i tabellen; efter det är det en framåträttning.</para>
    /// </summary>
    public class CompetitionResultMigrationService
    {
        private readonly NodeCompetitionResultStore _nodes;
        private readonly SqlCompetitionResultStore _table;
        private readonly SiteSettingService _settings;
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly ILogger<CompetitionResultMigrationService> _logger;

        public CompetitionResultMigrationService(
            NodeCompetitionResultStore nodes, SqlCompetitionResultStore table, SiteSettingService settings,
            IScopeProvider scopeProvider, IContentService contentService, ILogger<CompetitionResultMigrationService> logger)
        {
            _nodes = nodes;
            _table = table;
            _settings = settings;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _logger = logger;
        }

        public string CurrentStore => _settings.Get(CompetitionResultStoreSettings.StoreKey, CompetitionResultStoreSettings.StoreNode);
        public bool IsWriteFrozen => _settings.Get(CompetitionResultStoreSettings.WriteFreezeKey, "0") == "1";
        private bool NodesCurrent => string.Equals(CurrentStore, CompetitionResultStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase);

        /// <summary>Resultatnoderna, en rad per nod: (tävling, nod).</summary>
        private List<(int CompetitionId, int NodeId)> AllNodes()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<NodeRow>(@"
SELECT n.parentId AS CompetitionId, n.id AS NodeId
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = @0
JOIN umbracoNode p ON p.id = n.parentId
JOIN umbracoContent pc ON pc.nodeId = p.id
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId AND pct.alias = 'competition'
WHERE n.trashed = 0 AND p.trashed = 0
ORDER BY n.parentId, n.id", CompetitionResultFields.NodeAlias)
                .Select(r => (r.CompetitionId, r.NodeId)).ToList();
        }

        private sealed class NodeRow { public int CompetitionId { get; set; } public int NodeId { get; set; } }

        /// <summary>Det noderna säger, en post per tävling — samma uppslag som lagringen gör.</summary>
        public List<CompetitionResultRecord> LoadAllNodes()
        {
            var list = new List<CompetitionResultRecord>();
            foreach (var competitionId in AllNodes().Select(n => n.CompetitionId).Distinct())
            {
                var rec = _nodes.Get(competitionId);
                if (rec != null) list.Add(rec);
            }
            return list;
        }

        public sealed class PreviewResult
        {
            public string Store { get; init; } = "";
            public bool WriteFrozen { get; init; }
            public bool TableExists { get; init; }
            public int Nodes { get; init; }
            public int Competitions { get; init; }
            public int WithResultData { get; init; }
            public int Official { get; init; }
            public int RowsAlreadyInTable { get; init; }
            /// <summary>Tävlingar med fler än en resultatnod — bara den som lagringen läser flyttas.</summary>
            public List<string> Duplicates { get; init; } = new();
            /// <summary>Samma form som anmälningarnas och händelsernas torrkörning (underhållspanelen läser den).</summary>
            public List<string> MissingSchema => TableExists ? new List<string>() : new List<string> { "CompetitionResultList" };
        }

        public PreviewResult Preview()
        {
            var exists = CompetitionResultSchemaGuardHostedService.TableExists(_scopeProvider);
            var nodes = AllNodes();
            var records = LoadAllNodes();
            int inTable = 0;
            if (exists)
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                inTable = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlCompetitionResultStore.Table}");
            }
            var dupes = nodes.GroupBy(n => n.CompetitionId).Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var kept = records.FirstOrDefault(r => r.CompetitionId == g.Key)?.Id;
                    return $"Tävling {g.Key}: noder {string.Join(", ", g.Select(x => x.NodeId))} — {kept} flyttas";
                }).ToList();
            return new PreviewResult
            {
                Store = CurrentStore,
                WriteFrozen = IsWriteFrozen,
                TableExists = exists,
                Nodes = nodes.Count,
                Competitions = records.Count,
                WithResultData = records.Count(r => r.HasResultData),
                Official = records.Count(r => r.IsOfficial),
                RowsAlreadyInTable = inTable,
                Duplicates = dupes
            };
        }

        public void SetWriteFreeze(bool on, int? byMemberId)
            => _settings.Set(CompetitionResultStoreSettings.WriteFreezeKey, on ? "1" : "0", byMemberId);

        /// <summary>Tömmer tabellen inför ett nytt försök. Bara när noderna gäller — då läser ingen den.</summary>
        public (bool Ok, string Message) ClearTable(int? byMemberId)
        {
            if (!NodesCurrent) return (false, "Tabellen gäller — gå tillbaka till noderna först.");
            using var scope = _scopeProvider.CreateScope();
            var rows = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlCompetitionResultStore.Table}");
            scope.Database.Execute($@"DELETE FROM {SqlCompetitionResultStore.Table};
DBCC CHECKIDENT('{SqlCompetitionResultStore.Table}', RESEED, 0) WITH NO_INFOMSGS;");
            scope.Complete();
            _logger.LogWarning("Resultatlistornas tabell tömd ({Rows} rader) av medlem {MemberId}", rows, byMemberId);
            return (true, $"Tabellen tömd ({rows} rader). Flytten kan köras igen.");
        }

        public sealed class ApplyResult
        {
            public bool Ok { get; init; }
            public string Message { get; init; } = "";
            public int Inserted { get; init; }
            public int Skipped { get; init; }
            public int Failed { get; init; }
            public List<string> Errors { get; init; } = new();
            public int Reseed { get; init; }
        }

        /// <summary>
        /// Skriver nodernas listor till tabellen. Kräver frysning och att noderna fortfarande gäller.
        /// Idempotent: en tävling som redan har en rad hoppas över.
        /// </summary>
        public ApplyResult Apply()
        {
            if (!IsWriteFrozen)
                return new ApplyResult { Message = "Frys skrivningarna till resultatlistorna först (Results:WriteFreeze)." };
            if (!NodesCurrent)
                return new ApplyResult { Message = "Tabellagringen gäller redan — flytten körs bara från noderna." };
            if (!CompetitionResultSchemaGuardHostedService.TableExists(_scopeProvider))
                return new ApplyResult { Message = "Tabellen saknas: kör Migrations/create-competition-result-list-table.sql." };

            int inserted = 0, skipped = 0, failed = 0;
            var errors = new List<string>();
            foreach (var r in LoadAllNodes())
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope();
                    var db = scope.Database;
                    if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlCompetitionResultStore.Table} WHERE CompetitionId = @0", r.CompetitionId) > 0)
                    {
                        skipped++;
                        scope.Complete();
                        continue;
                    }
                    var now = DateTime.Now;
                    db.Execute($@"
SET IDENTITY_INSERT {SqlCompetitionResultStore.Table} ON;
INSERT INTO {SqlCompetitionResultStore.Table}
    (Id, CompetitionId, ResultType, ResultData, IsOfficial, SubIsOfficial, MergeConfig, SubMergeConfig,
     ClassNameOverrides, SubClassNameOverrides, ShootOffConfig, SubShootOffConfig, HonoraryAwardConfig,
     LastUpdated, CreatedDate, UpdatedDate)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @14);
SET IDENTITY_INSERT {SqlCompetitionResultStore.Table} OFF;",
                        r.Id, r.CompetitionId, r.ResultType, Null(r.ResultData), r.IsOfficial, r.SubIsOfficial,
                        Null(r.MergeConfig), Null(r.SubMergeConfig), Null(r.ClassNameOverrides), Null(r.SubClassNameOverrides),
                        Null(r.ShootOffConfig), Null(r.SubShootOffConfig), Null(r.HonoraryAwardConfig),
                        Truncate(r.LastUpdated), now);
                    scope.Complete();
                    inserted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"Tävling {r.CompetitionId}: {ex.Message}");
                    _logger.LogError(ex, "Flytten av tävling {CompetitionId}s resultatlista misslyckades — rullad tillbaka", r.CompetitionId);
                }
            }

            var reseed = Reseed();
            _logger.LogWarning("Resultatlistornas flytt: {Inserted} skrivna, {Skipped} fanns redan, {Failed} misslyckades, RESEED {Reseed}",
                inserted, skipped, failed, reseed);
            return new ApplyResult
            {
                Ok = failed == 0,
                Message = failed == 0 ? "Flytten är klar." : $"{failed} listor kunde inte flyttas — se felen. Kör om när de är åtgärdade.",
                Inserted = inserted, Skipped = skipped, Failed = failed, Errors = errors, Reseed = reseed
            };
        }

        /// <summary>Identiteten fortsätter efter det högsta id:t i tabellen.</summary>
        private int Reseed()
        {
            using var scope = _scopeProvider.CreateScope();
            var max = scope.Database.ExecuteScalar<int>($"SELECT ISNULL(MAX(Id), 0) FROM {SqlCompetitionResultStore.Table}");
            if (max > 0) scope.Database.Execute($"DBCC CHECKIDENT('{SqlCompetitionResultStore.Table}', RESEED, {max})");
            scope.Complete();
            return max;
        }

        public sealed class VerifyResult
        {
            public int Compared { get; init; }
            public int Mismatches => Problems.Count;
            public List<string> Problems { get; init; } = new();
        }

        /// <summary>Jämför noderna med tabellen, tävling för tävling och fält för fält. Läser bara.</summary>
        public VerifyResult Verify()
        {
            var problems = new List<string>();
            var nodes = LoadAllNodes();
            var rows = _table.GetMany(nodes.Select(n => n.CompetitionId).ToList());
            foreach (var n in nodes)
            {
                if (!rows.TryGetValue(n.CompetitionId, out var t)) { problems.Add($"Tävling {n.CompetitionId}: saknas i tabellen"); continue; }
                void Diff(string field, object? a, object? b)
                {
                    if (!Equals(a, b)) problems.Add($"Tävling {n.CompetitionId}: {field} skiljer (nod={Short(a)} rad={Short(b)})");
                }
                Diff("Id", n.Id, t.Id);
                Diff("ResultData", n.ResultData, t.ResultData);
                Diff("IsOfficial", n.IsOfficial, t.IsOfficial);
                Diff("SubIsOfficial", n.SubIsOfficial, t.SubIsOfficial);
                Diff("MergeConfig", n.MergeConfig, t.MergeConfig);
                Diff("SubMergeConfig", n.SubMergeConfig, t.SubMergeConfig);
                Diff("ClassNameOverrides", n.ClassNameOverrides, t.ClassNameOverrides);
                Diff("SubClassNameOverrides", n.SubClassNameOverrides, t.SubClassNameOverrides);
                Diff("ShootOffConfig", n.ShootOffConfig, t.ShootOffConfig);
                Diff("SubShootOffConfig", n.SubShootOffConfig, t.SubShootOffConfig);
                Diff("HonoraryAwardConfig", n.HonoraryAwardConfig, t.HonoraryAwardConfig);
                Diff("LastUpdated", Truncate(n.LastUpdated), t.LastUpdated);
            }
            return new VerifyResult { Compared = nodes.Count, Problems = problems };
        }

        /// <summary>
        /// Byter lagring. Till tabellen bara under frysning och bara när jämförelsen är ren; tillbaka till
        /// noderna alltid (återställningen ska aldrig kunna vägras av en kontroll).
        /// </summary>
        public (bool Ok, string Message, List<string> Problems) Switch(string store, int? byMemberId)
        {
            if (string.Equals(store, CompetitionResultStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Set(CompetitionResultStoreSettings.StoreKey, CompetitionResultStoreSettings.StoreNode, byMemberId);
                return (true, "Noderna gäller igen.", new List<string>());
            }
            if (!string.Equals(store, CompetitionResultStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                return (false, "Okänd lagring.", new List<string>());
            if (!IsWriteFrozen)
                return (false, "Frys skrivningarna till resultatlistorna först.", new List<string>());
            var verify = Verify();
            if (verify.Mismatches > 0)
                return (false, $"{verify.Mismatches} avvikelser mellan noderna och tabellen — växeln slås inte om.", verify.Problems.Take(50).ToList());
            _settings.Set(CompetitionResultStoreSettings.StoreKey, CompetitionResultStoreSettings.StoreTable, byMemberId);
            return (true, $"Tabellen gäller nu ({verify.Compared} resultatlistor jämförda).", new List<string>());
        }

        /// <summary>Avpublicerar högst <paramref name="batch"/> resultatnoder. Bara efter bytet. Raderar ingenting.</summary>
        public (int Unpublished, int Remaining, string Message) UnpublishNodes(int batch)
        {
            if (NodesCurrent)
                return (0, 0, "Avpubliceringen körs först efter bytet till tabellen.");
            batch = Math.Clamp(batch, 1, 200);

            List<int> published;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                published = scope.Database.Fetch<int>(@"
SELECT n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
JOIN umbracoDocument d ON d.nodeId = n.id
WHERE ct.alias = @0 AND d.published = 1 AND n.trashed = 0
ORDER BY n.id", CompetitionResultFields.NodeAlias);
            }

            int done = 0;
            foreach (var id in published.Take(batch))
            {
                try
                {
                    var node = _contentService.GetById(id);
                    if (node != null && node.Published) { _contentService.Unpublish(node); done++; }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Resultatnod {Id} kunde inte avpubliceras", id);
                }
            }
            var remaining = Math.Max(0, published.Count - done);
            return (done, remaining, remaining == 0 ? "Alla resultatnoder är avpublicerade." : $"{remaining} kvar — kör igen.");
        }

        /// <summary>Tabellen lagrar sekunder (DATETIME2(0)); jämförelsen och flytten använder samma upplösning.</summary>
        private static DateTime? Truncate(DateTime? d) =>
            d is DateTime v ? new DateTime(v.Ticks - v.Ticks % TimeSpan.TicksPerSecond, v.Kind) : null;

        private static string Short(object? v)
        {
            var s = v?.ToString() ?? "null";
            return s.Length > 40 ? s[..40] + $"… ({s.Length} tecken)" : s;
        }

        private static string? Null(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
