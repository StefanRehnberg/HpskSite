using HpskSite.Services.Events;
using HpskSite.Services.Hosting;
using HpskSite.Services.Registrations;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// C3-planen F6: läget för det som flyttas ur Umbraco-trädet, för <c>/health/competitions</c> och
    /// driftskollen. Läser bara.
    ///
    /// <para>Per lagring (anmälningar, händelser): vilken som gäller, frysningen, rader mot noder, rader som
    /// bara finns i tabellen, och <b>golvet</b> — att nästa id ligger över varje id någon pekar på (LAG:
    /// id-reglerna, regel 3). För tävlingarna: hur många publicerade tävlingar som saknar en adress.
    /// Plus signalerna sedan start (saknas i lagringen, nekad behörighet på grund av fel).</para>
    ///
    /// <para>Cachad i <see cref="CacheFor"/>: golvets källor läser <c>umbracoPropertyData</c>, och en
    /// övervakare som frågar varje minut ska inte göra det varje gång.</para>
    /// </summary>
    public class CompetitionHealthService
    {
        public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
        private const string CacheKey = "ops_competition_health";

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly SiteSettingService _settings;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly IPublishedUrlProvider _urlProvider;
        private readonly AppCaches _caches;
        private readonly ILogger<CompetitionHealthService> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.CompetitionLinks _links;

        public CompetitionHealthService(IUmbracoDatabaseFactory databaseFactory, SiteSettingService settings,
            IUmbracoContextFactory contextFactory, IPublishedUrlProvider urlProvider, AppCaches caches,
            ILogger<CompetitionHealthService> logger, HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Competitions.CompetitionLinks links)
        {
            _competitions = competitions;
            _links = links;
            _databaseFactory = databaseFactory;
            _settings = settings;
            _contextFactory = contextFactory;
            _urlProvider = urlProvider;
            _caches = caches;
            _logger = logger;
        }

        public sealed class StoreState
        {
            public string Name { get; init; } = "";
            public string Table { get; init; } = "";
            public string Store { get; init; } = "node";
            public bool WriteFrozen { get; init; }
            public bool TableExists { get; init; }
            public int TableRows { get; init; }
            public int Nodes { get; init; }
            public int TableBorn { get; init; }
            public int NextId { get; init; }
            public int HighestReferenced { get; init; }
            public string? Error { get; init; }
        }

        public sealed class Report
        {
            public DateTime At { get; init; }
            public List<StoreState> Stores { get; init; } = new();
            public int Competitions { get; init; }
            public List<string> CompetitionsWithoutUrl { get; init; } = new();
            public long StoreMisses { get; init; }
            public long AuthErrors { get; init; }
            public DateTime SignalsSince { get; init; }
            public string? CompetitionError { get; init; }
            /// <summary>C3-planen S5d: dbo.CompetitionChange och dbo.CompetitionStoreVersion finns. Standard sant.</summary>
            public bool ChangeLogExists { get; init; } = true;
            /// <summary>Skrivningarna till tävlingar och serier är frysta (Competitions:WriteFreeze).</summary>
            public bool CompetitionsFrozen { get; init; }
            public string Status { get; set; } = "OK";
            public List<string> Problems { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        public Report Get(bool fresh = false)
        {
            if (fresh) _caches.RuntimeCache.ClearByKey(CacheKey);
            return _caches.RuntimeCache.GetCacheItem(CacheKey, Build, CacheFor) ?? Build();
        }

        private Report Build()
        {
            var stores = new List<StoreState>
            {
                ReadStore("Anmälningar", "dbo.CompetitionRegistration", RegistrationStoreSettings.StoreKey,
                    RegistrationStoreSettings.WriteFreezeKey, new[] { NodeRegistrationStore.RegistrationAlias }, RegistrationMigrationService.IdSources),
                ReadStore("Händelser", "dbo.ClubEvent", ClubEventStoreSettings.StoreKey,
                    ClubEventStoreSettings.WriteFreezeKey, new[] { "clubSimpleEvent" }, ClubEventMigrationService.IdSources),
                ReadStore("Startlistor", "dbo.CompetitionStartList", HpskSite.Services.StartLists.StartListStoreSettings.StoreKey,
                    HpskSite.Services.StartLists.StartListStoreSettings.WriteFreezeKey,
                    new[] { "precisionStartList", "finalsStartList" }, // startliststore-ok: bara ett antal noder i hälsokontrollen

                    HpskSite.Services.StartLists.StartListMigrationService.IdSources),
                ReadStore("Resultatlistor", "dbo.CompetitionResultList", HpskSite.Services.Results.CompetitionResultStoreSettings.StoreKey,
                    HpskSite.Services.Results.CompetitionResultStoreSettings.WriteFreezeKey,
                    new[] { "competitionResult" }, // resultstore-ok: bara ett antal noder i hälsokontrollen

                    HpskSite.Services.Results.CompetitionResultMigrationService.IdSources),
                // C3-planen §5: tävlingarna och serierna.
                ReadStore("Tävlingar", HpskSite.Services.Competitions.SqlCompetitionStore.Table,
                    HpskSite.Services.Competitions.CompetitionStoreSettings.StoreKey,
                    HpskSite.Services.Competitions.CompetitionStoreSettings.WriteFreezeKey,
                    new[] { HpskSite.Models.Competitions.CompetitionFieldTable.NodeAlias },
                    HpskSite.Services.Competitions.CompetitionMigrationService.CompetitionIdSources),
                ReadStore("Serier", HpskSite.Services.Competitions.SqlCompetitionSeriesStore.Table,
                    HpskSite.Services.Competitions.CompetitionStoreSettings.StoreKey,
                    HpskSite.Services.Competitions.CompetitionStoreSettings.WriteFreezeKey,
                    new[] { HpskSite.Models.Competitions.CompetitionSeriesFieldTable.NodeAlias },
                    HpskSite.Services.Competitions.CompetitionMigrationService.SeriesIdSources),
            };

            var (count, noUrl, compError) = ReadCompetitions();
            var report = new Report
            {
                At = DateTime.Now,
                Stores = stores,
                Competitions = count,
                CompetitionsWithoutUrl = noUrl,
                CompetitionError = compError,
                StoreMisses = OpsSignals.Count(OpsSignals.KindStoreMiss),
                AuthErrors = OpsSignals.Count(OpsSignals.KindAuthError),
                SignalsSince = OpsSignals.Since,
                ChangeLogExists = ChangeLogTablesExist(),
                CompetitionsFrozen = SafeFrozen(),
            };
            Evaluate(report);
            return report;
        }

        /// <summary>
        /// Ren regel: vad som är fel (FAIL, 503) och vad som bara ska synas (WARN). Testas i
        /// <c>CompetitionHealthTests</c>.
        /// </summary>
        public static void Evaluate(Report r)
        {
            r.Problems = new();
            r.Warnings = new();
            foreach (var s in r.Stores)
            {
                if (s.Error != null) { r.Problems.Add($"{s.Name}: kunde inte läsas ({s.Error})."); continue; }
                var table = string.Equals(s.Store, "table", StringComparison.OrdinalIgnoreCase);
                if (table && !s.TableExists)
                    r.Problems.Add($"{s.Name}: tabellen gäller men {s.Table} finns inte.");
                if (!table && s.TableBorn > 0)
                    r.Problems.Add($"{s.Name}: noderna gäller men {s.TableBorn} rader finns bara i tabellen — de syns inte.");
                // Golvet gäller bara när tabellen gäller: i nodläget skapas noder fortfarande (deras id:n går
                // förbi identiteten), tabellen skrivs inte, och flytten sätter golvet innan bytet.
                if (table && s.TableExists && s.NextId <= s.HighestReferenced)
                    r.Problems.Add($"{s.Name}: nästa id ({s.NextId}) är inte över det högsta som någon pekar på ({s.HighestReferenced}). En ny rad ärver en gammal referens.");
                if (s.WriteFrozen)
                    r.Warnings.Add($"{s.Name}: skrivningar är frysta.");
            }
            if (!r.ChangeLogExists)
                r.Warnings.Add("Tävlingarnas ändringslogg saknas — kör Migrations/create-competition-change-tables.sql.");
            if (r.CompetitionsFrozen)
                r.Warnings.Add("Tävlingar: skrivningar är frysta.");
            if (r.CompetitionError != null) r.Problems.Add("Tävlingarna: kunde inte läsas (" + r.CompetitionError + ").");
            if (r.CompetitionsWithoutUrl.Count > 0)
                r.Warnings.Add($"{r.CompetitionsWithoutUrl.Count} publicerade tävlingar saknar adress.");
            r.Status = r.Problems.Count > 0 ? "FAIL" : r.Warnings.Count > 0 ? "WARN" : "OK";
        }

        private bool ChangeLogTablesExist()
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('CompetitionChange', 'CompetitionStoreVersion')") == 2;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hälsokontrollen kunde inte läsa ändringsloggens tabeller.");
                return true;
            }
        }

        private bool SafeFrozen()
        {
            try { return HpskSite.Services.Competitions.CompetitionStoreSettings.IsWriteFrozen(_settings); }
            catch { return false; }
        }

        private StoreState ReadStore(string name, string table, string storeKey, string freezeKey, string[] aliases,
            IEnumerable<IdentityFloor.Source> sources)
        {
            var store = _settings.Get(storeKey, "node");
            var frozen = _settings.Get(freezeKey, "0") == "1";
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.CommandTimeout = 20;
                var exists = db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0, 'U')", table) != null;
                var aliasList = string.Join(",", aliases.Select(a => "'" + a.Replace("'", "''") + "'"));
                var nodes = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId WHERE n.trashed = 0 AND ct.alias IN ({aliasList})");
                if (!exists)
                    return new StoreState { Name = name, Table = table, Store = store, WriteFrozen = frozen, Nodes = nodes };
                var rows = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
                var born = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM {table} r WHERE NOT EXISTS (SELECT 1 FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE n.id = r.Id AND ct.alias IN ({aliasList}))");
                var (next, highest) = IdentityFloor.Inspect(db, table, sources);
                return new StoreState
                {
                    Name = name, Table = table, Store = store, WriteFrozen = frozen, TableExists = true,
                    TableRows = rows, Nodes = nodes, TableBorn = born, NextId = next, HighestReferenced = highest,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hälsokontrollen kunde inte läsa {Name}.", name);
                return new StoreState { Name = name, Table = table, Store = store, WriteFrozen = frozen, Error = ex.GetType().Name };
            }
        }

        private (int Count, List<string> WithoutUrl, string? Error) ReadCompetitions()
        {
            try
            {
                // Tabelläget (C3-planen §5d): tävlingarna ur lagringen; utan adress = bara id-formen finns.
                if (HpskSite.Services.Competitions.CompetitionStoreSettings.UsesTable(_settings))
                {
                    var rows = _competitions.List();
                    var missing = rows.Where(c => _links.Url(c.Id).StartsWith("/competitions/", StringComparison.OrdinalIgnoreCase))
                        .Select(c => $"{c.Id} {c.Name}").ToList();
                    return (rows.Count, missing, null);
                }

                List<int> ids;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    db.CommandTimeout = 20;
                    ids = db.Fetch<int>(@"SELECT n.id FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId JOIN umbracoDocument d ON d.nodeId = n.id
WHERE ct.alias = 'competition' AND n.trashed = 0 AND d.published = 1");
                }
                using var ctxRef = _contextFactory.EnsureUmbracoContext();
                var cache = ctxRef.UmbracoContext.Content;
                var noUrl = new List<string>();
                var count = 0;
                foreach (var id in ids)
                {
                    var node = cache?.GetById(id);
                    if (node == null) continue;
                    count++;
                    var url = _urlProvider.GetUrl(node);
                    if (string.IsNullOrEmpty(url) || url == "#")
                        noUrl.Add($"{id} {node.Name}");
                }
                return (count, noUrl, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hälsokontrollen kunde inte läsa tävlingarna.");
                return (0, new List<string>(), ex.GetType().Name);
            }
        }
    }
}
