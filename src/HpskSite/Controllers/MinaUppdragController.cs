using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Member-facing "Mina uppdrag" page at /mina-uppdrag — where a person sees the functionary assignments
    /// they hold across competitions, accepts/declines an invitation, and declares their availability windows
    /// (P3: sign-up + tillgänglighet). Deliberately NOT the staff planning page (/tavlingsplanering) — an
    /// invited helper needn't be a competition admin. Everything is scoped to the current member's own rows.
    ///
    /// Routed controller, no backoffice node (mirrors TavlingsplaneringController / StyrelseController).
    /// The functionary invitation notification (StaffingController.NotifyAssignment) deep-links here.
    /// </summary>
    [Route("mina-uppdrag")]
    public partial class MinaUppdragController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly StaffingService _staffing;
        private readonly StaffingSignupService _signup;
        private readonly StaffHelpService _help;
        private readonly IDataProtectionProvider _dataProtection;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly StaffOwnerResolver _owners;

        public MinaUppdragController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            StaffingService staffing,
            StaffingSignupService signup,
            StaffHelpService help,
            IDataProtectionProvider dataProtection,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            StaffOwnerResolver owners)
        {
            _competitions = competitions;
            _owners = owners;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _staffing = staffing;
            _signup = signup;
            _help = help;
            _dataProtection = dataProtection;
        }

        [HttpGet("open")]
        public async Task<IActionResult> Open()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            return Json(new { success = true, competitions = _signup.GetOpenSignups(memberId.Value) });
        }

        [HttpPost("signup")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SignUp([FromBody] SelfSignUpRequest request)
        {
            var owner = request == null ? null : OwnerOf(request.CompetitionId, request.PlanningId);
            if (request == null || owner == null || string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Välj en roll." });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var (ok, msg) = _signup.SelfSignUp(memberId.Value, owner, request.RoleKey);
            return Json(new { success = ok, message = msg });
        }

        // ---- External (non-member) helper accept/decline via tokened link (no login) ----

        /// <summary>Raden länken gäller — null om länken är fel eller uppdraget har lämnats över sedan den skickades (K3).</summary>
        private int? InviteRowId(string? t)
        {
            var read = string.IsNullOrEmpty(t) ? null : StaffingInviteToken.Read(_dataProtection, t!);
            if (read is not { } r) return null;
            var a = _staffing.GetById(r.Id);
            return a != null && StaffingInviteToken.StillValid(r.Nonce, a.InviteNonce) ? r.Id : null;
        }

        [HttpGet("svar")]
        public IActionResult Svar(string? t)
        {
            var model = new InviteResponseModel { Token = t ?? "" };
            var id = InviteRowId(t);
            if (id != null)
            {
                var info = _staffing.GetInviteInfo(id.Value);
                if (info != null) { info.Token = t!; model = info; }
            }
            return View("UppdragSvar", model);
        }

        [HttpPost("svar")]
        [ValidateAntiForgeryToken]
        public IActionResult SvarRespond([FromForm] string t, [FromForm] string status)
        {
            var id = InviteRowId(t);
            if (id == null) return View("UppdragSvar", new InviteResponseModel { Token = t ?? "" });
            _staffing.SetInviteResponse(id.Value, status);
            var info = _staffing.GetInviteInfo(id.Value) ?? new InviteResponseModel { Token = t };
            info.Token = t;
            return View("UppdragSvar", info);
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString("/mina-uppdrag")}");

            return View("MinaUppdrag", rootNode);
        }

        [HttpGet("mine")]
        public async Task<IActionResult> GetMine()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var groups = _staffing.GetMyAssignments(memberId.Value);
            return Json(new { success = true, groups });
        }

        /// <summary>Per-competition sign-up landing page — the single action surface: accept/decline an
        /// invitation, declare availability, and (if the comp is open for you) volunteer for a role.
        /// P2b: <c>?p=</c> = en planerings bemanning (även innan den kopplats till en tävling); <c>?c=</c> =
        /// tävlingens, som leder till dess planering när den har en.</summary>
        [HttpGet("/bemanna")]
        public async Task<IActionResult> Bemanna(int c, int p)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
            {
                var back = p > 0 ? $"/bemanna?p={p}" : $"/bemanna?c={c}";
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(back)}");
            }

            var owner = p > 0 ? _owners.ForPlanning(p) : (c > 0 && _competitions.Get(c) != null ? _owners.ForCompetition(c) : null);
            ViewData["CompetitionId"] = owner?.Comp ?? 0;
            ViewData["PlanningId"] = owner?.PlanningId ?? 0;
            ViewData["CompetitionExists"] = owner != null;
            ViewData["CompetitionName"] = owner != null ? _owners.Name(owner) : "";
            // Preview mode: a manager verifying how the page looks for a fresh, not-yet-signed-up member.
            ViewData["Preview"] = Request.Query.ContainsKey("preview");
            return View("Bemanna", rootNode);
        }

        [HttpGet("for-competition")]
        public async Task<IActionResult> GetForCompetition(int competitionId, int planningId = 0, bool preview = false)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = OwnerOf(competitionId, planningId);
            if (owner == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

            var mine = _staffing.GetMyAssignments(memberId.Value)
                .FirstOrDefault(g => g.PlanningId == owner.PlanningId && g.CompetitionId == owner.Comp);
            var compName = mine?.CompName ?? _owners.Name(owner);
            string? compDate = mine?.CompDate;
            if (compDate == null && _owners.Span(owner).Start is { } dd && dd != default)
                compDate = dd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            var slots = _help.GetSlots(owner);
            // In preview a manager sees the page as a fresh member: no own assignments, no prior sign-up.
            var assignments = preview ? new List<Models.Staffing.MyAssignmentView>() : (mine?.Assignments ?? new List<Models.Staffing.MyAssignmentView>());
            var mySignup = preview ? null : _help.GetMySignup(owner, memberId.Value);
            // Omgång 3 steg 9 (Stefan 2026-10-24): veckans tävlingar (familjen) och de funktionären tänker tävla i — så att
            // planeringen kan ta hänsyn till det långt innan anmälan öppnar.
            object? shootTargets = null, myShootIntents = null;
            if (owner.PlanningId > 0)
            {
                try
                {
                    var intents = HttpContext.RequestServices.GetRequiredService<ShootIntentService>();
                    shootTargets = intents.Targets(owner.PlanningId).Select(t => new
                    {
                        planningId = t.PlanningId, name = t.Name,
                        from = t.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = t.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    }).ToList();
                    if (!preview)
                        myShootIntents = intents.ForFamily(owner.PlanningId).Where(i => i.MemberId == memberId.Value)
                            .Select(i => new { targetPlanningId = i.TargetPlanningId, classes = i.Classes }).ToList();
                }
                catch (Exception ex)
                {
                    HttpContext.RequestServices.GetRequiredService<ILogger<MinaUppdragController>>()
                        .LogWarning(ex, "Bemanna: tävlingsavsikterna gick inte att läsa för planering {Planning}", owner.PlanningId);
                }
            }

            return Json(new
            {
                success = true,
                compName = compName ?? "Tävling",
                compDate,
                assignments,
                slots,
                mySignup,
                // Planens dagar med rollerna som behövs (Stefan 2026-10-23: anmälan bygger på behoven).
                needDays = _help.NeedDays(owner),
                roleChoice = _owners.SignupRoleChoice(owner),
                shootTargets,
                myShootIntents,
            });
        }

        [HttpPost("help-signup")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveHelpSignup([FromBody] SaveHelpSignupRequest request)
        {
            var owner = request == null ? null : OwnerOf(request.CompetitionId, request.PlanningId);
            if (request == null || owner == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var md = _memberService.GetByEmail((await _memberManager.GetCurrentMemberAsync())?.Email ?? "");
            var name = md == null ? $"Medlem {memberId}"
                : ($"{md.GetValue<string>("firstName")} {md.GetValue<string>("lastName")}".Trim() is { Length: > 0 } n ? n : (md.Name ?? $"Medlem {memberId}"));
            _help.SaveMySignup(owner, memberId.Value, name, request.Comment, request.Slots);
            // Omgång 3 steg 9: tävlingarna funktionären tänker tävla i sparas med anmälan (null = en äldre sida, rör inte).
            if (request.ShootIntents != null && owner.PlanningId > 0)
            {
                var err = HttpContext.RequestServices.GetRequiredService<ShootIntentService>().Save(owner.PlanningId, "m:" + memberId.Value, name,
                    request.ShootIntents.Select(i => (i.TargetPlanningId, i.Classes)), Models.Planning.ShootIntentSource.Self, memberId.Value);
                if (err != null) return Json(new { success = false, message = err });
            }
            return Json(new { success = true });
        }

        [HttpPost("respond")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Respond([FromBody] RespondAssignmentRequest request)
        {
            if (request == null || request.AssignmentId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var (ok, msg) = _staffing.RespondAsMember(request.AssignmentId, memberId.Value, request.Status ?? "");
            return Json(new { success = ok, message = msg });
        }

        [HttpPost("availability")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAvailability([FromBody] SaveAvailabilityRequest request)
        {
            var owner = request == null ? null : OwnerOf(request.CompetitionId, request.PlanningId);
            if (request == null || owner == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            // Guard: only declare availability for a competition you actually hold an assignment in.
            if (!_staffing.MemberHasAssignment(owner, memberId.Value))
                return Json(new { success = false, message = "Du har inget uppdrag i den här tävlingen." });

            var from = ParseDateTime(request.From);
            var to = ParseDateTime(request.To);
            if (from != null && to != null && to < from)
                return Json(new { success = false, message = "Sluttiden är före starttiden." });
            var id = _staffing.AddAvailability(owner, memberId.Value, from, to, request.Note);
            return Json(new { success = true, id });
        }

        [HttpPost("availability/delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAvailability([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var ok = _staffing.DeleteAvailability(request.Id, memberId.Value);
            return Json(new { success = ok, message = ok ? null : "Kunde inte ta bort." });
        }

        /// <summary>P2b: planeringen när den anges, annars tävlingens bemanning (som leder till dess planering).</summary>
        private Models.Staffing.StaffOwner? OwnerOf(int competitionId, int planningId) =>
            planningId > 0 ? _owners.ForPlanning(planningId)
            : competitionId > 0 ? _owners.ForCompetition(competitionId) : null;

        private async Task<int?> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return null;
            var md = _memberService.GetByEmail(current.Email);
            return md?.Id;
        }

        private static DateTime? ParseDateTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return null;
        }
    }
}
