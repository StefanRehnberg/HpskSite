using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Får DEN HÄR besökaren öppna DEN HÄR raden i "Det här händer"-flödet?
    ///
    /// ⚠️ Regeln bor här och inte i partialen, av två skäl. Den ska gå att testa utan en renderad
    /// sida, och Razor-kodblock tål inte explicita generics (<c>new HashSet&lt;int&gt;()</c> parsas
    /// som en HTML-tagg och spräcker vyn med ett meddelandelöst UmbracoCompilationException — se
    /// minnet om HomePage.cshtml). Vyn ska bara anropa <see cref="CanLink"/>.
    ///
    /// ⚠️ FÖRSTA VERSIONEN AV LÄNKGRINDEN VAR FÖR SLÄPP. Den frågade "är någon inloggad", vilket
    /// gjorde en klubbintern tävling klickbar för varje medlem på sajten — även den som inte tillhör
    /// klubben. Rätt fråga är om besökaren har ÅTKOMST: medlem i klubben, kretsadmin för kretsen,
    /// eller sajtadmin (Stefan 2026-08-31).
    ///
    /// Läser INTE innehållscachen och gör inga extra frågor per rad: medlemmens klubbar och roller
    /// hämtas en gång per rendering, raderna jämförs mot den mängden.
    /// </summary>
    public sealed class WhatsHappeningAccess
    {
        private readonly bool _isLoggedIn;
        private readonly bool _isSiteAdmin;
        private readonly HashSet<int> _clubIds;
        private readonly HashSet<string> _regionCodes;

        private WhatsHappeningAccess(bool isLoggedIn, bool isSiteAdmin, HashSet<int> clubIds, HashSet<string> regionCodes)
        {
            _isLoggedIn = isLoggedIn;
            _isSiteAdmin = isSiteAdmin;
            _clubIds = clubIds;
            _regionCodes = regionCodes;
        }

        /// <summary>En utloggad besökare — ser bara omaskerade rader och kan bara öppna dem.</summary>
        public static WhatsHappeningAccess Anonymous() =>
            new WhatsHappeningAccess(false, false, new HashSet<int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Bygger åtkomstmängden för en inloggad medlem.
        ///
        /// ⚠️ Klubbmedlemskapet MÅSTE gå via <see cref="MemberClubService.GetAllClubIds"/>:
        /// <c>primaryClubId</c> är en STRÄNG-egenskap, så <c>GetValue&lt;int&gt;</c> konverterar
        /// inte utan returnerar tyst 0 — och medlemmen har dessutom ofta flera klubbar i
        /// <c>memberClubIds</c>.
        ///
        /// Rollerna läses direkt (<c>Administrators</c>, <c>RegionalAdmin_*</c>, <c>ClubAdmin_*</c>)
        /// i stället för via AdminAuthorizationService — vyn är synkron, och de tre prefixen är
        /// samma mönster som CompetitionManagement.cshtml redan läser inline.
        /// </summary>
        public static WhatsHappeningAccess For(IMember? member, IMemberService memberService, MemberClubService memberClubService)
        {
            if (member == null) return Anonymous();

            var clubIds = new HashSet<int>(memberClubService.GetAllClubIds(member));
            var regionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isSiteAdmin = false;

            try
            {
                var roles = memberService.GetAllRoles(member.Id) ?? Enumerable.Empty<string>();
                foreach (var role in roles)
                {
                    if (role == "Administrators") { isSiteAdmin = true; continue; }
                    if (role.StartsWith("RegionalAdmin_", StringComparison.Ordinal))
                    {
                        var code = role.Substring("RegionalAdmin_".Length).Trim();
                        if (code.Length > 0) regionCodes.Add(code);
                    }
                    else if (role.StartsWith("ClubAdmin_", StringComparison.Ordinal))
                    {
                        // En klubbadmin är normalt medlem i klubben, men inte alltid — och hen ska
                        // kunna nå klubbens egna händelser oavsett.
                        if (int.TryParse(role.Substring("ClubAdmin_".Length), out var cid) && cid > 0)
                            clubIds.Add(cid);
                    }
                }
            }
            catch
            {
                // Rollerna är en FÖRSTÄRKNING av åtkomsten, aldrig grunden för den. Går läsningen
                // fel ska medlemmen fortfarande kunna öppna sin egen klubbs rader — inte tappa dem.
            }

            return new WhatsHappeningAccess(true, isSiteAdmin, clubIds, regionCodes);
        }

        /// <summary>
        /// Bygger en åtkomstmängd direkt ur upplösta värden. FÖR TESTER, och för det enda som
        /// annars inte går att pröva: klubbmedlemskapsvägen kräver en medlem som tillhör just den
        /// klubb en maskerad rad ägs av, och den kombinationen finns inte i dev-datan — att mutera
        /// medlemskap för att framkalla den vore en dyrare fixtur än regeln är värd.
        /// Vyn ska alltid gå via <see cref="For"/>.
        /// </summary>
        public static WhatsHappeningAccess FromResolved(bool isLoggedIn, bool isSiteAdmin,
            IEnumerable<int>? clubIds, IEnumerable<string>? regionCodes) =>
            new WhatsHappeningAccess(
                isLoggedIn,
                isSiteAdmin,
                new HashSet<int>(clubIds ?? Enumerable.Empty<int>()),
                new HashSet<string>(regionCodes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Får raden bära en länk?
        ///
        /// Omaskerad rad = publik (en öppen tävling ligger redan på tävlingsnavet) och klickbar för
        /// alla. Maskerad rad = klubbintern tävling eller klubbhändelse, och kräver åtkomst.
        ///
        /// ⚠️ En rad utan Url får aldrig länkas — annars blir det en död länk som ser ut som ett fel
        /// på sidan, vilket är precis vad hela ändringen skulle bli av med.
        /// </summary>
        public bool CanLink(FeedItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Url)) return false;
            if (!item.Masked) return true;
            return HasAccess(item);
        }

        /// <summary>
        /// Har besökaren åtkomst till den maskerade radens innehåll? Medlem i klubben, kretsadmin
        /// för kretsen, eller sajtadmin.
        /// </summary>
        public bool HasAccess(FeedItem item)
        {
            if (!_isLoggedIn || item == null) return false;
            if (_isSiteAdmin) return true;
            if (item.ClubId > 0 && _clubIds.Contains(item.ClubId)) return true;
            // ⚠️ Kretsadmin räknas bara när raden VET vilken krets den hör till. En tom RegionCode
            // får aldrig matcha — det skulle ge varje kretsadmin åtkomst till varje rad utan krets.
            if (!string.IsNullOrWhiteSpace(item.RegionCode) && _regionCodes.Contains(item.RegionCode)) return true;
            return false;
        }
    }
}
