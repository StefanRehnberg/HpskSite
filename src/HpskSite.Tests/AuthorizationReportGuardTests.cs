using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// C3-planen S0: behörighetsrapporten (<c>CompetitionAuthorizationReport</c>) kör de riktiga grindarna
    /// i <c>AdminAuthorizationService</c> som en vald medlem (<c>EvaluateAs</c>). Det håller bara så länge
    /// varje grind läser "nuvarande medlem" genom <c>CurrentMemberAsync</c>. En grind som anropar
    /// <c>_memberManager.GetCurrentMemberAsync()</c> direkt läser förbi rapporten — den svarar då för
    /// den som ROPAR på rapporten (sajtadmin) i stället för kandidaten, och rapporten visar fel utan att
    /// något säger ifrån.
    /// </summary>
    public class AuthorizationReportGuardTests
    {
        [Fact]
        public void AdminAuthorizationService_reads_the_current_member_in_one_place()
        {
            var text = File.ReadAllText(Path.Combine(SiteRoot(), "Services", "AdminAuthorizationService.cs"));
            var direct = Regex.Matches(text, @"_memberManager\.GetCurrentMemberAsync\(").Count;
            Assert.True(direct == 1,
                $"AdminAuthorizationService anropar _memberManager.GetCurrentMemberAsync() {direct} gånger — bara " +
                "CurrentMemberAsync får göra det, annars läser grinden förbi behörighetsrapporten (EvaluateAs).");
            Assert.Matches(@"private async Task<CurrentMemberRef\?> CurrentMemberAsync\(\)", text);
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            var src = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            return Path.Combine(src, "HpskSite");
        }
    }
}
