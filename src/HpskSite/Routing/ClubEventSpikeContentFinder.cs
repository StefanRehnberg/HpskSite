using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    // SPIK (sajtstrukturen fas 2) — TAS BORT efter provet. Prövar om en content finder kan visa
    // ÄGARNODEN (klubb/krets) med en mall som bara finns i minnet, och lämna händelsens id i
    // HttpContext.Items. Svarar bara på /{ägare}/{år}/__spik/.
    public class ClubEventSpikeContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly IShortStringHelper _shortStrings;

        public ClubEventSpikeContentFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, IShortStringHelper shortStrings)
        {
            _ctx = ctx; _http = http; _shortStrings = shortStrings;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            var path = request.Uri.GetAbsolutePathDecoded();
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 3 || seg[^1] != "__spik" || !int.TryParse(seg[^2], out var year)) return Task.FromResult(false);
            if (!_ctx.TryGetUmbracoContext(out var uc)) return Task.FromResult(false);
            var ownerRoute = "/" + string.Join('/', seg[..^2]) + "/";
#pragma warning disable CS0618
            var owner = uc.Content?.GetByRoute(ownerRoute);
#pragma warning restore CS0618
            if (owner == null || (owner.ContentType.Alias != "club" && owner.ContentType.Alias != "regionalPage")) return Task.FromResult(false);
            request.SetPublishedContent(owner);
            request.SetTemplate(new Template(_shortStrings, "ClubEventSpike", "ClubEventSpike"));
            if (_http.HttpContext != null) _http.HttpContext.Items["clubEventSpikeYear"] = year;
            return Task.FromResult(true);
        }
    }

    public class ClubEventSpikeComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder) => builder.ContentFinders().Insert<ClubEventSpikeContentFinder>();
    }
}
