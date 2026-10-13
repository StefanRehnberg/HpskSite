using HpskSite.Models;
using HpskSite.Models.Registrations;
using HpskSite.Services.Hosting;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// <b>Flytten av anmälningarna från noder till tabeller</b> (sajtstrukturen fas 1d). Körs i ett
    /// underhållsfönster, i den här ordningen:
    /// <list type="number">
    /// <item><see cref="Preview"/> — torrkörning, skriver ingenting.</item>
    /// <item><see cref="SetWriteFreeze"/>(true) — alla anmälningsskrivningar vägras.</item>
    /// <item><see cref="Apply"/> — en transaktion per tävling, nodens id bevaras, idempotent.</item>
    /// <item><see cref="Verify"/> — fält för fält mellan nod och rad.</item>
    /// <item><see cref="Switch"/>("table") — vägras om jämförelsen inte är ren.</item>
    /// <item><see cref="SetWriteFreeze"/>(false).</item>
    /// <item><see cref="UnpublishNodes"/> — i portioner, EFTER att skrivningarna släppts: varje
    /// avpublicering tar Umbracos globala skrivlås.</item>
    /// </list>
    ///
    /// <para><b>Återställning</b> är <see cref="Switch"/>("node") — noderna ligger orörda kvar. Det gäller
    /// bara tills nya skrivningar gjorts i tabellen; efter det är det en framåträttning.</para>
    /// </summary>
    public class RegistrationMigrationService
    {
        private readonly NodeRegistrationStore _nodes;
        private readonly SqlRegistrationStore _table;
        private readonly SiteSettingService _settings;
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly ILogger<RegistrationMigrationService> _logger;

        public RegistrationMigrationService(
            NodeRegistrationStore nodes, SqlRegistrationStore table, SiteSettingService settings,
            IScopeProvider scopeProvider, IContentService contentService, ILogger<RegistrationMigrationService> logger)
        {
            _nodes = nodes;
            _table = table;
            _settings = settings;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _logger = logger;
        }

        public string CurrentStore => _settings.Get(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreNode);
        public bool IsWriteFrozen => _settings.Get(RegistrationStoreSettings.WriteFreezeKey, "0") == "1";

        /// <summary>Alla anmälningsnoder, tävling för tävling, via nodlagringen.</summary>
        public List<RegistrationRecord> LoadAllNodes()
        {
            var all = new List<RegistrationRecord>();
            foreach (var competitionId in _nodes.CountByCompetition().Keys.OrderBy(i => i))
                all.AddRange(_nodes.List(competitionId));
            return all;
        }

        public sealed class PreviewResult
        {
            public string Store { get; init; } = "";
            public bool WriteFrozen { get; init; }
            public int Competitions { get; init; }
            public int Nodes { get; init; }
            public int RowsToWrite { get; init; }
            public int MergedNodes { get; init; }
            public int DeactivatedRows { get; init; }
            public int ClubFromLegacyText { get; init; }
            public List<string> UnknownClasses { get; init; } = new();
            public int RowsAlreadyInTable { get; init; }
            public List<string> Notes { get; init; } = new();
            public List<string> MissingSchema { get; init; } = new();
        }

        public PreviewResult Preview()
        {
            var missing = CompetitionRegistrationSchemaGuardHostedService.MissingSchema(_scopeProvider);
            var nodes = LoadAllNodes();
            var plan = RegistrationMigrationPlanner.Plan(nodes);
            int inTable = 0;
            if (missing.Count == 0)
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                inTable = scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CompetitionRegistration");
            }
            return new PreviewResult
            {
                Store = CurrentStore,
                WriteFrozen = IsWriteFrozen,
                Competitions = nodes.Select(n => n.CompetitionId).Distinct().Count(),
                Nodes = plan.NodeCount,
                RowsToWrite = plan.Rows.Count,
                MergedNodes = plan.MergedNodes,
                DeactivatedRows = plan.DeactivatedRows,
                ClubFromLegacyText = plan.ClubFromLegacyText,
                UnknownClasses = plan.UnknownClasses.OrderBy(c => c).ToList(),
                RowsAlreadyInTable = inTable,
                Notes = plan.Notes,
                MissingSchema = missing
            };
        }

        /// <summary>
        /// Tömmer anmälningstabellerna inför ett nytt försök. Bara när nodlagringen gäller — då läser
        /// ingen tabellerna. Anmälningar som gjorts i tabellen efter en flytt (och sedan återställts)
        /// försvinner; de finns inte på noderna — men deras referenser (avgiftsval, liggaren) står kvar.
        /// ⚠️ Identiteten nollställs därför ALDRIG: ett återanvänt id hade ärvt dem (<see cref="IdentityFloor"/>).
        /// </summary>
        public (bool Ok, string Message) ClearTables(int? byMemberId)
        {
            // Förbi cachen och utan senast kända värde: en tömning får aldrig bygga på en gissning.
            string store;
            try { store = _settings.GetFresh(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreNode); }
            catch (SiteSettingUnavailableException) { return (false, "Växeln kunde inte läsas — ingenting tömt. Försök igen."); }
            if (!string.Equals(store, RegistrationStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
                return (false, "Tabellen gäller — gå tillbaka till noderna först.");
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var rows = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CompetitionRegistration");
            db.Execute(@"DELETE FROM dbo.CompetitionRegistrationLegacyId;
DELETE FROM dbo.CompetitionRegistrationClass;
DELETE FROM dbo.CompetitionRegistration;
UPDATE dbo.CompetitionTeam SET LegacyRegistrationDocId = NULL;");
            IdentityFloor.Reseed(db, "dbo.CompetitionRegistration", IdSources);
            scope.Complete();
            _logger.LogWarning("Anmälningstabellerna tömda ({Rows} rader) av medlem {MemberId}", rows, byMemberId);
            return (true, $"Tabellen tömd ({rows} rader). Flytten kan köras igen.");
        }

        public void SetWriteFreeze(bool on, int? byMemberId)
            => _settings.Set(RegistrationStoreSettings.WriteFreezeKey, on ? "1" : "0", byMemberId);

        public sealed class ApplyResult
        {
            public bool Ok { get; init; }
            public string Message { get; init; } = "";
            public int Inserted { get; init; }
            public int Skipped { get; init; }
            public int LegacyIdsWritten { get; init; }
            public int CompetitionsFailed { get; init; }
            public List<string> Errors { get; init; } = new();
            public int Reseed { get; init; }
            public int TeamDocsLinked { get; init; }
        }

        /// <summary>
        /// Skriver planen till tabellerna. Kräver frysning och att nodlagringen fortfarande gäller.
        /// Idempotent: en rad vars id redan finns hoppas över, så en avbruten körning går att köra om.
        /// </summary>
        public ApplyResult Apply()
        {
            if (!IsWriteFrozen)
                return new ApplyResult { Message = "Frys anmälningsskrivningarna först (Registrations:WriteFreeze)." };
            if (!string.Equals(CurrentStore, RegistrationStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
                return new ApplyResult { Message = "Tabellagringen gäller redan — flytten körs bara från nodlagringen." };
            var missing = CompetitionRegistrationSchemaGuardHostedService.MissingSchema(_scopeProvider);
            if (missing.Count > 0)
                return new ApplyResult { Message = "Tabellerna saknas: " + string.Join(", ", missing) };

            var plan = RegistrationMigrationPlanner.Plan(LoadAllNodes());
            int inserted = 0, skipped = 0, legacy = 0, failed = 0;
            var errors = new List<string>();

            foreach (var competition in plan.Rows.GroupBy(r => r.Row.CompetitionId).OrderBy(g => g.Key))
            {
                try
                {
                    using var scope = _scopeProvider.CreateScope();
                    var db = scope.Database;
                    int compInserted = 0, compSkipped = 0, compLegacy = 0;
                    db.Execute("SET IDENTITY_INSERT dbo.CompetitionRegistration ON");
                    try
                    {
                        foreach (var planned in competition)
                        {
                            var r = planned.Row;
                            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CompetitionRegistration WHERE Id = @0", r.Id) > 0)
                            {
                                compSkipped++;
                            }
                            else
                            {
                                db.Execute(@"
INSERT INTO dbo.CompetitionRegistration
    (Id, CompetitionId, MemberId, MemberName, ClubId, LegacyClubText, IsActive, IsSubCompetition, IsCheckedIn,
     RegistrationDate, CreatedDate, UpdatedDate, RegisteredBy, LegacyInvoiceId, MemberEmail, Notes, ShooterNotes)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15, @16)",
                                    r.Id, r.CompetitionId, r.MemberId, r.MemberName ?? "", r.ClubId, Blank(r.LegacyClubText),
                                    r.IsActive, r.IsSubCompetition, r.IsCheckedIn,
                                    r.RegistrationDate == default ? r.CreatedDate : r.RegistrationDate,
                                    r.CreatedDate == default ? r.RegistrationDate : r.CreatedDate, DateTime.Now,
                                    r.RegisteredBy ?? "", r.InvoiceId is int inv && inv > 0 ? inv : (int?)null,
                                    Blank(r.MemberEmail), Blank(r.Notes), Blank(r.ShooterNotes));
                                for (var i = 0; i < r.Classes.Count; i++)
                                    db.Execute(@"INSERT INTO dbo.CompetitionRegistrationClass (RegistrationId, SortOrder, ClassKey, StartPreference, TeamNumber)
                                                 VALUES (@0, @1, @2, @3, @4)",
                                        r.Id, i, r.Classes[i].Class,
                                        string.IsNullOrWhiteSpace(r.Classes[i].StartPreference) ? "Inget" : r.Classes[i].StartPreference,
                                        r.Classes[i].TeamNumber);
                                compInserted++;
                            }

                            foreach (var legacyId in planned.MergedNodeIds)
                            {
                                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.CompetitionRegistrationLegacyId WHERE LegacyNodeId = @0", legacyId) > 0)
                                    continue;
                                db.Execute("INSERT INTO dbo.CompetitionRegistrationLegacyId (LegacyNodeId, RegistrationId, CompetitionId) VALUES (@0, @1, @2)",
                                    legacyId, r.Id, r.CompetitionId);
                                compLegacy++;
                            }
                        }
                    }
                    finally
                    {
                        db.Execute("SET IDENTITY_INSERT dbo.CompetitionRegistration OFF");
                    }
                    scope.Complete();
                    inserted += compInserted; skipped += compSkipped; legacy += compLegacy;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"Tävling {competition.Key}: {ex.Message}");
                    _logger.LogError(ex, "Flytten av tävling {CompetitionId}s anmälningar misslyckades — rullad tillbaka", competition.Key);
                }
            }

            var reseed = Reseed();
            var teamDocs = LinkTeamMirrorDocs();
            _logger.LogWarning("Anmälningsflytten: {Inserted} skrivna, {Skipped} fanns redan, {Legacy} sammanslagna id:n, {Failed} tävlingar misslyckades, RESEED {Reseed}",
                inserted, skipped, legacy, failed, reseed);
            return new ApplyResult
            {
                Ok = failed == 0,
                Message = failed == 0 ? "Flytten är klar." : $"{failed} tävlingar kunde inte flyttas — se felen. Kör om när de är åtgärdade.",
                Inserted = inserted, Skipped = skipped, LegacyIdsWritten = legacy, CompetitionsFailed = failed, Errors = errors, Reseed = reseed,
                TeamDocsLinked = teamDocs
            };
        }

        /// <summary>
        /// Lagspeglarna (<c>competitionTeamRegistration</c>) skapas inte längre. Gamla lagfakturor bär
        /// spegelns nod-id i <c>registrationId</c>, så id:t sparas på laget innan noderna avpubliceras.
        /// Idempotent: bara lag som saknar värdet.
        /// </summary>
        private int LinkTeamMirrorDocs()
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute(@"
UPDATE t SET LegacyRegistrationDocId = d.NodeId
FROM dbo.CompetitionTeam t
JOIN (
    SELECT MIN(n.id) AS NodeId,
           COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20))) AS TeamId
    FROM umbracoNode n
    JOIN umbracoContent c          ON c.nodeId = n.id
    JOIN cmsContentType ct         ON ct.nodeId = c.contentTypeId AND ct.alias = 'competitionTeamRegistration'
    JOIN umbracoContentVersion v   ON v.nodeId = n.id AND v.[current] = 1
    JOIN cmsPropertyType pt        ON pt.contentTypeId = ct.nodeId AND pt.Alias = 'teamId'
    JOIN umbracoPropertyData pd    ON pd.versionId = v.id AND pd.propertyTypeId = pt.id
    WHERE n.trashed = 0
    GROUP BY COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20)))
) d ON d.TeamId = t.Id
WHERE t.LegacyRegistrationDocId IS NULL");
            scope.Complete();
            return n;
        }

        /// <summary>
        /// Identiteten fortsätter över varje id som NÅGON pekar på: tabellen, de sammanslagna nodernas id:n,
        /// liggaren, avgiftsvalen, fakturornas <c>registrationId</c> och varje anmälnings- och lagspegelnod
        /// (papperskorgen inräknad). Annars kan en ny anmälan få en borttagen anmälans id och ärva dess
        /// betalningar och val. Går aldrig bakåt — se <see cref="IdentityFloor"/>.
        /// </summary>
        private int Reseed()
        {
            using var scope = _scopeProvider.CreateScope();
            var seed = IdentityFloor.Reseed(scope.Database, "dbo.CompetitionRegistration", IdSources);
            scope.Complete();
            return seed;
        }

        /// <summary>Allt som bär ett anmälnings-id. Läggs en ny referens till: lägg till den här.</summary>
        internal static readonly IdentityFloor.Source[] IdSources =
        {
            new("dbo.CompetitionRegistrationLegacyId", "SELECT MAX(LegacyNodeId) FROM dbo.CompetitionRegistrationLegacyId"),
            new("dbo.LedgerPayment", "SELECT MAX(SourceItemId) FROM dbo.LedgerPayment WHERE SourceType = 'competition-registration'"),
            new("sbx.LedgerPayment", "SELECT MAX(SourceItemId) FROM sbx.LedgerPayment WHERE SourceType = 'competition-registration'"),
            new("dbo.CompetitionFeeChoice", "SELECT MAX(RegistrationId) FROM dbo.CompetitionFeeChoice"),
            IdentityFloor.NodesOfType("competitionRegistration"),
            IdentityFloor.NodesOfType("competitionTeamRegistration"),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'registrationId'"),
        };

        public sealed class VerifyResult
        {
            public int Compared { get; init; }
            public int Mismatches => Problems.Count;
            public List<string> Problems { get; init; } = new();
        }

        /// <summary>
        /// Jämför planen (det noderna säger) med tabellerna, rad för rad och fält för fält. Läser bara.
        /// </summary>
        public VerifyResult Verify()
        {
            var plan = RegistrationMigrationPlanner.Plan(LoadAllNodes());
            var problems = new List<string>();
            int compared = 0;
            foreach (var competition in plan.Rows.GroupBy(r => r.Row.CompetitionId))
            {
                var rows = _table.List(competition.Key).ToDictionary(r => r.Id);
                foreach (var planned in competition)
                {
                    compared++;
                    var p = planned.Row;
                    if (!rows.TryGetValue(p.Id, out var t)) { problems.Add($"{p.Id} (tävling {p.CompetitionId}): saknas i tabellen"); continue; }
                    void Diff(string field, object? a, object? b)
                    {
                        if (!Equals(a, b)) problems.Add($"{p.Id} (tävling {p.CompetitionId}): {field} nod={a} rad={b}");
                    }
                    Diff("MemberId", p.MemberId, t.MemberId);
                    Diff("MemberName", p.MemberName ?? "", t.MemberName ?? "");
                    Diff("ClubId", p.ClubId, t.ClubId);
                    Diff("IsActive", p.IsActive, t.IsActive);
                    Diff("IsSubCompetition", p.IsSubCompetition, t.IsSubCompetition);
                    Diff("IsCheckedIn", p.IsCheckedIn, t.IsCheckedIn);
                    Diff("InvoiceId", p.InvoiceId is > 0 ? p.InvoiceId : null, t.InvoiceId);
                    Diff("RegisteredBy", p.RegisteredBy ?? "", t.RegisteredBy ?? "");
                    Diff("Klasser", ClassSignature(p.Classes), ClassSignature(t.Classes));
                    foreach (var legacyId in planned.MergedNodeIds)
                    {
                        var viaLegacy = _table.Get(p.CompetitionId, legacyId);
                        if (viaLegacy?.Id != p.Id) problems.Add($"{legacyId} (tävling {p.CompetitionId}): det sammanslagna id:t leder inte till {p.Id}");
                    }
                }
            }
            return new VerifyResult { Compared = compared, Problems = problems };
        }

        private static string ClassSignature(IEnumerable<ShootingClassEntry> classes)
            => string.Join("|", classes.Select(c => $"{c.Class}/{(string.IsNullOrWhiteSpace(c.StartPreference) ? "Inget" : c.StartPreference)}/{c.TeamNumber}"));

        /// <summary>
        /// Byter lagring. Till tabellen bara under frysning och bara när jämförelsen är ren; tillbaka till
        /// noderna alltid (återställningen ska aldrig kunna vägras av en kontroll).
        /// </summary>
        public (bool Ok, string Message, List<string> Problems) Switch(string store, int? byMemberId)
        {
            if (string.Equals(store, RegistrationStoreSettings.StoreNode, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Set(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreNode, byMemberId);
                return (true, "Nodlagringen gäller igen.", new List<string>());
            }
            if (!string.Equals(store, RegistrationStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                return (false, "Okänd lagring.", new List<string>());
            if (!IsWriteFrozen)
                return (false, "Frys anmälningsskrivningarna först.", new List<string>());
            var verify = Verify();
            if (verify.Mismatches > 0)
                return (false, $"{verify.Mismatches} avvikelser mellan noderna och tabellen — växeln slås inte om.", verify.Problems.Take(50).ToList());
            _settings.Set(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreTable, byMemberId);
            return (true, $"Tabellagringen gäller nu ({verify.Compared} anmälningar jämförda).", new List<string>());
        }

        /// <summary>
        /// Avpublicerar högst <paramref name="batch"/> anmälningsnoder (och tomma publicerade hubbar).
        /// Bara efter bytet. Raderar ingenting — noderna ligger kvar för återställning.
        /// </summary>
        public (int Unpublished, int Remaining, string Message) UnpublishNodes(int batch)
        {
            if (!string.Equals(CurrentStore, RegistrationStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase))
                return (0, 0, "Avpubliceringen körs först efter bytet till tabellagringen.");
            batch = Math.Clamp(batch, 1, 200);

            List<int> published;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                published = scope.Database.Fetch<int>(@"
SELECT n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
JOIN umbracoDocument d ON d.nodeId = n.id
WHERE ct.alias IN (@0, @1, @2) AND d.published = 1 AND n.trashed = 0
ORDER BY CASE WHEN ct.alias = @1 THEN 1 ELSE 0 END, n.id",
                    NodeRegistrationStore.RegistrationAlias, NodeRegistrationStore.HubAlias, "competitionTeamRegistration");
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
                    _logger.LogError(ex, "Anmälningsnod {Id} kunde inte avpubliceras", id);
                }
            }
            var remaining = Math.Max(0, published.Count - done);
            return (done, remaining, remaining == 0 ? "Alla anmälningsnoder är avpublicerade." : $"{remaining} kvar — kör igen.");
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
