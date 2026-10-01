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

            var data = roles.Select(ToDto);

            return Json(new { success = true, data });
        }

        /// <summary>
        /// Active roles whose mandate ends within the given window (default 12 months). Requires login.
        /// Drives the "Mandat som löper ut" / valberedning panel.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetExpiringTerms(int ownerType, int ownerId, int withinDays = 365)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad" });

            var before = DateTime.Today.AddDays(withinDays);
            var roles = _boardRoleService.GetExpiringRoles(ownerType, ownerId, before);

            return Json(new { success = true, data = roles.Select(ToDto) });
        }

        /// <summary>
        /// Serialize a role for the management UI, including term fields (ISO date strings for Flatpickr).
        /// </summary>
        private static object ToDto(BoardRole r) => new
        {
            r.Id,
            r.MemberId,
            r.MemberName,
            title = r.DisplayTitle,
            r.RoleKey,
            r.CustomTitle,
            r.IsBoardMember,
            r.SortOrder,
            electedDate = r.ElectedDate?.ToString("yyyy-MM-dd"),
            termEndsDate = r.TermEndsDate?.ToString("yyyy-MM-dd"),
            r.TermYears,
            r.IsTermExpired,
            r.DaysLeftInTerm
        };

        /// <summary>
        /// Parse a Flatpickr Y-m-d date string; returns null for empty/invalid input.
        /// </summary>
        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d : (DateTime?)null;

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
        public IActionResult GetAvailableRoles(int? ownerType = null)
        {
            // ⚠️ Filtered by owner type so a club is never OFFERED a krets assignment. The write
            // paths refuse it as well — the picker is a convenience, not the gate. Without
            // ownerType (older callers) every role is listed, as before.
            var roles = BoardRoleDefinitions.AllRoles
                .Where(r => ownerType == null || BoardRoleDefinitions.AppliesTo(r.Key, ownerType.Value))
                .Select(r => new
            {
                key = r.Key,
                label = r.Label,
                defaultSort = r.DefaultSort,
                isBoardMember = r.IsBoardMember,
                isKretsUppdrag = BoardRoleDefinitions.IsKretsUppdrag(r.Key)
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
            string roleKey, string? customTitle, bool isBoardMember,
            string? electedDate = null, string? termEndsDate = null, int? termYears = null)
        {
            try
            {
                if (!await CanManageBoardRoles(ownerType, ownerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                if (string.IsNullOrWhiteSpace(roleKey))
                    return Json(new { success = false, message = "Roll måste anges" });

                if (roleKey == "Custom" && string.IsNullOrWhiteSpace(customTitle))
                    return Json(new { success = false, message = "Titel måste anges för anpassad roll" });

                var refusal = RoleRefusal(roleKey, ownerType);
                if (refusal != null)
                    return Json(new { success = false, message = refusal });

                // A krets assignment is never a board seat — it must not count toward quorum or be
                // seeded into meeting attendance, whatever the client posted.
                if (BoardRoleDefinitions.IsKretsUppdrag(roleKey)) isBoardMember = false;

                // Verify member exists
                var member = _memberService.GetById(memberId);
                if (member == null)
                    return Json(new { success = false, message = "Medlemmen hittades inte" });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var currentMemberData = currentMember != null ? _memberService.GetByEmail(currentMember.Email ?? "") : null;
                var assignedBy = currentMemberData?.Id ?? 0;

                var role = _boardRoleService.AssignBoardRole(ownerType, ownerId, memberId, roleKey,
                    customTitle, isBoardMember, assignedBy,
                    ParseDate(electedDate), ParseDate(termEndsDate), termYears);

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
            string? customTitle, bool isBoardMember, int sortOrder,
            string? electedDate = null, string? termEndsDate = null, int? termYears = null)
        {
            try
            {
                var role = _boardRoleService.GetById(boardRoleId);
                if (role == null)
                    return Json(new { success = false, message = "Rollen hittades inte" });

                if (!await CanManageBoardRoles(role.OwnerType, role.OwnerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                // An unchanged key is let through even if it is no longer in AllRoles — a legacy
                // row must stay editable (dates, sort order). A krets assignment on a club is
                // refused regardless.
                var refusal = roleKey == role.RoleKey && !BoardRoleDefinitions.IsKretsUppdrag(roleKey)
                    ? null
                    : RoleRefusal(roleKey, role.OwnerType);
                if (refusal != null)
                    return Json(new { success = false, message = refusal });
                if (BoardRoleDefinitions.IsKretsUppdrag(roleKey)) isBoardMember = false;

                _boardRoleService.UpdateBoardRole(boardRoleId, roleKey, customTitle, isBoardMember, sortOrder,
                    ParseDate(electedDate), ParseDate(termEndsDate), termYears);

                return Json(new { success = true, message = "Roll uppdaterad" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating board role {Id}", boardRoleId);
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        /// <summary>
        /// Why this role may not be given to this owner type, or null when it may. The server's own
        /// rule, not the picker's — a hand-posted request must be refused just the same.
        /// </summary>
        private static string? RoleRefusal(string roleKey, int ownerType)
        {
            if (BoardRoleDefinitions.AppliesTo(roleKey, ownerType)) return null;
            if (BoardRoleDefinitions.IsKretsUppdrag(roleKey))
                return $"{BoardRoleDefinitions.GetLabel(roleKey)} är ett uppdrag i kretsen och kan inte ges i en klubbstyrelse.";
            return "Okänd roll.";
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
