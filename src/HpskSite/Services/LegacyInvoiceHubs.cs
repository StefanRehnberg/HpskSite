using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Den gamla betalmodellens fakturamapp (<c>registrationInvoicesHub</c>) för en tävling — EN väg dit
    /// (C3-planen F1, 2026-10-13; LAG: id-reglerna, regel 2).
    ///
    /// <para><b>Varför:</b> ~33 ställen letade upp mappen bland tävlingens barn. När tävlingen lämnar
    /// Umbraco-trädet flyttas mapparna, oförändrade, till ett arkiv. Då hittas mappen inte längre bland
    /// barnen, men varje faktura bär sin tävling i egenskapen <c>competitionId</c>. Tjänsten svarar på båda:
    /// först en mapp direkt under tävlingen (som i dag), annars den mapp vars fakturor bär tävlingens id.</para>
    ///
    /// <para><b>Ingen cache, med flit.</b> Varje anropare gjorde redan en databasfråga (<c>GetPagedChildren</c>),
    /// och skapandet i <c>PaymentService</c> bygger på svaret — en inaktuell cache hade gett två mappar för
    /// samma tävling.</para>
    ///
    /// <para>En tävling har högst en mapp. Skulle två finnas väljs den direkt under tävlingen, sedan lägst id
    /// (samma som de gamla <c>FirstOrDefault</c>-uppslagen). <see cref="Hubs"/> ger alla, för de två ställen
    /// som redan läste flera.</para>
    /// </summary>
    public class LegacyInvoiceHubs
    {
        public const string HubAlias = "registrationInvoicesHub";
        public const string InvoiceAlias = "registrationInvoice";
        public const string CompetitionAlias = "competition";
        public const string ArchiveTable = "dbo.LegacyInvoiceHubArchive";
        /// <summary>Fakturaarkivets nod (C3-planen F1), sparad som id — aldrig uppslagen på namnet.</summary>
        public const string ArchiveFolderSettingKey = "LegacyInvoices:ArchiveFolderId";

        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly HpskSite.Services.SiteSettings.SiteSettingService _settings;
        private static volatile bool _archiveTableSeen;

        public LegacyInvoiceHubs(IScopeProvider scopeProvider, IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor,
            HpskSite.Services.SiteSettings.SiteSettingService settings)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _settings = settings;
        }

        /// <summary>
        /// Finns arkivtabellen? Ett ja minns för processens livstid; ett nej prövas om (migreringen kan köras under drift).
        /// En fråga mot en tabell som saknas fäller hela satsen, så frågorna nedan väljer form efter svaret.
        /// </summary>
        internal bool ArchiveTableExists(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db)
        {
            if (_archiveTableSeen) return true;
            var exists = db.ExecuteScalar<int?>($"SELECT OBJECT_ID('{ArchiveTable}', 'U')") != null;
            if (exists) _archiveTableSeen = true;
            return exists;
        }

        /// <summary>
        /// Mappens id(n) för tävlingen: den direkt under tävlingsNODEN först, sedan den som flyttats till arkivet med
        /// tävlingen inskriven, sist den vars fakturor bär tävlingens id. Tom lista = ingen mapp.
        /// <para>⚠️ Föräldern måste VARA en tävling (LAG: id-reglerna, regel 1). Ett tävlings-id i tabellen kan vara
        /// samma tal som en annan nod — till exempel fakturaarkivets — och då hade arkivets alla mappar räknats som
        /// den tävlingens.</para>
        /// </summary>
        public IReadOnlyList<int> HubIds(int competitionId)
        {
            if (competitionId <= 0) return Array.Empty<int>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var archive = ArchiveTableExists(db);
            var archived = archive ? $"UNION ALL SELECT a.HubId, 1 FROM {ArchiveTable} a WHERE a.CompetitionId = @0" : "";
            // ⚠️ Tre KANDIDATKÄLLOR med UNION, inte tre villkor med OR (skalplanen, mätningen 2026-10-19). Med OR
            // prövades fakturornas egenskapsdata för VARJE fakturamapp i hela sajten — 111 000 logiska läsningar och
            // ~620 ms per anrop på 20 000-kopian, i varje öppning av Anmälningar. Nu söks fakturorna från sitt
            // competitionId: 25 ms, samma svar för varje tävling i 20 000-kopian och i prod. Rangen ger samma ordning.
            // ⚠️ Frågan MÅSTE börja med SELECT: NPoco:s Fetch lägger annars till "SELECT * FROM …" framför (en första
            // version med ;WITH gav "Incorrect syntax near FROM" — och anmälningslistan svarade med felet).
            return db.Fetch<int>($@"SELECT h.id FROM (
    SELECT cand.id, MIN(cand.rnk) AS rnk FROM (
        SELECT h.id, 0 AS rnk FROM umbracoNode h
        JOIN umbracoContent pc ON pc.nodeId = h.parentId
        JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
        WHERE h.parentId = @0 AND pct.alias = @2
        {archived}
        UNION ALL SELECT i.parentId, 2 FROM umbracoPropertyData pd
            JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId AND pt.alias = 'competitionId'
            JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.[current] = 1
            JOIN umbracoNode i ON i.id = cv.nodeId AND i.trashed = 0
            WHERE pd.intValue = @0) cand
    GROUP BY cand.id) r
JOIN umbracoNode h ON h.id = r.id
JOIN umbracoContent c ON c.nodeId = h.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = @1 AND h.trashed = 0
ORDER BY r.rnk, h.id",
                competitionId, HubAlias, CompetitionAlias);
        }

        /// <summary>Fakturaarkivets nod-id, eller 0 när arkivet inte är upplagt.</summary>
        public int ArchiveFolderId() =>
            int.TryParse(_settings.Get(ArchiveFolderSettingKey, ""), out var id) && id > 0 ? id : 0;

        /// <summary>
        /// Skapar tävlingens fakturamapp — i arkivet när det finns (tävlingen skrivs in i arkivtabellen), annars under
        /// tävlingsnoden som förut. Null när ingen av vägarna går. ⚠️ Tävlingsnoden prövas med alias (regel 2).
        /// </summary>
        public IContent? CreateHub(int competitionId, out string? problem)
        {
            problem = null;
            int parentId;
            var folderId = ArchiveFolderId();
            bool toArchive;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                toArchive = folderId > 0 && ArchiveTableExists(scope.Database);
            if (toArchive)
            {
                parentId = folderId;
            }
            else
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null || competition.ContentType.Alias != CompetitionAlias || competition.Trashed)
                {
                    problem = "Tävlingen finns inte som nod och fakturaarkivet är inte upplagt.";
                    return null;
                }
                parentId = competition.Id;
            }

            var hub = _contentService.Create("Fakturor", parentId, HubAlias);
            if (!_contentService.Save(hub).Success) { problem = "Fakturamappen kunde inte sparas."; return null; }
            if (toArchive)
            {
                using var scope = _scopeProvider.CreateScope();
                scope.Database.Execute($@"INSERT INTO {ArchiveTable} (HubId, CompetitionId, OldParentId, MovedUtc, MovedBy)
                    VALUES (@0, @1, 0, GETUTCDATE(), NULL)", hub.Id, competitionId);
                scope.Complete();
            }
            if (!_contentService.Publish(hub, new[] { "*" }, -1).Success)
            {
                // En opublicerad mapp syns inte för betalningen — ta bort den, och arkivraden, hellre än att lämna en
                // mapp som nästa anrop hittar och lägger fakturor i.
                problem = "Fakturamappen kunde inte publiceras.";
                if (toArchive)
                {
                    using var scope = _scopeProvider.CreateScope();
                    scope.Database.Execute($"DELETE FROM {ArchiveTable} WHERE HubId = @0", hub.Id);
                    scope.Complete();
                }
                _contentService.Delete(hub);
                return null;
            }
            return hub;
        }

        /// <summary>Tävlingens fakturamapp (utkastet, <see cref="IContent"/>), eller null.</summary>
        public IContent? Hub(int competitionId)
        {
            var id = HubIds(competitionId).FirstOrDefault();
            return id > 0 ? _contentService.GetById(id) : null;
        }

        /// <summary>Alla tävlingens fakturamappar (normalt en).</summary>
        public IReadOnlyList<IContent> Hubs(int competitionId)
            => HubIds(competitionId).Select(id => _contentService.GetById(id)).Where(h => h != null).Select(h => h!).ToList();

        /// <summary>Tävlingens fakturamapp ur den publicerade cachen, eller null.</summary>
        public IPublishedContent? PublishedHub(int competitionId)
        {
            var id = HubIds(competitionId).FirstOrDefault();
            if (id <= 0 || !_umbracoContextAccessor.TryGetUmbracoContext(out var ctx)) return null;
            return ctx.Content?.GetById(id);
        }

        /// <summary>
        /// Varje mapp och dess tävling i EN fråga — för svepen som går igenom alla fakturor. Tävlingen är
        /// föräldern när den är en tävling, annars det id som mappens fakturor bär.
        /// </summary>
        public IReadOnlyDictionary<int, int> CompetitionByHub()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // Arkivets mapp ligger inte under sin tävling — tävlingen står i arkivtabellen (även för en tom mapp).
            var ArchivedCompetition = ArchiveTableExists(scope.Database)
                ? $"(SELECT a.CompetitionId FROM {ArchiveTable} a WHERE a.HubId = h.id)"
                : "NULL";
            var rows = scope.Database.Fetch<HubRow>($@"
SELECT h.id AS HubId,
       CASE WHEN pct.alias = 'competition' THEN h.parentId ELSE COALESCE({ArchivedCompetition},
         (SELECT TOP 1 pd.intValue FROM umbracoNode i
          JOIN umbracoContentVersion cv ON cv.nodeId = i.id AND cv.[current] = 1
          JOIN umbracoPropertyData pd ON pd.versionId = cv.id
          JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
          WHERE i.parentId = h.id AND i.trashed = 0 AND pt.alias = 'competitionId' AND pd.intValue IS NOT NULL)) END AS CompetitionId
FROM umbracoNode h
JOIN umbracoContent c ON c.nodeId = h.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
LEFT JOIN umbracoContent pc ON pc.nodeId = h.parentId
LEFT JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
WHERE ct.alias = @0 AND h.trashed = 0", HubAlias);
            return rows.Where(r => r.CompetitionId is > 0).ToDictionary(r => r.HubId, r => r.CompetitionId!.Value);
        }

        /// <summary>
        /// Har tävlingen några gamla fakturor? Går på fakturornas egen <c>competitionId</c>, inte på var
        /// mappen ligger i trädet — svaret är detsamma före och efter flytten till arkivet.
        /// </summary>
        public bool HasInvoices(int competitionId)
        {
            if (competitionId <= 0) return false;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM umbracoNode i
    JOIN umbracoContent c ON c.nodeId = i.id
    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
    JOIN umbracoContentVersion cv ON cv.nodeId = i.id AND cv.[current] = 1
    JOIN umbracoPropertyData pd ON pd.versionId = cv.id
    JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
    WHERE ct.alias = @1 AND i.trashed = 0 AND pt.alias = 'competitionId' AND pd.intValue = @0)
THEN 1 ELSE 0 END", competitionId, InvoiceAlias) == 1;
        }

        private sealed class HubRow
        {
            public int HubId { get; set; }
            public int? CompetitionId { get; set; }
        }
    }
}
