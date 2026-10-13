using HpskSite.Models;
using HpskSite.Services.Events;
using HpskSite.Services.SiteSettings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Händelsernas adress när de ligger i tabellen (sajtstrukturen fas 2):
    /// <c>/{klubb eller krets}/{år}/{slug}/</c>.
    ///
    /// <list type="bullet">
    /// <item><b>Aktuell adress</b> → ägarnoden visas med vyn <c>ClubSimpleEvent</c>, och händelsen läggs i
    /// <c>HttpContext.Items</c> (<see cref="ClubEventRoute.ItemKey"/>). Mallen finns bara i minnet — vyn
    /// hittas på sitt namn, ingen Umbraco-mall behöver läggas upp (provat med spiken 2026-10-09).</item>
    /// <item><b>En äldre adress</b> (namn- eller datumbyte, eller den gamla trädadressen
    /// <c>/{ägare}/{slug}/</c> från tiden som nod) → 301 till den aktuella.</item>
    /// </list>
    ///
    /// <para><b>Gör ingenting när nodlagringen gäller</b> — då är händelsen en nod med sin vanliga
    /// trädadress. Billig för alla andra adresser: formen prövas först, ägaren slås upp i cachen, och
    /// databasen frågas bara när adressen ligger direkt under en klubb eller krets.</para>
    /// </summary>
    public class ClubEventUrlContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly IShortStringHelper _shortStrings;
        private readonly SiteSettingService _settings;

        public ClubEventUrlContentFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http,
            IShortStringHelper shortStrings, SiteSettingService settings)
        {
            _ctx = ctx;
            _http = http;
            _shortStrings = shortStrings;
            _settings = settings;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                if (!ClubEventStoreSettings.UsesTable(_settings)) return Task.FromResult(false);
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
                var http = _http.HttpContext;
                if (http == null) return Task.FromResult(false);
                var store = http.RequestServices.GetRequiredService<SqlClubEventStore>();

                // /{ägare}/{år}/{slug}/
                if (seg.Length >= 3 && seg[^2].Length == 4 && int.TryParse(seg[^2], out var year) && year is > 1990 and < 2200)
                {
                    var owner = Owner(uc, seg[..^2]);
                    if (owner != null && Resolve(request, http, store, owner, year, seg[^1])) return Task.FromResult(true);
                }

                // /{ägare}/{slug}/ — den gamla trädadressen.
                var legacyOwner = Owner(uc, seg[..^1]);
                if (legacyOwner != null && Resolve(request, http, store, legacyOwner, null, seg[^1])) return Task.FromResult(true);
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår — Umbraco går vidare till nästa finder.
            }
            return Task.FromResult(false);
        }

        private static Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent? Owner(IUmbracoContext uc, string[] ownerSegments)
        {
            if (ownerSegments.Length == 0) return null;
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
            var node = uc.Content!.GetByRoute("/" + string.Join('/', ownerSegments) + "/");
#pragma warning restore CS0618
            return node?.ContentType.Alias is ClubEvents.OwnerClubAlias or ClubEvents.OwnerRegionAlias ? node : null;
        }

        private bool Resolve(IPublishedRequestBuilder request, HttpContext http, SqlClubEventStore store,
            Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent owner, int? year, string slug)
        {
            var ownerType = owner.ContentType.Alias == ClubEvents.OwnerRegionAlias
                ? HpskSite.Models.Events.ClubEventRecord.OwnerRegion
                : HpskSite.Models.Events.ClubEventRecord.OwnerClub;
            var hit = store.FindByAddress(ownerType, owner.Id, year, slug);
            if (hit == null) return false;
            var ev = store.Get(hit.Value.EventId);
            if (ev == null || !ev.IsPublished) return false;

            if (!hit.Value.IsCurrent || year == null)
            {
                if (string.IsNullOrEmpty(ev.Url)) return false;
                request.SetRedirectPermanent(ev.Url);
                return true;
            }

            http.Items[ClubEventRoute.ItemKey] = ev;
            request.SetPublishedContent(owner);
            request.SetTemplate(new Template(_shortStrings, "ClubSimpleEvent", "ClubSimpleEvent"));
            return true;
        }
    }

    public class ClubEventUrlComposer : IComposer
    {
        // Först i kedjan: en händelseadress får inte fångas av en äldre omdirigeringsrad.
        public void Compose(IUmbracoBuilder builder) => builder.ContentFinders().Insert<ClubEventUrlContentFinder>();
    }
}
