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
        private readonly SitePageReader _reader;
        private readonly Umbraco.Cms.Core.Strings.IShortStringHelper _shortStrings;
        private readonly ILogger<SitePageContentFinder> _logger;

        public SitePageContentFinder(IUmbracoContextAccessor ctx, IHttpContextAccessor http, SiteRouteLookup routes,
            SiteOwnerService owners, SitePageReader reader, Umbraco.Cms.Core.Strings.IShortStringHelper shortStrings,
            ILogger<SitePageContentFinder> logger)
        {
            _ctx = ctx;
            _http = http;
            _routes = routes;
            _owners = owners;
            _reader = reader;
            _shortStrings = shortStrings;
            _logger = logger;
        }

        public async Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var seg = request.Uri.GetAbsolutePathDecoded().Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length < 2) return false;

                // Billigast först: finns sluggen i någon mall alls? Annars kan det vara en egen sida (steg 2) — bara när ägaren har
                // brytare 2 PÅ, och bara för en adress som ser ut som en slugg (ingen SQL för skräp).
                var last = seg[^1];
                if (SiteTemplates.ClubTemplate.PageBySlug(last) == null && SiteTemplates.RegionTemplate.PageBySlug(last) == null)
                {
                    if (string.Equals(last, HemsidanSlug, StringComparison.OrdinalIgnoreCase)) return TryFindHemsidan(request, seg);
                    return seg.Length >= 3 && IsSlugShaped(last) && await TryFindFreePageAsync(request, seg);
                }

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

                // En sida som inte är öppen för en utloggad besökare: inloggning med återvändsadress (Inloggade/Klubbmedlemmar) eller 404
                // (Styrelse/Dold) — samma regel som filtret efter inloggningen (SitePageAccessRules). Sluggarna i mallen är fasta och
                // publika, så en omdirigering avslöjar ingenting. Vyn visar ändå bara den publika startsidan för en utloggad.
                if (!await IsLoggedInAsync(http))
                {
                    switch (SitePageAccessRules.Decide(SiteTemplates.For(ownerType), page, SiteViewer.Anonymous))
                    {
                        case SitePageDecision.LoginRedirect:
                            request.SetRedirect(LoginUrl(request));
                            return true;
                        case SitePageDecision.NotFound:
                            return false;
                    }
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

        /// <summary>Redigeringsytans adress: <c>/{ägare}/hemsidan/</c>. Ett reserverat ord (<see cref="SitePageSlugRules"/>), aldrig en egen sida.</summary>
        public const string HemsidanSlug = "hemsidan";

        /// <summary>
        /// Hemsidan (redigeringsytan): bara när ägaren har brytare 2 PÅ. Findern avgör inte vem som får se den (filtret efter inloggningen
        /// kräver ägarens EGEN admin, annars 404 för alla — även utloggade, så att ytans existens inte avslöjas).
        /// </summary>
        private bool TryFindHemsidan(IPublishedRequestBuilder request, string[] seg)
        {
            if (seg.Length < 2 || !_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return false;
            var cache = uc.Content;
            var owner = _routes.Owner(cache, seg[..^1]);
            var ownerType = owner?.ContentType.Alias switch { "club" => SiteTemplates.Club, "regionalPage" => SiteTemplates.Region, _ => null };
            if (owner == null || ownerType == null || !_owners.IsEditingEnabled(ownerType, owner.Id)) return false;
            if (_routes.TreeRoute(cache, seg) != null) return false;
            var http = _http.HttpContext;
            if (http == null) return false;
            http.Items[SitePageRequest.ItemKey] = new SitePageRequest(ownerType, owner.Id, SitePageRequest.HemsidanKey);
            request.SetPublishedContent(owner);
            request.SetTemplate(new Umbraco.Cms.Core.Models.Template(_shortStrings, PageViews.Hemsidan, PageViews.Hemsidan));
            return true;
        }

        private static string LoginUrl(IPublishedRequestBuilder request) =>
            "/login-register/?tab=login&returnUrl=" + Uri.EscapeDataString(request.Uri.AbsolutePath.TrimEnd('/') + "/");

        /// <summary>Ser sista segmentet ut som en slugg (a–z, 0–9, bindestreck, högst 60)? Allt annat är aldrig en egen sida: ingen SQL.</summary>
        private static bool IsSlugShaped(string s) =>
            s.Length is > 0 and <= SitePageSlugRules.MaxLength && s.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-');

        /// <summary>
        /// Klubbens/kretsens egen sida (steg 2): adressen slås upp i <c>PageAddress</c> (sorten sitepage, utan år) — bara när ägaren har
        /// brytare 2 PÅ. En gammal adress (sluggbyte) ger 301 till den nya om sidan var öppen för minst medlemmar (v3 A8); en
        /// Styrelse/Dold-sida ger 404. Findern avgör bara "finns och är publicerad": synligheten prövas efter inloggningen
        /// (<see cref="SitePageAccessFilter"/>), färskt ur SQL.
        /// </summary>
        private async Task<bool> TryFindFreePageAsync(IPublishedRequestBuilder request, string[] seg)
        {
            if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return false;
            var cache = uc.Content;
            var owner = _routes.Owner(cache, seg[..^1]);
            var ownerType = owner?.ContentType.Alias switch { "club" => SiteTemplates.Club, "regionalPage" => SiteTemplates.Region, _ => null };
            if (owner == null || ownerType == null || !_owners.IsEditingEnabled(ownerType, owner.Id)) return false;

            var hit = _reader.Resolve(ownerType, owner.Id, seg[^1].ToLowerInvariant());
            if (hit == null) return false;
            if (_routes.TreeRoute(cache, seg) != null) return false;   // en riktig nod på adressen har företräde

            var http = _http.HttpContext;
            if (http == null) return false;

            if (!hit.IsCurrentAddress)
            {
                // Bara en sida som var öppen för minst medlemmar får peka vidare; en Styrelse/Dold-sida avslöjas inte.
                if (hit.Page.VisibilityLevel > SiteVisibility.Klubbmedlemmar) return false;
                var ownerUrl = "/" + string.Join('/', seg[..^1]) + "/";
                request.SetRedirectPermanent(ownerUrl + hit.CurrentSlug + "/");
                return true;
            }

            // Utloggad: samma regel som filtret efter inloggningen, men redan här så att ingen sida hinner renderas.
            if (!await IsLoggedInAsync(http))
            {
                var def = new SitePageDef(SitePageRequest.FreePageKey, hit.CurrentSlug, hit.Page.Title, 0,
                    new[] { new SiteBlockDef("page", hit.Page.VisibilityLevel) });
                switch (SitePageAccessRules.Decide(SiteTemplates.For(ownerType), def, SiteViewer.Anonymous))
                {
                    case SitePageDecision.LoginRedirect: request.SetRedirect(LoginUrl(request)); return true;
                    case SitePageDecision.NotFound: return false;
                }
            }

            http.Items[SitePageRequest.ItemKey] = new SitePageRequest(ownerType, owner.Id, SitePageRequest.FreePageKey, hit.Page.Id);
            request.SetPublishedContent(owner);
            request.SetTemplate(new Umbraco.Cms.Core.Models.Template(_shortStrings, PageViews.SitePage, PageViews.SitePage));
            return true;
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
