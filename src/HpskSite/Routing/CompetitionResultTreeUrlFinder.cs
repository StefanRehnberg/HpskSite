using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// <c>{tävlingens trädadress}/resultat/</c> när resultatnoden inte längre finns (skalplanen C1), och
    /// <c>/startlista/</c> och <c>/finalstartlista-x/</c> när startlistan saknar egen nod under tävlingen (C2).
    ///
    /// <para>De flesta tävlingar har en egen adressform som <see cref="CompetitionUrlContentFinder"/> löser.
    /// Den här tar resten — en tävling vars adress är dess plats i trädet — och körs SIST, efter Umbracos
    /// vanliga uppslag: finns noden matchar den där som förut.</para>
    /// </summary>
    public class CompetitionResultTreeUrlFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly IShortStringHelper _shortStrings;

        public CompetitionResultTreeUrlFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, IShortStringHelper shortStrings)
        {
            _ctx = ctx;
            _http = http;
            _shortStrings = shortStrings;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2) return Task.FromResult(false);
                var last = seg[^1];
                var isResult = string.Equals(last, "resultat", StringComparison.OrdinalIgnoreCase);
                // Vilket segment som helst prövas som en lista: den gamla hubbens listor
                // ("startlista-2025-11-08-0855"), springskyttets namngivna och "(1)"-listor fick annars 404
                // (genomgången 2026-10-13, C). Finnern körs SIST — det här är bara adresser som annars hade
                // varit 404 — och TryServeStartListPage svarar bara för en publicerad lista ur lagringen.
                if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
                var competition = uc.Content.GetByRoute("/" + string.Join('/', seg[..^1]) + "/");
#pragma warning restore CS0618
                if (competition?.ContentType.Alias != "competition") return Task.FromResult(false);
                return Task.FromResult(isResult
                    ? CompetitionUrlContentFinder.TryServeResultPage(request, competition, _http, _shortStrings)
                    : CompetitionUrlContentFinder.TryServeStartListPage(request, competition, last, _http, _shortStrings));
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
                return Task.FromResult(false);
            }
        }
    }
}
