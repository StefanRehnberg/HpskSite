using HpskSite.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Vem får SIGNERA märken för en klubb: sajtadmin, klubbens styrelseledamöter, och klubbens
    /// skjutledare när klubben slagit på <c>markenSignoffSkjutledare</c>.
    ///
    /// <para>Utbrutet ur <c>MarkenController</c> (2026-10-06) så att kursens serier följer EXAKT samma
    /// regel: en kursinstruktör godkänner själv brons och silver, men en guldserie kräver den här
    /// behörigheten (Stefans beslut). Två kopior av regeln hade glidit isär.</para>
    /// </summary>
    public class MarkenSignoffAuthority
    {
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
        private readonly IContentService _content;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public MarkenSignoffAuthority(AdminAuthorizationService auth, BoardRoleService boardRoles,
            IContentService content, IMemberManager memberManager, IMemberService memberService)
        {
            _auth = auth;
            _boardRoles = boardRoles;
            _content = content;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        public async Task<bool> CanSignOffForClubAsync(int clubId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (clubId <= 0) return false;

            var current = await _memberManager.GetCurrentMemberAsync();
            var actingId = current?.Email == null ? 0 : _memberService.GetByEmail(current.Email)?.Id ?? 0;
            if (actingId <= 0) return false;

            var board = _boardRoles.GetBoardMembers(DocumentOwnerType.Club, clubId, boardOnly: true);
            if (board.Any(r => r.MemberId == actingId)) return true;

            return SkjutledareSignoffEnabled(clubId) && await _auth.IsSkjutledareForClub(clubId);
        }

        /// <summary>Klubbens <c>markenSignoffSkjutledare</c> (av som standard = bara styrelsen).</summary>
        public bool SkjutledareSignoffEnabled(int clubId)
        {
            try
            {
                var club = _content.GetById(clubId);
                if (club == null || !club.HasProperty("markenSignoffSkjutledare")) return false;
                return club.GetValue<bool>("markenSignoffSkjutledare");
            }
            catch { return false; }
        }
    }
}
