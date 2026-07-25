using System.Globalization;
using System.Text;
using HpskSite.Models.Schedule;
using HpskSite.Services.Schedule;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// "Mitt schema" — a member's personal competition itinerary, merging what they SHOOT (start lists,
    /// patrols) with what they WORK (funktionärsuppdrag) and the day's programme into one timeline.
    ///
    /// Routed controller, no Umbraco node (same pattern as /styrelse, /mina-uppdrag, /siktbild).
    ///   /mitt-schema            → the member's upcoming competitions
    ///   /mitt-schema?c=123      → the full itinerary for one competition (print-friendly)
    ///   /mitt-schema/kalender.ics?c=123 → one-shot calendar download
    ///
    /// Rendered SERVER-SIDE on purpose: this is the page you open at the range on a bad connection, so
    /// it must not depend on a client fetch completing. Members only — external free-text helpers can't
    /// log in and see their shifts on the tokened /mina-uppdrag/svar page instead.
    /// </summary>
    [Route("mitt-schema")]
    public class MittSchemaController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MyScheduleService _schedule;
        private readonly ScheduleIcsBuilder _ics;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public MittSchemaController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            MyScheduleService schedule,
            ScheduleIcsBuilder ics)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _schedule = schedule;
            _ics = ics;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int c = 0)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var memberId = await CurrentMemberIdAsync();
            if (memberId == null)
            {
                var back = c > 0 ? $"/mitt-schema?c={c}" : "/mitt-schema";
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(back)}");
            }

            if (c > 0)
            {
                var sched = _schedule.GetSchedule(memberId.Value, c);
                ViewData["Schedule"] = sched;
                ViewData["Mode"] = "single";
                ViewData["Title"] = string.IsNullOrWhiteSpace(sched.CompName)
                    ? "Mitt schema" : $"Mitt schema — {sched.CompName}";
                return View("MittSchema", rootNode);
            }

            // Cross-competition list. Looks a little further back than forward so a member can still
            // pull up yesterday's programme (who was where) the morning after.
            var today = DateTime.Today;
            var ids = _schedule.GetCompetitionIdsForMember(memberId.Value, today.AddDays(-3), today.AddDays(120));
            var all = ids
                .Select(id => _schedule.GetSchedule(memberId.Value, id))
                .Where(s => s.HasAny || s.StartListPending)
                .OrderBy(s => s.CompDate ?? DateTime.MaxValue)
                .ThenBy(s => s.CompName, StringComparer.Create(Sv, false))
                .ToList();

            ViewData["Schedules"] = all;
            ViewData["Mode"] = "list";
            ViewData["Title"] = "Mitt schema";
            return View("MittSchema", rootNode);
        }

        /// <summary>
        /// One-shot .ics for a single competition. Items without an absolute time can't be written and
        /// are reported back in a header so the page can tell the member instead of silently dropping them.
        /// </summary>
        [HttpGet("kalender.ics")]
        public async Task<IActionResult> Calendar(int c)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Unauthorized();
            if (c <= 0) return NotFound();

            var sched = _schedule.GetSchedule(memberId.Value, c);
            if (!sched.HasAny) return NotFound();

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var (ics, exported, skipped) = _ics.Build(sched, baseUrl);
            if (exported == 0) return NotFound();

            Response.Headers["X-Schedule-Exported"] = exported.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Schedule-Skipped"] = skipped.ToString(CultureInfo.InvariantCulture);

            var fileName = BuildFileName(sched.CompName);
            return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", fileName);
        }

        private static string BuildFileName(string compName)
        {
            var safe = new string((compName ?? "tavling")
                .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
                .ToArray());
            while (safe.Contains("--")) safe = safe.Replace("--", "-");
            safe = safe.Trim('-');
            if (string.IsNullOrEmpty(safe)) safe = "tavling";
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            return $"schema-{safe}.ics";
        }

        private async Task<int?> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return null;
            return _memberService.GetByEmail(current.Email)?.Id;
        }
    }
}
