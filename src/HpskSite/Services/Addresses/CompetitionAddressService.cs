using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Addresses
{
    /// <summary>
    /// C3-planen S6: tävlingarnas och seriernas adresser (Stefans beslut 2026-10-14, §9a).
    /// <list type="bullet">
    /// <item>Klubbens: <c>/{krets}/klubbar/{klubb}/{år}/{tävling}/</c></item>
    /// <item>Kretsens: <c>/{krets}/{år}/{tävling}/</c></item>
    /// <item>SM: <c>/sm/{år}/{tävling}/</c>, landsdel: <c>/ssm|vsm|osm|nsm/{år}/{tävling}/</c></item>
    /// <item>Serien: <c>/{värd}/{år}/{serie}/</c> — serien lämnar tävlingens adress.</item>
    /// </list>
    /// Barnen som förut: <c>…/resultat/</c>, <c>…/startlista/</c>, <c>…/finalstartlista-x/</c>.
    ///
    /// <para>Ägaren räknas ur tävlingens egna värden i samma ordning som den gamla adressgivaren: SM →
    /// landsdel (med kretsens <c>area</c>) → klubben → kretsen. Ingen ägare = ingen adress, och sidan behåller
    /// sin gamla. Sluggen är nodens <c>UrlSegment</c>, ordagrant; en krock ger <c>-2</c>.</para>
    /// </summary>
    public class CompetitionAddressService
    {
        private readonly ICompetitionStore _competitions;
        private readonly ICompetitionSeriesStore _series;
        private readonly PageAddressService _addresses;
        private readonly IUmbracoContextAccessor _ctx;
        private readonly ILogger<CompetitionAddressService> _logger;

        public CompetitionAddressService(ICompetitionStore competitions, ICompetitionSeriesStore series,
            PageAddressService addresses, IUmbracoContextAccessor ctx, ILogger<CompetitionAddressService> logger)
        {
            _competitions = competitions;
            _series = series;
            _addresses = addresses;
            _ctx = ctx;
            _logger = logger;
        }

        public sealed record SyncResult(string Kind, int Id, string? Address, bool Changed, string? Problem);

        /// <summary>Tävlingens ägare, år och grundslug — eller ett skäl till att den saknar adress.</summary>
        public (PageAddressOwner? Owner, int Year, string Slug, string? Problem) Plan(CompetitionInfo c)
        {
            var slug = c.UrlSegment;
            if (string.IsNullOrWhiteSpace(slug)) return (null, 0, "", "saknar adressdel");
            var year = c.CompetitionDate?.Year ?? SeriesYear(c.SeriesId) ?? YearOfNode(c.ParentId) ?? c.CreateDate.Year;
            var owner = OwnerFor(c.CompetitionScope, c.ClubId, c.RegionalFederation, allowNational: true);
            return owner == null
                ? (null, year, slug, "ingen värd (varken klubb, krets eller mästerskap)")
                : (owner, year, slug, null);
        }

        public (PageAddressOwner? Owner, int Year, string Slug, string? Problem) Plan(CompetitionSeriesInfo s)
        {
            var slug = s.UrlSegment;
            if (string.IsNullOrWhiteSpace(slug)) return (null, 0, "", "saknar adressdel");
            var year = s.SeriesStartDate?.Year ?? YearOfNode(s.ParentId) ?? s.CreateDate.Year;
            var owner = OwnerFor(null, s.ClubId, s.RegionalFederation, allowNational: false);
            return owner == null ? (null, year, slug, "ingen värd") : (owner, year, slug, null);
        }

        /// <summary>Tävlingens adress i takt med dess värden (det sparade läget). Skriver bara när något ändrats.</summary>
        public SyncResult SyncCompetition(int id)
        {
            var c = _competitions.GetSaved(id);
            if (c == null) return new SyncResult(PageAddressKinds.Competition, id, null, false, "hittades inte");
            var (owner, year, slug, problem) = Plan(c);
            if (owner == null) return new SyncResult(PageAddressKinds.Competition, id, null, false, problem);
            var (final, changed) = _addresses.SetCurrent(PageAddressKinds.Competition, id, owner.Value, year, slug);
            if (!string.Equals(final, slug, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Tävlingen {Id}: adressen {Owner}/{Year}/{Slug} var upptagen — fick {Final}", id, owner, year, slug, final);
            return new SyncResult(PageAddressKinds.Competition, id, $"{owner.Value.Type}:{owner.Value.Id}/{year}/{final}", changed,
                string.Equals(final, slug, StringComparison.OrdinalIgnoreCase) ? null : $"krock — fick {final}");
        }

        public SyncResult SyncSeries(int id)
        {
            var s = _series.GetSaved(id);
            if (s == null) return new SyncResult(PageAddressKinds.Series, id, null, false, "hittades inte");
            var (owner, year, slug, problem) = Plan(s);
            if (owner == null) return new SyncResult(PageAddressKinds.Series, id, null, false, problem);
            var (final, changed) = _addresses.SetCurrent(PageAddressKinds.Series, id, owner.Value, year, slug);
            return new SyncResult(PageAddressKinds.Series, id, $"{owner.Value.Type}:{owner.Value.Id}/{year}/{final}", changed,
                string.Equals(final, slug, StringComparison.OrdinalIgnoreCase) ? null : $"krock — fick {final}");
        }

        /// <summary>Alla publicerade tävlingar och serier. Serierna först, så att en tävling som krockar med sin serie får -2.</summary>
        public IReadOnlyList<SyncResult> SyncAll()
        {
            var results = new List<SyncResult>();
            foreach (var s in _series.List().OrderBy(s => s.Id)) results.Add(Safe(() => SyncSeries(s.Id), PageAddressKinds.Series, s.Id));
            foreach (var c in _competitions.List().OrderBy(c => c.Id)) results.Add(Safe(() => SyncCompetition(c.Id), PageAddressKinds.Competition, c.Id));
            return results;
        }

        private SyncResult Safe(Func<SyncResult> run, string kind, int id)
        {
            try { return run(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adressen kunde inte sättas för {Kind} {Id}", kind, id);
                return new SyncResult(kind, id, null, false, "fel: " + ex.Message);
            }
        }

        // ── Ägaren ───────────────────────────────────────────────────────────────────────

        private PageAddressOwner? OwnerFor(string? rawScope, int clubId, string? regionalFederation, bool allowNational)
        {
            if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return null;
            var clubNode = clubId > 0 ? uc.Content.GetById(clubId) : null;
            var clubRegion = clubNode?.ContentType.Alias == "club" ? clubNode.Parent?.Parent : null;
            if (clubRegion?.ContentType.Alias != "regionalPage") clubRegion = null;
            var fedRegion = string.IsNullOrWhiteSpace(regionalFederation) ? null : RegionByCode(uc, regionalFederation);

            if (allowNational)
            {
                var scope = CompetitionScopeHelper.NormalizeScopeValue(rawScope);
                if (string.Equals(scope, CompetitionScopeHelper.SvensktMasterskap, StringComparison.Ordinal))
                    return PageAddressOwner.ForNational("sm");
                if (string.Equals(scope, CompetitionScopeHelper.Landsdelsmasterskap, StringComparison.Ordinal))
                {
                    var area = (clubRegion ?? fedRegion)?.Value<string>("area");
                    var xsm = area switch { "Syd" => "ssm", "Vast" => "vsm", "Ost" => "osm", "Nord" => "nsm", _ => null };
                    if (xsm != null) return PageAddressOwner.ForNational(xsm);
                }
            }

            // En satt klubb som inte går att lösa upp ger INGEN adress — inte kretsens (samma regel som förut:
            // kretsformen gäller bara tävlingar utan klubb).
            if (clubId > 0)
                return clubRegion != null ? new PageAddressOwner(PageAddressOwner.Club, clubNode!.Id) : null;
            return fedRegion != null ? new PageAddressOwner(PageAddressOwner.Region, fedRegion.Id) : null;
        }

        private static IPublishedContent? RegionByCode(IUmbracoContext uc, string code)
            => uc.Content!.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage"
                && string.Equals(c.Value<string>("regionCode") ?? "", code, StringComparison.OrdinalIgnoreCase));

        private int? SeriesYear(int? seriesId)
        {
            if (seriesId is not > 0) return null;
            var s = _series.GetSaved(seriesId.Value);
            if (s == null) return null;
            return int.TryParse(s.Name, out var y) && y > 1900 ? y : YearOfNode(s.ParentId);
        }

        private int? YearOfNode(int nodeId)
        {
            if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return null;
            var n = uc.Content.GetById(nodeId);
            return n?.ContentType.Alias == "competitionSeason" && int.TryParse(n.Name, out var y) && y > 1900 ? y : null;
        }
    }
}
