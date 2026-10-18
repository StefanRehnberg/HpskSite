using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Services.Results;
using HpskSite.Services.StartLists;
using HpskSite.Models.StartLists;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Resolves URLs produced by <see cref="CompetitionUrlProvider"/> back to the
    /// underlying competition (or one of its child nodes). Six URL shapes are accepted:
    ///   /competitions/{year}/{xSM}/{comp}/                          (SM/Landsdel)
    ///   /competitions/{year}/{xSM}/{series}/{comp}/                 (SM/Landsdel in series)
    ///   /competitions/{year}/{region}/{comp}/                       (region-hosted)
    ///   /competitions/{year}/{region}/{series}/{comp}/              (region-hosted in series)
    ///   /competitions/{year}/{region}/{club}/{comp}/                (club-hosted)
    ///   /competitions/{year}/{region}/{club}/{series}/{comp}/       (club-hosted in series)
    /// plus an optional trailing /startlista/ , /resultat/ , or /finalstartlista[-vapengrupp]/
    /// for the matching child node.
    ///
    /// Returns false (Umbraco falls through to the next finder) for anything that
    /// doesn't parse cleanly.
    /// </summary>
    public class CompetitionUrlContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<CompetitionUrlContentFinder> _logger;
        private readonly AppCaches _caches;
        private readonly IHttpContextAccessor _http;
        private readonly IShortStringHelper _shortStrings;

        private static readonly HashSet<string> XSmSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "sm", "ssm", "vsm", "osm", "nsm"
        };

        // Trailing segments that map to a child node of the competition.
        // Values are the URL-segment we expect on the child (== node name slug).
        private static readonly HashSet<string> ChildSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "startlista", "resultat", "finalstartlista"
        };

        /// <summary>
        /// Är sista segmentet ett barnsegment?
        ///
        /// ⚠️ En FAST lista räcker inte för finalstartlistorna. Sedan 2026-09-08 finns EN
        /// per vapengrupp, och noderna heter därför "Finalstartlista A" → segmentet
        /// "finalstartlista-a", som ingen fast lista känner. Följden var **404** på varje
        /// finalstartlista utom den första — både arrangörens Öppna-knapp (som redirigerar till
        /// nodens publika URL) och skyttarnas länk på tävlingssidan.
        ///
        /// Att peela ett segment som INTE är ett barn är ofarligt: upplösningen av resten
        /// misslyckas då, finnern returnerar false, och Umbraco går vidare till nästa finder med
        /// hela sökvägen. Felet degraderar alltså till "inte vår URL", inte till en 404.
        /// </summary>
        private static bool IsChildSlug(string segment) =>
            ChildSlugs.Contains(segment)
            || HpskSite.CompetitionTypes.Precision.Models.FinalsWeaponGroup.IsFinalsSlug(segment);

        private readonly HpskSite.Services.Competitions.CompetitionCacheVersion _cacheVersion;

        public CompetitionUrlContentFinder(IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<CompetitionUrlContentFinder> logger, AppCaches caches,
            IHttpContextAccessor http,
            IShortStringHelper shortStrings,
            HpskSite.Services.Competitions.CompetitionCacheVersion cacheVersion)
        {
            _cacheVersion = cacheVersion;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
            _caches = caches;
            _http = http;
            _shortStrings = shortStrings;
        }

        /// <summary>
        /// <c>/resultat/</c> utan resultatnod (skalplanen C1): när listan ligger i tabellen och noden är
        /// avpublicerad visas TÄVLINGEN med vyn <c>CompetitionResult</c>, som läser listan ur
        /// <see cref="ICompetitionResultStore"/>. Mallen finns bara i minnet, som händelsernas adress.
        /// Ingen lista = inte vår adress (404 som förut).
        /// </summary>
        public static bool TryServeResultPage(IPublishedRequestBuilder request, IPublishedContent competition,
            IHttpContextAccessor http, IShortStringHelper shortStrings)
        {
            var ctx = http.HttpContext;
            if (ctx == null) return false;
            var store = ctx.RequestServices.GetRequiredService<ICompetitionResultStore>();
            if (store.GetMany(new[] { competition.Id }).Count == 0) return false;
            request.SetPublishedContent(competition);
            request.SetTemplate(new Template(shortStrings, "CompetitionResult", "CompetitionResult"));
            return true;
        }

        /// <summary>
        /// <c>/startlista/</c> och <c>/finalstartlista-x/</c> utan egen nod under tävlingen (skalplanen C2): listorna
        /// i tabellen, och i nodlagringen de äldre listorna under <c>competitionStartListsHub</c>. TÄVLINGEN visas
        /// med vyn <c>PrecisionStartList</c> resp. <c>PrecisionFinalsStartList</c>, som läser listan ur
        /// <c>CompetitionPageContext.StartList</c>. Bara publika listor; ingen = inte vår adress.
        /// </summary>
        public static bool TryServeStartListPage(IPublishedRequestBuilder request, IPublishedContent competition,
            string segment, IHttpContextAccessor http, IShortStringHelper shortStrings)
        {
            var ctx = http.HttpContext;
            if (ctx == null) return false;
            var store = ctx.RequestServices.GetRequiredService<IStartListStore>();
            var list = store.ListPublished(competition.Id)
                .FirstOrDefault(r => string.Equals(r.UrlSegment, segment, StringComparison.OrdinalIgnoreCase));
            if (list == null) return false;
            ctx.RequestServices.GetRequiredService<HpskSite.Services.Addresses.CompetitionPageContext>().StartList = list;
            request.SetPublishedContent(competition);
            var view = list.Kind == StartListKind.Finals ? "PrecisionFinalsStartList" : "PrecisionStartList"; // startliststore-ok: mallnamn, inte doctyp
            request.SetTemplate(new Template(shortStrings, view, view));
            return true;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var path = request.Uri.GetAbsolutePathDecoded();
                if (!path.StartsWith("/competitions/", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);

                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // Need at minimum: "competitions" + year + 2 more (e.g. xSM/comp or region/comp)
                if (segments.Length < 4) return Task.FromResult(false);
                if (!segments[0].Equals("competitions", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);
                if (!int.TryParse(segments[1], out var year))
                    return Task.FromResult(false);

                // Peel off optional trailing child segment.
                string? childSegment = null;
                if (IsChildSlug(segments[^1]))
                {
                    childSegment = segments[^1];
                    segments = segments[..^1];
                }

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return Task.FromResult(false);

                // After year, what remains determines which shape this is.
                var competition = FindCompetition(year, segments.AsSpan(2).ToArray());

                // En lista vars segment inte är ett av de kända ("startlista", "finalstartlista-x") —
                // de gamla hubbens listor ("startlista-grovskottet-2025-09-26-1133") och en lista som fått
                // " (1)". Leder hela adressen inte till en tävling prövas den utan sista segmentet, och det
                // segmentet slås upp som en lista i lagringen (genomgången 2026-10-13, C). Ingen lista = inte
                // vår adress, som förut.
                // (Tävlingar på trädadressen — utan klubb — tas av CompetitionResultTreeUrlFinder, som körs sist.)
                if (competition == null && childSegment == null && segments.Length >= 5)
                {
                    var listCompetition = FindCompetition(year, segments.AsSpan(2, segments.Length - 3).ToArray());
                    // Bara en PUBLICERAD lista ur lagringen visas — aldrig en godtycklig barnnod.
                    // ⚠️ Bara en lista som FINNS — ett omdöpt segment har en rad i Umbracos omdirigeringar.
                    if (listCompetition != null && HasPublishedList(listCompetition.Id, segments[^1]))
                        return Task.FromResult(RedirectToNewAddress(request, listCompetition, segments[^1])
                            || TryServeStartListPage(request, listCompetition, segments[^1], _http, _shortStrings));
                }

                if (competition == null) return Task.FromResult(false);

                // C3-planen S6: den gamla adressformen ger 301 till tävlingens adress i PageAddress.
                if (RedirectToNewAddress(request, competition, childSegment)) return Task.FromResult(true);

                IPublishedContent target = competition;
                if (childSegment != null)
                {
                    var child = competition.Children.FirstOrDefault(c =>
                        string.Equals(c.UrlSegment, childSegment, StringComparison.OrdinalIgnoreCase));
                    if (child == null)
                    {
                        if (string.Equals(childSegment, "resultat", StringComparison.OrdinalIgnoreCase))
                            return Task.FromResult(TryServeResultPage(request, competition, _http, _shortStrings));
                        return Task.FromResult(TryServeStartListPage(request, competition, childSegment, _http, _shortStrings));
                    }
                    target = child;
                }

                request.SetPublishedContent(target);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CompetitionUrlContentFinder failed for {Path}", request.Uri.AbsolutePath);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// 301 till tävlingens nya adress (+ barnsegmentet) när den har en. Saknar den en (ingen värd) är
        /// adressen fortfarande den gamla, och sidan visas här som förut.
        /// </summary>
        private bool HasPublishedList(int competitionId, string segment)
            => _http.HttpContext?.RequestServices.GetService<IStartListStore>()?.ListPublished(competitionId)
                .Any(r => string.Equals(r.UrlSegment, segment, StringComparison.OrdinalIgnoreCase)) == true;

        private static bool RedirectToNewAddress(IPublishedRequestBuilder request, IPublishedContent competition, string? childSegment)
        {
            var url = competition.Url();
            if (string.IsNullOrEmpty(url) || url == "#" || url.StartsWith("/competitions/", StringComparison.OrdinalIgnoreCase))
                return false;
            var target = (url.EndsWith('/') ? url : url + "/") + (childSegment == null ? "" : childSegment + "/");
            request.SetRedirectPermanent(target + request.Uri.Query);
            return true;
        }

        /// <summary>Tävlingen bakom segmenten efter årtalet, eller null. Formerna, se klassens kommentar.</summary>
        private IPublishedContent? FindCompetition(int year, string[] rest)
        {
            // Length 2: xSM/comp OR region/comp (region-hosted, no series, no club).
            if (rest.Length == 2)
                return XSmSlugs.Contains(rest[0])
                    ? FindBySmShape(year, rest[0], series: null, compSlug: rest[1])
                    : FindByRegionShape(year, regionSlug: rest[0], series: null, compSlug: rest[1]);
            // Length 3: xSM/series/comp OR region/club/comp OR region/series/comp.
            if (rest.Length == 3)
                return XSmSlugs.Contains(rest[0])
                    ? FindBySmShape(year, rest[0], series: rest[1], compSlug: rest[2])
                    // Try club-hosted first; if no match, fall back to region-hosted-in-series.
                    : FindByRegionClubShape(year, rest[0], rest[1], series: null, compSlug: rest[2])
                      ?? FindByRegionShape(year, rest[0], series: rest[1], compSlug: rest[2]);
            // Length 4: region/club/series/comp.
            if (rest.Length == 4)
                return FindByRegionClubShape(year, rest[0], rest[1], series: rest[2], compSlug: rest[3]);
            return null;
        }

        private IPublishedContent? FindBySmShape(int year, string xSm, string? series, string compSlug)
        {
            // xSm in {sm, ssm, vsm, osm, nsm}. Reverse-map to scope + (optional) area.
            string? requiredArea = xSm.ToLowerInvariant() switch
            {
                "ssm" => "Syd",
                "vsm" => "Vast",
                "osm" => "Ost",
                "nsm" => "Nord",
                _ => null   // "sm" → no area constraint
            };
            var requiredScope = xSm.Equals("sm", StringComparison.OrdinalIgnoreCase)
                ? CompetitionScopeHelper.SvensktMasterskap
                : CompetitionScopeHelper.Landsdelsmasterskap;

            foreach (var comp in EnumerateCompetitionsByYear(year, compSlug))
            {
                if (!string.Equals(comp.UrlSegment, compSlug, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchSeries(comp, series)) continue;

                var scope = ReadScopeValue(comp);
                if (!string.Equals(scope, requiredScope, StringComparison.Ordinal)) continue;

                if (requiredArea != null)
                {
                    var area = ResolveArea(comp);
                    if (!string.Equals(area, requiredArea, StringComparison.Ordinal)) continue;
                }
                return comp;
            }
            return null;
        }

        /// <summary>
        /// Region-hosted shape: no club, regionalFederation property points at the region
        /// whose UrlSegment matches the URL's region segment.
        /// </summary>
        private IPublishedContent? FindByRegionShape(int year, string regionSlug, string? series, string compSlug)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return null;
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return null;

            // Find the regional page whose UrlSegment matches the URL's region segment.
            var region = root.Children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                string.Equals(c.UrlSegment, regionSlug, StringComparison.OrdinalIgnoreCase));
            if (region == null) return null;
            var regionCode = region.Value<string>("regionCode") ?? "";

            foreach (var comp in EnumerateCompetitionsByYear(year, compSlug))
            {
                if (!string.Equals(comp.UrlSegment, compSlug, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchSeries(comp, series)) continue;

                // Region-hosted: clubId must be empty and regionalFederation must match.
                if (comp.Value<int>("clubId") > 0) continue;
                var compRegion = comp.Value<string>("regionalFederation") ?? "";
                if (!string.Equals(compRegion, regionCode, StringComparison.OrdinalIgnoreCase)) continue;

                return comp;
            }
            return null;
        }

        /// <summary>True if the competition's series-parent matches the URL's series segment
        /// (or both are null/absent).</summary>
        private static bool MatchSeries(IPublishedContent comp, string? urlSeriesSlug)
        {
            var parent = comp.Parent;
            var actual = (parent?.ContentType.Alias == "competitionSeries") ? parent.UrlSegment : null;
            if (urlSeriesSlug == null) return actual == null;
            return string.Equals(actual, urlSeriesSlug, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Defensive read of competitionScope — see CompetitionUrlProvider.ReadScopeValue
        /// for the rationale (FlexibleDropdown converter chokes on raw-string values).</summary>
        private static string? ReadScopeValue(IPublishedContent competition)
        {
            try
            {
                var raw = competition.Value("competitionScope");
                return raw switch
                {
                    string s => string.IsNullOrWhiteSpace(s) ? null : s,
                    string[] arr => arr.FirstOrDefault(),
                    IEnumerable<string> e => e.FirstOrDefault(),
                    _ => null
                };
            }
            catch { return null; }
        }

        private IPublishedContent? FindByRegionClubShape(int year, string regionSlug, string clubSlug,
            string? series, string compSlug)
        {
            foreach (var comp in EnumerateCompetitionsByYear(year, compSlug))
            {
                if (!string.Equals(comp.UrlSegment, compSlug, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchSeries(comp, series)) continue;

                var clubId = comp.Value<int>("clubId");
                if (clubId <= 0) continue;
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) continue;
                var club = ctx.Content.GetById(clubId);
                if (club == null || club.ContentType.Alias != "club") continue;
                if (!string.Equals(club.UrlSegment, clubSlug, StringComparison.OrdinalIgnoreCase)) continue;

                var region = club.Parent?.Parent;
                if (region?.ContentType.Alias != "regionalPage") continue;
                if (!string.Equals(region.UrlSegment, regionSlug, StringComparison.OrdinalIgnoreCase)) continue;

                return comp;
            }
            return null;
        }

        /// <summary>
        /// Tävlingarna med det året och den sluggen. Förut gicks ALLA tävlingar igenom upp till tre gånger
        /// per förfrågan; nu slås de upp i ett index (år, slug) → id som byggs en gång och cachas
        /// (C3-planen Q2, 2026-10-13). Töms med <see cref="HpskSite.Services.CompetitionNavigationCaches"/>
        /// när en tävling eller serie skrivs, och lever högst fem minuter.
        /// </summary>
        private IEnumerable<IPublishedContent> EnumerateCompetitionsByYear(int year, string compSlug)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                yield break;
            _cacheVersion.EnsureFresh(); // en annan instans kan ha skrivit (C3-planen S5d)
            var index = _caches.RuntimeCache.GetCacheItem(HpskSite.Services.CompetitionNavigationCaches.UrlIndexKey,
                            BuildYearSlugIndex, HpskSite.Services.CompetitionNavigationCaches.CacheFor)
                        ?? new Dictionary<string, List<int>>();
            if (!index.TryGetValue(IndexKey(year, compSlug), out var ids)) yield break;
            foreach (var id in ids)
            {
                var node = ctx.Content.GetById(id);
                if (node?.ContentType.Alias == "competition") yield return node;
            }
        }

        private static string IndexKey(int year, string slug) => year + "|" + (slug ?? "").ToLowerInvariant();

        private Dictionary<string, List<int>> BuildYearSlugIndex()
        {
            var index = new Dictionary<string, List<int>>();
            foreach (var (id, year, slug) in AllCompetitionsByYear())
            {
                var key = IndexKey(year, slug);
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>();
                list.Add(id);
            }
            return index;
        }

        private IEnumerable<(int Id, int Year, string Slug)> AllCompetitionsByYear()
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                yield break;

            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) yield break;

            // competitionsHub is a direct child of root. Competitions live under it,
            // either directly or nested under a competitionSeries.
            var hub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            if (hub == null) yield break;

            foreach (var node in hub.DescendantsOfType("competition"))
            {
                // Year from competitionDate if set, else from the parent series's name (e.g. "2026").
                // Mirrors CompetitionUrlProvider.ResolveYear so the provider and finder agree.
                var date = node.Value<DateTime>("competitionDate");
                int nodeYear = (date != default && date.Year > 1900) ? date.Year : 0;
                if (nodeYear == 0)
                {
                    var p = node.Parent;
                    if (p?.ContentType.Alias == "competitionSeries")
                    {
                        if (int.TryParse(p.Name, out var y) && y > 1900) nodeYear = y;
                        else if (int.TryParse(p.UrlSegment, out var y2) && y2 > 1900) nodeYear = y2;
                    }
                }
                if (nodeYear > 0)
                    yield return (node.Id, nodeYear, node.UrlSegment ?? "");
            }
        }

        /// <summary>Try the host club's regional area first; fall back to the regionalFederation
        /// lookup. Mirrors CompetitionUrlProvider.ResolveAreaForCompetition.</summary>
        private string? ResolveArea(IPublishedContent competition)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;

            var clubId = competition.Value<int>("clubId");
            if (clubId > 0)
            {
                var club = ctx.Content.GetById(clubId);
                var region = club?.Parent?.Parent;
                if (region?.ContentType.Alias == "regionalPage")
                {
                    var area = region.Value<string>("area");
                    if (!string.IsNullOrWhiteSpace(area)) return area;
                }
            }

            var regionalFederation = competition.Value<string>("regionalFederation");
            if (!string.IsNullOrWhiteSpace(regionalFederation))
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                var region = root?.Children.FirstOrDefault(c =>
                    c.ContentType.Alias == "regionalPage" &&
                    string.Equals(c.Value<string>("regionCode") ?? "", regionalFederation, StringComparison.OrdinalIgnoreCase));
                var area = region?.Value<string>("area");
                if (!string.IsNullOrWhiteSpace(area)) return area;
            }

            return null;
        }
    }
}
