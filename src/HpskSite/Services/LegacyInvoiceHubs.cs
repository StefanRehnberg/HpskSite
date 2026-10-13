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

        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public LegacyInvoiceHubs(IScopeProvider scopeProvider, IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        /// <summary>Mappens id(n) för tävlingen, den direkt under tävlingen först. Tom lista = ingen mapp.</summary>
        public IReadOnlyList<int> HubIds(int competitionId)
        {
            if (competitionId <= 0) return Array.Empty<int>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(@"
SELECT h.id FROM umbracoNode h
JOIN umbracoContent c ON c.nodeId = h.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = @1 AND h.trashed = 0
  AND (h.parentId = @0 OR EXISTS (
        SELECT 1 FROM umbracoNode i
        JOIN umbracoContentVersion cv ON cv.nodeId = i.id AND cv.[current] = 1
        JOIN umbracoPropertyData pd ON pd.versionId = cv.id
        JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
        WHERE i.parentId = h.id AND i.trashed = 0 AND pt.alias = 'competitionId' AND pd.intValue = @0))
ORDER BY CASE WHEN h.parentId = @0 THEN 0 ELSE 1 END, h.id", competitionId, HubAlias);
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
            var rows = scope.Database.Fetch<HubRow>(@"
SELECT h.id AS HubId,
       CASE WHEN pct.alias = 'competition' THEN h.parentId ELSE
         (SELECT TOP 1 pd.intValue FROM umbracoNode i
          JOIN umbracoContentVersion cv ON cv.nodeId = i.id AND cv.[current] = 1
          JOIN umbracoPropertyData pd ON pd.versionId = cv.id
          JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
          WHERE i.parentId = h.id AND i.trashed = 0 AND pt.alias = 'competitionId' AND pd.intValue IS NOT NULL) END AS CompetitionId
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
