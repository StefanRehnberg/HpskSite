using HpskSite.Services;
using HpskSite.Services.Hosting;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Läsarnas yta på en nyhet: reaktioner (gilla) och "ta bort min bild". Egen controller — skribentens (<see cref="SiteNewsController"/>) gate kräver SKRIVRÄTT och accepterade
    /// villkor, vilket en läsare aldrig ska behöva. Ordningen är alltid: inloggad FÖRST (authn före authz, ingen räknare och ingen databas för en anonym), sedan ägaren, sedan rätten att
    /// reagera ur <see cref="SitePostReactionRules.CanReact"/>, sedan läsrätten till just den här nyheten (<see cref="SitePosts.GetReadable"/>): en nyhet man inte får läsa är för
    /// reaktionen densamma som en som inte finns. Nyhets-id:t slås upp bara mot den ägare gaten fastställt (id-lagen). En reaktion rör aldrig <c>SitePost.Revision</c>.
    /// </summary>
    public class SiteNewsReactionsController : SurfaceController
    {
        private readonly IScopeProvider _scopes;
        private readonly RateCounterService _rate;
        private readonly IContentService _content;
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly SiteViewerResolver _viewers;

        public SiteNewsReactionsController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            IScopeProvider scopes, RateCounterService rate, IContentService content, IMemberManager members, IMemberService memberService, SiteViewerResolver viewers)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _scopes = scopes; _rate = rate; _content = content; _members = members; _memberService = memberService; _viewers = viewers;
        }

        private sealed record Gate(IActionResult? Error, string OwnerType = "", int OwnerId = 0, int MemberId = 0, SitePostRow? Post = null, string OwnerName = "", string OwnerUrl = "/");

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private bool Throttled(string key, int limit, TimeSpan window) => _rate.Increment(key, window) > limit;

        private async Task<Gate> GateAsync(string ownerType, int ownerId, int postId, bool mustBeMember)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region) return new Gate(Fail("Okänd ägartyp."));
            if (ownerId <= 0 || postId <= 0) return new Gate(Fail("Nyheten finns inte.", "notfound"));
            var node = _content.GetById(ownerId);
            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            if (node == null || node.Trashed || node.ContentType.Alias != alias) return new Gate(Fail("Nyheten finns inte.", "notfound"));

            var regionCode = node.GetValue<string>("regionCode");
            var viewer = await _viewers.ResolveAsync(ownerType, ownerId, regionCode);
            if (!viewer.LoggedIn) return new Gate(Fail("Logga in för att reagera.", "login"));
            if (mustBeMember && !SitePostReactionRules.CanReact(viewer)) return new Gate(Fail("Bara medlemmar i föreningen kan reagera på nyheterna.", "denied"));

            var cur = await _members.GetCurrentMemberAsync();
            var member = cur?.Email == null ? null : _memberService.GetByEmail(cur.Email);
            if (member == null) return new Gate(Fail("Logga in för att reagera.", "login"));

            using var scope = _scopes.CreateScope(autoComplete: true);
            var post = SitePosts.GetReadable(scope.Database, postId, ownerType, ownerId, viewer, includeDrafts: false);
            if (post == null || !post.IsPublished) return new Gate(Fail("Nyheten finns inte.", "notfound"));
            var ownerName = node.GetValue<string>("clubName") ?? node.GetValue<string>("regionName") ?? node.Name ?? "";
            var ownerUrl = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
            return new Gate(null, ownerType, ownerId, member.Id, post, ownerName, ownerUrl);
        }

        private static object State(NPoco.IDatabase db, Gate g)
        {
            var s = SitePostReactions.Summaries(db, g.OwnerType, g.OwnerId, new[] { g.Post!.Id }).GetValueOrDefault(g.Post.Id) ?? new SitePostReactionSummary();
            return new
            {
                success = true,
                mine = SitePostReactions.Mine(db, g.Post.Id, g.OwnerType, g.OwnerId, g.MemberId),
                total = s.Total,
                counts = SitePostReactionKinds.All.ToDictionary(k => k.Key, k => s.Of(k.Key)),
            };
        }

        /// <summary>Sätter medlemmens reaktion (byter om hen redan har en). Explicit, aldrig en växling: klienten kallar <see cref="Clear"/> när hen trycker på sin egen igen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i ägaren (SitePostReactionRules.CanReact) + läsrätt till nyheten (SitePosts.GetReadable), räknare per medlem")]
        public async Task<IActionResult> React(string ownerType, int ownerId, int postId, string kind)
        {
            var g = await GateAsync(ownerType, ownerId, postId, mustBeMember: true);
            if (g.Error != null) return g.Error;
            if (!SitePostReactionKinds.IsValid(kind)) return Fail("Okänd reaktion.", "invalid");
            if (Throttled($"postreact:{g.MemberId}", SitePostReactionRules.PerMinute, TimeSpan.FromMinutes(1))) return Fail("För många reaktioner på kort tid. Vänta en stund.", "rate");
            using var scope = _scopes.CreateScope();
            if (!SitePostReactions.Set(scope.Database, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId, kind, DateTime.Now)) return Fail("Nyheten finns inte.", "notfound");
            scope.Complete();
            using var read = _scopes.CreateScope(autoComplete: true);
            return Json(State(read.Database, g));
        }

        /// <summary>Tar bort medlemmens reaktion. Idempotent.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i ägaren (SitePostReactionRules.CanReact) + läsrätt till nyheten (SitePosts.GetReadable), räknare per medlem")]
        public async Task<IActionResult> Clear(string ownerType, int ownerId, int postId)
        {
            var g = await GateAsync(ownerType, ownerId, postId, mustBeMember: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"postreact:{g.MemberId}", SitePostReactionRules.PerMinute, TimeSpan.FromMinutes(1))) return Fail("För många reaktioner på kort tid. Vänta en stund.", "rate");
            using (var scope = _scopes.CreateScope())
            {
                SitePostReactions.Clear(scope.Database, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId);
                scope.Complete();
            }
            using var read = _scopes.CreateScope(autoComplete: true);
            return Json(State(read.Database, g));
        }

        /// <summary>
        /// "Jag syns på den här bilden och vill att den tas bort": en inloggad som får läsa nyheten lägger en förfrågan som skribenterna ser på nyhetens sida. Ingen behöver vara medlem i föreningen
        /// (den som syns kan vara en tävlingsdeltagare från en annan klubb) men måste vara inloggad. Högst tio förfrågningar per dygn och person.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad + läsrätt till nyheten (SitePosts.GetReadable), räknare per medlem; bilden måste ligga i nyhetens galleri")]
        public async Task<IActionResult> RequestImageRemoval(string ownerType, int ownerId, int postId, int fileId, string? message, [FromServices] SiteNewsAlerts alerts)
        {
            var g = await GateAsync(ownerType, ownerId, postId, mustBeMember: false);
            if (g.Error != null) return g.Error;
            if (Throttled($"postimgreq:{g.MemberId}", 10, TimeSpan.FromDays(1))) return Fail("Du har redan skickat flera förfrågningar i dag.", "rate");
            bool added;
            using (var scope = _scopes.CreateScope())
            {
                added = SitePosts.AddImageRequest(scope.Database, g.Post!.Id, g.OwnerType, g.OwnerId, fileId, g.MemberId, message, DateTime.Now);
                if (added) scope.Complete();
            }
            if (!added) return Fail("Förfrågan finns redan, eller bilden ligger inte längre i galleriet.", "exists");
            // Mejl till klubbens administratörer, EFTER att scopet stängts. Ett misslyckat mejl ändrar inte svaret: förfrågan syns på nyhetens sida ändå.
            try { await alerts.ImageRemovalRequestedAsync(g.OwnerType, g.OwnerId, g.OwnerName, g.Post!, fileId, message, g.MemberId, Request.PublicBase() + g.OwnerUrl.TrimEnd('/') + "/nyheter/" + g.Post!.Slug + "/"); }
            catch { /* loggas i tjänsten */ }
            return Json(new { success = true });
        }
    }
}
