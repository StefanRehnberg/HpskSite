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

                // Ett barnsegment utan egen nod (listor och resultat i lagringen): tävlingens nya adress + segmentet.
                if (seg.Length >= 2)
                {
                    // ⚠️ Bara segment som FINNS under tävlingen — resultatet eller en publicerad lista. Ett omdöpt
                    // barn ("slutresultat" → "resultat") har en rad i Umbracos omdirigeringar, och den ska få svara.
                    var parent = Route(uc, seg[..^1]);
                    if (parent?.ContentType.Alias == "competition" && IsServedChild(parent.Id, seg[^1])) // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                        return Task.FromResult(RedirectIfMoved(request, parent.Url(), seg[^1] + "/", path));
                }
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
            }
            return Task.FromResult(false);
        }

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
