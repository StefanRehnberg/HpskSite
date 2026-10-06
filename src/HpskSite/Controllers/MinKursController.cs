using System.Text;
using HpskSite.Services.Training;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fas D: <c>/min-kurs</c> — kursens yta. Tränaren får upprop, serier och översikt; DELTAGAREN
    /// (sedan UX-omgången 2026-10-06) får en läsvy med nästa tillfälle, sina krav, sin närvaro och
    /// sitt märkesläge. Vilken vy som visas avgörs av rollen i <c>TrainingCourse/MyCourses</c>.
    ///
    /// <para>Routad, ingen Umbraco-nod — samma mönster som <c>/styrelse</c>: rotnoden som modell så
    /// att layoutens <c>Model.Root()</c> fungerar. All data hämtas av sidan genom
    /// <c>TrainingCourse/*</c>, som grindar varje anrop själv; sidan visar inget utan dem.</para>
    /// </summary>
    [Route("min-kurs")]
    public class MinKursController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly TrainingCourseService _courses;

        public MinKursController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager,
            IMemberService memberService, TrainingCourseService courses)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _courses = courses;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int? g)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
                return Redirect("/login-register/?tab=login&returnUrl=" + Uri.EscapeDataString("/min-kurs" + (g is > 0 ? $"?g={g}" : "")));

            ViewData["MinKursGroupId"] = g ?? 0;
            return View("MinKurs", root);
        }

        /// <summary>
        /// Kursens kommande tillfällen som en kalenderfil. För den som är med i kursen (deltagare
        /// eller tränare).
        /// </summary>
        [HttpGet("kalender.ics")]
        public async Task<IActionResult> Calendar(int g)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            var me = current?.Email == null ? 0 : _memberService.GetByEmail(current.Email)?.Id ?? 0;
            if (me <= 0) return Unauthorized();
            if (!_courses.IsInCourse(g, me)) return NotFound();
            var course = _courses.Get(g, DateTime.Today);
            if (course == null) return NotFound();

            var (ics, exported) = CourseIcsBuilder.Build(course.Name, g, course.Occasions, $"{Request.Scheme}://{Request.Host}");
            if (exported == 0) return NotFound();
            var safe = new string(course.Name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray()).Trim('-');
            while (safe.Contains("--")) safe = safe.Replace("--", "-");
            if (safe.Length == 0) safe = "kurs";
            if (safe.Length > 60) safe = safe[..60];
            return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", $"kurs-{safe}.ics");
        }
    }
}
