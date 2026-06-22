using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Årshjul (annual cycle checklist) + Valberedning (nominations) endpoints. Same single access gate
    /// as the rest of board work: admin OR active board member of the owner. Phase 3.
    /// </summary>
    public class BoardGovernanceController : SurfaceController
    {
        private readonly BoardGovernanceService _gov;
        private readonly BoardRoleService _boardRoleService;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<BoardGovernanceController> _logger;

        public BoardGovernanceController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardGovernanceService gov,
            BoardRoleService boardRoleService,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<BoardGovernanceController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _gov = gov;
            _boardRoleService = boardRoleService;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        // ---- Årshjul --------------------------------------------------------

        [HttpGet]
        public async Task<IActionResult> GetYearWheel(int ownerType, int ownerId, int year)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var items = _gov.GetYearWheel(ownerType, ownerId, year);
            var years = _gov.GetWheelYears(ownerType, ownerId);
            if (!years.Contains(year)) years.Insert(0, year);
            return Json(new { success = true, data = items.Select(WheelDto), years });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetWheelDone(int itemId, bool done)
        {
            var ot = _gov.GetWheelItem(itemId);
            if (ot == null || !await CanAccessBoardWork(ot.OwnerType, ot.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.SetWheelDone(itemId, done) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWheelItem(int ownerType, int ownerId, int year, string title, string? targetDate)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(title))
                return Json(new { success = false, message = "Rubrik krävs" });
            var it = _gov.AddWheelItem(ownerType, ownerId, year, title, ParseDate(targetDate));
            return Json(new { success = true, data = new { it.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateWheelItem(int itemId, string title, string? targetDate)
        {
            var it = _gov.GetWheelItem(itemId);
            if (it == null || !await CanAccessBoardWork(it.OwnerType, it.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.UpdateWheelItem(itemId, title, ParseDate(targetDate)) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveWheelItem(int itemId)
        {
            var it = _gov.GetWheelItem(itemId);
            if (it == null || !await CanAccessBoardWork(it.OwnerType, it.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.RemoveWheelItem(itemId) });
        }

        // ---- Valberedning ---------------------------------------------------

        [HttpGet]
        public async Task<IActionResult> GetNominations(int ownerType, int ownerId, int year)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var noms = _gov.GetNominations(ownerType, ownerId, year);
            var posts = _gov.GetPostsUpForElection(ownerType, ownerId, year);
            var years = _gov.GetNominationYears(ownerType, ownerId);
            if (!years.Contains(year)) years.Insert(0, year);
            return Json(new
            {
                success = true,
                data = noms.Select(NominationDto),
                postsUpForElection = posts.Select(p => new { p.MemberName, title = p.DisplayTitle, termEndsDate = p.TermEndsDate?.ToString("yyyy-MM-dd") }),
                years
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddNomination(int ownerType, int ownerId, int year, string? postKey,
            string postLabel, string candidateName, int? candidateMemberId, string? status, string? notes)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(postLabel) || string.IsNullOrWhiteSpace(candidateName))
                return Json(new { success = false, message = "Post och namn krävs" });
            var meId = await GetCurrentMemberId();
            var n = _gov.AddNomination(ownerType, ownerId, year, postKey, postLabel, candidateName, candidateMemberId, status ?? "Föreslagen", notes, meId);
            return Json(new { success = true, data = new { n.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateNomination(int nominationId, string postLabel, string candidateName, string status, string? notes)
        {
            var n = _gov.GetNomination(nominationId);
            if (n == null || !await CanAccessBoardWork(n.OwnerType, n.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.UpdateNomination(nominationId, postLabel, candidateName, status, notes) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetNominationStatus(int nominationId, string status)
        {
            var n = _gov.GetNomination(nominationId);
            if (n == null || !await CanAccessBoardWork(n.OwnerType, n.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.SetNominationStatus(nominationId, status) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveNomination(int nominationId)
        {
            var n = _gov.GetNomination(nominationId);
            if (n == null || !await CanAccessBoardWork(n.OwnerType, n.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = _gov.RemoveNomination(nominationId) });
        }

        // ---- DTOs + auth ----------------------------------------------------

        private static object WheelDto(BoardYearWheelItem i) => new
        {
            i.Id, i.Title, i.Done, i.IsOverdue,
            targetDate = i.TargetDate?.ToString("yyyy-MM-dd")
        };

        private static object NominationDto(BoardNomination n) => new
        {
            n.Id, n.PostKey, n.PostLabel, n.CandidateName, n.CandidateMemberId, n.Status, n.Notes
        };

        private async Task<bool> CanAccessBoardWork(int ownerType, int ownerId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (ownerType == DocumentOwnerType.Club)
            {
                if (await _auth.IsClubAdminForClub(ownerId)) return true;
            }
            else if (ownerType == DocumentOwnerType.Region)
            {
                var content = UmbracoContext.Content?.GetById(ownerId);
                var regionCode = content?.Value<string>("regionCode") ?? "";
                if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                    return true;
            }
            var meId = await GetCurrentMemberId();
            return meId > 0 && _boardRoleService.IsBoardMemberOf(ownerType, ownerId, meId);
        }

        private async Task<int> GetCurrentMemberId()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null) return 0;
            return _memberService.GetByEmail(currentMember.Email)?.Id ?? 0;
        }

        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null;
    }
}
