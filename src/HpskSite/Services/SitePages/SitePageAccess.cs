using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>Vad som ska hända med en begäran till en av ägarens sidor.</summary>
    public enum SitePageDecision
    {
        /// <summary>Visa sidan.</summary>
        Serve,
        /// <summary>
        /// Sidan finns och en vanlig medlem får se den, men inte den här tittaren (utloggad, eller inloggad utan medlemskap): ägarens vy visas med menyn kvar och en
        /// vänlig ruta i stället för innehållet ("Den här sidan är för klubbens medlemmar. Logga in …"). Status 200, aldrig sidans titel eller innehåll.
        /// </summary>
        Locked,
        /// <summary>Sidan finns inte för den här tittaren (avslöjar inte att Styrelse- eller Dold-sidor finns).</summary>
        NotFound,
    }

    /// <summary>
    /// Beslutet per begäran (klubbsidorna F0-spik a, plan v2 §4). Ren funktion, testad utan databas.
    ///
    /// <para><b>Åtkomstbesked (Besökarens vy):</b> en sida som en vanlig medlem får se (Inloggade, Klubbmedlemmar) men inte den här tittaren ger
    /// <see cref="SitePageDecision.Locked"/>: sidan finns i menyn med ett hänglås och visar en vänlig ruta (v3 B3: att sidan finns avslöjas medvetet, delade
    /// länkar ska fungera efter inloggning). En sida som BARA styrelsen eller admin ser (Styrelse, Dold) ger <b>404 åt alla</b> som inte har rätt, även
    /// utloggade och inloggade — annars skulle hänglåset avslöja att en dold sida finns.</para>
    /// </summary>
    public static class SitePageAccessRules
    {
        /// <summary>En vanlig inloggad klubbmedlem (inte styrelse, inte admin): avgör om en sida är en låst medlemssida eller en dold sida.</summary>
        private static readonly SiteViewer PlainMember = new(true, true, false, false);
        /// <summary>Inloggad men inte medlem: avgör om en låst sida kräver medlemskap eller bara inloggning.</summary>
        private static readonly SiteViewer LoggedInNonMember = new(true, false, false, false);

        public static SitePageDecision Decide(SiteTemplate template, SitePageDef page, SiteViewer viewer)
        {
            if (template.PageVisible(page, viewer)) return SitePageDecision.Serve;
            return template.PageVisible(page, PlainMember) ? SitePageDecision.Locked : SitePageDecision.NotFound;
        }

        /// <summary>Är sidan låst för <paramref name="viewer"/> och syns därför i menyn med hänglås? (Samma fråga som <see cref="Decide"/>.)</summary>
        public static bool IsLockedFor(SiteTemplate template, SitePageDef page, SiteViewer viewer) =>
            !page.IsAdmin && Decide(template, page, viewer) == SitePageDecision.Locked;

        /// <summary>Kräver den låsta sidan medlemskap (annars räcker inloggning)? Styr texten i rutan: "är för medlemmar" respektive "kräver inloggning".</summary>
        public static bool NeedsMembership(SiteTemplate template, SitePageDef page) => !template.PageVisible(page, LoggedInNonMember);
    }

    /// <summary>
    /// Tar fram <see cref="SiteViewer"/> för en ägare — EN plats för regeln som vyerna (Club.cshtml, RegionalPage.cshtml)
    /// hittills räknat ut var för sig. Körs EFTER inloggningen (i ett MVC-filter), så <c>MemberManager</c> ser medlemmen.
    /// Samma källor som vyerna: aktiv medlem = <c>ClubMembership</c>, styrelse = <c>BoardRoles</c>, admin =
    /// <c>IsClubAdminForClub</c>/<c>IsRegionalAdminForRegion</c> (inkl. krets och sajt).
    /// </summary>
    public sealed class SiteViewerResolver
    {
        private readonly Umbraco.Cms.Core.Security.IMemberManager _members;
        private readonly Umbraco.Cms.Core.Services.IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly ClubRosterService _roster;
        private readonly BoardRoleService _board;
        private readonly IUmbracoContextAccessor _ctx;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _http;

        public SiteViewerResolver(Umbraco.Cms.Core.Security.IMemberManager members, Umbraco.Cms.Core.Services.IMemberService memberService,
            AdminAuthorizationService auth, ClubRosterService roster, BoardRoleService board, IUmbracoContextAccessor ctx,
            Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null)
        {
            _http = http;
            _ctx = ctx;
            _members = members;
            _memberService = memberService;
            _auth = auth;
            _roster = roster;
            _board = board;
        }

        /// <summary>
        /// Är den inloggade ägarens EGEN admin (<c>ClubAdmin_{id}</c> / <c>RegionalAdmin_{kod}</c>) — varken sajt- eller kretsadmin? Det är vem som
        /// får redigera ägarens egna sidor (Hemsidan). EN plats för regeln: filtret, vyerna och kontrollern frågar samma sak.
        /// </summary>
        public async Task<bool> IsOwnAdminAsync(string ownerType, int ownerId, string? regionCode) =>
            PreviewFor(ownerType, ownerId) != null ? false   // i förhandsläget ser sidan inga redigeringsknappar: fliken är en besökare, inte adminen
            : ownerType == SiteTemplates.Club
                ? ownerId > 0 && await _auth.IsOwnClubAdmin(ownerId)
                : !string.IsNullOrEmpty(regionCode) && await _auth.IsOwnRegionAdmin(regionCode);

        /// <summary>Är den inloggade sajtadministratör (Administrators)? EN plats för frågan: kontrollern frågar HÄR och aldrig <c>IsCurrentUserAdminAsync</c> direkt.</summary>
        public Task<bool> IsSiteAdminAsync() => _auth.IsCurrentUserAdminAsync();

        /// <summary>
        /// Får den inloggade redigera ägarens sidor i Hemsidan? Ägarens egen admin, och (Stefan 2026-11-12: "siteadmin ska ha tillgång till allt") sajtadmin.
        /// Styr länken Hemsidan i Admin och vänstermenyn. Kretsadmin för en klubb är fortfarande hänvisad till stödläget. I förhandsläget är svaret alltid nej.
        /// </summary>
        public async Task<bool> CanEditSiteAsync(string ownerType, int ownerId, string? regionCode) =>
            PreviewFor(ownerType, ownerId) == null && (await IsOwnAdminAsync(ownerType, ownerId, regionCode) || await IsSiteAdminAsync());

        /// <summary>
        /// Får den inloggade ÖPPNA ett stödläge hos ägaren? Klubb: kretsens admin i klubbens krets och sajtadmin (<c>IsClubAdminForClub</c>). Krets: sajtadmin.
        /// Ett stödläge är aldrig en rättighet att skriva: det är rätten att be om ett loggat, tidsbegränsat läge (<see cref="SiteSupport"/>). Ägarens egen admin
        /// behöver det inte. Frågan ställs HÄR och inte i <c>SiteEditController</c>, vars spärrtest förbjuder de breda adminkontrollerna.
        /// </summary>
        public async Task<bool> IsSupportEligibleAsync(string ownerType, int ownerId) =>
            ownerType == SiteTemplates.Club
                ? ownerId > 0 && await _auth.IsClubAdminForClub(ownerId)
                : await _auth.IsCurrentUserAdminAsync();

        /// <param name="regionCode">Kretsens kod (krets-ägare); behövs för kretsens admin.</param>
        /// <summary>Förhandsläget för just den här ägaren, om begäran har ett (satt av <see cref="SitePreviewMiddleware"/> efter att biljetten godkänts).</summary>
        private SitePreviewTicket? PreviewFor(string ownerType, int ownerId)
        {
            var t = SitePreviewContext.Current(_http?.HttpContext);
            return t != null && t.OwnerType == ownerType && t.OwnerId == ownerId ? t : null;
        }

        public async Task<SiteViewer> ResolveAsync(string ownerType, int ownerId, string? regionCode)
        {
            // Förhandsläget: tittaren är den fasta besökaren biljetten gäller, aldrig den inloggade adminen.
            if (PreviewFor(ownerType, ownerId) is { } preview)
                return SitePagePreview.Persona(ownerType, preview.Persona)?.Viewer ?? SiteViewer.Anonymous;
            var current = await _members.GetCurrentMemberAsync();
            if (current == null) return SiteViewer.Anonymous;

            var member = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (ownerType == SiteTemplates.Club)
            {
                var admin = ownerId > 0 && await _auth.IsClubAdminForClub(ownerId);
                var isMember = member != null && ownerId > 0 && _roster.ActiveMemberIds(ownerId).Contains(member.Id);
                var isBoard = member != null && ownerId > 0 && _board.IsBoardMemberOf(HpskSite.Models.DocumentOwnerType.Club, ownerId, member.Id);
                return new SiteViewer(true, isMember, isBoard, admin);
            }
            var regionAdmin = !string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode);
            var regionBoard = member != null && ownerId > 0 && _board.IsBoardMemberOf(HpskSite.Models.DocumentOwnerType.Region, ownerId, member.Id);
            // Kretsmedlem (2d) = aktiv medlem (ClubMembership) i minst en av kretsens klubbar; "Klubbmedlemmar" på en krets betyder det.
            var regionMember = member != null && ownerId > 0 && _roster.IsActiveInRegion(member.Id, ownerId);
            return new SiteViewer(true, regionMember, regionBoard, regionAdmin);
        }
    }

    /// <summary>
    /// Efter inloggningen: prövar att tittaren får se sidan som <c>SitePageContentFinder</c> pekat ut (finder körs före
    /// autentiseringen och kan därför bara se om någon är inloggad, inte vem). Globalt resursfilter — det körs före
    /// åtgärden, så vyn aldrig renderas för någon som inte får se sidan. Rotadressen (Hem) har ingen
    /// <see cref="SitePageRequest"/> och rörs inte: vyn prövar den som förut.
    /// </summary>
    public sealed class SitePageAccessFilter : IAsyncResourceFilter
    {
        private readonly SiteViewerResolver _viewers;
        private readonly IUmbracoContextAccessor _ctx;
        private readonly SitePageReader _reader;
        private readonly ILogger<SitePageAccessFilter> _logger;

        private readonly AdminAuthorizationService _auth;

        private readonly SiteConvertedSource _converted;

        public SitePageAccessFilter(SiteViewerResolver viewers, IUmbracoContextAccessor ctx, SitePageReader reader, AdminAuthorizationService auth,
            ILogger<SitePageAccessFilter> logger, SiteConvertedSource converted)
        {
            _converted = converted;
            _viewers = viewers;
            _ctx = ctx;
            _reader = reader;
            _auth = auth;
            _logger = logger;
        }

        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            var http = context.HttpContext;
            var req = SitePageRequest.Current(http);
            if (req != null && !await AllowsAsync(context, http, req)) return;
            await next();
        }

        private async Task<bool> AllowsAsync(ResourceExecutingContext context, HttpContext http, SitePageRequest req)
        {
            try
            {
                if (req.IsHemsidan)
                {
                    // Redigeringsytan: ägarens EGEN admin (varken sajt- eller kretsadmin), annars 404 för alla — även utloggade.
                    string? code = null;
                    if (req.OwnerType == SiteTemplates.Region && _ctx.TryGetUmbracoContext(out var huc) && huc.Content != null)
                        code = huc.Content.GetById(req.OwnerId)?.Value<string>("regionCode");
                    var own = req.OwnerType == SiteTemplates.Club ? await _auth.IsOwnClubAdmin(req.OwnerId) : await _auth.IsOwnRegionAdmin(code ?? "");
                    // Den som FÅR öppna ett stödläge (kretsadmin, sajtadmin) släpps in till ytan, som då visar stödlägesformuläret; skrivning prövas av kontrollern.
                    if (!own && !await _viewers.IsSupportEligibleAsync(req.OwnerType, req.OwnerId)) { context.Result = new NotFoundResult(); return false; }
                    return true;
                }

                // Mallen med ägarens omvandlade sidor utbytta: en omvandlad sida syns för den som får se minst ett av DESS block.
                var template = _converted.Template(req.OwnerType, req.OwnerId);
                SitePageDef? page;
                if (req.IsFreePage)
                {
                    // En egen sida: synligheten läses FÄRSKT ur SQL (ingen cache av synlighet i steg 2) och sidan måste vara publicerad.
                    var row = _reader.Page(req.OwnerType, req.OwnerId, req.PageId!.Value);
                    page = row == null || row.PublishedVersionId == null
                        ? null
                        : new SitePageDef(SitePageRequest.FreePageKey, "", row.Title, 0, new[] { new SiteBlockDef("page", row.VisibilityLevel) });
                }
                else page = template.PageByKey(req.PageKey);
                if (page == null) { context.Result = new NotFoundResult(); return false; }

                string? regionCode = null;
                if (req.OwnerType == SiteTemplates.Region && _ctx.TryGetUmbracoContext(out var uc) && uc.Content != null)
                    regionCode = uc.Content.GetById(req.OwnerId)?.Value<string>("regionCode");

                var viewer = await _viewers.ResolveAsync(req.OwnerType, req.OwnerId, regionCode);
                switch (SitePageAccessRules.Decide(template, page, viewer))
                {
                    case SitePageDecision.Serve:
                        return true;
                    case SitePageDecision.Locked:
                        // Vyn visar ägarens meny och en vänlig ruta i stället för sidan (ingen titel, inget innehåll); status 200, aldrig cachad eller indexerad.
                        SitePageRequest.MarkLocked(http, SitePageAccessRules.NeedsMembership(template, page));
                        http.Response.Headers["Cache-Control"] = "private, no-store";
                        http.Response.Headers["X-Robots-Tag"] = "noindex";
                        return true;
                    default:
                        context.Result = new NotFoundResult();
                        return false;
                }
            }
            catch (Exception ex)
            {
                // Kan vi inte avgöra ska ingen se sidan: 404 hellre än en sida någon inte fick se.
                _logger.LogError(ex, "SitePageAccessFilter: {Path} kunde inte prövas — svarar 404", http.Request.Path);
                context.Result = new NotFoundResult();
                return false;
            }
        }
    }
}
