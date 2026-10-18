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

        public ClubEventLegacyUrlFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, SiteSettingService settings,
            PageAddressService addresses)
        {
            _ctx = ctx;
            _http = http;
            _settings = settings;
            _addresses = addresses;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                if (!ClubEventStoreSettings.UsesTable(_settings)) return Task.FromResult(false);
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
                var ownerNode = uc.Content.GetByRoute("/" + string.Join('/', seg[..^1]) + "/");
#pragma warning restore CS0618
                var owner = PageAddressUrls.OwnerOf(ownerNode);
                if (owner == null) return Task.FromResult(false);
                var row = _addresses.Find(owner.Value, null, seg[^1]);
                if (row?.TargetKind != PageAddressKinds.Event) return Task.FromResult(false);
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
            builder.Services.AddScoped<CompetitionAddressService>();
            builder.Services.AddScoped<CompetitionPageContext>();
            builder.Services.AddHostedService<PageAddressSeedHostedService>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentPublishedNotification, PageAddressPublishedHandler>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentSavingNotification, PageAddressReservedNames>();
            // Först i kedjan: en gammal händelseadress får inte fångas av en äldre omdirigeringsrad.
            builder.ContentFinders().Insert<ClubEventLegacyUrlFinder>();
            // Före Umbracos trädupplag: gamla /competitions/-adresser ger 301 till de nya.
            builder.ContentFinders().Insert<CompetitionTreeRedirectFinder>(1);
        }
    }
}
