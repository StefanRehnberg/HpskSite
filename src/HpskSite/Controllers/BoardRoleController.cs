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
    public class BoardRoleController : SurfaceController
    {
        private readonly BoardRoleService _boardRoleService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<BoardRoleController> _logger;

        public BoardRoleController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardRoleService boardRoleService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<BoardRoleController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _boardRoleService = boardRoleService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        /// <summary>
        /// Get all board members/roles for a club or region. Requires login.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetBoardMembers(int ownerType, int ownerId, bool boardOnly = false)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad" });

            var roles = _boardRoleService.GetBoardMembers(ownerType, ownerId, boardOnly);

            var data = roles.Select(r => new
            {
                r.Id,
                r.MemberId,
                r.MemberName,
                title = r.DisplayTitle,
                r.RoleKey,
                r.CustomTitle,
                r.IsBoardMember,
                r.SortOrder
            });

            return Json(new { success = true, data });
        }

        /// <summary>
        /// Get board roles for all members in a club (for member directory column). Requires login.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetBoardRolesForClubMembers(int clubId)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad" });

            var rolesMap = _boardRoleService.GetBoardRolesForClubMembers(clubId);

            // Convert to serializable format: { memberId: [{ title, isBoardMember }] }
            var data = rolesMap.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value.Select(r => new { r.Title, r.IsBoardMember })
            );

            return Json(new { success = true, data });
        }

        /// <summary>
        /// Get the list of predefined roles.
        /// </summary>
        [HttpGet]
        public IActionResult GetAvailableRoles()
        {
            var roles = BoardRoleDefinitions.AllRoles.Select(r => new
            {
                key = r.Key,
                label = r.Label,
                defaultSort = r.DefaultSort,
                isBoardMember = r.IsBoardMember
            }).ToList();

            return Json(new { success = true, data = roles });
        }

        /// <summary>
        /// Search members for board role assignment.
        /// For clubs: searches club members. For regions: searches all members.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query, int ownerType, int ownerId)
        {
            if (!await CanManageBoardRoles(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return Json(new { success = true, data = Array.Empty<object>() });

            var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved)
                .ToList();

            // For clubs, filter to club members only
            if (ownerType == DocumentOwnerType.Club)
            {
                var clubIdStr = ownerId.ToString();
                allMembers = allMembers.Where(m =>
                    m.GetValue("primaryClubId")?.ToString() == clubIdStr ||
                    (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                        .Select(s => s.Trim())
                        .Contains(clubIdStr) ?? false))
                    .ToList();
            }

            var results = allMembers
                .Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            (m.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(m => m.Name)
                .Take(20)
                .Select(m => new
                {
                    id = m.Id,
                    name = m.Name,
                    email = m.Email
                });

            return Json(new { success = true, data = results });
        }

        /// <summary>
        /// Assign a board role to a member.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignBoardRole(int ownerType, int ownerId, int memberId,
            string roleKey, string? customTitle, bool isBoardMember)
        {
            try
            {
                if (!await CanManageBoardRoles(ownerType, ownerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                if (string.IsNullOrWhiteSpace(roleKey))
                    return Json(new { success = false, message = "Roll måste anges" });

                if (roleKey == "Custom" && string.IsNullOrWhiteSpace(customTitle))
                    return Json(new { success = false, message = "Titel måste anges för anpassad roll" });

                // Verify member exists
                var member = _memberService.GetById(memberId);
                if (member == null)
                    return Json(new { success = false, message = "Medlemmen hittades inte" });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var currentMemberData = currentMember != null ? _memberService.GetByEmail(currentMember.Email ?? "") : null;
                var assignedBy = currentMemberData?.Id ?? 0;

                var role = _boardRoleService.AssignBoardRole(ownerType, ownerId, memberId, roleKey,
                    customTitle, isBoardMember, assignedBy);

                _logger.LogInformation("Board role {RoleKey} assigned to member {MemberId} for {OwnerType}/{OwnerId}",
                    roleKey, memberId, ownerType, ownerId);

                return Json(new { success = true, message = "Roll tilldelad", data = new { role.Id } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning board role");
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        /// <summary>
        /// Remove a board role (soft delete).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveBoardRole(int boardRoleId)
        {
            try
            {
                var role = _boardRoleService.GetById(boardRoleId);
                if (role == null)
                    return Json(new { success = false, message = "Rollen hittades inte" });

                if (!await CanManageBoardRoles(role.OwnerType, role.OwnerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                _boardRoleService.RemoveBoardRole(boardRoleId);

                _logger.LogInformation("Board role {Id} removed from {OwnerType}/{OwnerId}",
                    boardRoleId, role.OwnerType, role.OwnerId);

                return Json(new { success = true, message = "Roll borttagen" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing board role {Id}", boardRoleId);
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        /// <summary>
        /// Update an existing board role.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateBoardRole(int boardRoleId, string roleKey,
            string? customTitle, bool isBoardMember, int sortOrder)
        {
            try
            {
                var role = _boardRoleService.GetById(boardRoleId);
                if (role == null)
                    return Json(new { success = false, message = "Rollen hittades inte" });

                if (!await CanManageBoardRoles(role.OwnerType, role.OwnerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                _boardRoleService.UpdateBoardRole(boardRoleId, roleKey, customTitle, isBoardMember, sortOrder);

                return Json(new { success = true, message = "Roll uppdaterad" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating board role {Id}", boardRoleId);
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        /// <summary>
        /// Check if the current user can manage board roles for a given owner.
        /// </summary>
        private async Task<bool> CanManageBoardRoles(int ownerType, int ownerId)
        {
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            if (isSiteAdmin) return true;

            if (ownerType == DocumentOwnerType.Club)
            {
                return await _authorizationService.IsClubAdminForClub(ownerId);
            }

            if (ownerType == DocumentOwnerType.Region)
            {
                var publishedContent = UmbracoContext.Content?.GetById(ownerId);
                if (publishedContent == null) return false;
                var regionCode = publishedContent.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode)) return false;
                return await _authorizationService.IsRegionalAdminForRegion(regionCode);
            }

            return false;
        }
    }
}
