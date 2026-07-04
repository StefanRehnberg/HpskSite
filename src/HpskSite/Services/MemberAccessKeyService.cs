using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// CRUD for a member's clubhouse keys / access tags / door codes (MemberAccessKey table).
    /// Keys are club-managed: a club administers the register on the member's behalf.
    /// </summary>
    public class MemberAccessKeyService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;

        public MemberAccessKeyService(IScopeProvider scopeProvider, IMemberService memberService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
        }

        /// <summary>
        /// All access keys for a member. Outstanding keys first (not yet returned), then by
        /// issue date. MemberName is resolved via a single batched lookup (no N+1).
        /// </summary>
        public List<MemberAccessKey> GetForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var keys = db.Fetch<MemberAccessKey>(
                "SELECT * FROM MemberAccessKey WHERE MemberId = @0 " +
                "ORDER BY CASE WHEN ReturnedDate IS NULL THEN 0 ELSE 1 END, IssuedDate DESC, Id DESC",
                memberId);

            ResolveMemberNames(keys);
            return keys;
        }

        /// <summary>
        /// Resolve display names for a set of keys, batching distinct member lookups to avoid
        /// an N+1 cascade.
        /// </summary>
        private void ResolveMemberNames(List<MemberAccessKey> keys)
        {
            var byId = new Dictionary<int, string>();
            foreach (var memberId in keys.Select(k => k.MemberId).Distinct())
            {
                var member = _memberService.GetById(memberId);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[memberId] = string.IsNullOrEmpty(name) ? member.Name : name;
            }

            foreach (var key in keys)
                if (byId.TryGetValue(key.MemberId, out var name))
                    key.MemberName = name;
        }

        /// <summary>Insert a new access key. Returns the inserted row (with Id populated).</summary>
        public MemberAccessKey Add(MemberAccessKey k)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            if (k.CreatedDate == default) k.CreatedDate = DateTime.UtcNow;
            db.Insert(k);
            return k;
        }

        /// <summary>Update an existing access key.</summary>
        public bool Update(MemberAccessKey k)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var existing = db.SingleOrDefaultById<MemberAccessKey>(k.Id);
            if (existing == null) return false;

            db.Update(k);
            return true;
        }

        /// <summary>Hard-delete an access key by id.</summary>
        public bool Delete(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.Execute("DELETE FROM MemberAccessKey WHERE Id = @0", id) > 0;
        }

        /// <summary>Get a single access key by id (or null).</summary>
        public MemberAccessKey? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.SingleOrDefaultById<MemberAccessKey>(id);
        }

        /// <summary>The member a key belongs to, for authorization checks. 0 if not found.</summary>
        public int GetMemberIdForKey(int keyId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return db.ExecuteScalar<int>("SELECT MemberId FROM MemberAccessKey WHERE Id = @0", keyId);
        }
    }
}
