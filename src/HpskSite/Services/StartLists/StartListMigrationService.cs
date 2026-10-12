using HpskSite.Models.StartLists;
using HpskSite.Services.SiteSettings;
using HpskSite.Services.Hosting;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// <b>Flytten av startlistorna från noder till tabellen</b> (skalplanen fas C2). Samma ordning och samma
    /// regler som resultatlistornas flytt (<c>CompetitionResultMigrationService</c>):
    /// <list type="number">
    /// <item><see cref="Preview"/> — torrkörning, skriver ingenting.</item>
    /// <item><see cref="SetWriteFreeze"/>(true) — alla skrivningar till startlistorna vägras.</item>
    /// <item><see cref="Apply"/> — en rad per lista, nodens id bevaras, idempotent.</item>
    /// <item><see cref="Verify"/> — fält för fält mellan nod och rad.</item>
    /// <item><see cref="Switch"/>("table") — vägras om jämförelsen inte är ren.</item>
    /// <item><see cref="SetWriteFreeze"/>(false).</item>
    /// <item><see cref="UnpublishNodes"/> — i portioner, efter att skrivningarna släppts. Adresserna
    /// <c>/startlista/</c> och <c>/finalstartlista-x/</c> fungerar ändå: tävlingssidans adressuppslag visar
    /// listan ur tabellen när noden inte längre är publicerad.</item>
    /// </list>
    ///
    /// <para><b>⚠️ Id:n bevaras</b> — klienten, <c>&amp;fsl=</c>, påminnelseloggen och kalenderfilernas UID
    /// bär dem, och finalens <c>QualificationStartListId</c> pekar på grundomgångens id.</para>
    ///
    /// <para><b>⚠️ Utkastet flyttas, inte det publicerade.</b> En lista vars utkast skiljer sig från det
    /// publicerade (sparad med <c>StartListPublish.Never</c>) visar efter flytten det senast sparade.
    /// Torrkörningen räknar dem. De publika ytorna frågar ändå efter <c>IsOfficial</c>.</para>
    ///
    /// <para><b>Återställning</b> är <see cref="Switch"/>("node") — noderna ligger orörda kvar. Det gäller
    /// bara tills nya skrivningar gjorts i tabellen; efter det är det en framåträttning.</para>
    /// </summary>
    public class StartListMigrationService
    {
        private readonly NodeStartListStore _nodes;
        private readonly SqlStartListStore _table;
        private readonly SiteSettingService _settings;
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly ILogger<StartListMigrationService> _logger;

        public StartListMigrationService(
            NodeStartListStore nodes, SqlStartListStore table, SiteSettingService settings,
            IScopeProvider scopeProvider, IContentService contentService, ILogger<StartListMigrationService> logger)
        {
            _nodes = nodes;
            _table = table;
            _settings = settings;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _logger = logger;
        }

        public string CurrentStore => _settings.Get(StartListStoreSettings.StoreKey, StartListStoreSettings.StoreNode);
        public bool IsWriteFrozen => _settings.Get(StartListStoreSettings.WriteFreezeKey, "0") == "1";
        private bool NodesCurrent => string.Equals(CurrentStore, StartListStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase);

        private sealed class NodeRow
        {
            public int NodeId { get; set; }
            public int CompetitionId { get; set; }
            public bool Published { get; set; }
            public bool Edited { get; set; }
        }

        /// <summary>
        /// Startlistenoderna under en tävling (direkt eller under den gamla hubben). Noder som inte ligger
        /// under en tävling räknas som föräldralösa och flyttas inte — ingen lagring hittar dem.
        /// </summary>
        private (List<NodeRow> Lists, int Orphans) AllNodes()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var all = scope.Database.Fetch<NodeRow>(@"
SELECT n.id AS NodeId,
       CASE WHEN pct.alias = 'competition' THEN p.id
            WHEN pct.alias = @2 AND gpct.alias = 'competition' THEN gp.id
            ELSE 0 END AS CompetitionId,
       CAST(ISNULL(d.published, 0) AS BIT) AS Published,
       CAST(ISNULL(d.edited, 0) AS BIT) AS Edited
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias IN (@0, @1)
LEFT JOIN umbracoDocument d ON d.nodeId = n.id
JOIN umbracoNode p ON p.id = n.parentId
JOIN umbracoContent pc ON pc.nodeId = p.id
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
LEFT JOIN umbracoNode gp ON gp.id = p.parentId AND gp.trashed = 0
LEFT JOIN umbracoContent gpc ON gpc.nodeId = gp.id
LEFT JOIN cmsContentType gpct ON gpct.nodeId = gpc.contentTypeId
WHERE n.trashed = 0 AND p.trashed = 0
ORDER BY n.id", StartListNodes.QualifyingAlias, StartListNodes.FinalsAlias, StartListNodes.LegacyHubAlias);
            return (all.Where(r => r.CompetitionId > 0).ToList(), all.Count(r => r.CompetitionId <= 0));
        }

        /// <summary>Det noderna säger, en post per lista — samma uppslag som lagringen gör.</summary>
        public List<StartListRecord> LoadAllNodes()
        {
            var list = new List<StartListRecord>();
            foreach (var competitionId in AllNodes().Lists.Select(n => n.CompetitionId).Distinct())
                list.AddRange(_nodes.List(competitionId));
            return list;
        }

        public sealed class PreviewResult
        {
            public string Store { get; init; } = "";
            public bool WriteFrozen { get; init; }
            public bool TableExists { get; init; }
            public int Nodes { get; init; }
            public int Competitions { get; init; }
            public int Qualifying { get; init; }
            public int Finals { get; init; }
            public int Official { get; init; }
            public int LegacyHub { get; init; }
            /// <summary>Utkastet skiljer sig från det publicerade — efter flytten gäller utkastet.</summary>
            public int DraftAhead { get; init; }
            /// <summary>Startlistenoder som inte ligger under en tävling — flyttas inte.</summary>
            public int Orphans { get; init; }
            public int RowsAlreadyInTable { get; init; }
            public List<string> Notes { get; init; } = new();
            /// <summary>Samma form som de andra delarnas torrkörning (underhållspanelen läser den).</summary>
            public List<string> MissingSchema => TableExists ? new List<string>() : new List<string> { "CompetitionStartList" };
        }

        public PreviewResult Preview()
        {
            var exists = StartListSchemaGuardHostedService.TableExists(_scopeProvider);
            var (nodes, orphans) = AllNodes();
            var records = LoadAllNodes();
            int inTable = 0;
            if (exists)
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                inTable = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlStartListStore.Table}");
            }
            var notes = new List<string>();
            var draftAhead = nodes.Count(n => n.Published && n.Edited);
            if (draftAhead > 0)
                notes.Add($"{draftAhead} listor har sparade ändringar som inte publicerats. Efter flytten gäller det senast sparade.");
            if (orphans > 0)
                notes.Add($"{orphans} startlistenoder ligger inte under en tävling och flyttas inte.");
            var missing = nodes.Select(n => n.NodeId).Except(records.Select(r => r.Id)).ToList();
            if (missing.Count > 0)
                notes.Add($"{missing.Count} noder kunde inte läsas genom lagringen: {string.Join(", ", missing.Take(20))}");

            return new PreviewResult
            {
                Store = CurrentStore,
                WriteFrozen = IsWriteFrozen,
                TableExists = exists,
                Nodes = nodes.Count,
                Competitions = records.Select(r => r.CompetitionId).Distinct().Count(),
                Qualifying = records.Count(r => r.Kind == StartListKind.Qualifying),
                Finals = records.Count(r => r.Kind == StartListKind.Finals),
                Official = records.Count(r => r.IsOfficial),
                LegacyHub = records.Count(r => r.LegacyHub),
                DraftAhead = draftAhead,
                Orphans = orphans,
                RowsAlreadyInTable = inTable,
                Notes = notes,
            };
        }

        public void SetWriteFreeze(bool on, int? byMemberId)
            => _settings.Set(StartListStoreSettings.WriteFreezeKey, on ? "1" : "0", byMemberId);

        /// <summary>Tömmer tabellen inför ett nytt försök. Bara när noderna gäller — då läser ingen den.</summary>
        public (bool Ok, string Message) ClearTable(int? byMemberId)
        {
            if (!NodesCurrent) return (false, "Tabellen gäller — gå tillbaka till noderna först.");
            using var scope = _scopeProvider.CreateScope();
            var rows = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlStartListStore.Table}");
            scope.Database.Execute($"DELETE FROM {SqlStartListStore.Table}");
            NegativeIdentity.Reseed(scope.Database, SqlStartListStore.Table);
            scope.Complete();
            _logger.LogWarning("Startlistornas tabell tömd ({Rows} rader) av medlem {MemberId}", rows, byMemberId);
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
        /// Skriver nodernas listor till tabellen, en tävling per transaktion. Kräver frysning och att noderna
        /// fortfarande gäller. Idempotent: en lista vars id redan finns hoppas över.
        /// </summary>
        public ApplyResult Apply()
        {
            if (!IsWriteFrozen)
                return new ApplyResult { Message = "Frys skrivningarna till startlistorna först (StartLists:WriteFreeze)." };
            if (!NodesCurrent)
                return new ApplyResult { Message = "Tabellagringen gäller redan — flytten körs bara från noderna." };
            if (!StartListSchemaGuardHostedService.TableExists(_scopeProvider))
                return new ApplyResult { Message = "Tabellen saknas: kör Migrations/create-competition-start-list-table.sql." };

            int inserted = 0, skipped = 0, failed = 0;
            var errors = new List<string>();
            foreach (var group in LoadAllNodes().GroupBy(r => r.CompetitionId))
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope();
                    var db = scope.Database;
                    var now = DateTime.Now;
                    int ins = 0, skip = 0;
                    foreach (var r in group)
                    {
                        if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {SqlStartListStore.Table} WHERE Id = @0", r.Id) > 0)
                        {
                            skip++;
                            continue;
                        }
                        db.Execute($@"
SET IDENTITY_INSERT {SqlStartListStore.Table} ON;
INSERT INTO {SqlStartListStore.Table}
    (Id, CompetitionId, Kind, Name, UrlSegment, ConfigurationData, StartListContent, IsOfficial, GeneratedDate,
     GeneratedBy, TeamFormat, Notes, QualifyingResultsSnapshot, PerClassConfigData, TotalFinalists,
     MaxShootersPerTeam, QualificationStartListId, LegacyHub, SortOrder, CreatedDate, UpdatedDate)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15, @16, @17, @18, @19, @19);
SET IDENTITY_INSERT {SqlStartListStore.Table} OFF;",
                            r.Id, r.CompetitionId, (byte)(r.Kind == StartListKind.Finals ? 1 : 0),
                            string.IsNullOrWhiteSpace(r.Name) ? StartListNodes.QualifyingName : r.Name, r.UrlSegment ?? "",
                            Null(r.ConfigurationData), Null(r.StartListContent), r.IsOfficial, SqlStartListStore.Truncate(r.GeneratedDate),
                            Null(r.GeneratedBy), Null(r.TeamFormat), Null(r.Notes), Null(r.QualifyingResultsSnapshot),
                            Null(r.PerClassConfigData), r.TotalFinalists, r.MaxShootersPerTeam, r.QualificationStartListId,
                            r.LegacyHub, r.SortOrder, now);
                        ins++;
                    }
                    scope.Complete();
                    inserted += ins;
                    skipped += skip;
                }
                catch (Exception ex)
                {
                    failed += group.Count();
                    errors.Add($"Tävling {group.Key}: {ex.Message}");
                    _logger.LogError(ex, "Flytten av tävling {CompetitionId}s startlistor misslyckades — rullad tillbaka", group.Key);
                }
            }

            var reseed = Reseed();
            _logger.LogWarning("Startlistornas flytt: {Inserted} skrivna, {Skipped} fanns redan, {Failed} misslyckades, RESEED {Reseed}",
                inserted, skipped, failed, reseed);
            return new ApplyResult
            {
                Ok = failed == 0,
                Message = failed == 0 ? "Flytten är klar." : $"{failed} listor kunde inte flyttas — se felen. Kör om när de är åtgärdade.",
                Inserted = inserted, Skipped = skipped, Failed = failed, Errors = errors, Reseed = reseed
            };
        }

        /// <summary>
        /// Nya rader får NEGATIVA id:n under alla befintliga, aldrig samma tal som en innehållsnod. Se
        /// <see cref="NegativeIdentity"/>.
        /// </summary>
        private int Reseed()
        {
            using var scope = _scopeProvider.CreateScope();
            var seed = NegativeIdentity.Reseed(scope.Database, SqlStartListStore.Table);
            scope.Complete();
            return seed;
        }

        public sealed class VerifyResult
        {
            public int Compared { get; init; }
            public int Mismatches => Problems.Count;
            public List<string> Problems { get; init; } = new();
        }

        /// <summary>Jämför noderna med tabellen, lista för lista och fält för fält. Läser bara.</summary>
        public VerifyResult Verify()
        {
            var problems = new List<string>();
            var nodes = LoadAllNodes();
            var nodeIds = new HashSet<int>(nodes.Select(n => n.Id));
            foreach (var n in nodes)
            {
                var t = _table.Get(n.Id);
                if (t == null) { problems.Add($"Lista {n.Id} (tävling {n.CompetitionId}): saknas i tabellen"); continue; }
                void Diff(string field, object? a, object? b)
                {
                    if (!Equals(a, b)) problems.Add($"Lista {n.Id}: {field} skiljer (nod={Short(a)} rad={Short(b)})");
                }
                Diff("CompetitionId", n.CompetitionId, t.CompetitionId);
                Diff("Kind", n.Kind, t.Kind);
                Diff("Name", string.IsNullOrWhiteSpace(n.Name) ? StartListNodes.QualifyingName : n.Name, t.Name);
                Diff("UrlSegment", n.UrlSegment ?? "", t.UrlSegment);
                Diff("ConfigurationData", n.ConfigurationData ?? "", t.ConfigurationData);
                Diff("StartListContent", n.StartListContent ?? "", t.StartListContent);
                Diff("IsOfficial", n.IsOfficial, t.IsOfficial);
                Diff("GeneratedDate", SqlStartListStore.Truncate(n.GeneratedDate), t.GeneratedDate);
                Diff("GeneratedBy", n.GeneratedBy ?? "", t.GeneratedBy);
                Diff("TeamFormat", n.TeamFormat ?? "", t.TeamFormat);
                Diff("Notes", n.Notes ?? "", t.Notes);
                Diff("QualifyingResultsSnapshot", n.QualifyingResultsSnapshot ?? "", t.QualifyingResultsSnapshot);
                Diff("PerClassConfigData", n.PerClassConfigData ?? "", t.PerClassConfigData);
                Diff("TotalFinalists", n.TotalFinalists, t.TotalFinalists);
                Diff("MaxShootersPerTeam", n.MaxShootersPerTeam, t.MaxShootersPerTeam);
                Diff("QualificationStartListId", n.QualificationStartListId, t.QualificationStartListId);
                Diff("LegacyHub", n.LegacyHub, t.LegacyHub);
            }
            // Rader utan nod: något har skrivit i tabellen medan noderna gällde.
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var extra = scope.Database.Fetch<int>($"SELECT Id FROM {SqlStartListStore.Table}").Where(id => !nodeIds.Contains(id)).ToList();
                if (extra.Count > 0)
                    problems.Add($"{extra.Count} rader i tabellen saknar nod: {string.Join(", ", extra.Take(20))}");
            }
            return new VerifyResult { Compared = nodes.Count, Problems = problems };
        }

        /// <summary>
        /// Byter lagring. Till tabellen bara under frysning och bara när jämförelsen är ren; tillbaka till
        /// noderna alltid (återställningen ska aldrig kunna vägras av en kontroll).
        /// </summary>
        public (bool Ok, string Message, List<string> Problems) Switch(string store, int? byMemberId)
        {
            if (string.Equals(store, StartListStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Set(StartListStoreSettings.StoreKey, StartListStoreSettings.StoreNode, byMemberId);
                return (true, "Noderna gäller igen.", new List<string>());
            }
            if (!string.Equals(store, StartListStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                return (false, "Okänd lagring.", new List<string>());
            if (!IsWriteFrozen)
                return (false, "Frys skrivningarna till startlistorna först.", new List<string>());
            var verify = Verify();
            if (verify.Mismatches > 0)
                return (false, $"{verify.Mismatches} avvikelser mellan noderna och tabellen — växeln slås inte om.", verify.Problems.Take(50).ToList());
            _settings.Set(StartListStoreSettings.StoreKey, StartListStoreSettings.StoreTable, byMemberId);
            return (true, $"Tabellen gäller nu ({verify.Compared} startlistor jämförda).", new List<string>());
        }

        /// <summary>Avpublicerar högst <paramref name="batch"/> startlistenoder. Bara efter bytet. Raderar ingenting.</summary>
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
WHERE ct.alias IN (@0, @1) AND d.published = 1 AND n.trashed = 0
ORDER BY n.id", StartListNodes.QualifyingAlias, StartListNodes.FinalsAlias);
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
                    _logger.LogError(ex, "Startlistenod {Id} kunde inte avpubliceras", id);
                }
            }
            var remaining = Math.Max(0, published.Count - done);
            return (done, remaining, remaining == 0 ? "Alla startlistenoder är avpublicerade." : $"{remaining} kvar — kör igen.");
        }

        private static string Short(object? v)
        {
            var s = v?.ToString() ?? "null";
            return s.Length > 40 ? s[..40] + $"… ({s.Length} tecken)" : s;
        }

        private static string? Null(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
