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
        /// Get roles for a club or region. When boardOnly=true, filters to IsBoardMember=1.
        /// When includeInactive=true, soft-deleted past holders are included too (for the history view).
        /// </summary>
        public List<BoardRole> GetBoardMembers(int ownerType, int ownerId, bool boardOnly = false, bool includeInactive = false)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var sql = "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1";
            if (!includeInactive) sql += " AND IsActive = 1";
            if (boardOnly) sql += " AND IsBoardMember = 1";
            sql += " ORDER BY SortOrder, RoleKey";

            var roles = db.Fetch<BoardRole>(sql, ownerType, ownerId);

            ResolveMemberNames(roles);
            return roles;
        }

        /// <summary>
        /// Resolve display names for a set of roles, batching distinct member lookups to avoid an
        /// N+1 cascade (matters for the region rollup which spans many clubs).
        /// </summary>
        private void ResolveMemberNames(List<BoardRole> roles)
        {
            var byId = new Dictionary<int, string>();
            foreach (var memberId in roles.Select(r => r.MemberId).Distinct())
            {
                var member = _memberService.GetById(memberId);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[memberId] = string.IsNullOrEmpty(name) ? member.Name : name;
            }

            foreach (var role in roles)
                if (byId.TryGetValue(role.MemberId, out var name))
                    role.MemberName = name;
        }

        /// <summary>
        /// Active roles whose mandate ends before the given date (ordered soonest-first).
        /// Drives the "Mandat som löper ut" / valberedning view. Roles with no term set are excluded.
        /// </summary>
        public List<BoardRole> GetExpiringRoles(int ownerType, int ownerId, DateTime before)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var roles = db.Fetch<BoardRole>(
                "SELECT * FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 " +
                "AND TermEndsDate IS NOT NULL AND TermEndsDate < @2 ORDER BY TermEndsDate, SortOrder",
                ownerType, ownerId, before);

            ResolveMemberNames(roles);
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
            string? customTitle, bool isBoardMember, int assignedByMemberId,
            DateTime? electedDate = null, DateTime? termEndsDate = null, int? termYears = null)
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
                existing.ElectedDate = electedDate;
                existing.TermEndsDate = termEndsDate;
                existing.TermYears = termYears;
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
                ElectedDate = electedDate,
                TermEndsDate = termEndsDate,
                TermYears = termYears,
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
        public bool UpdateBoardRole(int id, string roleKey, string? customTitle, bool isBoardMember, int sortOrder,
            DateTime? electedDate = null, DateTime? termEndsDate = null, int? termYears = null)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var role = db.SingleOrDefaultById<BoardRole>(id);
            if (role == null) return false;

            role.RoleKey = roleKey;
            role.CustomTitle = roleKey == "Custom" ? customTitle : null;
            role.IsBoardMember = isBoardMember;
            role.SortOrder = sortOrder;
            role.ElectedDate = electedDate;
            role.TermEndsDate = termEndsDate;
            role.TermYears = termYears;
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
        /// True if the member holds an active board-member role (IsBoardMember=1) for the owner.
        /// The single capability check used to gate board-work access (no per-post permissions).
        /// </summary>
        public bool IsBoardMemberOf(int ownerType, int ownerId, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM BoardRoles WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2 AND IsActive = 1 AND IsBoardMember = 1",
                ownerType, ownerId, memberId) > 0;
        }

        /// <summary>True if the member sits on any active board (club or region). Cheap menu-gate check.</summary>
        public bool IsOnAnyBoard(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM BoardRoles WHERE MemberId = @0 AND IsActive = 1 AND IsBoardMember = 1", memberId) > 0;
        }

        /// <summary>Distinct (OwnerType, OwnerId) scopes where the member is an active board member.</summary>
        public List<(int OwnerType, int OwnerId)> GetBoardMembershipsForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardRole>(
                    "SELECT DISTINCT OwnerType, OwnerId FROM BoardRoles WHERE MemberId = @0 AND IsActive = 1 AND IsBoardMember = 1",
                    memberId)
                .Select(r => (r.OwnerType, r.OwnerId))
                .ToList();
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
