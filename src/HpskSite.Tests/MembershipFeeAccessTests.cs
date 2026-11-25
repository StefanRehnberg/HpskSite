using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️ Klubbens avgift skrivs bara av den som får skriva i klubbens ekonomi (kassör, delegat, sajtadmin,
    /// admin utan kassör). Styrelsen läser; revisorn skriver aldrig. Baslinjen för endpoints jämför bara
    /// attribut, så grinden prövas här: regeln, och att varje skrivande klubbändpunkt går genom den.
    /// </summary>
    public class MembershipFeeAccessTests
    {
        [Theory]
        [InlineData(LedgerAccess.Write, true)]
        [InlineData(LedgerAccess.Read, false)]   // styrelsen, revisorn, admin när kassör finns
        [InlineData(LedgerAccess.None, false)]
        public void Bara_skrivratt_far_skriva_avgiften(LedgerAccess access, bool expected)
            => Assert.Equal(expected, MembershipFeeAccess.Decide(access));

        private static string ControllerSource([CallerFilePath] string thisFile = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!,
                "..", "HpskSite", "Controllers", "MembershipFeeAdminController.cs")));

        [Theory]
        [InlineData("SaveCategory")]
        [InlineData("DeleteCategory")]
        [InlineData("GenerateCharges")]
        [InlineData("EnsureClubCharge")]
        [InlineData("SendPaymentRequests")]
        [InlineData("SetFeeEmail")]
        public void Varje_skrivande_klubbandpunkt_gar_genom_MembershipFeeAccess(string action)
        {
            var src = ControllerSource();
            var m = Regex.Match(src, @"public async Task<IActionResult> " + action + @"\(.*?(?=\n        (?:\[Http|public |private |//))", RegexOptions.Singleline);
            Assert.True(m.Success, action + " hittades inte");
            Assert.Contains("_feeAccess.CanWriteAsync", m.Value);
        }

        [Fact]
        public void Kravets_endpoints_prövas_med_skrivgrinden_for_klubbavgifter()
        {
            var src = ControllerSource();
            var m = Regex.Match(src, @"private async Task<bool> CanManageChargeAsync.*?;", RegexOptions.Singleline);
            Assert.True(m.Success);
            Assert.Contains("_feeAccess.CanWriteAsync(charge.ClubId)", m.Value);
            Assert.DoesNotContain("IsClubAdminForClub", m.Value);
        }

        [Fact]
        public void Ingen_klubbskrivning_prövas_med_enbart_IsClubAdminForClub()
        {
            Assert.DoesNotContain("_auth.IsClubAdminForClub", ControllerSource());
        }
    }
}
