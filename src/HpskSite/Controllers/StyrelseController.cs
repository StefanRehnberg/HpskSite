using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Dedicated board-work page at /styrelse — the home for board members (and admins) to run
    /// meetings and manage the styrelse. Routed controller, no backoffice node needed (mirrors
    /// SightPictureController). The scope picker lists only boards the member sits on; admins can
    /// still reach a specific scope via the club/region panel link (?type=&id=).
    /// Also serves formal print views: /styrelse/dagordning/{id} and /styrelse/protokoll/{id}.
    /// </summary>
    [Route("styrelse")]
    public class StyrelseController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoleService;
        private readonly BoardMeetingService _meetingService;
        private readonly ClubService _clubService;

        public StyrelseController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            BoardRoleService boardRoleService,
            BoardMeetingService meetingService,
            ClubService clubService)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _boardRoleService = boardRoleService;
            _meetingService = meetingService;
            _clubService = clubService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int? type, int? id)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect("/login-&-register/?tab=login&RedirectUrl=/styrelse");

            var memberData = _memberService.GetByEmail(currentMember.Email);
            var memberId = memberData?.Id ?? 0;

            var model = new StyrelsePageModel { MemberName = currentMember.Name ?? "" };

            // Scope list = boards the member actually sits on (the request: only board memberships).
            var scopes = new List<StyrelseScope>();
            var seen = new HashSet<(int, int)>();
            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            foreach (var (ot, oid) in _boardRoleService.GetBoardMembershipsForMember(memberId))
            {
                if (!seen.Add((ot, oid))) continue;
                var s = await BuildScopeAsync(ot, oid, isSiteAdmin);
                if (s != null) scopes.Add(s);
            }
            model.Scopes = scopes.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList();

            // Selected scope: a query scope the member can access (board member OR admin) wins, even if
            // it's not a board membership (e.g. an admin arriving from the club/region panel link); in
            // that case add it to the list so the picker stays consistent.
            if (type.HasValue && id.HasValue && await CanAccessScopeAsync(type.Value, id.Value, isSiteAdmin))
            {
                model.Selected = model.Scopes.FirstOrDefault(s => s.OwnerType == type.Value && s.OwnerId == id.Value)
                                 ?? await BuildScopeAsync(type.Value, id.Value, isSiteAdmin);
                if (model.Selected != null && !model.Scopes.Any(s => s.OwnerType == model.Selected.OwnerType && s.OwnerId == model.Selected.OwnerId))
                    model.Scopes.Insert(0, model.Selected);
            }
            model.Selected ??= model.Scopes.FirstOrDefault();

            ViewData["StyrelseData"] = model;
            return View("Styrelse", rootNode);
        }

        // ---- Formal print views --------------------------------------------

        [HttpGet("dagordning/{id:int}")]
        public Task<IActionResult> Dagordning(int id) => PrintAsync(id, "dagordning");

        [HttpGet("protokoll/{id:int}")]
        public Task<IActionResult> Protokoll(int id) => PrintAsync(id, "protokoll");

        private async Task<IActionResult> PrintAsync(int meetingId, string mode)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !meeting.IsActive) return NotFound();

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-&-register/?tab=login&RedirectUrl=/styrelse/{mode}/{meetingId}");

            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            if (!await CanAccessScopeAsync(meeting.OwnerType, meeting.OwnerId, isSiteAdmin))
                return Forbid();

            var attendees = _meetingService.GetAttendees(meetingId);
            var pm = new StyrelsePrintModel
            {
                Mode = mode,
                Meeting = meeting,
                Agenda = _meetingService.GetAgenda(meetingId),
                Attendees = attendees,
                Links = _meetingService.GetLinksForMeeting(meetingId),
                OrgName = ResolveOrgName(meeting.OwnerType, meeting.OwnerId),
                ChairmanName = attendees.FirstOrDefault(a => a.IsChairman)?.MemberName,
                SecretaryName = attendees.FirstOrDefault(a => a.IsSecretary)?.MemberName,
                AdjusterName = meeting.AdjusterMemberId.HasValue
                    ? attendees.FirstOrDefault(a => a.MemberId == meeting.AdjusterMemberId.Value)?.MemberName
                    : attendees.FirstOrDefault(a => a.IsAdjuster)?.MemberName
            };
            return View("StyrelseProtokoll", pm);
        }

        // ---- Helpers --------------------------------------------------------

        private async Task<StyrelseScope?> BuildScopeAsync(int ownerType, int ownerId, bool isSiteAdmin)
        {
            if (ownerType == DocumentOwnerType.Club)
            {
                var name = _clubService.GetClubNameById(ownerId);
                if (string.IsNullOrEmpty(name)) return null;
                return new StyrelseScope
                {
                    OwnerType = ownerType, OwnerId = ownerId, Name = name, Kind = "Klubb",
                    CanManageRoles = isSiteAdmin || await _auth.IsClubAdminForClub(ownerId)
                };
            }
            if (ownerType == DocumentOwnerType.Region)
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var node = ctx.Content.GetById(ownerId);
                if (node == null) return null;
                var regionCode = node.Value<string>("regionCode") ?? "";
                return new StyrelseScope
                {
                    OwnerType = ownerType, OwnerId = ownerId,
                    Name = node.Value<string>("regionName") ?? node.Name ?? "Krets", Kind = "Krets",
                    CanManageRoles = isSiteAdmin || (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                };
            }
            return null;
        }

        private async Task<bool> CanAccessScopeAsync(int ownerType, int ownerId, bool isSiteAdmin)
        {
            if (isSiteAdmin) return true;

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            var memberId = currentMember?.Email != null ? (_memberService.GetByEmail(currentMember.Email)?.Id ?? 0) : 0;
            if (memberId > 0 && _boardRoleService.IsBoardMemberOf(ownerType, ownerId, memberId)) return true;

            if (ownerType == DocumentOwnerType.Club)
                return await _auth.IsClubAdminForClub(ownerId);
            if (ownerType == DocumentOwnerType.Region)
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return false;
                var code = ctx.Content.GetById(ownerId)?.Value<string>("regionCode") ?? "";
                return !string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code);
            }
            return false;
        }

        private string ResolveOrgName(int ownerType, int ownerId)
        {
            if (ownerType == DocumentOwnerType.Club)
                return _clubService.GetClubNameById(ownerId) ?? "";
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var node = ctx.Content.GetById(ownerId);
                return node?.Value<string>("regionName") ?? node?.Name ?? "";
            }
            return "";
        }
    }
}
