using HpskSite.Models;
using HpskSite.Services.Kretsgranskning;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Vem får vad i ärendekön och motionerna (2026-10-08). EN plats, så att sidan och endpoints
    /// inte kan säga olika saker.
    ///
    /// <list type="bullet">
    /// <item><b>Styrelsearbetet</b> (läsa, anmäla ärenden): sajtadmin, klubb-/kretsadmin och aktiva
    /// styrelseledamöter (<c>IsBoardMember</c>). Revisorer är INTE med — de sitter inte i styrelsen
    /// (Stefan 2026-10-08).</item>
    /// <item><b>Placera ärenden, skriva styrelsens yttrande:</b> sekreteraren och ordföranden
    /// (härlett ur det aktiva uppdraget i <c>BoardRoles</c>) plus administratören.</item>
    /// <item><b>Läsa motioner till ett årsmöte:</b> styrelsearbetet ovan, eller medlem i föreningen
    /// — för en krets: medlem i någon av kretsens klubbar. Motionerna och namnen syns för alla
    /// medlemmar (Stefan 2026-10-08).</item>
    /// </list>
    /// </summary>
    public class BoardWorkAccess
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _roles;
        private readonly MemberClubService _memberClubs;
        private readonly KretsCalendarService _krets;

        public BoardWorkAccess(IMemberManager memberManager, IMemberService memberService, IContentService contentService,
            AdminAuthorizationService auth, BoardRoleService roles, MemberClubService memberClubs, KretsCalendarService krets)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _auth = auth;
            _roles = roles;
            _memberClubs = memberClubs;
            _krets = krets;
        }

        /// <summary>Sekreteraren och ordföranden — rollnycklarna i BoardRoles.</summary>
        public static readonly string[] PlacerRoleKeys = { "Sekreterare", BoardRoleDefinitions.RoleOrdforande };

        private int? _memberId;
        public async Task<int> CurrentMemberIdAsync()
        {
            if (_memberId.HasValue) return _memberId.Value;
            var cur = await _memberManager.GetCurrentMemberAsync();
            _memberId = cur?.Email == null ? 0 : (_memberService.GetByEmail(cur.Email)?.Id ?? 0);
            return _memberId.Value;
        }

        public async Task<bool> IsAdminAsync(int ownerType, int ownerId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (ownerType == DocumentOwnerType.Club) return await _auth.IsClubAdminForClub(ownerId);
            if (ownerType == DocumentOwnerType.Region)
            {
                var code = RegionCode(ownerId);
                return !string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code);
            }
            return false;
        }

        public async Task<bool> CanAccessBoardWorkAsync(int ownerType, int ownerId)
        {
            if (await IsAdminAsync(ownerType, ownerId)) return true;
            var me = await CurrentMemberIdAsync();
            return me > 0 && _roles.IsBoardMemberOf(ownerType, ownerId, me);
        }

        public async Task<bool> CanPlaceAsync(int ownerType, int ownerId)
        {
            if (await IsAdminAsync(ownerType, ownerId)) return true;
            var me = await CurrentMemberIdAsync();
            return me > 0 && _roles.HasActiveRole(ownerType, ownerId, me, PlacerRoleKeys);
        }

        /// <summary>Medlem i föreningen: klubben själv, eller för en krets någon av kretsens klubbar.</summary>
        public async Task<bool> IsMemberOfOwnerAsync(int ownerType, int ownerId)
        {
            var me = await CurrentMemberIdAsync();
            if (me <= 0) return false;
            var member = _memberService.GetById(me);
            var clubs = _memberClubs.GetAllClubIds(member);
            if (ownerType == DocumentOwnerType.Club) return clubs.Contains(ownerId);
            if (ownerType == DocumentOwnerType.Region)
            {
                var inRegion = _krets.ClubsInRegion(ownerId);
                return clubs.Any(inRegion.ContainsKey);
            }
            return false;
        }

        public async Task<bool> CanReadMotionsAsync(int ownerType, int ownerId) =>
            await CanAccessBoardWorkAsync(ownerType, ownerId) || await IsMemberOfOwnerAsync(ownerType, ownerId);

        /// <summary>Kretsnodens id för en klubb (trädet), 0 om den inte går att hitta.</summary>
        public int RegionIdForClub(int clubId) => _krets.RegionIdForClub(clubId);

        public string RegionCode(int regionNodeId)
        {
            try
            {
                var node = _contentService.GetById(regionNodeId);
                return node?.ContentType.Alias == "regionalPage" ? node.GetValue<string>("regionCode") ?? "" : "";
            }
            catch { return ""; }
        }
    }
}
