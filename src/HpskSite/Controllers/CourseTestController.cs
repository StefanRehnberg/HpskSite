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
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Course-test actions: participant online submission, and trainer-side management
    /// (enable/revoke test access, record paper results, per-group status). See COURSE_SYSTEM.md.
    /// </summary>
    public class CourseTestController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly CourseTestService _testService;
        private readonly CourseService _courseService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly TrainingGroupService _trainingGroupService;
        private readonly ILogger<CourseTestController> _logger;

        public CourseTestController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            CourseTestService testService,
            CourseService courseService,
            AdminAuthorizationService authorizationService,
            TrainingGroupService trainingGroupService,
            ILogger<CourseTestController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _testService = testService;
            _courseService = courseService;
            _authorizationService = authorizationService;
            _trainingGroupService = trainingGroupService;
            _logger = logger;
        }

        // ── Participant ────────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitOnline(int courseId, int versionId, [FromForm] List<int> answers)
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (ok, result, msg) = await _testService.SubmitOnlineAsync(memberId, courseId, versionId, answers ?? new List<int>());
            if (!ok) return Json(new { success = false, message = msg });
            return Json(new { success = true, passed = result!.Passed, score = result.Score, max = result.MaxScore });
        }

        // ── Trainer management ───────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnableAccess(int memberId, int courseId)
        {
            var actingId = await GetCurrentMemberIdAsync();
            if (actingId == 0 || !await CanManageMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet för den här deltagaren." });

            var (ok, msg) = await _testService.EnableAccessAsync(memberId, courseId, actingId);
            return Json(new { success = ok, message = msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeAccess(int memberId, int courseId)
        {
            if (await GetCurrentMemberIdAsync() == 0 || !await CanManageMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var ok = await _testService.RevokeAccessAsync(memberId, courseId);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordPaperResult(int memberId, int courseId, int score, int maxScore, bool passed, string? notes)
        {
            var actingId = await GetCurrentMemberIdAsync();
            if (actingId == 0 || !await CanManageMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet." });

            var (ok, msg) = await _testService.RecordPaperResultAsync(memberId, courseId, score, maxScore, passed, actingId, notes);
            return Json(new { success = ok, message = msg });
        }

        /// <summary>Per-member test status for a training group the acting user manages.</summary>
        [HttpGet]
        public async Task<IActionResult> GetGroupTestStatus(int courseId, int groupId)
        {
            if (!await _trainingGroupService.CanManageTrainingGroup(groupId))
                return Json(new { success = false, message = "Du har inte behörighet för den här gruppen." });

            var course = await _courseService.GetCourseAsync(courseId);
            if (course == null) return Json(new { success = false, message = "Kursen hittades inte." });

            var rows = new List<object>();
            foreach (var mid in _trainingGroupService.GetGroupMemberIds(groupId))
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;

                var (eligible, missing) = await _testService.CheckEligibilityAsync(mid, courseId);
                var hasAccess = await _testService.GetActiveAccessAsync(mid, courseId) != null;
                var results = await _testService.GetResultsForMemberAsync(mid, courseId);
                var last = results.FirstOrDefault();

                rows.Add(new
                {
                    memberId = mid,
                    name = member.Name,
                    eligible,
                    missing,
                    hasAccess,
                    passed = results.Any(r => r.Passed),
                    lastResult = last == null ? null : new { last.Mode, last.Score, last.MaxScore, last.Passed, last.TakenAt }
                });
            }
            return Json(new { success = true, course = new { course.Id, course.Title, course.HasTest, course.TestPassMark }, members = rows });
        }

        /// <summary>Training groups the acting user may manage (their trainer groups + managed-club groups).</summary>
        [HttpGet]
        public async Task<IActionResult> GetManageableGroups()
        {
            var memberId = await GetCurrentMemberIdAsync();
            if (memberId == 0) return Json(new { success = false, message = "Inte inloggad." });

            var groups = new Dictionary<int, object>();
            foreach (var g in _trainingGroupService.GetTrainingGroupsForMember(memberId))
                groups[g.Id] = new { id = g.Id, name = g.Name };

            foreach (var clubId in await _authorizationService.GetManagedClubIds())
                foreach (var g in _trainingGroupService.GetTrainingGroupsForClub(clubId))
                    groups[g.Id] = new { id = g.Id, name = g.Name };

            return Json(new { success = true, groups = groups.Values });
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var member = _memberService.GetByEmail(current.Email ?? "");
            return member?.Id ?? 0;
        }

        private async Task<bool> CanManageMemberAsync(int targetMemberId)
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;

            var actingId = await GetCurrentMemberIdAsync();
            if (actingId != 0 && await _trainingGroupService.IsTrainerForMember(actingId, targetMemberId)) return true;

            if (await _authorizationService.IsSkjutledareForMember(targetMemberId)) return true;

            // Club admin (incl. regional admin) for the member's primary club.
            var member = _memberService.GetById(targetMemberId);
            var pc = member?.GetValue("primaryClubId")?.ToString();
            if (!int.TryParse(pc, out var cid)) return false;
            return await _authorizationService.IsClubAdminForClub(cid);
        }
    }
}
