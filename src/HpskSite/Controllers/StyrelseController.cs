using Microsoft.AspNetCore.Mvc;
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
    /// SightPictureController). Accessible to anyone on a board OR any admin; the page auto-opens
    /// the scope you sit on (a picker if several). See BOARD_WORK_PHASE2_MEETINGS.md.
    /// </summary>
    [Route("styrelse")]
    public class StyrelseController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoleService;
        private readonly ClubService _clubService;

        public StyrelseController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            BoardRoleService boardRoleService,
            ClubService clubService)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _boardRoleService = boardRoleService;
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

            // ---- Gather accessible scopes: board memberships ∪ managed clubs ∪ managed regions ----
            var seen = new HashSet<(int, int)>();
            var scopes = new List<StyrelseScope>();
            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();

            void AddClub(int clubId, bool canManage)
            {
                if (clubId <= 0 || !seen.Add((DocumentOwnerType.Club, clubId))) return;
                var name = _clubService.GetClubNameById(clubId);
                if (string.IsNullOrEmpty(name)) return;
                scopes.Add(new StyrelseScope { OwnerType = DocumentOwnerType.Club, OwnerId = clubId, Name = name, Kind = "Klubb", CanManageRoles = canManage });
            }

            // Board memberships
            foreach (var (ot, oid) in _boardRoleService.GetBoardMembershipsForMember(memberId))
            {
                if (ot == DocumentOwnerType.Club)
                    AddClub(oid, isSiteAdmin || await _auth.IsClubAdminForClub(oid));
                else if (ot == DocumentOwnerType.Region && seen.Add((DocumentOwnerType.Region, oid)))
                {
                    var node = ctx.Content.GetById(oid);
                    if (node != null)
                    {
                        var regionCode = node.Value<string>("regionCode") ?? "";
                        var canManage = isSiteAdmin || (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode));
                        scopes.Add(new StyrelseScope { OwnerType = DocumentOwnerType.Region, OwnerId = oid, Name = node.Value<string>("regionName") ?? node.Name ?? "Krets", Kind = "Krets", CanManageRoles = canManage });
                    }
                }
            }

            // Managed clubs (admins who may not be on the board)
            foreach (var clubId in await _auth.GetManagedClubIds())
                AddClub(clubId, true);

            // Managed regions (region codes → region nodes)
            var managedRegions = await _auth.GetManagedRegions();
            if (managedRegions.Count > 0)
            {
                var regionPages = rootNode.Children?.Where(c => c.ContentType.Alias == "regionalPage").ToList()
                                  ?? new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();
                foreach (var code in managedRegions)
                {
                    var node = regionPages.FirstOrDefault(r => string.Equals(r.Value<string>("regionCode") ?? "", code, StringComparison.OrdinalIgnoreCase));
                    if (node != null && seen.Add((DocumentOwnerType.Region, node.Id)))
                        scopes.Add(new StyrelseScope { OwnerType = DocumentOwnerType.Region, OwnerId = node.Id, Name = node.Value<string>("regionName") ?? node.Name ?? "Krets", Kind = "Krets", CanManageRoles = true });
                }
            }

            model.Scopes = scopes.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToList();

            // ---- Selected scope ----
            if (type.HasValue && id.HasValue)
                model.Selected = model.Scopes.FirstOrDefault(s => s.OwnerType == type.Value && s.OwnerId == id.Value);
            model.Selected ??= model.Scopes.FirstOrDefault();

            ViewData["StyrelseData"] = model;
            return View("Styrelse", rootNode);
        }
    }
}
