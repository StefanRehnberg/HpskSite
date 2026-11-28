using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Pqt;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <c>/pqt</c>: informationssidan om PQT (pistolprovet från andra världskriget, ett eget experiment från pistol.nu) med gilla, kommentarer och intresseanmälan för märken.
    /// Routad controller utan Umbraco-nod (samma mönster som /siktbild och /motion): fungerar direkt vid deploy, ingen backoffice-uppsättning, och adressen kan inte tas av en klubb.
    ///
    /// <para><b>Vem får vad:</b> alla läser sidan och ser antalet gillningar och kommentarer. Bara inloggade medlemmar gillar, läser kommentarernas text och skriver. Ordningen i varje
    /// POST är: inloggad FÖRST (authn före authz, ingen räknare och ingen databas för en anonym), sedan räknare per medlem, sedan skrivningen med villkoren i SAMMA SQL-sats.
    /// Sajtadministratören (<see cref="AdminAuthorizationService.IsCurrentUserAdminAsync"/>) är moderator och ensam om att se intresseanmälningarna. Juniorers kommentarer hålls tills de godkänts.
    /// Ingen IP lagras. Texten visas som text (kodad): en länk blir aldrig klickbar.</para>
    /// </summary>
    [Route("pqt")]
    public class PqtController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly IScopeProvider _scopes;
        private readonly RateCounterService _rate;
        private readonly AdminAuthorizationService _auth;

        public PqtController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager members, IMemberService memberService,
            IScopeProvider scopes, RateCounterService rate, AdminAuthorizationService auth)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _members = members;
            _memberService = memberService;
            _scopes = scopes;
            _rate = rate;
            _auth = auth;
        }

        private sealed record Me(int Id, string Name, int BirthYear);

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private bool Throttled(string key, int limit, TimeSpan window) => _rate.Increment(key, window) > limit;

        /// <summary>Den inloggade medlemmen, eller null (anonym). Ingen databas för en anonym.</summary>
        private async Task<Me?> CurrentAsync()
        {
            var cur = await _members.GetCurrentMemberAsync();
            if (cur?.Email == null) return null;
            var member = _memberService.GetByEmail(cur.Email);
            if (member == null) return null;
            var birthYear = Marken.BirthYearFromPersonNumber(member.GetValue("personNumber")?.ToString(), DateTime.Now.Year);
            return new Me(member.Id, member.Name ?? "", birthYear);
        }

        private PqtCommentsModel BuildComments(NPoco.IDatabase db, Me? me, bool moderator)
        {
            var now = DateTime.Now;
            var visible = PqtStore.VisibleCommentCount(db);
            if (me == null) return new PqtCommentsModel { LoggedIn = false, VisibleCount = visible, Now = now };
            var (rows, total) = PqtStore.ListComments(db, moderator, me.Id, 0, PqtRules.PageSize);
            return new PqtCommentsModel { LoggedIn = true, Moderator = moderator, ViewerMemberId = me.Id, VisibleCount = visible, Total = total, Rows = rows, Now = now };
        }

        [HttpGet("")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> Index()
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var me = await CurrentAsync();
            var admin = me != null && await _auth.IsCurrentUserAdminAsync();
            using var scope = _scopes.CreateScope(autoComplete: true);
            ViewData["Pqt"] = new PqtPageModel
            {
                LoggedIn = me != null,
                Admin = admin,
                LikeCount = PqtStore.LikeCount(scope.Database),
                Liked = me != null && PqtStore.HasLiked(scope.Database, me.Id),
                MyInterest = me == null ? null : PqtStore.MyInterest(scope.Database, me.Id),
                AllInterest = admin ? PqtStore.ListInterest(scope.Database) : new List<PqtInterestRow>(),
                Comments = BuildComments(scope.Database, me, admin),
            };
            return View("Pqt", root);
        }

        /// <summary>Kommentarsdelen som HTML, som sidan byter in efter varje åtgärd. En anonym får bara antalet och en inloggningsrad.</summary>
        [HttpGet("kommentarer")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> Comments()
        {
            var me = await CurrentAsync();
            var admin = me != null && await _auth.IsCurrentUserAdminAsync();
            using var scope = _scopes.CreateScope(autoComplete: true);
            return PartialView("~/Views/Partials/_PqtComments.cshtml", BuildComments(scope.Database, me, admin));
        }

        // ── Gilla ─────────────────────────────────────────────────────────────
        [HttpPost("gilla")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync, anonym får 'login'), räknare per medlem; ett gilla per medlem (unikt index)")]
        public async Task<IActionResult> Like()
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att gilla.", "login");
            if (Throttled($"pqt-like:{me.Id}", PqtRules.LikesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många tryck på kort tid. Vänta en stund.", "rate");
            return LikeState(me, like: true);
        }

        [HttpPost("ogilla")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync, anonym får 'login'), räknare per medlem; tar bara bort den egna raden")]
        public async Task<IActionResult> Unlike()
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att gilla.", "login");
            if (Throttled($"pqt-like:{me.Id}", PqtRules.LikesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många tryck på kort tid. Vänta en stund.", "rate");
            return LikeState(me, like: false);
        }

        private IActionResult LikeState(Me me, bool like)
        {
            using (var scope = _scopes.CreateScope())
            {
                if (like) PqtStore.Like(scope.Database, me.Id, DateTime.Now); else PqtStore.Unlike(scope.Database, me.Id);
                scope.Complete();
            }
            using var read = _scopes.CreateScope(autoComplete: true);
            return Json(new { success = true, liked = PqtStore.HasLiked(read.Database, me.Id), count = PqtStore.LikeCount(read.Database) });
        }

        // ── Kommentarer ───────────────────────────────────────────────────────
        [HttpPost("kommentar")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync), räknare per medlem, juniorer förgranskas (PqtRules.NeedsApproval)")]
        public async Task<IActionResult> Add(string? body)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att kommentera.", "login");
            var (text, error) = SiteCommentRules.Validate(body);
            if (text == null) return Fail(error!, "invalid");
            if (Throttled($"pqtcomment-m:{me.Id}", PqtRules.PerMinute, TimeSpan.FromMinutes(1)) || Throttled($"pqtcomment-d:{me.Id}", PqtRules.PerDay, TimeSpan.FromDays(1)))
                return Fail("Du har skrivit många kommentarer på kort tid. Vänta en stund.", "rate");
            var admin = await _auth.IsCurrentUserAdminAsync();
            var needs = PqtRules.NeedsApproval(me.BirthYear, DateTime.Now.Year, admin);
            PqtCommentAddResult result; int id;
            using (var scope = _scopes.CreateScope())
            {
                (result, id) = PqtStore.AddComment(scope.Database, me.Id, me.Name, text, needs, DateTime.Now);
                if (result == PqtCommentAddResult.Ok) scope.Complete();
            }
            return result == PqtCommentAddResult.Ok
                ? Json(new { success = true, id, pending = needs })
                : Fail($"Sidan har nått taket på {PqtRules.MaxPerPage} kommentarer.", "full");
        }

        [HttpPost("kommentar/andra")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync); bara kommentarens egen skribent (AuthorMemberId i SQL-satsen), högst 15 minuter")]
        public async Task<IActionResult> Edit(int commentId, string? body)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att kommentera.", "login");
            var (text, error) = SiteCommentRules.Validate(body);
            if (text == null) return Fail(error!, "invalid");
            if (Throttled($"pqtcomment-m:{me.Id}", PqtRules.PerMinute, TimeSpan.FromMinutes(1))) return Fail("Du har skrivit många kommentarer på kort tid. Vänta en stund.", "rate");
            var admin = await _auth.IsCurrentUserAdminAsync();
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = PqtStore.EditComment(scope.Database, commentId, me.Id, text, PqtRules.NeedsApproval(me.BirthYear, DateTime.Now.Year, admin), DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail($"Kommentaren går inte att ändra: den är inte din, är borttagen eller är äldre än {SiteCommentRules.EditMinutes} minuter.", "expired");
        }

        [HttpPost("kommentar/ta-bort")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync); kommentatorns egen eller sajtadministratör (IsCurrentUserAdminAsync) — villkoret sitter i SQL-satsen")]
        public async Task<IActionResult> Delete(int commentId)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att ta bort en kommentar.", "login");
            if (Throttled($"pqtcomment-m:{me.Id}", PqtRules.PerMinute * 3, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            var admin = await _auth.IsCurrentUserAdminAsync();
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = PqtStore.SoftDelete(scope.Database, commentId, me.Id, admin ? null : me.Id, DateTime.Now);
                if (ok && admin) PqtStore.HandleReports(scope.Database, commentId, me.Id, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail("Kommentaren finns inte eller är redan borttagen.", "notfound");
        }

        [HttpPost("kommentar/aterstall")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad sajtadministratör (IsCurrentUserAdminAsync)")]
        public async Task<IActionResult> Restore(int commentId)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in.", "login");
            if (!await _auth.IsCurrentUserAdminAsync()) return Fail("Bara administratören kan återställa kommentarer.", "denied");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = PqtStore.Restore(scope.Database, commentId, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail($"Kommentaren finns inte eller är äldre än {SiteCommentRules.RestoreDays} dagar.", "notfound");
        }

        [HttpPost("kommentar/godkann")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad sajtadministratör (IsCurrentUserAdminAsync)")]
        public async Task<IActionResult> Approve(int commentId)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in.", "login");
            if (!await _auth.IsCurrentUserAdminAsync()) return Fail("Bara administratören kan godkänna kommentarer.", "denied");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = PqtStore.Approve(scope.Database, commentId);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail("Kommentaren väntar inte på granskning.", "notfound");
        }

        [HttpPost("kommentar/anmal")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync), räknare per medlem, en anmälan per person och kommentar (unikt index)")]
        public async Task<IActionResult> Report(int commentId, string? reason)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att anmäla.", "login");
            if (Throttled($"pqtcomment-rep:{me.Id}", PqtRules.ReportsPerDay, TimeSpan.FromDays(1))) return Fail("Du har anmält många kommentarer i dag.", "rate");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = PqtStore.Report(scope.Database, commentId, me.Id, reason, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail("Du har redan anmält kommentaren, eller så går den inte att anmäla.", "exists");
        }

        [HttpPost("kommentar/hanterad")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad sajtadministratör (IsCurrentUserAdminAsync)")]
        public async Task<IActionResult> HandleReport(int commentId)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in.", "login");
            if (!await _auth.IsCurrentUserAdminAsync()) return Fail("Bara administratören kan hantera anmälningar.", "denied");
            int n;
            using (var scope = _scopes.CreateScope())
            {
                n = PqtStore.HandleReports(scope.Database, commentId, me.Id, DateTime.Now);
                if (n > 0) scope.Complete();
            }
            return n > 0 ? Json(new { success = true }) : Fail("Det finns ingen anmälan att hantera.", "notfound");
        }

        // ── Intresseanmälan för märken ───────────────────────────────────────
        [HttpPost("intresse")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync), räknare per medlem; en rad per medlem (unikt index), uppdateras vid ny anmälan")]
        public async Task<IActionResult> SaveInterest(int marksman, int sharpshooter, int expert, string? clubOrEvent, string? note)
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in för att anmäla intresse.", "login");
            var (input, error) = PqtRules.ValidateInterest(marksman, sharpshooter, expert, clubOrEvent, note);
            if (input == null) return Fail(error!, "invalid");
            if (Throttled($"pqt-interest:{me.Id}", PqtRules.InterestPerDay, TimeSpan.FromDays(1))) return Fail("Du har ändrat din anmälan många gånger i dag. Försök igen i morgon.", "rate");
            using (var scope = _scopes.CreateScope())
            {
                PqtStore.SaveInterest(scope.Database, me.Id, me.Name, input, DateTime.Now);
                scope.Complete();
            }
            return Json(new { success = true });
        }

        [HttpPost("intresse/ta-bort")]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Inloggad medlem (CurrentAsync); tar bara bort den egna raden (MemberId i SQL-satsen)")]
        public async Task<IActionResult> DeleteInterest()
        {
            var me = await CurrentAsync();
            if (me == null) return Fail("Logga in.", "login");
            using (var scope = _scopes.CreateScope())
            {
                PqtStore.DeleteMyInterest(scope.Database, me.Id);
                scope.Complete();
            }
            return Json(new { success = true });
        }
    }
}
