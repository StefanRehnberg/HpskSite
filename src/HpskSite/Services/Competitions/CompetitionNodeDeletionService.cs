using HpskSite.Models.Competitions;
using HpskSite.Services.Addresses;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Raderar de gamla tävlings- och serienoderna för gott</b> (C3-planen §5.5 punkt 7). Bara när tabellerna gäller,
    /// noderna är avpublicerade och allt under dem finns någon annanstans: tävlingen och serien i sina tabeller och i
    /// arkivet (<c>CompetitionNodeArchive</c>), start- och resultatlistorna i sina tabeller, anmälningarna rensade
    /// (Anmälningar → 4), fakturamapparna i fakturaarkivet. Spärrarna i <see cref="Blockers"/> prövas före varje omgång.
    ///
    /// <para>Ordningen: tävlingarna först (med allt under dem), sedan serierna och sist de tomma årsmapparna. En nod per
    /// anrop till <c>IContentService.Delete</c>, alltså en tävling per scope, i omgångar med en tidsgräns — kör utanför
    /// trafiktid. Papperskorgen töms aldrig; tävlingar som ligger där raderas som de andra. Före varje omgång kopieras
    /// Umbracos lagrade omdirigeringar till <c>dbo.LegacyRedirect</c> — raderna försvinner med noden.</para>
    ///
    /// <para>⚠️ <see cref="HpskSite.Services.StartLists.CompetitionTableRowsCleanupHandler"/> gör ingenting i tabelläget;
    /// utan den spärren hade raderingen tömt varje tävlings start- och resultatlistor (samma id).</para>
    /// </summary>
    public class CompetitionNodeDeletionService
    {
        public const string SeasonAlias = "competitionSeason"; // competitionstore-ok: årsmappen, raderas tom
        private const string ResultAlias = "competitionResult"; // resultstore-ok: kontroll mot tabellen före radering
        private const string StartListAlias = "precisionStartList"; // startliststore-ok: kontroll mot tabellen före radering
        private const string FinalsAlias = "finalsStartList"; // startliststore-ok: kontroll mot tabellen före radering
        private const string StartListHubAlias = "competitionStartListsHub"; // startliststore-ok: tom mapp under tävlingen
        private const string RegistrationHubAlias = "competitionRegistrationsHub";
        private const string RegistrationAlias = "competitionRegistration";
        private const string TeamRegistrationAlias = "competitionTeamRegistration";

        private static readonly string[] AllowedDescendants =
            { ResultAlias, StartListAlias, FinalsAlias, StartListHubAlias, RegistrationHubAlias };

        private readonly IScopeProvider _scopes;
        private readonly SiteSettingService _settings;
        private readonly CompetitionMigrationService _migration;
        private readonly LegacyRedirectService _redirects;
        private readonly IContentService _content;
        private readonly ILogger<CompetitionNodeDeletionService> _logger;

        public CompetitionNodeDeletionService(IScopeProvider scopes, SiteSettingService settings, CompetitionMigrationService migration,
            LegacyRedirectService redirects, IContentService content, ILogger<CompetitionNodeDeletionService> logger)
        {
            _scopes = scopes;
            _settings = settings;
            _migration = migration;
            _redirects = redirects;
            _content = content;
            _logger = logger;
        }

        public sealed class State
        {
            public int Competitions { get; init; }
            public int Series { get; init; }
            public int EmptySeasons { get; init; }
            public int Descendants { get; init; }
            public int RedirectsToCopy { get; init; }
            public List<string> Blockers { get; init; } = new();
        }

        public State Preview()
        {
            var blockers = Blockers();
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            return new State
            {
                Competitions = NodeIds(db, CompetitionFieldTable.NodeAlias).Count,
                Series = NodeIds(db, CompetitionSeriesFieldTable.NodeAlias).Count,
                EmptySeasons = EmptySeasonIds(db).Count,
                Descendants = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode d WHERE EXISTS (
                    SELECT 1 FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                    WHERE ct.alias IN (@0, @1) AND d.path LIKE n.path + ',%')", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias),
                RedirectsToCopy = _redirects.TableExists() ? CountUncopiedRedirects(db) : 0,
                Blockers = blockers,
            };
        }

        /// <summary>Det som måste vara klart innan noderna får raderas. Tom = fritt.</summary>
        public List<string> Blockers()
        {
            var blockers = new List<string>(_migration.UnpublishBlockers());
            if (!string.Equals(_settings.GetFresh(CompetitionStoreSettings.WriteFreezeKey, "0"), "0", StringComparison.Ordinal))
                blockers.Add("Tävlingarna är frysta — släpp frysningen först.");
            if (!_redirects.TableExists())
                blockers.Add("dbo.LegacyRedirect saknas — kör Migrations/create-legacy-redirect-table.sql, annars försvinner de gamla omdirigeringarna med noderna.");

            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;

            var published = db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId JOIN umbracoDocument d ON d.nodeId = n.id
                WHERE ct.alias IN (@0, @1) AND d.published = 1 AND n.trashed = 0", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias);
            if (published > 0) blockers.Add($"{published} tävlings- eller serienoder är publicerade — avpublicera dem först (knappen 3).");

            Missing(db, blockers, CompetitionFieldTable.NodeAlias, SqlCompetitionStore.Table, "tävlingar som inte finns i tabellen");
            Missing(db, blockers, CompetitionSeriesFieldTable.NodeAlias, SqlCompetitionSeriesStore.Table, "serier som inte finns i tabellen");
            Missing(db, blockers, ResultAlias, HpskSite.Services.Results.SqlCompetitionResultStore.Table, "resultatnoder som inte finns i resultattabellen");
            Missing(db, blockers, StartListAlias, HpskSite.Services.StartLists.SqlStartListStore.Table, "startlistenoder som inte finns i startlistetabellen");
            Missing(db, blockers, FinalsAlias, HpskSite.Services.StartLists.SqlStartListStore.Table, "finallistenoder som inte finns i startlistetabellen");

            var notArchived = db.Fetch<int>($@"SELECT n.id FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE ct.alias IN (@0, @1) AND NOT EXISTS (SELECT 1 FROM {CompetitionMigrationService.ArchiveTable} a WHERE a.NodeId = n.id)",
                CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias);
            if (notArchived.Count > 0)
                blockers.Add($"{notArchived.Count} noder saknas i arkivet (CompetitionNodeArchive): {Ids(notArchived)}.");

            var registrations = db.ExecuteScalar<int>($@"SELECT COUNT(*) FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId WHERE ct.alias IN (@3, @4) AND {UnderFamily}",
                "", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias, RegistrationAlias, TeamRegistrationAlias);
            if (registrations > 0)
                blockers.Add($"{registrations} anmälnings- eller lagnoder finns kvar — kör Anmälningar → 4. Ta bort de gamla noderna först (lagfakturorna får sin klubb där).");

            // Allt annat under en tävling eller serie än det som räknats upp: namnges, raderas aldrig på chans.
            var others = db.Fetch<OtherRow>($@"SELECT ct.alias AS Alias, COUNT(*) AS Count FROM umbracoNode d
                JOIN umbracoContent c ON c.nodeId = d.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE EXISTS (SELECT 1 FROM umbracoNode n JOIN umbracoContent nc ON nc.nodeId = n.id JOIN cmsContentType nct ON nct.nodeId = nc.contentTypeId
                              WHERE nct.alias IN (@0, @1) AND d.path LIKE n.path + ',%')
                  AND ct.alias NOT IN (@0, @1, @2, @3, @4, @5, @6, @7, @8)
                GROUP BY ct.alias", CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias,
                AllowedDescendants[0], AllowedDescendants[1], AllowedDescendants[2], AllowedDescendants[3], AllowedDescendants[4],
                RegistrationAlias, TeamRegistrationAlias);
            if (others.Count > 0)
                blockers.Add("Under tävlingarna ligger noder som ingen flytt tagit hand om: "
                    + string.Join(", ", others.Select(o => $"{o.Count} {o.Alias}")) + ". Raderas inte förrän de är utredda.");
            return blockers;
        }

        private sealed class OtherRow
        {
            public string Alias { get; set; } = "";
            public int Count { get; set; }
        }

        /// <summary>
        /// En omgång: raderar noder tills tidsgränsen eller antalet nås. Tävlingarna först, sedan serierna, sist tomma
        /// årsmappar. Returnerar antalet raderade och hur många som återstår.
        /// </summary>
        public (bool Ok, int Deleted, int Remaining, string Message, List<string> Errors) Run(int batch, TimeSpan budget)
        {
            var blockers = Blockers();
            if (blockers.Count > 0) return (false, 0, 0, "Raderingen vägras: " + string.Join(" ", blockers), new List<string>());
            var copied = _redirects.CopyFromUmbraco();
            if (copied > 0) _logger.LogWarning("Raderingen av tävlingsnoderna: {Copied} omdirigeringar kopierades först.", copied);

            List<int> queue;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                queue = NodeIds(db, CompetitionFieldTable.NodeAlias);
                if (queue.Count == 0) queue = NodeIds(db, CompetitionSeriesFieldTable.NodeAlias);
                if (queue.Count == 0) queue = EmptySeasonIds(db);
            }
            var started = DateTime.UtcNow;
            var deleted = 0;
            var errors = new List<string>();
            foreach (var id in queue.Take(Math.Clamp(batch, 1, 200)))
            {
                if (DateTime.UtcNow - started > budget) break;
                try
                {
                    var node = _content.GetById(id);
                    if (node == null) continue;
                    var r = _content.Delete(node, Umbraco.Cms.Core.Constants.Security.SuperUserId);
                    if (r.Success) deleted++;
                    else errors.Add($"{id}: vägrades ({r.Result})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tävlingsnod {Id} kunde inte raderas", id);
                    errors.Add($"{id}: {ex.Message}");
                }
            }
            var after = Preview();
            var remaining = after.Competitions + after.Series + after.EmptySeasons;
            _logger.LogWarning("Raderingen av tävlingsnoderna: {Deleted} raderade, {Remaining} kvar, {Errors} fel.", deleted, remaining, errors.Count);
            if (remaining == 0) UpdateStatistics();
            var message = remaining == 0 ? "Alla tävlings- och serienoder är raderade."
                : errors.Count > 0 && deleted == 0 ? "Ingenting kunde raderas — se felen."
                : $"{remaining} kvar — kör igen.";
            return (errors.Count == 0 || deleted > 0, deleted, remaining, message, errors);
        }

        private void UpdateStatistics()
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                s.Database.Execute("UPDATE STATISTICS dbo.umbracoNode; UPDATE STATISTICS dbo.umbracoContent; UPDATE STATISTICS dbo.umbracoPropertyData;");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Statistiken kunde inte uppdateras efter raderingen (ofarligt).");
            }
        }

        private static List<int> NodeIds(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, string alias)
            => db.Fetch<int>(@"SELECT n.id FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId WHERE ct.alias = @0 ORDER BY n.level DESC, n.id", alias);

        private static List<int> EmptySeasonIds(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db)
            => db.Fetch<int>(@"SELECT n.id FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE ct.alias = @0 AND NOT EXISTS (SELECT 1 FROM umbracoNode k WHERE k.parentId = n.id) ORDER BY n.id", SeasonAlias);

        /// <summary>
        /// Noder av sorten som saknar rad i tabellen. Tävlingar och serier räknas alla; barnsorterna bara under en tävling
        /// eller serie — det är de som raderas. (En lista som lagts i papperskorgen för sig rörs inte.)
        /// </summary>
        private static void Missing(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, List<string> blockers, string alias, string table, string what)
        {
            var ids = db.Fetch<int>($@"SELECT n.id FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE ct.alias = @0 AND NOT EXISTS (SELECT 1 FROM {table} t WHERE t.Id = n.id)
                  AND (@0 IN (@1, @2) OR {UnderFamily})
                ORDER BY n.id", alias, CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias);
            if (ids.Count > 0) blockers.Add($"{ids.Count} {what}: {Ids(ids)}.");
        }

        /// <summary>SQL-villkor: noden <c>n</c> ligger under en tävling eller serie (parametrarna @1 och @2).</summary>
        private const string UnderFamily = @"EXISTS (SELECT 1 FROM umbracoNode f JOIN umbracoContent fc ON fc.nodeId = f.id
            JOIN cmsContentType fct ON fct.nodeId = fc.contentTypeId WHERE fct.alias IN (@1, @2) AND n.path LIKE f.path + ',%')";

        private int CountUncopiedRedirects(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db)
            => db.ExecuteScalar<int>(@"SELECT COUNT(DISTINCT LOWER(r.url)) FROM umbracoRedirectUrl r
                JOIN umbracoNode n ON n.uniqueId = r.contentKey JOIN umbracoContent c ON c.nodeId = n.id
                JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                WHERE ct.alias IN (@0, @1, @2, @3, @4)
                  AND NOT EXISTS (SELECT 1 FROM dbo.LegacyRedirect x WHERE x.TargetId = n.id)",
                CompetitionFieldTable.NodeAlias, CompetitionSeriesFieldTable.NodeAlias, ResultAlias, StartListAlias, FinalsAlias);

        private static string Ids(List<int> ids) => string.Join(", ", ids.Take(10)) + (ids.Count > 10 ? " …" : "");
    }
}
