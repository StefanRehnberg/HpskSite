using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// The educator-facing Utbildning library at /utbildning. Course material (lessons,
    /// slides, videos) is visible ONLY to holders of a course's EducatorCertType + admins —
    /// participants never reach it (their only touchpoint is the test, Phase 2).
    /// Routed MVC Controller (parameterised URLs) — passes the site root node as the Model so
    /// Master.cshtml renders the chrome. Same approach as SightPictureController. No Umbraco node.
    /// See COURSE_SYSTEM.md.
    /// </summary>
    [Route("utbildning")]
    public class UtbildningController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly CourseService _courseService;
        private readonly CertificationService _certificationService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UtbildningController> _logger;

        public UtbildningController(
            IUmbracoContextAccessor umbracoContextAccessor,
            CourseService courseService,
            CertificationService certificationService,
            AdminAuthorizationService authorizationService,
            IMemberManager memberManager,
            IMemberService memberService,
            IWebHostEnvironment env,
            ILogger<UtbildningController> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _courseService = courseService;
            _certificationService = certificationService;
            _authorizationService = authorizationService;
            _memberManager = memberManager;
            _memberService = memberService;
            _env = env;
            _logger = logger;
        }

        // ── Routed pages ───────────────────────────────────────────────────────

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var root = GetRoot();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var memberId = await GetCurrentMemberIdAsync();
            var isAdmin = await IsAdminAsync();
            ViewBag.IsLoggedIn = memberId > 0;
            ViewBag.IsAdmin = isAdmin;

            // Showcase: any logged-in member sees the published catalog. Educators see THEIR
            // courses unlocked (clickable into the material); everyone else sees a locked teaser
            // of the breadth of modernised material. Admins also see unpublished (draft) courses.
            var all = await _courseService.GetAllCoursesAsync();
            var courses = all.Where(c => c.IsPublished || isAdmin).ToList();

            var unlocked = new Dictionary<int, bool>();
            var moduleCounts = new Dictionary<int, int>();
            foreach (var c in courses)
            {
                unlocked[c.Id] = memberId > 0 && await CanAccessCourseAsync(c, memberId);
                moduleCounts[c.Id] = (await _courseService.GetModulesAsync(c.Id)).Count;
            }

            ViewBag.Courses = courses;
            ViewBag.Unlocked = unlocked;
            ViewBag.ModuleCounts = moduleCounts;
            ViewBag.HasAnyUnlocked = unlocked.Values.Any(v => v);
            return View("Utbildning", root);
        }

        [HttpGet("{courseKey}")]
        public async Task<IActionResult> CourseOverview(string courseKey)
        {
            var root = GetRoot();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var memberId = await GetCurrentMemberIdAsync();
            var isAdmin = await IsAdminAsync();
            ViewBag.IsLoggedIn = memberId > 0;
            ViewBag.IsAdmin = isAdmin;

            var course = await _courseService.GetCourseByKeyAsync(courseKey);
            if (course == null || (memberId > 0 && !await CanAccessCourseAsync(course, memberId)) || memberId == 0)
            {
                ViewBag.Denied = true;
                ViewBag.Course = course;
                return View("UtbildningCourse", root);
            }

            var modules = await _courseService.GetModulesAsync(course.Id);
            if (!isAdmin) modules = modules.Where(m => m.IsPublished).ToList();
            ViewBag.Course = course;
            ViewBag.Modules = modules;
            return View("UtbildningCourse", root);
        }

        [HttpGet("{courseKey}/{moduleSlug}")]
        public async Task<IActionResult> Module(string courseKey, string moduleSlug)
        {
            var root = GetRoot();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var memberId = await GetCurrentMemberIdAsync();
            var isAdmin = await IsAdminAsync();
            ViewBag.IsLoggedIn = memberId > 0;
            ViewBag.IsAdmin = isAdmin;

            var course = await _courseService.GetCourseByKeyAsync(courseKey);
            var module = course == null ? null : (await _courseService.GetModulesAsync(course.Id))
                .FirstOrDefault(m => string.Equals(m.Slug, moduleSlug, StringComparison.OrdinalIgnoreCase));

            if (course == null || module == null || memberId == 0 ||
                !await CanAccessCourseAsync(course, memberId) ||
                (!module.IsPublished && !isAdmin))
            {
                ViewBag.Denied = true;
                ViewBag.Course = course;
                return View("UtbildningModule", root);
            }

            ViewBag.Course = course;
            ViewBag.Module = module;
            return View("UtbildningModule", root);
        }

        /// <summary>Raw self-contained lesson HTML for the iframe. Auth-gated; no chrome.</summary>
        [HttpGet("{courseKey}/{moduleSlug}/innehall")]
        public async Task<IActionResult> ModuleContent(string courseKey, string moduleSlug)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0) return Unauthorized();

            var isAdmin = await IsAdminAsync();
            var course = await _courseService.GetCourseByKeyAsync(courseKey);
            var module = course == null ? null : (await _courseService.GetModulesAsync(course.Id))
                .FirstOrDefault(m => string.Equals(m.Slug, moduleSlug, StringComparison.OrdinalIgnoreCase));

            if (course == null || module == null || string.IsNullOrEmpty(module.LessonPath) ||
                !await CanAccessCourseAsync(course, memberId) ||
                (!module.IsPublished && !isAdmin))
                return Forbid();

            // Resolve the file under wwwroot and ensure it can't escape the utbildning folder.
            var webRoot = _env.WebRootPath;
            var relative = module.LessonPath!.Replace('\\', '/').TrimStart('/');
            var fullPath = Path.GetFullPath(Path.Combine(webRoot, relative));
            var utbildningRoot = Path.GetFullPath(Path.Combine(webRoot, "utbildning"));
            if (!fullPath.StartsWith(utbildningRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(fullPath))
                return NotFound();

            var html = await System.IO.File.ReadAllTextAsync(fullPath);
            return Content(html, "text/html");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent? GetRoot()
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return null;
            return ctx.Content.GetAtRoot().FirstOrDefault();
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return 0;
            var member = _memberService.GetByEmail(currentMember.Email ?? "");
            return member?.Id ?? 0;
        }

        private async Task<bool> IsAdminAsync()
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;
            return (await _authorizationService.GetManagedRegions()).Any();
        }

        /// <summary>Material visibility: admins, or holders of the course's EducatorCertType.</summary>
        private async Task<bool> CanAccessCourseAsync(Course course, int memberId)
        {
            if (await IsAdminAsync()) return true;
            if (string.IsNullOrEmpty(course.EducatorCertType)) return false;
            return await _certificationService.HasActiveCertAsync(memberId, course.EducatorCertType);
        }
    }
}
