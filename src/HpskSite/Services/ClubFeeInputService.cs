using HpskSite.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// EN källa för vad klubbens medlemsavgift räknas på: vilka medlemmar som är aktiva och deras
    /// typ och hushåll, samt namn och e-post.
    ///
    /// <para><b>⚠️ Förut låg detta privat i <c>MembershipFeeAdminController</c></b>, så listan, genereringen
    /// och "Skapa avgiften" matades av controllern men betalsidan och mejlen inte kunde få samma indata.
    /// Allt som ska räkna eller visa en avgift hämtar sin indata härifrån, så att typ, hushåll
    /// (och sedan huvudklubb och tillval) alltid är desamma överallt.</para>
    /// </summary>
    public class ClubFeeInputService
    {
        private readonly ClubMembershipService _clubMembershipService;
        private readonly IMemberService _memberService;

        public ClubFeeInputService(ClubMembershipService clubMembershipService, IMemberService memberService)
        {
            _clubMembershipService = clubMembershipService;
            _memberService = memberService;
        }

        /// <summary>Klubbens aktiva medlemmar (utom utträdda och avlidna) som underlag för avgiften.</summary>
        public List<MemberFeeInput> ActiveFeeMembers(int clubId)
            => _clubMembershipService.GetForClubApprovedMembers(clubId)
                .Where(cm => cm.MembershipStatus != "Utträdd" && cm.MembershipStatus != "Avliden")
                .Select(cm => new MemberFeeInput
                {
                    MemberId = cm.MemberId,
                    MembershipType = cm.MembershipType ?? "",
                    HouseholdId = cm.HouseholdId ?? "",
                    HouseholdPrimary = cm.HouseholdPrimary
                })
                .ToList();

        /// <summary>Namn och e-post för många medlemmar i ETT uppslag (inte ett per rad).</summary>
        public Dictionary<int, (string? Name, string? Email)> MemberInfo(IEnumerable<int> ids)
        {
            var list = ids.Where(i => i > 0).Distinct().ToArray();
            var result = new Dictionary<int, (string? Name, string? Email)>();
            // ⚠️ GetAllMembers(ids) vill ha högst ~2 000 parametrar; en stor klubb plus gamla avgifter kan närma sig det.
            foreach (var chunk in list.Chunk(1000))
                foreach (var m in _memberService.GetAllMembers(chunk))
                {
                    var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                    result[m.Id] = (string.IsNullOrEmpty(name) ? m.Name : name, m.Email);
                }
            return result;
        }
    }
}
