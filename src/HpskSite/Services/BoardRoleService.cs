using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    public class BoardRoleService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;

        public BoardRoleService(IScopeProvider scopeProvider, IMemberService memberService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
        }

        /// <summary>
        /// Get all active roles for a club or region. When boardOnly=true, filters to IsBoardMember=1.
        /// </summary>
        public List<BoardRole> GetBoardMembers(int ownerType, int ownerId, bool boardOnly = false)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var sql = boardOnly
                ? "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 AND IsBoardMember = 1 ORDER BY SortOrder, RoleKey"
                : "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 ORDER BY SortOrder, RoleKey";

            var roles = db.Fetch<BoardRole>(sql, ownerType, ownerId);

            // Resolve member names
            foreach (var role in roles)
            {
                var member = _memberService.GetById(role.MemberId);
                if (member != null)
                {
                    var first = member.GetValue<string>("firstName") ?? "";
                    var last = member.GetValue<string>("lastName") ?? "";
                    role.MemberName = $"{first} {last}".Trim();
                    if (string.IsNullOrEmpty(role.MemberName))
                        role.MemberName = member.Name;
                }
            }

            return roles;
        }

        /// <summary>
        /// Batch lookup of board roles for all members in a club.
        /// Returns memberId -> list of display titles (for the member directory column).
        /// </summary>
        public Dictionary<int, List<BoardRoleInfo>> GetBoardRolesForClubMembers(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var roles = db.Fetch<BoardRole>(
                "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 ORDER BY SortOrder",
                DocumentOwnerType.Club, clubId);

            var result = new Dictionary<int, List<BoardRoleInfo>>();
            foreach (var role in roles)
            {
                if (!result.ContainsKey(role.MemberId))
                    result[role.MemberId] = new List<BoardRoleInfo>();

                result[role.MemberId].Add(new BoardRoleInfo
                {
                    Title = role.DisplayTitle,
                    IsBoardMember = role.IsBoardMember
                });
            }

            return result;
        }

        /// <summary>
        /// Assign a board role. If the same role was previously soft-deleted, reactivate it.
        /// </summary>
        public BoardRole AssignBoardRole(int ownerType, int ownerId, int memberId, string roleKey,
            string? customTitle, bool isBoardMember, int assignedByMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            // Check for existing soft-deleted record
            var existing = db.FirstOrDefault<BoardRole>(
                "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2 AND RoleKey = @3 AND (@4 IS NULL AND CustomTitle IS NULL OR CustomTitle = @4)",
                ownerType, ownerId, memberId, roleKey, customTitle);

            if (existing != null)
            {
                existing.IsActive = true;
                existing.IsBoardMember = isBoardMember;
                existing.CustomTitle = customTitle;
                existing.SortOrder = BoardRoleDefinitions.GetDefaultSort(roleKey);
                existing.AssignedDate = DateTime.UtcNow;
                existing.AssignedByMemberId = assignedByMemberId;
                db.Update(existing);
                return existing;
            }

            var role = new BoardRole
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                MemberId = memberId,
                RoleKey = roleKey,
                CustomTitle = roleKey == "Custom" ? customTitle : null,
                IsBoardMember = isBoardMember,
                SortOrder = BoardRoleDefinitions.GetDefaultSort(roleKey),
                AssignedDate = DateTime.UtcNow,
                AssignedByMemberId = assignedByMemberId,
                IsActive = true
            };

            db.Insert(role);
            return role;
        }

        /// <summary>
        /// Soft-delete a board role.
        /// </summary>
        public bool RemoveBoardRole(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var role = db.SingleOrDefaultById<BoardRole>(id);
            if (role == null) return false;

            role.IsActive = false;
            db.Update(role);
            return true;
        }

        /// <summary>
        /// Update an existing board role's key, custom title, board member flag, or sort order.
        /// </summary>
        public bool UpdateBoardRole(int id, string roleKey, string? customTitle, bool isBoardMember, int sortOrder)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var role = db.SingleOrDefaultById<BoardRole>(id);
            if (role == null) return false;

            role.RoleKey = roleKey;
            role.CustomTitle = roleKey == "Custom" ? customTitle : null;
            role.IsBoardMember = isBoardMember;
            role.SortOrder = sortOrder;
            db.Update(role);
            return true;
        }

        /// <summary>
        /// Club ids where the given member is an active board member (Styrelse). Reverse of
        /// <see cref="GetBoardMembers"/> — used to scope märke sign-off authority to a person.
        /// </summary>
        public List<int> GetClubIdsWhereBoardMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Fetch<int>(
                "SELECT OwnerId FROM BoardRoles WHERE OwnerType = @0 AND MemberId = @1 AND IsActive = 1 AND IsBoardMember = 1",
                DocumentOwnerType.Club, memberId);
        }

        /// <summary>
        /// Get a single board role by ID.
        /// </summary>
        public BoardRole? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.SingleOrDefaultById<BoardRole>(id);
        }
    }

    public class BoardRoleInfo
    {
        public string Title { get; set; } = "";
        public bool IsBoardMember { get; set; }
    }
}
