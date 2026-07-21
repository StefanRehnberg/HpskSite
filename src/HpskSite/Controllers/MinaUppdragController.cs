using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
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
    public class MinaUppdragController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly StaffingService _staffing;

        public MinaUppdragController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            StaffingService staffing)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _staffing = staffing;
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
                return Redirect("/login-&-register/?tab=login&RedirectUrl=/mina-uppdrag");

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
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            // Guard: only declare availability for a competition you actually hold an assignment in.
            if (!_staffing.MemberHasAssignment(request.CompetitionId, memberId.Value))
                return Json(new { success = false, message = "Du har inget uppdrag i den här tävlingen." });

            var from = ParseDateTime(request.From);
            var to = ParseDateTime(request.To);
            if (from != null && to != null && to < from)
                return Json(new { success = false, message = "Sluttiden är före starttiden." });
            var id = _staffing.AddAvailability(request.CompetitionId, memberId.Value, from, to, request.Note);
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
