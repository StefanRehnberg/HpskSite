using HpskSite.Models;
using HpskSite.Services.Kretsgranskning;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Vem får utlysa, ändra och utse åt en krets (kretsutlysningen F2) — <b>på ETT ställe</b>.
    ///
    /// <para><b>Regeln (<see cref="CanManageCallsForRegion"/>):</b> sajtadmin, kretsens administratör
    /// (<c>RegionalAdmin_{kod}</c>), ledamot i kretsens styrelse, eller kretsens tävlingsansvarige (uppdraget i
    /// <c>BoardRoles</c>). Kretsstyrelserna är ofta små och äldre; en krets som bara har en ledamot och ingen
    /// tävlingsansvarig måste ändå kunna utlysa.</para>
    ///
    /// <para><b>⚠️ <c>AdminAuthorizationService.IsRegionalAdminForRegion</c> är OFÖRÄNDRAD</b> (tjugotalet ytor litar på
    /// den) — den här regeln är bredare och gäller bara utlysningen. Därför en egen tjänst som anropar den, i stället
    /// för ett fjärde läge i den gamla. Kretsens id kommer ur serverns data (utlysningens <c>RegionId</c>), aldrig ur
    /// ett fält klienten skickar.</para>
    /// </summary>
    public class CompetitionCallAccess
    {
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _roles;
        private readonly KretsCalendarService _krets;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private int? _memberId;

        public CompetitionCallAccess(AdminAuthorizationService auth, BoardRoleService roles, KretsCalendarService krets,
            IMemberManager memberManager, IMemberService memberService)
        {
            _auth = auth;
            _roles = roles;
            _krets = krets;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        public async Task<int> CurrentMemberIdAsync()
        {
            if (_memberId.HasValue) return _memberId.Value;
            var cur = await _memberManager.GetCurrentMemberAsync();
            _memberId = cur?.Email == null ? 0 : (_memberService.GetByEmail(cur.Email)?.Id ?? 0);
            return _memberId.Value;
        }

        public async Task<string> CurrentNameAsync()
        {
            var id = await CurrentMemberIdAsync();
            if (id <= 0) return "";
            var m = _memberService.GetById(id);
            var n = $"{m?.GetValue<string>("firstName")} {m?.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m?.Name ?? "");
        }

        /// <summary>Är kretsen en riktig krets (regionalPage-nod)? Annars finns inget att hantera.</summary>
        public bool RegionExists(int regionId) => regionId > 0 && _krets.Region(regionId) != null;

        /// <summary>Får den inloggade utlysa/ändra/utse åt kretsen? Se klassens kommentar.</summary>
        public async Task<bool> CanManageCallsForRegion(int regionId)
        {
            var region = regionId > 0 ? _krets.Region(regionId) : null;
            if (region == null) return false;
            var me = await CurrentMemberIdAsync();
            if (me <= 0) return false;
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (await _auth.IsRegionalAdminForRegion(region.Code)) return true;
            if (_roles.IsBoardMemberOf(DocumentOwnerType.Region, regionId, me)) return true;
            return _roles.HasActiveRole(DocumentOwnerType.Region, regionId, me, BoardRoleDefinitions.RoleTavlingsansvarig);
        }

        /// <summary>Kretsar den inloggade får utlysa åt (flera när hen är med i fler, eller sajtadmin = alla).</summary>
        public async Task<List<(int Id, string Name)>> ManageableRegionsAsync()
        {
            var result = new List<(int, string)>();
            foreach (var r in _krets.AllRegions())
                if (await CanManageCallsForRegion(r.Id)) result.Add((r.Id, r.Name));
            return result;
        }
    }
}
