using HpskSite.Controllers;
using HpskSite.Services.Addresses;
using HpskSite.Services.Events;
using HpskSite.Services.SiteSettings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Händelsernas gamla trädadress <c>/{ägare}/{slug}/</c> (från tiden som nod) → 301 till den aktuella.
    /// Den formen saknar årtal och kan därför inte vara en route i <see cref="CompetitionPagesController"/>
    /// (den hade fångat varje sida under en krets). Gör ingenting när nodlagringen gäller.
    /// </summary>
    public class ClubEventLegacyUrlFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly SiteSettingService _settings;
        private readonly PageAddressService _addresses;
        private readonly SiteRouteLookup _routes;

        public ClubEventLegacyUrlFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, SiteSettingService settings,
            PageAddressService addresses, SiteRouteLookup routes)
        {
            _ctx = ctx;
            _http = http;
            _settings = settings;
            _addresses = addresses;
            _routes = routes;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                if (!ClubEventStoreSettings.UsesTable(_settings)) return Task.FromResult(false);
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
                // Ägaren i trädets form eller klubbens korta (/halland/haaplinge-goass/gasaskjutning/).
                var owner = PageAddressUrls.OwnerOf(_routes.Owner(uc.Content, seg[..^1]));
                if (owner == null) return Task.FromResult(false);
                var row = _addresses.Find(owner.Value, PageAddressKinds.Event, null, seg[^1]);
                if (row == null) return Task.FromResult(false);
                var ev = _http.HttpContext?.RequestServices.GetRequiredService<SqlClubEventStore>().Get(row.TargetId);
                if (ev is not { IsPublished: true } || string.IsNullOrEmpty(ev.Url)) return Task.FromResult(false);
                request.SetRedirectPermanent(ev.Url + request.Uri.Query);
                return Task.FromResult(true);
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
                return Task.FromResult(false);
            }
        }
    }

    public class PageAddressComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<PageAddressService>();
            builder.Services.AddSingleton<PageAddressUrls>();
            builder.Services.AddSingleton<SiteRouteLookup>();
            builder.Services.AddScoped<CompetitionAddressService>();
            builder.Services.AddScoped<CompetitionPageContext>();
            builder.Services.AddHostedService<PageAddressSeedHostedService>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentPublishedNotification, PageAddressPublishedHandler>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentSavingNotification, PageAddressReservedNames>();
            // Klubbens korta adress (/halland/haaplinge-goass/): utåt och inåt, och 301 från den långa formen.
            builder.UrlProviders().Insert<ClubShortUrlProvider>();
            // Först i kedjan: en gammal händelseadress får inte fångas av en äldre omdirigeringsrad — och inte av
            // klubbens korta form, som annars visar händelsens kvarliggande nod (tabelläget, före avpubliceringen).
            // Den läser ägaren i båda klubbformerna, så den långa formen tar ett steg direkt till den nya adressen.
            builder.ContentFinders().Insert<ClubEventLegacyUrlFinder>();
            // Sedan klubbens långa form → 301, innan Umbracos trädupplag hinner servera den.
            builder.ContentFinders().Insert<ClubShortUrlContentFinder>(1);
            // Före Umbracos trädupplag: gamla /competitions/-adresser ger 301 till de nya.
            builder.ContentFinders().Insert<CompetitionTreeRedirectFinder>(2);
        }
    }
}
