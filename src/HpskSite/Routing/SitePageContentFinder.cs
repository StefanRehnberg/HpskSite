using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// Klubbens och kretsens sidor (klubbsidorna plan v4, 2.5): <c>/halland/haaplinge-goass/banor/</c> visar klubbens
    /// nod, och <see cref="SitePageRequest"/> i <c>HttpContext.Items</c> säger vilken sida det gäller. Vyn
    /// (<c>Club.cshtml</c>, <c>RegionalPage.cshtml</c>) visar då bara den sidans panel.
    ///
    /// <para><b>Svarar bara när alla fyra stämmer:</b> adressen slutar på ett slugg i ägarens mall, ägaren är en klubb eller
    /// krets, den nya strukturen är PÅ för ägaren, och det inte redan finns en riktig nod på adressen. Allt annat är inte
    /// vårt (<c>false</c>) och går vidare i kedjan. Av för ägaren = dagens beteende, byte för byte.</para>
    ///
    /// <para><b>Ingen SQL per träfflös begäran:</b> adressen slås upp i Umbracos adressindex (minnet); brytaren ligger i
    /// minnet (<see cref="SiteOwnerService"/>). Stannar efter första steget när sista segmentet inte är ett mallslugg.</para>
    ///
    /// <para>Placeras EFTER <c>ClubShortUrlContentFinder</c> och <c>ClubEventLegacyUrlFinder</c>: en gammal händelseadress
    /// och klubbens långa form ska redan vara omdirigerade, och en klubb som heter som en kretssida vinner över kretsens
    /// kodsida (skuggningen är känd, se <c>PageAddressReservedNames</c>).</para>
    ///
    /// <para>⚠️ <b>Inloggningen:</b> findern körs i routingen, FÖRE autentiseringen — <c>HttpContext.User</c> är tom här
    /// (en inloggad medlem såg ut som utloggad och fick 404; sett på dev 2026-11-07). Medlemskakan läses därför direkt
    /// (<c>IdentityConstants.ApplicationScheme</c>, dekryptering, ingen databas), som i <c>MemberActivityTrackingMiddleware</c>.
    /// Det avgör bara om en utloggad ska skickas till inloggning; vyn prövar allt på riktigt.</para>
    /// </summary>
    public class SitePageContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _ctx;
        private readonly IHttpContextAccessor _http;
        private readonly SiteRouteLookup _routes;
        private readonly SiteOwnerService _owners;
        private readonly ILogger<SitePageContentFinder> _logger;

        public SitePageContentFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, SiteRouteLookup routes,
            SiteOwnerService owners, ILogger<SitePageContentFinder> logger)
        {
            _ctx = ctx;
            _http = http;
            _routes = routes;
            _owners = owners;
            _logger = logger;
        }

        public async Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2) return false;

                // Billigast först: finns sluggen i någon mall alls?
                var last = seg[^1];
                if (SiteTemplates.ClubTemplate.PageBySlug(last) == null && SiteTemplates.RegionTemplate.PageBySlug(last) == null)
                    return false;

                if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return false;
                var cache = uc.Content;

                var owner = _routes.Owner(cache, seg[..^1]);
                var ownerType = owner?.ContentType.Alias switch
                {
                    "club" => SiteTemplates.Club,
                    "regionalPage" => SiteTemplates.Region,
                    _ => null,
                };
                if (owner == null || ownerType == null) return false;
                if (!_owners.IsEnabled(ownerType, owner.Id)) return false;

                var page = SiteTemplates.For(ownerType).PageBySlug(last);
                if (page == null) return false;

                // En riktig nod på adressen (t.ex. en gammal händelsenod under klubben) har företräde: den är inte vår att ta.
                if (_routes.TreeRoute(cache, seg) != null) return false;

                var http = _http.HttpContext;
                if (http == null) return false;

                // En sida som inte är öppen för en utloggad besökare: skicka till inloggning med återvändsadress i stället
                // för att visa en sida som inte finns. Sluggarna är fasta och publika, så det avslöjar ingenting.
                // (Dold är steg 2 och ger 404.) Vyn visar ändå bara den publika startsidan för en utloggad.
                if (!SiteTemplates.For(ownerType).PageVisible(page, SiteViewer.Anonymous) && !await IsLoggedInAsync(http))
                {
                    var back = request.Uri.AbsolutePath.TrimEnd('/') + "/";
                    request.SetRedirect("/login-register/?tab=login&returnUrl=" + Uri.EscapeDataString(back));
                    return true;
                }

                http.Items[SitePageRequest.ItemKey] = new SitePageRequest(ownerType, owner.Id, page.Key);
                request.SetPublishedContent(owner);
                return true;
            }
            catch (Exception ex)
            {
                // En adress vi inte kan avgöra är inte vår (dagens beteende).
                _logger.LogWarning(ex, "SitePageContentFinder: {Path} kunde inte avgöras", request.Uri.AbsolutePath);
                return false;
            }
        }

        /// <summary>Har besökaren en giltig medlemskaka? Läser kakan direkt (ingen databas).</summary>
        private static async Task<bool> IsLoggedInAsync(HttpContext http)
        {
            try
            {
                var auth = await AuthenticationHttpContextExtensions.AuthenticateAsync(http, IdentityConstants.ApplicationScheme);
                return auth?.Succeeded == true && auth.Principal != null;
            }
            catch
            {
                // Kan kakan inte läsas är besökaren inte inloggad (och vyn prövar på riktigt).
                return false;
            }
        }
    }
}
