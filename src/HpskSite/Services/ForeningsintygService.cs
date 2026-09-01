using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// CRUD for the föreningsintyg log (<see cref="MemberCertificateIssue"/>) — the per-member
    /// record of licence-support certificates a club has issued.
    /// </summary>
    public class ForeningsintygService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;

        public ForeningsintygService(IScopeProvider scopeProvider, IMemberService memberService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
        }

        /// <summary>
        /// All intyg issued to a member, newest first, with member + issuer display names resolved.
        /// </summary>
        public List<MemberCertificateIssue> GetForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var entries = db.Fetch<MemberCertificateIssue>(
                "SELECT * FROM MemberCertificateIssue WHERE MemberId = @0 ORDER BY IssuedDate DESC",
                memberId);

            ResolveMemberNames(entries);
            return entries;
        }

        /// <summary>
        /// Resolve MemberName + IssuedByName for a set of entries, batching distinct member lookups
        /// to avoid an N+1 cascade (no GetById calls inside a loop over entries).
        /// </summary>
        private void ResolveMemberNames(List<MemberCertificateIssue> entries)
        {
            var ids = entries.Select(e => e.MemberId)
                .Concat(entries.Where(e => e.IssuedByMemberId.HasValue).Select(e => e.IssuedByMemberId!.Value))
                .Distinct();

            var byId = new Dictionary<int, string>();
            foreach (var id in ids)
            {
                var member = _memberService.GetById(id);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[id] = string.IsNullOrEmpty(name) ? member.Name : name;
            }

            foreach (var entry in entries)
            {
                if (byId.TryGetValue(entry.MemberId, out var memberName))
                    entry.MemberName = memberName;
                if (entry.IssuedByMemberId.HasValue && byId.TryGetValue(entry.IssuedByMemberId.Value, out var issuedByName))
                    entry.IssuedByName = issuedByName;
            }
        }

        /// <summary>
        /// Record a new föreningsintyg. <paramref name="snapshot"/> is the full signed document as
        /// JSON — see <see cref="MemberCertificateIssue.Snapshot"/>. Null for a bare log entry
        /// (the pre-existing "registrera ett utfärdat intyg" path), which then cannot be reprinted.
        /// </summary>
        public MemberCertificateIssue Add(int memberId, int clubId, DateTime issuedDate, string purpose,
            string? description, int? issuedByMemberId, string? notes, string? snapshot = null)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var entry = new MemberCertificateIssue
            {
                MemberId = memberId,
                ClubId = clubId,
                IssuedDate = issuedDate,
                Purpose = purpose,
                Description = description,
                IssuedByMemberId = issuedByMemberId,
                Notes = notes,
                Snapshot = snapshot,
                CreatedDate = DateTime.UtcNow
            };

            db.Insert(entry);
            return entry;
        }

        /// <summary>
        /// En enskild intygsrad, för utskrift. Returnerar raden även när <c>Snapshot</c> är null —
        /// anroparen ska då säga att intyget inte kan återges, inte bygga ett nytt ur dagens data.
        /// </summary>
        public MemberCertificateIssue? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var entry = scope.Database.SingleOrDefaultById<MemberCertificateIssue>(id);
            if (entry == null) return null;

            var list = new List<MemberCertificateIssue> { entry };
            ResolveMemberNames(list);
            return entry;
        }

        /// <summary>
        /// Delete an intyg by id. Returns false if it didn't exist.
        /// </summary>
        public bool Delete(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var entry = db.SingleOrDefaultById<MemberCertificateIssue>(id);
            if (entry == null) return false;

            db.Delete(entry);
            return true;
        }

        /// <summary>
        /// The MemberId an intyg belongs to, or 0 if the entry doesn't exist. Used for authorization.
        /// </summary>
        public int GetMemberIdForEntry(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var entry = db.SingleOrDefaultById<MemberCertificateIssue>(id);
            return entry?.MemberId ?? 0;
        }
    }
}
