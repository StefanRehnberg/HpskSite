using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Board governance: the Årshjul (annual cycle checklist) and Valberedning (nominations).
    /// Club/region-scoped via OwnerType/OwnerId. See BOARD_WORK_PHASE3_GOVERNANCE.md.
    /// </summary>
    public class BoardGovernanceService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly BoardRoleService _boardRoleService;

        public BoardGovernanceService(IScopeProvider scopeProvider, IMemberService memberService, BoardRoleService boardRoleService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _boardRoleService = boardRoleService;
        }

        // ---- Årshjul --------------------------------------------------------

        /// <summary>Get the year wheel for a year, seeding the standard template the first time.</summary>
        public List<BoardYearWheelItem> GetYearWheel(int ownerType, int ownerId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var items = db.Fetch<BoardYearWheelItem>(
                "SELECT * FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1 AND Year=@2 AND IsActive=1 ORDER BY TargetDate, SortOrder, Id",
                ownerType, ownerId, year);
            if (items.Count == 0)
            {
                int sort = 0;
                foreach (var (month, day, title) in BoardYearWheelTemplate.Items)
                {
                    db.Insert(new BoardYearWheelItem
                    {
                        OwnerType = ownerType, OwnerId = ownerId, Year = year, Title = title,
                        TargetDate = SafeDate(year, month, day), SortOrder = sort++, IsActive = true
                    });
                }
                items = db.Fetch<BoardYearWheelItem>(
                    "SELECT * FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1 AND Year=@2 AND IsActive=1 ORDER BY TargetDate, SortOrder, Id",
                    ownerType, ownerId, year);
            }
            return items;
        }

        public List<int> GetWheelYears(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT DISTINCT Year FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1 AND IsActive=1 ORDER BY Year DESC",
                ownerType, ownerId);
        }

        public bool SetWheelDone(int id, bool done)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var it = db.SingleOrDefaultById<BoardYearWheelItem>(id);
            if (it == null) return false;
            it.Done = done;
            it.DoneDate = done ? DateTime.Now : null;
            db.Update(it);
            return true;
        }

        public BoardYearWheelItem AddWheelItem(int ownerType, int ownerId, int year, string title, DateTime? targetDate)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var it = new BoardYearWheelItem
            {
                OwnerType = ownerType, OwnerId = ownerId, Year = year, Title = title,
                TargetDate = targetDate, SortOrder = 100, IsActive = true
            };
            db.Insert(it);
            return it;
        }

        public bool UpdateWheelItem(int id, string title, DateTime? targetDate)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var it = db.SingleOrDefaultById<BoardYearWheelItem>(id);
            if (it == null) return false;
            it.Title = title;
            it.TargetDate = targetDate;
            db.Update(it);
            return true;
        }

        public bool RemoveWheelItem(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var it = db.SingleOrDefaultById<BoardYearWheelItem>(id);
            if (it == null) return false;
            it.IsActive = false;
            db.Update(it);
            return true;
        }

        public int? GetWheelItemOwnerType(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardYearWheelItem>(id)?.OwnerType;
        }

        public BoardYearWheelItem? GetWheelItem(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardYearWheelItem>(id);
        }

        // ---- Valberedning ---------------------------------------------------

        public List<BoardNomination> GetNominations(int ownerType, int ownerId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardNomination>(
                "SELECT * FROM BoardNominations WHERE OwnerType=@0 AND OwnerId=@1 AND Year=@2 AND IsActive=1 ORDER BY SortOrder, Id",
                ownerType, ownerId, year);
        }

        public List<int> GetNominationYears(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT DISTINCT Year FROM BoardNominations WHERE OwnerType=@0 AND OwnerId=@1 AND IsActive=1 ORDER BY Year DESC",
                ownerType, ownerId);
        }

        public BoardNomination AddNomination(int ownerType, int ownerId, int year, string? postKey, string postLabel,
            string candidateName, int? candidateMemberId, string status, string? notes, int createdByMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = new BoardNomination
            {
                OwnerType = ownerType, OwnerId = ownerId, Year = year, PostKey = postKey, PostLabel = postLabel,
                CandidateName = candidateName, CandidateMemberId = candidateMemberId,
                Status = string.IsNullOrWhiteSpace(status) ? "Föreslagen" : status, Notes = notes,
                CreatedByMemberId = createdByMemberId, CreatedDate = DateTime.Now, IsActive = true
            };
            db.Insert(n);
            return n;
        }

        public bool UpdateNomination(int id, string postLabel, string candidateName, string status, string? notes)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.PostLabel = postLabel;
            n.CandidateName = candidateName;
            n.Status = status;
            n.Notes = notes;
            db.Update(n);
            return true;
        }

        public bool SetNominationStatus(int id, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.Status = status;
            db.Update(n);
            return true;
        }

        public bool RemoveNomination(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.IsActive = false;
            db.Update(n);
            return true;
        }

        public BoardNomination? GetNomination(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardNomination>(id);
        }

        /// <summary>Posts whose mandate ends on/before the end of the election year (drives "poster på val").</summary>
        public List<BoardRole> GetPostsUpForElection(int ownerType, int ownerId, int year)
        {
            return _boardRoleService.GetExpiringRoles(ownerType, ownerId, new DateTime(year, 12, 31).AddDays(1));
        }

        private static DateTime? SafeDate(int year, int month, int day)
        {
            try { return new DateTime(year, month, day); } catch { return null; }
        }
    }
}
