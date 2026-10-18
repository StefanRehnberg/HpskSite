using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// C3-planen S6: tävlingarnas gamla trädadresser (<c>/competitions/…</c>) ger 301 till de nya — navet till
    /// <c>/tavlingar/</c>, en säsong till <c>/tavlingar/{år}/</c>, en serie och en tävling (med barn) till sin
    /// adress i <c>PageAddress</c>. Körs före Umbracos trädupplag.
    ///
    /// <para>Gör ingenting när nodens adress ÄR den begärda: en tävling som ännu saknar ny adress (ingen
    /// värd) visas på sin trädadress som förut. Alla andra noder under navet (anmälningar, fakturor) rörs inte.</para>
    /// </summary>
    public class CompetitionTreeRedirectFinder : IContentFinder
    {
        private static readonly HashSet<string> Redirected = new(StringComparer.OrdinalIgnoreCase)
        {
            "competitionsHub", "competitionSeason", "competitionSeries", "competition", // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
            "precisionStartList", "competitionResult", "finalsStartList", // startliststore-ok: resultstore-ok: competitionstore-ok: gamla trädadresser 301:as (C3 S6)
        };

        private readonly IUmbracoContextAccessor _ctx;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;

        public CompetitionTreeRedirectFinder(IUmbracoContextAccessor ctx, Microsoft.AspNetCore.Http.IHttpContextAccessor http)
        {
            _ctx = ctx;
            _http = http;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var path = request.Uri.GetAbsolutePathDecoded();
                var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length == 0 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
                var hub = uc.Content.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (hub == null || !string.Equals(seg[0], hub.UrlSegment, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(false);

                var node = Route(uc, seg);
                if (node != null)
                    return Task.FromResult(Redirected.Contains(node.ContentType.Alias) && RedirectIfMoved(request, node.Url(), "", path));

                // ⚠️ Umbracos GAMLA stavning (Fas E, 2026-10-18): segment som byggdes innan vår teckentabell fanns bar
                // å/ä/ö som aa/ae/oe ("omgaang", "vaexjoetraeffen"). Ombyggnaden av adresstabellen vid 17-deployen
                // skrev om dem till a/a/o, så besökarnas gamla adresser hittade ingen nod. Pröva den översatta
                // formen — bara som reservväg, och bara när den leder till en nod vi omdirigerar.
                var folded = seg.Select(LegacyAsciiFold).ToArray();
                if (!folded.SequenceEqual(seg, StringComparer.OrdinalIgnoreCase))
                {
                    var legacy = Route(uc, folded);
                    if (legacy != null && Redirected.Contains(legacy.ContentType.Alias))
                        return Task.FromResult(RedirectIfMoved(request, legacy.Url(), "", path));
                    if (folded.Length >= 2)
                    {
                        var legacyParent = Route(uc, folded[..^1]);
                        if (legacyParent?.ContentType.Alias == "competition" && IsServedChild(legacyParent.Id, folded[^1])) // competitionstore-ok: samma som ovan, för den gamla stavningen
                            return Task.FromResult(RedirectIfMoved(request, legacyParent.Url(), folded[^1] + "/", path));
                    }
                    if (folded.Length >= 2 && FromStore(folded) is { } movedLegacy)
                        return Task.FromResult(RedirectIfMoved(request, movedLegacy.Url, movedLegacy.Suffix, path));
                }

                // Ett barnsegment utan egen nod (listor och resultat i lagringen): tävlingens nya adress + segmentet.
                if (seg.Length >= 2)
                {
                    // ⚠️ Bara segment som FINNS under tävlingen — resultatet eller en publicerad lista. Ett omdöpt
                    // barn ("slutresultat" → "resultat") har en rad i Umbracos omdirigeringar, och den ska få svara.
                    var parent = Route(uc, seg[..^1]);
                    if (parent?.ContentType.Alias == "competition" && IsServedChild(parent.Id, seg[^1])) // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                        return Task.FromResult(RedirectIfMoved(request, parent.Url(), seg[^1] + "/", path));
                }

                // Tabelläget (C3-planen §5e): noden kan vara avpublicerad — sök adressdelen i lagringen.
                if (seg.Length >= 2 && FromStore(seg) is { } moved)
                    return Task.FromResult(RedirectIfMoved(request, moved.Url, moved.Suffix, path));
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Trädadressen när noden inte finns i den publicerade cachen (tabelläget efter avpubliceringen): sista
        /// segmentet (eller näst sista + ett barn) är tävlingens eller seriens adressdel. Flera träffar avgörs av
        /// ett årtal i adressen; går det inte att avgöra, ingen omdirigering.
        /// </summary>
        private (string Url, string Suffix)? FromStore(string[] seg)
        {
            var services = _http.HttpContext?.RequestServices;
            var settings = services?.GetService(typeof(HpskSite.Services.SiteSettings.SiteSettingService)) as HpskSite.Services.SiteSettings.SiteSettingService;
            if (settings == null || !HpskSite.Services.Competitions.CompetitionStoreSettings.UsesTable(settings)) return null;
            var competitions = services!.GetService(typeof(HpskSite.Services.Competitions.ICompetitionStore)) as HpskSite.Services.Competitions.ICompetitionStore;
            var seriesStore = services.GetService(typeof(HpskSite.Services.Competitions.ICompetitionSeriesStore)) as HpskSite.Services.Competitions.ICompetitionSeriesStore;
            var links = services.GetService(typeof(HpskSite.Services.Competitions.CompetitionLinks)) as HpskSite.Services.Competitions.CompetitionLinks;
            if (competitions == null || seriesStore == null || links == null) return null;
            var years = seg.Select(s => int.TryParse(s, out var y) && y > 1900 && y < 2200 ? y : 0).Where(y => y > 0).ToHashSet();

            int? Pick(IEnumerable<(int Id, int? Year)> found)
            {
                var list = found.ToList();
                if (list.Count == 1) return list[0].Id;
                var byYear = list.Where(x => x.Year.HasValue && years.Contains(x.Year.Value)).ToList();
                return byYear.Count == 1 ? byYear[0].Id : null;
            }

            foreach (var (slug, suffix) in new[] { (seg[^1], ""), (seg.Length >= 3 ? seg[^2] : "", seg[^1] + "/") })
            {
                if (string.IsNullOrEmpty(slug)) continue;
                var compId = Pick(competitions.List()
                    .Where(c => string.Equals(c.UrlSegment, slug, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (c.Id, c.CompetitionDate?.Year)));
                if (compId != null && (suffix == "" || IsServedChild(compId.Value, seg[^1])))
                    return (links.Url(compId.Value), suffix);
                if (suffix != "") continue;
                var seriesId = Pick(seriesStore.List()
                    .Where(s => string.Equals(s.UrlSegment, slug, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (s.Id, s.SeriesStartDate?.Year)));
                if (seriesId != null)
                {
                    var url = links.SeriesUrl(seriesId.Value);
                    if (!string.IsNullOrEmpty(url)) return (url, "");
                }
            }
            return null;
        }

        /// <summary>Umbracos standardstavning av å/ä/ö (aa/ae/oe) översatt till vår teckentabells (a/a/o).</summary>
        public static string LegacyAsciiFold(string segment) =>
            segment.Replace("aa", "a", StringComparison.OrdinalIgnoreCase)
                   .Replace("ae", "a", StringComparison.OrdinalIgnoreCase)
                   .Replace("oe", "o", StringComparison.OrdinalIgnoreCase);

        private bool IsServedChild(int competitionId, string segment)
        {
            if (string.Equals(segment, "resultat", StringComparison.OrdinalIgnoreCase)) return true;
            var store = _http.HttpContext?.RequestServices.GetService(typeof(HpskSite.Services.StartLists.IStartListStore))
                as HpskSite.Services.StartLists.IStartListStore;
            return store != null && store.ListPublished(competitionId)
                .Any(r => string.Equals(r.UrlSegment, segment, StringComparison.OrdinalIgnoreCase));
        }

        private static IPublishedContent? Route(IUmbracoContext uc, string[] segments)
        {
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
            return uc.Content!.GetByRoute("/" + string.Join('/', segments) + "/");
#pragma warning restore CS0618
        }

        private static bool RedirectIfMoved(IPublishedRequestBuilder request, string? url, string suffix, string path)
        {
            if (string.IsNullOrEmpty(url) || url == "#") return false;
            var target = url.EndsWith('/') ? url + suffix : url + "/" + suffix;
            if (target.TrimEnd('/').Equals(path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            request.SetRedirectPermanent(target + request.Uri.Query);
            return true;
        }
    }
}
