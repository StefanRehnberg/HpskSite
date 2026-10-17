using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Services.Events;
using HpskSite.Services.SiteSettings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// C3-planen S6: adresserna utanför trädet, ur <c>PageAddress</c> — händelser, tävlingar och serier
    /// (<c>/{ägare}/{år}/{slug}/</c>, ägaren en klubb, en krets eller <c>sm|ssm|vsm|osm|nsm</c>), plus
    /// listan <c>/tavlingar/</c> och årssidorna <c>/tavlingar/{år}/</c>.
    ///
    /// <list type="bullet">
    /// <item><b>Aktuell adress</b> → sidan visas. Tävlingens barn som förut: <c>…/resultat/</c>,
    /// <c>…/startlista/</c>, <c>…/finalstartlista-x/</c>.</item>
    /// <item><b>En äldre adress</b> (ny värd, nytt år, nytt namn) → 301 till den aktuella.</item>
    /// <item><b>En gammal ÄGARadress</b> (klubben bytte namn eller krets) → 301 via Umbracos omdirigering
    /// av klubbsidan, så att varje adress under den följer med.</item>
    /// <item><b>Årssidan</b> <c>/halland/2026/</c>, <c>/sm/2026/</c> → 301 till <c>/tavlingar/</c> — folk kortar
    /// adresser, och en sådan ska aldrig ge 404.</item>
    /// </list>
    ///
    /// <para>Först i kedjan: en adress här får inte fångas av en äldre rad i <c>umbracoRedirectUrl</c>.
    /// Billig för andra adresser — formen prövas först, och databasen frågas bara när adressen har ett
    /// årtal under en klubb, krets eller ett mästerskap.</para>
    /// </summary>
    public class PageAddressContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly IShortStringHelper _shortStrings;
        private readonly SiteSettingService _settings;
        private readonly PageAddressService _addresses;
        private readonly PageAddressUrls _urls;
        private readonly IRedirectUrlService _redirects;
        private readonly ILogger<PageAddressContentFinder> _logger;

        public PageAddressContentFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, IShortStringHelper shortStrings,
            SiteSettingService settings, PageAddressService addresses, PageAddressUrls urls, IRedirectUrlService redirects,
            ILogger<PageAddressContentFinder> logger)
        {
            _ctx = ctx;
            _http = http;
            _shortStrings = shortStrings;
            _settings = settings;
            _addresses = addresses;
            _urls = urls;
            _redirects = redirects;
            _logger = logger;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length == 0 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return Task.FromResult(false);
                var query = request.Uri.Query;

                // /tavlingar/ och /tavlingar/{år}/
                if (seg[0].Equals("tavlingar", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(ServeList(request, uc, seg));

                // /sm/{år}/{slug}/…
                if (PageAddressOwner.IsNationalType(seg[0]))
                {
                    if (seg.Length == 1) return Task.FromResult(Redirect(request, PageAddressUrls.CompetitionsListPath, query));
                    if (!PageAddressRules.IsYearSegment(seg[1], out var nYear)) return Task.FromResult(false);
                    return Task.FromResult(Serve(request, uc, PageAddressOwner.ForNational(seg[0]), nYear,
                        seg.Length > 2 ? seg[2] : null, seg.Length > 3 ? seg[3..] : Array.Empty<string>(), query));
                }

                var parsed = PageAddressRules.Parse(seg);
                if (parsed != null)
                {
                    var (ownerSegs, year, slug, rest) = parsed.Value;
                    var ownerNode = Route(uc, ownerSegs);
                    var owner = PageAddressUrls.OwnerOf(ownerNode);
                    if (owner != null)
                        return Task.FromResult(Serve(request, uc, owner.Value, year, slug, rest, query));
                    // Klubben eller kretsen har bytt adress: följ Umbracos omdirigering av ägarsidan.
                    var moved = MovedOwnerPrefix(uc, ownerSegs);
                    if (moved != null)
                        return Task.FromResult(Redirect(request, moved + string.Join('/', seg[ownerSegs.Length..]) + "/", query));
                    return Task.FromResult(false);
                }

                // /{ägare}/{slug}/ — händelsernas gamla trädadress.
                if (seg.Length >= 2 && ClubEventStoreSettings.UsesTable(_settings))
                {
                    var legacyOwner = PageAddressUrls.OwnerOf(Route(uc, seg[..^1]));
                    if (legacyOwner != null)
                    {
                        var row = _addresses.Find(legacyOwner.Value, null, seg[^1]);
                        if (row?.TargetKind == PageAddressKinds.Event)
                        {
                            var url = CurrentUrl(row);
                            if (url != null) return Task.FromResult(Redirect(request, url, query));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // En adress vi inte kan avgöra är inte vår — Umbraco går vidare till nästa finder.
                _logger.LogDebug(ex, "PageAddressContentFinder: {Path}", request.Uri.AbsolutePath);
            }
            return Task.FromResult(false);
        }

        private bool Serve(IPublishedRequestBuilder request, IUmbracoContext uc, PageAddressOwner owner, int year,
            string? slug, string[] rest, string query)
        {
            if (slug == null)
            {
                // Årssidan: ägarens tävlingar det året finns i listan.
                return Redirect(request, PageAddressUrls.CompetitionsListPath, query);
            }

            var row = _addresses.Find(owner, year, slug);
            if (row == null) return false;
            var restPath = rest.Length == 0 ? "" : string.Join('/', rest) + "/";

            if (!row.IsCurrent)
            {
                var current = CurrentUrl(row);
                return current != null && Redirect(request, current + restPath, query);
            }

            switch (row.TargetKind)
            {
                case PageAddressKinds.Event:
                    return rest.Length == 0 && ServeEvent(request, uc, row);
                case PageAddressKinds.Series:
                {
                    if (rest.Length > 0) return false;
                    var node = uc.Content!.GetById(row.TargetId);
                    if (node?.ContentType.Alias != "competitionSeries") return false; // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                    request.SetPublishedContent(node);
                    return true;
                }
                case PageAddressKinds.Competition:
                {
                    var competition = uc.Content!.GetById(row.TargetId);
                    if (competition?.ContentType.Alias != "competition") return false; // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                    if (rest.Length == 0)
                    {
                        request.SetPublishedContent(competition);
                        return true;
                    }
                    if (rest.Length > 1) return false;
                    var child = competition.Children.FirstOrDefault(c =>
                        string.Equals(c.UrlSegment, rest[0], StringComparison.OrdinalIgnoreCase));
                    if (child != null)
                    {
                        request.SetPublishedContent(child);
                        return true;
                    }
                    return string.Equals(rest[0], "resultat", StringComparison.OrdinalIgnoreCase)
                        ? CompetitionUrlContentFinder.TryServeResultPage(request, competition, _http, _shortStrings)
                        : CompetitionUrlContentFinder.TryServeStartListPage(request, competition, rest[0], _http, _shortStrings);
                }
            }
            return false;
        }

        /// <summary>
        /// Sidans aktuella adress. En händelses tas ur händelselagringen (den publicerade, med adressen ur
        /// <c>ClubEvent</c>) — flytten skriver adresserna med SQL, och cachen över aktuella rader vet inte om dem.
        /// </summary>
        private string? CurrentUrl(PageAddressRow row)
        {
            if (row.TargetKind != PageAddressKinds.Event) return _urls.UrlFor(row.TargetKind, row.TargetId);
            if (!ClubEventStoreSettings.UsesTable(_settings)) return null;
            var ev = _http.HttpContext?.RequestServices.GetRequiredService<SqlClubEventStore>().Get(row.TargetId);
            return ev is { IsPublished: true } && !string.IsNullOrEmpty(ev.Url) ? ev.Url : null;
        }

        private bool ServeEvent(IPublishedRequestBuilder request, IUmbracoContext uc, PageAddressRow row)
        {
            if (!ClubEventStoreSettings.UsesTable(_settings)) return false;
            var http = _http.HttpContext;
            if (http == null) return false;
            var store = http.RequestServices.GetRequiredService<SqlClubEventStore>();
            var ev = store.Get(row.TargetId);
            if (ev == null || !ev.IsPublished) return false;
            var owner = uc.Content!.GetById(ev.OwnerId);
            if (owner == null) return false;
            http.Items[ClubEventRoute.ItemKey] = ev;
            request.SetPublishedContent(owner);
            request.SetTemplate(new Template(_shortStrings, "ClubSimpleEvent", "ClubSimpleEvent"));
            return true;
        }

        /// <summary><c>/tavlingar/</c> = tävlingsnavet, <c>/tavlingar/{år}/</c> = årets säsongssida.</summary>
        private static bool ServeList(IPublishedRequestBuilder request, IUmbracoContext uc, string[] seg)
        {
            var hub = uc.Content!.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            if (hub == null) return false;
            if (seg.Length == 1)
            {
                request.SetPublishedContent(hub);
                return true;
            }
            if (seg.Length != 2) return false;
            var season = hub.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionSeason"
                && string.Equals(c.UrlSegment, seg[1], StringComparison.OrdinalIgnoreCase));
            if (season == null) return false;
            request.SetPublishedContent(season);
            return true;
        }

        private static IPublishedContent? Route(IUmbracoContext uc, string[] segments)
        {
            if (segments.Length == 0) return null;
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
            return uc.Content!.GetByRoute("/" + string.Join('/', segments) + "/");
#pragma warning restore CS0618
        }

        /// <summary>Den nya adressen för en klubb eller krets som bytt adress, eller null.</summary>
        private string? MovedOwnerPrefix(IUmbracoContext uc, string[] ownerSegs)
        {
            var old = "/" + string.Join('/', ownerSegs);
            var redirect = _redirects.GetMostRecentRedirectUrl(old) ?? _redirects.GetMostRecentRedirectUrl(old + "/");
            if (redirect == null) return null;
            var node = uc.Content!.GetById(redirect.ContentKey);
            var owner = PageAddressUrls.OwnerOf(node);
            return owner == null ? null : _urls.OwnerPrefix(owner.Value);
        }

        private static bool Redirect(IPublishedRequestBuilder request, string url, string query)
        {
            request.SetRedirectPermanent(url + query);
            return true;
        }
    }

    public class PageAddressComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<PageAddressService>();
            builder.Services.AddSingleton<PageAddressUrls>();
            builder.Services.AddScoped<CompetitionAddressService>();
            builder.Services.AddHostedService<PageAddressSeedHostedService>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentPublishedNotification, PageAddressPublishedHandler>();
            builder.AddNotificationHandler<Umbraco.Cms.Core.Notifications.ContentSavingNotification, PageAddressReservedNames>();
            // Först i kedjan: en adress här får inte fångas av en äldre omdirigeringsrad.
            builder.ContentFinders().Insert<PageAddressContentFinder>();
            // Före Umbracos trädupplag: gamla /competitions/-adresser ger 301 till de nya.
            builder.ContentFinders().Insert<CompetitionTreeRedirectFinder>(1);
        }
    }
}
