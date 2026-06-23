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
        private readonly BoardGovernanceService _gov;
        private readonly ClubService _clubService;

        public StyrelseController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            BoardRoleService boardRoleService,
            BoardMeetingService meetingService,
            BoardGovernanceService gov,
            ClubService clubService)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _boardRoleService = boardRoleService;
            _meetingService = meetingService;
            _gov = gov;
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

            // Scope list = boards the member sits on, plus any valberedning the member is part of
            // (valberedning members get scoped access to the Valberedning tab only).
            var scopes = new List<StyrelseScope>();
            var seen = new HashSet<(int, int)>();
            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            var candidates = _boardRoleService.GetBoardMembershipsForMember(memberId)
                .Concat(_boardRoleService.GetValberedningMembershipsForMember(memberId));
            foreach (var (ot, oid) in candidates)
            {
                if (!seen.Add((ot, oid))) continue;
                var s = await BuildScopeAsync(ot, oid, isSiteAdmin);
                if (s == null) continue;
                // Valberedning-only when the member's access to this scope is purely via the valberedning
                // (not an admin and not a board member).
                s.ValberedningOnly = !s.CanManageRoles && !_boardRoleService.IsBoardMemberOf(ot, oid, memberId);
                scopes.Add(s);
            }
            model.Scopes = scopes.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList();

            // Selected scope: a query scope the member can access (full OR valberedning) wins, even if
            // it's not in the list yet (e.g. an admin arriving from the club/region panel link); in
            // that case add it to the list so the picker stays consistent.
            if (type.HasValue && id.HasValue && await CanAccessValberedningAsync(type.Value, id.Value, isSiteAdmin))
            {
                model.Selected = model.Scopes.FirstOrDefault(s => s.OwnerType == type.Value && s.OwnerId == id.Value);
                if (model.Selected == null)
                {
                    model.Selected = await BuildScopeAsync(type.Value, id.Value, isSiteAdmin);
                    if (model.Selected != null)
                    {
                        model.Selected.ValberedningOnly = !model.Selected.CanManageRoles
                            && !_boardRoleService.IsBoardMemberOf(type.Value, id.Value, memberId);
                        model.Scopes.Insert(0, model.Selected);
                    }
                }
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
            var agenda = _meetingService.GetAgenda(meetingId);

            // Resolve names for elected persons (may be non-attendees in "members"-source elections).
            var memberNames = attendees.Where(a => !string.IsNullOrEmpty(a.MemberName))
                .GroupBy(a => a.MemberId).ToDictionary(g => g.Key, g => g.First().MemberName!);
            var electedIds = agenda.Where(a => !string.IsNullOrEmpty(a.ElectedMemberIds))
                .SelectMany(a => a.ElectedMemberIds!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct();
            foreach (var id in electedIds.Where(id => !memberNames.ContainsKey(id)))
            {
                var mem = _memberService.GetById(id);
                if (mem == null) continue;
                var nm = $"{mem.GetValue<string>("firstName")} {mem.GetValue<string>("lastName")}".Trim();
                memberNames[id] = string.IsNullOrEmpty(nm) ? mem.Name : nm;
            }

            var pm = new StyrelsePrintModel
            {
                Mode = mode,
                Meeting = meeting,
                Agenda = agenda,
                Attendees = attendees,
                MemberNames = memberNames,
                Links = _meetingService.GetLinksForMeeting(meetingId),
                OrgName = ResolveOrgName(meeting.OwnerType, meeting.OwnerId),
                ChairmanName = attendees.FirstOrDefault(a => a.IsChairman)?.MemberName,
                SecretaryName = attendees.FirstOrDefault(a => a.IsSecretary)?.MemberName,
                // Justerare are the flagged attendees (0–2); fall back to the legacy single id for old data.
                AdjusterNames = (attendees.Any(a => a.IsAdjuster)
                        ? attendees.Where(a => a.IsAdjuster)
                        : attendees.Where(a => meeting.AdjusterMemberId.HasValue && a.MemberId == meeting.AdjusterMemberId.Value))
                    .Select(a => a.MemberName ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
            };
            return View("StyrelseProtokoll", pm);
        }

        [HttpGet("valforslag")]
        public async Task<IActionResult> Valforslag(int type, int id, int year)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-&-register/?tab=login&RedirectUrl=/styrelse/valforslag?type={type}%26id={id}%26year={year}");
            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            if (!await CanAccessValberedningAsync(type, id, isSiteAdmin)) return Forbid();

            return View("StyrelseValforslag", new StyrelseValforslagModel
            {
                OrgName = ResolveOrgName(type, id),
                Year = year,
                Nominations = _gov.GetNominations(type, id, year)
            });
        }

        /// <summary>
        /// Chromeless on-site/phone justering page (opened from the QR code or the emailed link).
        /// Login required; the page reads state + approves by token via BoardMeeting endpoints.
        /// </summary>
        [HttpGet("justera")]
        public async Task<IActionResult> Justera(string t)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
            {
                var back = Uri.EscapeDataString($"/styrelse/justera?t={t}");
                return Redirect($"/login-&-register/?tab=login&RedirectUrl={back}");
            }
            return View("StyrelseJustera");
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

        /// <summary>
        /// Full board access OR an active valberedning role for the owner. Used to let the page load
        /// and the valförslag print open for valberedning members (the UI then limits them to the
        /// Valberedning tab). Note: protokoll/dagordning prints stay on the stricter CanAccessScopeAsync.
        /// </summary>
        private async Task<bool> CanAccessValberedningAsync(int ownerType, int ownerId, bool isSiteAdmin)
        {
            if (await CanAccessScopeAsync(ownerType, ownerId, isSiteAdmin)) return true;
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            var memberId = currentMember?.Email != null ? (_memberService.GetByEmail(currentMember.Email)?.Id ?? 0) : 0;
            return memberId > 0 && _boardRoleService.IsValberedningOf(ownerType, ownerId, memberId);
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
