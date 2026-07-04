using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Per-club membership records (ClubMembership). A person (hpskMember) has one row
    /// per club they belong to. Follows the IScopeProvider CRUD pattern (see BoardRoleService).
    /// See Documentation/MEMBER_DATABASE.md.
    /// </summary>
    public class ClubMembershipService
    {
        private readonly IScopeProvider _scopeProvider;

        public ClubMembershipService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public ClubMembership? Get(int memberId, int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.FirstOrDefault<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0 AND ClubId = @1", memberId, clubId);
        }

        /// <summary>All memberships for one club (used by fee generation).</summary>
        public List<ClubMembership> GetForClub(int clubId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE ClubId = @0", clubId);
        }

        /// <summary>All of a person's memberships across clubs.</summary>
        public List<ClubMembership> GetForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0", memberId);
        }

        /// <summary>
        /// Upsert on (MemberId, ClubId). Returns the saved row. CreatedDate is set on insert.
        /// </summary>
        public ClubMembership Save(ClubMembership m)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var existing = db.FirstOrDefault<ClubMembership>(
                "SELECT * FROM ClubMembership WHERE MemberId = @0 AND ClubId = @1", m.MemberId, m.ClubId);
            if (existing != null)
            {
                m.Id = existing.Id;
                m.CreatedDate = existing.CreatedDate;
                db.Update(m);
            }
            else
            {
                m.CreatedDate = DateTime.UtcNow;
                db.Insert(m);
            }
            return m;
        }

        /// <summary>
        /// Ensure a membership row exists for (memberId, clubId); create a minimal one
        /// (status Aktiv) if missing. Used when a club adds/imports a member.
        /// </summary>
        public ClubMembership EnsureExists(int memberId, int clubId)
        {
            var existing = Get(memberId, clubId);
            if (existing != null) return existing;
            return Save(new ClubMembership { MemberId = memberId, ClubId = clubId, MembershipStatus = "Aktiv" });
        }
    }
}
