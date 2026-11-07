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
        /// <summary>Utloggad besökare på en sida som inte är öppen: skicka till inloggning.</summary>
        LoginRedirect,
        /// <summary>Inloggad utan rätt: sidan finns inte (avslöjar inte att Styrelse- eller Dold-sidor finns).</summary>
        NotFound,
    }

    /// <summary>
    /// Beslutet per begäran (klubbsidorna F0-spik a, plan v4 4.x). Ren funktion, testad utan databas.
    /// Utloggad → inloggning (sluggarna är fasta och publika, så det avslöjar ingenting); inloggad utan rätt → 404.
    /// </summary>
    public static class SitePageAccessRules
    {
        public static SitePageDecision Decide(SiteTemplate template, SitePageDef page, SiteViewer viewer)
        {
            if (template.PageVisible(page, viewer)) return SitePageDecision.Serve;
            return viewer.LoggedIn ? SitePageDecision.NotFound : SitePageDecision.LoginRedirect;
        }
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

        public SiteViewerResolver(Umbraco.Cms.Core.Security.IMemberManager members, Umbraco.Cms.Core.Services.IMemberService memberService,
            AdminAuthorizationService auth, ClubRosterService roster, BoardRoleService board)
        {
            _members = members;
            _memberService = memberService;
            _auth = auth;
            _roster = roster;
            _board = board;
        }

        /// <param name="regionCode">Kretsens kod (krets-ägare); behövs för kretsens admin.</param>
        public async Task<SiteViewer> ResolveAsync(string ownerType, int ownerId, string? regionCode)
        {
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
            return new SiteViewer(true, false, regionBoard, regionAdmin);
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
        private readonly ILogger<SitePageAccessFilter> _logger;

        public SitePageAccessFilter(SiteViewerResolver viewers, IUmbracoContextAccessor ctx, ILogger<SitePageAccessFilter> logger)
        {
            _viewers = viewers;
            _ctx = ctx;
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
                var template = SiteTemplates.For(req.OwnerType);
                var page = template.PageByKey(req.PageKey);
                if (page == null) { context.Result = new NotFoundResult(); return false; }

                string? regionCode = null;
                if (req.OwnerType == SiteTemplates.Region && _ctx.TryGetUmbracoContext(out var uc) && uc.Content != null)
                    regionCode = uc.Content.GetById(req.OwnerId)?.Value<string>("regionCode");

                var viewer = await _viewers.ResolveAsync(req.OwnerType, req.OwnerId, regionCode);
                switch (SitePageAccessRules.Decide(template, page, viewer))
                {
                    case SitePageDecision.Serve:
                        return true;
                    case SitePageDecision.LoginRedirect:
                        var back = http.Request.Path.Value!.TrimEnd('/') + "/";
                        context.Result = new RedirectResult("/login-register/?tab=login&returnUrl=" + Uri.EscapeDataString(back));
                        return false;
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
