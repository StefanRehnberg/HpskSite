using HpskSite.CompetitionTypes.Common.Utilities;
using Microsoft.Extensions.Logging;
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
    /// plus an optional trailing /startlista/ , /resultat/ , or /finalstartlista/ for
    /// the matching child node.
    ///
    /// Returns false (Umbraco falls through to the next finder) for anything that
    /// doesn't parse cleanly.
    /// </summary>
    public class CompetitionUrlContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<CompetitionUrlContentFinder> _logger;

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

        public CompetitionUrlContentFinder(IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<CompetitionUrlContentFinder> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
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
                if (ChildSlugs.Contains(segments[^1]))
                {
                    childSegment = segments[^1];
                    segments = segments[..^1];
                }

                // After year, what remains determines which shape this is.
                var rest = segments.AsSpan(2).ToArray();

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return Task.FromResult(false);

                IPublishedContent? competition = null;

                // Length 2: xSM/comp OR region/comp (region-hosted, no series, no club).
                if (rest.Length == 2)
                {
                    if (XSmSlugs.Contains(rest[0]))
                        competition = FindBySmShape(year, rest[0], series: null, compSlug: rest[1]);
                    else
                        competition = FindByRegionShape(year, regionSlug: rest[0], series: null, compSlug: rest[1]);
                }
                // Length 3: xSM/series/comp OR region/club/comp OR region/series/comp.
                else if (rest.Length == 3)
                {
                    if (XSmSlugs.Contains(rest[0]))
                    {
                        competition = FindBySmShape(year, rest[0], series: rest[1], compSlug: rest[2]);
                    }
                    else
                    {
                        // Try club-hosted first; if no match, fall back to region-hosted-in-series.
                        competition = FindByRegionClubShape(year, rest[0], rest[1], series: null, compSlug: rest[2])
                                      ?? FindByRegionShape(year, rest[0], series: rest[1], compSlug: rest[2]);
                    }
                }
                // Length 4: region/club/series/comp.
                else if (rest.Length == 4)
                {
                    competition = FindByRegionClubShape(year, rest[0], rest[1], series: rest[2], compSlug: rest[3]);
                }

                if (competition == null) return Task.FromResult(false);

                IPublishedContent target = competition;
                if (childSegment != null)
                {
                    var child = competition.Children.FirstOrDefault(c =>
                        string.Equals(c.UrlSegment, childSegment, StringComparison.OrdinalIgnoreCase));
                    if (child == null) return Task.FromResult(false);
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

            foreach (var comp in EnumerateCompetitionsByYear(year))
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

            foreach (var comp in EnumerateCompetitionsByYear(year))
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
            foreach (var comp in EnumerateCompetitionsByYear(year))
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

        private IEnumerable<IPublishedContent> EnumerateCompetitionsByYear(int year)
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
                if (nodeYear == year)
                    yield return node;
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
