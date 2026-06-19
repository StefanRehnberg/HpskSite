using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Microsoft.Extensions.Logging;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Site-admin CRUD for the Utbildning course catalog (the "Utbildning" admin tab).
    /// Catalog definition is a platform-wide concern → site-admin gated. See COURSE_SYSTEM.md.
    /// </summary>
    public class UtbildningAdminController : SurfaceController
    {
        private readonly CourseService _courseService;
        private readonly CourseTestService _testService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<UtbildningAdminController> _logger;

        public UtbildningAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            CourseService courseService,
            CourseTestService testService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<UtbildningAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _courseService = courseService;
            _testService = testService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        // ── Courses ──────────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetCourses()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var courses = await _courseService.GetAllCoursesAsync();
            var moduleCounts = new List<object>();
            foreach (var c in courses)
            {
                var modules = await _courseService.GetModulesAsync(c.Id);
                moduleCounts.Add(new
                {
                    c.Id, c.CourseKey, c.Title, c.TargetCertType, c.EducatorCertType,
                    educatorCertDisplay = c.EducatorCertDisplay,
                    c.IsPublished, c.SortOrder, c.HasTest,
                    moduleCount = modules.Count,
                    publishedModuleCount = modules.Count(m => m.IsPublished)
                });
            }
            return Json(new { success = true, courses = moduleCounts });
        }

        [HttpGet]
        public async Task<IActionResult> GetCourse(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var course = await _courseService.GetCourseAsync(id);
            if (course == null) return Json(new { success = false, message = "Kursen hittades inte." });

            var modules = await _courseService.GetModulesAsync(id);
            var prereqs = await _courseService.GetPrerequisitesAsync(id);
            return Json(new { success = true, course, modules, prerequisites = prereqs });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCourse(
            int id, string courseKey, string title, string? description,
            string? targetCertType, string? educatorCertType,
            bool isPublished, int sortOrder, bool hasTest, int? testPassMark, int? testMaxScore)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (string.IsNullOrWhiteSpace(courseKey) || string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Nyckel och titel krävs." });

            var course = new Course
            {
                Id = id,
                CourseKey = courseKey.Trim().ToLowerInvariant(),
                Title = title.Trim(),
                Description = description,
                TargetCertType = string.IsNullOrWhiteSpace(targetCertType) ? null : targetCertType,
                EducatorCertType = string.IsNullOrWhiteSpace(educatorCertType) ? null : educatorCertType,
                AccessRule = CourseAccessRules.Educator,
                IsPublished = isPublished,
                SortOrder = sortOrder,
                HasTest = hasTest,
                TestPassMark = testPassMark,
                TestMaxScore = testMaxScore
            };

            if (id > 0)
            {
                var (ok, msg) = await _courseService.UpdateCourseAsync(course);
                return Json(new { success = ok, message = msg, id });
            }
            else
            {
                var (ok, newId, msg) = await _courseService.CreateCourseAsync(course);
                return Json(new { success = ok, message = msg, id = newId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCourse(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var (ok, msg) = await _courseService.DeleteCourseAsync(id);
            return Json(new { success = ok, message = msg });
        }

        // ── Modules ──────────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveModule(
            int id, int courseId, string slug, string title,
            string? lessonPath, string? videoUrl, int sortOrder, bool isPublished)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Slug och titel krävs." });

            var module = new CourseModule
            {
                Id = id,
                CourseId = courseId,
                Slug = slug.Trim().ToLowerInvariant(),
                Title = title.Trim(),
                LessonPath = string.IsNullOrWhiteSpace(lessonPath) ? null : lessonPath.Trim().TrimStart('/'),
                VideoUrl = string.IsNullOrWhiteSpace(videoUrl) ? null : videoUrl.Trim(),
                SortOrder = sortOrder,
                IsPublished = isPublished
            };

            if (id > 0)
            {
                var (ok, msg) = await _courseService.UpdateModuleAsync(module);
                return Json(new { success = ok, message = msg, id });
            }
            else
            {
                var (ok, newId, msg) = await _courseService.CreateModuleAsync(module);
                return Json(new { success = ok, message = msg, id = newId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteModule(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var (ok, msg) = await _courseService.DeleteModuleAsync(id);
            return Json(new { success = ok, message = msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReorderModules(int courseId, [FromForm] List<int> moduleIds)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (moduleIds != null && moduleIds.Count > 0)
                await _courseService.ReorderModulesAsync(courseId, moduleIds);
            return Json(new { success = true });
        }

        // ── Prerequisites ────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddPrerequisite(int courseId, string prereqType, string prereqKey)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (string.IsNullOrWhiteSpace(prereqType) || string.IsNullOrWhiteSpace(prereqKey))
                return Json(new { success = false, message = "Typ och nyckel krävs." });

            var newId = await _courseService.AddPrerequisiteAsync(new CoursePrerequisite
            {
                CourseId = courseId,
                PrereqType = prereqType.Trim(),
                PrereqKey = prereqKey.Trim()
            });
            return Json(new { success = true, id = newId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePrerequisite(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var ok = await _courseService.DeletePrerequisiteAsync(id);
            return Json(new { success = ok });
        }

        // ── Test versions ────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetTestVersions(int courseId)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var versions = (await _testService.GetVersionsAsync(courseId)).Select(v => new
            {
                v.Id, v.VersionLabel, v.IsActive,
                questionCount = CourseTestService.ParseContent(v.ContentRef).Questions.Count,
                v.ContentRef
            });
            return Json(new { success = true, versions });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTestVersion(int id, int courseId, string versionLabel, bool isActive, string? contentRef)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (string.IsNullOrWhiteSpace(versionLabel))
                return Json(new { success = false, message = "Versionsnamn krävs." });

            // Validate the questions JSON if supplied.
            if (!string.IsNullOrWhiteSpace(contentRef))
            {
                var parsed = CourseTestService.ParseContent(contentRef);
                if (parsed.Questions.Count == 0)
                    return Json(new { success = false, message = "Frågor-JSON kunde inte tolkas (förväntar { \"questions\": [ { \"q\", \"options\", \"correct\" } ] })." });
            }

            var version = new CourseTestVersion
            {
                Id = id,
                CourseId = courseId,
                VersionLabel = versionLabel.Trim(),
                IsActive = isActive,
                ContentRef = string.IsNullOrWhiteSpace(contentRef) ? null : contentRef
            };

            if (id > 0)
            {
                var ok = await _testService.UpdateVersionAsync(version);
                return Json(new { success = ok, id, message = ok ? null : "Versionen hittades inte." });
            }
            var newId = await _testService.CreateVersionAsync(version);
            return Json(new { success = true, id = newId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTestVersion(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var ok = await _testService.DeleteVersionAsync(id);
            return Json(new { success = ok });
        }

        // ── Reviewers — site-admin grants FULL course-material access ───────────
        // For proofreaders/verifiers: a reviewer sees every course's material
        // (all modules, incl. unpublished) regardless of certifications.

        [HttpGet]
        public async Task<IActionResult> GetReviewers()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var reviewers = (await _courseService.GetReviewersAsync()).Select(r => new
            {
                r.Id, r.MemberId,
                name = string.IsNullOrWhiteSpace(r.MemberName) ? (_memberService.GetById(r.MemberId)?.Name ?? $"Medlem {r.MemberId}") : r.MemberName,
                grantedBy = r.GrantedByName,
                grantedAt = r.GrantedAt
            });
            return Json(new { success = true, reviewers });
        }

        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                return Json(new { success = true, members = new List<object>() });

            var q = query.Trim();
            var matches = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => (m.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                         || (m.Email ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .Select(m => new { memberId = m.Id, name = MemberDisplayName(m), email = m.Email })
                .ToList();
            return Json(new { success = true, members = matches });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddReviewer(int memberId)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var member = _memberService.GetById(memberId);
            if (member == null) return Json(new { success = false, message = "Medlemmen hittades inte." });

            var (byId, byName) = await CurrentMemberAsync();
            var (ok, msg) = await _courseService.AddReviewerAsync(memberId, MemberDisplayName(member), byId, byName);
            return Json(new { success = ok, message = msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveReviewer(int id)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });

            var ok = await _courseService.RemoveReviewerAsync(id);
            return Json(new { success = ok });
        }

        private static string MemberDisplayName(Umbraco.Cms.Core.Models.IMember m)
        {
            var first = m.HasProperty("firstName") ? m.GetValue<string>("firstName") : null;
            var last = m.HasProperty("lastName") ? m.GetValue<string>("lastName") : null;
            var full = $"{first} {last}".Trim();
            return string.IsNullOrWhiteSpace(full) ? m.Name : full;
        }

        private async Task<(int Id, string? Name)> CurrentMemberAsync()
        {
            var cur = await _memberManager.GetCurrentMemberAsync();
            if (cur == null) return (0, null);
            var m = _memberService.GetByEmail(cur.Email ?? "");
            return m == null ? (0, null) : (m.Id, MemberDisplayName(m));
        }
    }
}
