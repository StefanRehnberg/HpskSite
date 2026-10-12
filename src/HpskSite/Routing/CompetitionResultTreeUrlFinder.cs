using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// <c>{tävlingens trädadress}/resultat/</c> när resultatnoden inte längre finns (skalplanen C1).
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
                if (seg.Length < 2 || !string.Equals(seg[^1], "resultat", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);
                if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
                var competition = uc.Content.GetByRoute("/" + string.Join('/', seg[..^1]) + "/");
#pragma warning restore CS0618
                if (competition?.ContentType.Alias != "competition") return Task.FromResult(false);
                return Task.FromResult(CompetitionUrlContentFinder.TryServeResultPage(request, competition, _http, _shortStrings));
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
                return Task.FromResult(false);
            }
        }
    }
}
