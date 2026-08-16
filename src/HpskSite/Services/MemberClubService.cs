using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>One club a member belongs to, as offered in a "tävlar för"-picker.</summary>
    public class MemberClubOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        /// <summary>True for the member's <c>primaryClubId</c>; the rest come from <c>memberClubIds</c>.</summary>
        public bool IsPrimary { get; set; }
    }

    /// <summary>
    /// The ONE place a member's club memberships are resolved. A member has one
    /// <c>primaryClubId</c> plus any number of additional clubs in the CSV property
    /// <c>memberClubIds</c>, and until 2026-08-16 practically every caller read only the
    /// primary one — which is why a shooter could never enter a competition for their
    /// second club.
    ///
    /// <para><b>primaryClubId is stored as a STRING.</b> <c>GetValue&lt;int&gt;("primaryClubId")</c>
    /// does not reliably convert it and quietly yields 0; several call sites did exactly that
    /// and wrote clubId=0 onto registrations (which then fell back to a read-time lookup, so
    /// the display looked right and the stored value was wrong). Always go through
    /// <see cref="GetPrimaryClubId(IMember)"/>.</para>
    /// </summary>
    public class MemberClubService
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;

        public MemberClubService(IMemberService memberService, IContentService contentService, ClubService clubService)
        {
            _memberService = memberService;
            _contentService = contentService;
            _clubService = clubService;
        }

        /// <summary>
        /// The member's primary club id, or 0. Parses the string property rather than
        /// trusting <c>GetValue&lt;int&gt;</c> — see the class remarks.
        /// </summary>
        public int GetPrimaryClubId(IMember? member)
        {
            var raw = member?.GetValue<string>("primaryClubId");
            return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var id) && id > 0
                ? id
                : 0;
        }

        /// <summary>The member's additional (non-primary) club ids, from the CSV property.</summary>
        public List<int> GetAdditionalClubIds(IMember? member)
        {
            var result = new List<int>();
            var raw = member?.GetValue<string>("memberClubIds");
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var primary = GetPrimaryClubId(member);
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var id) && id > 0 && id != primary && !result.Contains(id))
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Every club id the member belongs to, primary first.</summary>
        public List<int> GetAllClubIds(IMember? member)
        {
            var result = new List<int>();
            var primary = GetPrimaryClubId(member);
            if (primary > 0) result.Add(primary);
            result.AddRange(GetAdditionalClubIds(member));
            return result;
        }

        /// <summary>
        /// Every club the member belongs to, resolved to names, primary first. Clubs whose
        /// node no longer resolves are dropped rather than shown as "Club 1234" — a picker
        /// offering a phantom club is worse than a short list.
        /// </summary>
        public List<MemberClubOption> GetClubOptions(IMember? member)
        {
            var options = new List<MemberClubOption>();
            if (member == null) return options;

            var primary = GetPrimaryClubId(member);
            if (primary > 0)
            {
                var name = _clubService.GetClubNameById(primary);
                if (!string.IsNullOrWhiteSpace(name))
                    options.Add(new MemberClubOption { Id = primary, Name = name, IsPrimary = true });
            }

            foreach (var id in GetAdditionalClubIds(member))
            {
                var name = _clubService.GetClubNameById(id);
                if (!string.IsNullOrWhiteSpace(name))
                    options.Add(new MemberClubOption { Id = id, Name = name, IsPrimary = false });
            }

            return options;
        }

        /// <summary>Overload that loads the member first. Returns an empty list for an unknown member.</summary>
        public List<MemberClubOption> GetClubOptions(int memberId)
            => GetClubOptions(memberId > 0 ? _memberService.GetById(memberId) : null);

        /// <summary>Is this member a member of that club, primary or additional?</summary>
        public bool IsMemberOfClub(IMember? member, int clubId)
            => clubId > 0 && GetAllClubIds(member).Contains(clubId);

        /// <summary>
        /// memberId → the club id their registration for <paramref name="competitionId"/> is filed
        /// under. Only registrations carrying an explicit clubId are included, so a caller can fall
        /// back to the member's primary club for anything missing (legacy rows, and rows created
        /// before the club was stored at all).
        ///
        /// <para>Exists because several result paths resolve a shooter's club straight off the
        /// MEMBER record. That is wrong once a shooter can enter for a club other than their
        /// primary one: the result list would print the club they did not compete for. Use this to
        /// look the answer up per competition instead.</para>
        /// </summary>
        public Dictionary<int, int> GetRegistrationClubIds(int competitionId)
        {
            var map = new Dictionary<int, int>();
            if (competitionId <= 0) return map;

            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return map;

                var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                if (hub == null) return map;

                // Deliberately NOT filtered on Published: RegisterForCompetition saves
                // synchronously and defers publishing to an unreliable background task, so a
                // freshly-registered shooter is Saved but not yet Published.
                foreach (var reg in _contentService.GetPagedChildren(hub.Id, 0, 2000, out _)
                                                   .Where(r => r.ContentType.Alias == "competitionRegistration"))
                {
                    var memberId = reg.GetValue<int>("memberId");
                    var clubId = reg.GetValue<int>("clubId");
                    if (memberId > 0 && clubId > 0) map[memberId] = clubId;
                }
            }
            catch
            {
                // A failed lookup degrades to the caller's primary-club fallback, which is the
                // pre-existing behaviour — never to a broken result list.
            }

            return map;
        }

        /// <summary>
        /// Resolve the club a registration should be filed under.
        /// <paramref name="requestedClubId"/> is honoured only when the member actually belongs
        /// to it; anything else falls back to the primary club. Deliberately a silent fallback
        /// rather than an error: the value arrives from a picker that is hidden for single-club
        /// members, so an absent or stale id is the normal case, not a fault. Callers that want
        /// to REPORT a rejected club should compare the result against what they asked for.
        /// </summary>
        public int ResolveRegistrationClubId(IMember? member, int? requestedClubId)
        {
            if (requestedClubId is > 0 && IsMemberOfClub(member, requestedClubId.Value))
                return requestedClubId.Value;
            return GetPrimaryClubId(member);
        }
    }
}
