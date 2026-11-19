using HpskSite.Models;
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
    /// Kommentarerna på en nyhet (läsarsidan). Klubbstyrelsen ansvarar för dem (BBS-lagen): bara en förening med godkänt PUB-avtal och påslagen brytare kan ta emot kommentarer, och bara klubbens
    /// medlemmar (samma krets som får reagera) skriver och läser texten. Ordningen är alltid som i <see cref="SiteNewsReactionsController"/>: inloggad FÖRST (ingen räknare och ingen databas för en
    /// anonym), sedan ägaren, rätten att läsa/reagera, läsrätten till nyheten — en nyhet man inte får läsa är för kommentaren densamma som en som inte finns. Alla id slås upp mot den ägare och
    /// den nyhet gaten fastställt (id-lagen). En kommentar rör aldrig <c>SitePost.Revision</c>. Kommentarer på en krets nyheter byggs inte i v1.
    /// </summary>
    public class SiteNewsCommentsController : SurfaceController
    {
        private readonly IScopeProvider _scopes;
        private readonly RateCounterService _rate;
        private readonly IContentService _content;
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly SiteViewerResolver _viewers;
        private readonly DpaAcceptanceService _dpa;
        private readonly SiteNewsAlerts _alerts;

        public SiteNewsCommentsController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            IScopeProvider scopes, RateCounterService rate, IContentService content, IMemberManager members, IMemberService memberService,
            SiteViewerResolver viewers, DpaAcceptanceService dpa, SiteNewsAlerts alerts)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _scopes = scopes; _rate = rate; _content = content; _members = members; _memberService = memberService; _viewers = viewers; _dpa = dpa; _alerts = alerts;
        }

        private sealed record Gate(IActionResult? Error, string OwnerType = "", int OwnerId = 0, int MemberId = 0, string MemberName = "", int BirthYear = 0,
            SitePostRow? Post = null, SiteViewer? Viewer = null, bool Moderator = false, SiteCommentSettings? Settings = null, bool DpaAccepted = false, string OwnerName = "", string OwnerUrl = "/");

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private bool Throttled(string key, int limit, TimeSpan window) => _rate.Increment(key, window) > limit;

        private async Task<Gate> GateAsync(string ownerType, int ownerId, int postId)
        {
            if (ownerType != SiteTemplates.Club) return new Gate(Fail("Kommentarer finns bara på en klubbs nyheter."));
            if (ownerId <= 0 || postId <= 0) return new Gate(Fail("Nyheten finns inte.", "notfound"));
            var node = _content.GetById(ownerId);
            if (node == null || node.Trashed || node.ContentType.Alias != "club") return new Gate(Fail("Nyheten finns inte.", "notfound"));

            var viewer = await _viewers.ResolveAsync(ownerType, ownerId, null);
            if (!viewer.LoggedIn) return new Gate(Fail("Logga in för att kommentera.", "login"));
            if (!SiteCommentRules.CanReadText(viewer)) return new Gate(Fail("Bara medlemmar i föreningen kan läsa och skriva kommentarer.", "denied"));

            var cur = await _members.GetCurrentMemberAsync();
            var member = cur?.Email == null ? null : _memberService.GetByEmail(cur.Email);
            if (member == null) return new Gate(Fail("Logga in för att kommentera.", "login"));

            var dpa = await _dpa.GetStatusForClubAsync(ownerId);   // async: FÖRE databasscopet
            var birthYear = Marken.BirthYearFromPersonNumber(member.GetValue("personNumber")?.ToString(), DateTime.Now.Year);

            using var scope = _scopes.CreateScope(autoComplete: true);
            var post = SitePosts.GetReadable(scope.Database, postId, ownerType, ownerId, viewer, includeDrafts: false);
            if (post == null || !post.IsPublished) return new Gate(Fail("Nyheten finns inte.", "notfound"));
            var settings = SitePostComments.Settings(scope.Database, ownerType, ownerId);
            var moderator = SiteCommentRules.CanModerate(viewer, post.AuthorMemberId == member.Id);
            var ownerName = node.GetValue<string>("clubName") ?? node.Name ?? "";
            var ownerUrl = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
            return new Gate(null, ownerType, ownerId, member.Id, member.Name ?? "", birthYear, post, viewer, moderator, settings, dpa.Accepted, ownerName, ownerUrl);
        }

        private bool NeedsApproval(Gate g) => SiteCommentRules.NeedsApproval(g.Settings!.Moderation, g.BirthYear, DateTime.Now.Year, g.Moderator);

        /// <summary>Skriver en kommentar. Alla villkor prövas en gång till i SQL-satsen (nyheten publicerad, läget På, brytaren på, taket).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben (SitePostReactionRules.CanReact) + läsrätt till nyheten + SiteCommentRules.CanComment (läge På, brytare på, godkänt PUB-avtal), räknare per medlem")]
        public async Task<IActionResult> Add(string ownerType, int ownerId, int postId, string? body)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (!SiteCommentRules.CanComment(g.Viewer!.Value, g.OwnerType, g.Post!.CommentsMode, g.Settings!.Enabled, g.DpaAccepted))
                return Fail(g.Post.CommentsMode == 2 ? "Kommentarerna på den här nyheten är stängda." : "Det går inte att kommentera den här nyheten.", "closed");
            var (text, error) = SiteCommentRules.Validate(body);
            if (text == null) return Fail(error!, "invalid");
            if (Throttled($"postcomment-m:{g.MemberId}", SiteCommentRules.PerMinute, TimeSpan.FromMinutes(1)) || Throttled($"postcomment-d:{g.MemberId}", SiteCommentRules.PerDay, TimeSpan.FromDays(1)))
                return Fail("Du har skrivit många kommentarer på kort tid. Vänta en stund.", "rate");
            var needs = NeedsApproval(g);
            SiteCommentAddResult result; int id;
            using (var scope = _scopes.CreateScope())
            {
                (result, id) = SitePostComments.Add(scope.Database, g.Post.Id, g.OwnerType, g.OwnerId, g.MemberId, g.MemberName, text, needs, DateTime.Now);
                if (result == SiteCommentAddResult.Ok) scope.Complete();
            }
            return result switch
            {
                SiteCommentAddResult.Ok => Json(new { success = true, id, pending = needs }),
                SiteCommentAddResult.Full => Fail($"Nyheten har nått taket på {SiteCommentRules.MaxPerPost} kommentarer.", "full"),
                _ => Fail("Det går inte att kommentera den här nyheten.", "closed"),
            };
        }

        /// <summary>Ändrar den egna kommentaren, högst femton minuter efter att den skrevs.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten; bara kommentarens egen skribent (AuthorMemberId i SQL-satsen), högst 15 minuter")]
        public async Task<IActionResult> Edit(string ownerType, int ownerId, int postId, int commentId, string? body)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            var (text, error) = SiteCommentRules.Validate(body);
            if (text == null) return Fail(error!, "invalid");
            if (Throttled($"postcomment-m:{g.MemberId}", SiteCommentRules.PerMinute, TimeSpan.FromMinutes(1))) return Fail("Du har skrivit många kommentarer på kort tid. Vänta en stund.", "rate");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = SitePostComments.Edit(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId, text, NeedsApproval(g), DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail($"Kommentaren går inte att ändra: den är inte din, är borttagen eller är äldre än {SiteCommentRules.EditMinutes} minuter.", "expired");
        }

        /// <summary>Tar bort en kommentar: kommentatorn sin egen (alltid), en moderator vilken som helst. Texten sparas 30 dagar och kan då återställas av en moderator.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten; kommentatorns egen eller moderator (SiteCommentRules.CanModerate)")]
        public async Task<IActionResult> Delete(string ownerType, int ownerId, int postId, int commentId)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (Throttled($"postcomment-m:{g.MemberId}", SiteCommentRules.PerMinute * 3, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                // Moderator: vilken som helst (och anmälningarna på den blir hanterade). Annars bara den egna — villkoret sitter i SQL-satsen, inte i koden.
                ok = SitePostComments.SoftDelete(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId, g.Moderator ? null : g.MemberId, DateTime.Now);
                if (ok && g.Moderator) SitePostComments.HandleReports(scope.Database, commentId, g.Post.Id, g.OwnerType, g.OwnerId, g.MemberId, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail("Kommentaren finns inte eller är redan borttagen.", "notfound");
        }

        /// <summary>Återställer en borttagen kommentar (moderator), inom 30 dagar.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten + moderator (SiteCommentRules.CanModerate)")]
        public async Task<IActionResult> Restore(string ownerType, int ownerId, int postId, int commentId)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (!g.Moderator) return Fail("Bara klubbens administratörer, styrelse och nyhetens skribent kan återställa kommentarer.", "denied");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = SitePostComments.Restore(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail($"Kommentaren finns inte eller är äldre än {SiteCommentRules.RestoreDays} dagar.", "notfound");
        }

        /// <summary>Godkänner en kommentar som väntar på granskning (moderator).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten + moderator (SiteCommentRules.CanModerate)")]
        public async Task<IActionResult> Approve(string ownerType, int ownerId, int postId, int commentId)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (!g.Moderator) return Fail("Bara klubbens administratörer, styrelse och nyhetens skribent kan godkänna kommentarer.", "denied");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = SitePostComments.Approve(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail("Kommentaren väntar inte på granskning.", "notfound");
        }

        /// <summary>Anmäler en kommentar till klubbens administratörer. Mejlet avslöjar inte vem som anmält.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten; räknare per medlem, en anmälan per person och kommentar (unikt index)")]
        public async Task<IActionResult> Report(string ownerType, int ownerId, int postId, int commentId, string? reason)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (Throttled($"postcomment-rep:{g.MemberId}", SiteCommentRules.ReportsPerDay, TimeSpan.FromDays(1))) return Fail("Du har anmält många kommentarer i dag.", "rate");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = SitePostComments.Report(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId, reason, DateTime.Now);
                if (ok) scope.Complete();
            }
            if (!ok) return Fail("Du har redan anmält kommentaren, eller så går den inte att anmäla.", "exists");
            try { await _alerts.CommentReportedAsync(g.OwnerType, g.OwnerId, g.OwnerName, g.Post!, commentId, Request.PublicBase() + g.OwnerUrl.TrimEnd('/') + "/nyheter/" + g.Post!.Slug + "/"); }
            catch { /* loggas i tjänsten; anmälan ligger kvar i kön på nyhetens sida */ }
            return Json(new { success = true });
        }

        /// <summary>Markerar anmälningarna på en kommentar som hanterade (kommentaren behålls) — moderator.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: inloggad medlem i klubben + läsrätt till nyheten + moderator (SiteCommentRules.CanModerate)")]
        public async Task<IActionResult> HandleReport(string ownerType, int ownerId, int postId, int commentId)
        {
            var g = await GateAsync(ownerType, ownerId, postId);
            if (g.Error != null) return g.Error;
            if (!g.Moderator) return Fail("Bara klubbens administratörer, styrelse och nyhetens skribent kan hantera anmälningar.", "denied");
            int n;
            using (var scope = _scopes.CreateScope())
            {
                n = SitePostComments.HandleReports(scope.Database, commentId, g.Post!.Id, g.OwnerType, g.OwnerId, g.MemberId, DateTime.Now);
                if (n > 0) scope.Complete();
            }
            return n > 0 ? Json(new { success = true }) : Fail("Det finns ingen anmälan att hantera.", "notfound");
        }
    }
}
