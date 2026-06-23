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
    /// Club/region-editable meeting agenda templates per meeting type. Editing is admin-only
    /// (site/club/regional admin for the owner); the typed-item catalog is readable to any logged-in
    /// board member so the agenda editor can offer the dropdown.
    /// </summary>
    public class BoardMeetingTemplateController : SurfaceController
    {
        private readonly BoardMeetingTemplateService _templates;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<BoardMeetingTemplateController> _logger;

        public BoardMeetingTemplateController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardMeetingTemplateService templates,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<BoardMeetingTemplateController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _templates = templates;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        /// <summary>The typed-item catalog + the meeting types (for the agenda editor + template editor).</summary>
        [HttpGet]
        public async Task<IActionResult> GetCatalog()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return Json(new { success = false, message = "Inte inloggad" });

            var items = BoardAgendaItemCatalog.Items.Select(i => new
            {
                i.Key, i.Heading, itemType = i.ItemType, electionRole = i.ElectionRole ?? "",
                electionCount = i.ElectionCount, electionSource = i.ElectionSource, i.Hint
            });
            var types = BoardMeetingTemplates.Types.Select(t => new { t.Key, t.Label });
            return Json(new { success = true, items, types });
        }

        /// <summary>The effective agenda (saved template or built-in default) for a meeting type.</summary>
        [HttpGet]
        public async Task<IActionResult> GetTemplate(int ownerType, int ownerId, string meetingTypeKey)
        {
            if (!await CanManage(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var items = _templates.GetEffectiveAgenda(ownerType, ownerId, meetingTypeKey);
            return Json(new
            {
                success = true,
                hasSaved = _templates.HasSavedTemplate(ownerType, ownerId, meetingTypeKey),
                label = BoardMeetingTemplates.GetLabel(meetingTypeKey),
                items = items.Select(i => new { itemType = i.ItemType, i.Heading, electionRole = i.ElectionRole ?? "", electionCount = i.ElectionCount, electionSource = i.ElectionSource })
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTemplate(int ownerType, int ownerId, string meetingTypeKey, [FromForm] string itemsJson)
        {
            if (!await CanManage(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            List<BoardTemplateItem> items;
            try
            {
                items = System.Text.Json.JsonSerializer.Deserialize<List<BoardTemplateItem>>(
                    itemsJson ?? "[]", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new();
            }
            catch
            {
                return Json(new { success = false, message = "Ogiltig mall" });
            }

            // Normalise: drop blank headings; keep only known item types.
            items = items.Where(i => !string.IsNullOrWhiteSpace(i.Heading)).ToList();
            foreach (var i in items)
            {
                if (i.ItemType != "note" && i.ItemType != "election") i.ItemType = "text";
                if (i.ItemType != "election")
                {
                    i.ElectionRole = null; i.ElectionCount = 1; i.ElectionSource = "attendees";
                }
                else
                {
                    if (i.ElectionCount < 1) i.ElectionCount = 1;
                    i.ElectionSource = i.ElectionSource == "members" ? "members" : "attendees";
                    if (i.ElectionRole != "chairman" && i.ElectionRole != "secretary" && i.ElectionRole != "adjuster")
                        i.ElectionRole = null;
                }
            }

            var meId = await GetCurrentMemberId();
            _templates.SaveTemplate(ownerType, ownerId, meetingTypeKey, items, meId);
            return Json(new { success = true, message = "Mall sparad" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetTemplate(int ownerType, int ownerId, string meetingTypeKey)
        {
            if (!await CanManage(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            _templates.ResetTemplate(ownerType, ownerId, meetingTypeKey);
            return Json(new { success = true, message = "Mall återställd till standard" });
        }

        // ---- auth ----------------------------------------------------------

        private async Task<bool> CanManage(int ownerType, int ownerId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (ownerType == DocumentOwnerType.Club)
                return await _auth.IsClubAdminForClub(ownerId);
            if (ownerType == DocumentOwnerType.Region)
            {
                var content = UmbracoContext.Content?.GetById(ownerId);
                var regionCode = content?.Value<string>("regionCode") ?? "";
                return !string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode);
            }
            return false;
        }

        private async Task<int> GetCurrentMemberId()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null) return 0;
            return _memberService.GetByEmail(currentMember.Email)?.Id ?? 0;
        }
    }
}
