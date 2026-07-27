using HpskSite.CompetitionTypes.Common.Utilities;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Custom URL provider that renders competition URLs as one of:
    ///   /competitions/{year}/{sm|ssm|vsm|osm|nsm}/[{series}/]{comp}/
    ///     — Svenskt Mästerskap or Landsdelsmästerskap (scope-driven; area from host
    ///       club's region OR from competitionRegionalFederation when no club is set)
    ///   /competitions/{year}/{region}/{club}/[{series}/]{comp}/
    ///     — club-hosted (region derived from club.Parent.Parent)
    ///   /competitions/{year}/{region}/[{series}/]{comp}/
    ///     — region-hosted (regionalFederation set, no clubId)
    ///
    /// Returns null when none of the above can be formed (no scope, no club, no
    /// regionalFederation) so Umbraco's default URL provider takes over with the
    /// tree-derived URL. Validation in the create/edit flow makes that state rare.
    /// </summary>
    public class CompetitionUrlProvider : IUrlProvider
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<CompetitionUrlProvider> _logger;

        // Recognised competition-child doctypes. Their URL is built as parent-URL + own segment.
        private static readonly HashSet<string> ChildAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            "precisionStartList",
            "competitionResult",
            "finalsStartList"
        };

        public CompetitionUrlProvider(IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<CompetitionUrlProvider> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        public UrlInfo? GetUrl(IPublishedContent content, UrlMode mode, string? culture, Uri current)
        {
            try
            {
                if (content == null) return null;

                var alias = content.ContentType.Alias;
                if (alias == "competition")
                {
                    var url = BuildCompetitionUrl(content);
                    return url == null ? null : UrlInfo.Url(url, culture);
                }

                if (ChildAliases.Contains(alias))
                {
                    var parent = content.Parent;
                    if (parent?.ContentType.Alias != "competition") return null;
                    var parentUrl = BuildCompetitionUrl(parent);
                    if (parentUrl == null) return null;
                    return UrlInfo.Url(parentUrl + content.UrlSegment + "/", culture);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CompetitionUrlProvider failed for content {Id} — falling through to default", content?.Id);
                return null;
            }
        }

        public IEnumerable<UrlInfo> GetOtherUrls(int id, Uri current) => Enumerable.Empty<UrlInfo>();

        /// <summary>
        /// Builds the URL for a competition. Priority chain:
        ///   1. scope = SM           → /competitions/{year}/sm/[seriesSlug/]{compSlug}/
        ///   2. scope = Landsdel + resolvable area → xSM URL
        ///   3. clubId set           → club-hosted URL (region from club's tree position)
        ///   4. regionalFederation set → region-hosted URL (region from regionalFederation lookup)
        ///   5. otherwise null (Umbraco default takes over)
        /// </summary>
        private string? BuildCompetitionUrl(IPublishedContent competition)
        {
            var year = ResolveYear(competition);
            if (year == null) return null;

            // competitionScope can be stored either as a plain string OR as a JSON array
            // (FlexibleDropdownPropertyValueConverter throws when the stored value isn't valid
            // JSON — see Models/Competition.cs:31-62 which uses the same defensive pattern
            // for shootingClassIds).
            var scope = ReadScopeValue(competition);
            var compSlug = competition.UrlSegment;
            if (string.IsNullOrWhiteSpace(compSlug)) return null;

            // Optional series segment (same for every shape below).
            var seriesParent = competition.Parent;
            string? seriesSlug = null;
            if (seriesParent?.ContentType.Alias == "competitionSeries" && !string.IsNullOrWhiteSpace(seriesParent.UrlSegment))
                seriesSlug = seriesParent.UrlSegment;

            // ── 1. SM (Svenskt Mästerskap) ───────────────────────────────────────────
            if (string.Equals(scope, CompetitionScopeHelper.SvensktMasterskap, StringComparison.Ordinal))
                return Compose(year.Value, "sm", clubSlug: null, seriesSlug, compSlug);

            // ── 2. Landsdelsmästerskap (needs area from club's region OR regionalFederation's region) ──
            if (string.Equals(scope, CompetitionScopeHelper.Landsdelsmasterskap, StringComparison.Ordinal))
            {
                var area = ResolveAreaForCompetition(competition);
                var xSm = area switch
                {
                    "Syd" => "ssm",
                    "Vast" => "vsm",
                    "Ost" => "osm",
                    "Nord" => "nsm",
                    _ => null
                };
                if (xSm != null) return Compose(year.Value, xSm, clubSlug: null, seriesSlug, compSlug);
                // No area found → fall through and try club/regionalFederation paths below.
            }

            // ── 3. Club-hosted (region derived from club's tree position) ────────────
            var clubId = competition.Value<int>("clubId");
            if (clubId > 0)
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var club = ctx.Content.GetById(clubId);
                if (club != null && club.ContentType.Alias == "club" && !string.IsNullOrWhiteSpace(club.UrlSegment))
                {
                    var region = club.Parent?.Parent;
                    if (region?.ContentType.Alias == "regionalPage" && !string.IsNullOrWhiteSpace(region.UrlSegment))
                        return Compose(year.Value, region.UrlSegment, club.UrlSegment, seriesSlug, compSlug);
                }

                // A club is set but we can't resolve it to a published `club` under a
                // `regionalPage` (unpublished, deleted, moved out of regionalPage > clubsPage, or
                // a stale id). Bail out to Umbraco's tree URL rather than falling through to the
                // region-hosted shape below: CompetitionUrlContentFinder.FindByRegionShape rejects
                // any competition with clubId > 0, so that URL could never resolve back and the
                // backoffice would report "published but its URL cannot be routed".
                return null;
            }

            // ── 4. Region-hosted via regionalFederation ──────────────────────────────
            var regionalFederation = competition.Value<string>("regionalFederation");
            if (!string.IsNullOrWhiteSpace(regionalFederation))
            {
                var regionByCode = LookupRegionalPageByCode(regionalFederation);
                if (regionByCode != null && !string.IsNullOrWhiteSpace(regionByCode.UrlSegment))
                    return Compose(year.Value, regionByCode.UrlSegment, clubSlug: null, seriesSlug, compSlug);
            }

            return null;
        }

        /// <summary>
        /// Stitches the URL segments together. region and club may each be null
        /// (omitted from the URL). The xSM cases pass region="sm"/"ssm"/etc. and club=null.
        /// </summary>
        private static string Compose(int year, string region, string? clubSlug, string? seriesSlug, string compSlug)
        {
            var club = string.IsNullOrWhiteSpace(clubSlug) ? "" : $"{clubSlug}/";
            var series = string.IsNullOrWhiteSpace(seriesSlug) ? "" : $"{seriesSlug}/";
            return $"/competitions/{year}/{region}/{club}{series}{compSlug}/";
        }

        /// <summary>
        /// Find the regionalPage whose regionCode equals the given value (case-insensitive).
        /// Uses the same content-tree walk as AdminAuthorizationService.GetAreaForRegion.
        /// </summary>
        private IPublishedContent? LookupRegionalPageByCode(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return null;
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return null;
            return root.Children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                string.Equals(c.Value<string>("regionCode") ?? "", regionCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolve the regional `area` value (Syd/Vast/Ost/Nord) for a competition.
        /// Tries the host club's tree chain first (clubId → club → clubsPage → regionalPage);
        /// falls back to the competition's regionalFederation property if no club is set.
        /// </summary>
        private string? ResolveAreaForCompetition(IPublishedContent competition)
        {
            // Path A: host club → its regional page.
            var clubId = competition.Value<int>("clubId");
            if (clubId > 0 && _umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var club = ctx.Content.GetById(clubId);
                var region = club?.Parent?.Parent;
                if (region?.ContentType.Alias == "regionalPage")
                {
                    var area = region.Value<string>("area");
                    if (!string.IsNullOrWhiteSpace(area)) return area;
                }
            }

            // Path B: regionalFederation → regional page → its area.
            var regionalFederation = competition.Value<string>("regionalFederation");
            if (!string.IsNullOrWhiteSpace(regionalFederation))
            {
                var region = LookupRegionalPageByCode(regionalFederation);
                var area = region?.Value<string>("area");
                if (!string.IsNullOrWhiteSpace(area)) return area;
            }

            return null;
        }

        /// <summary>
        /// Read competitionScope robustly. Some nodes store the value as a plain string,
        /// others as a JSON array (the FlexibleDropdown editor's default). Try several
        /// shapes and swallow conversion exceptions — a missing scope just means "no
        /// championship" and the URL provider falls back to the standard region/club shape.
        /// </summary>
        private static string? ReadScopeValue(IPublishedContent competition)
        {
            // Untyped Value() returns the source value without applying the typed converter,
            // so it doesn't throw when the FlexibleDropdown JSON deserializer chokes.
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
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Year is competition.competitionDate.Year if set; otherwise parse the parent
        /// series' name as an integer (the series is typically named after the year, e.g. "2026").
        /// Returns null only if both fail.
        /// </summary>
        private static int? ResolveYear(IPublishedContent competition)
        {
            var date = competition.Value<DateTime>("competitionDate");
            if (date != default && date.Year > 1900) return date.Year;

            var parent = competition.Parent;
            if (parent?.ContentType.Alias == "competitionSeries")
            {
                if (int.TryParse(parent.Name, out var y) && y > 1900) return y;
                if (int.TryParse(parent.UrlSegment, out var y2) && y2 > 1900) return y2;
            }
            return null;
        }

    }
}
