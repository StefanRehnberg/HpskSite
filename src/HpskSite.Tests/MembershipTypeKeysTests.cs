using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Medlemstypens nycklar: dagens lagrade text, skiftlägesokänslig matchning, förval på nyckeln.</summary>
    public class MembershipTypeKeysTests
    {
        [Fact]
        public void Standardnycklarna_ar_dagens_lagrade_text()
            => Assert.Equal(new[] { "Senior", "Pensionär", "Junior", "Familj", "Hedersmedlem", "Ständig", "Stödjande" },
                MembershipTypeKeys.Standard);

        [Theory]
        [InlineData("familj", "Familj")]
        [InlineData("  STÄNDIG ", "Ständig")]
        [InlineData("hedersmedlem", "Hedersmedlem")]
        public void Standardnyckel_matchas_skiftlagesokant(string text, string key)
            => Assert.Equal(key, MembershipTypeKeys.StandardKey(text));

        [Theory]
        [InlineData("Veteran")]
        [InlineData("Heder")]            // gammal kortform är okänd text, inte Hedersmedlem
        [InlineData("Ständig medlem")]
        [InlineData("Familjemedlem")]
        [InlineData("")]
        [InlineData(null)]
        public void Okand_text_ar_ingen_standardnyckel(string? text)
            => Assert.Null(MembershipTypeKeys.StandardKey(text));

        [Fact]
        public void Tomt_ar_aldrig_samma_som_tomt()
        {
            Assert.False(MembershipTypeKeys.Same(null, null));
            Assert.False(MembershipTypeKeys.Same("", " "));
            Assert.True(MembershipTypeKeys.Same("senior", " Senior"));
        }

        [Theory]
        [InlineData("Senior", true)]
        [InlineData("Pensionär", true)]
        [InlineData("Junior", true)]
        [InlineData("Stödjande", true)]
        [InlineData("Familj", false)]
        [InlineData("hedersmedlem", false)]
        [InlineData("Ständig", false)]
        [InlineData("x-student", true)]   // egen typ beter sig som Senior
        [InlineData("Familjemedlem", true)] // inget namnprefix längre
        [InlineData("Veteran", true)]
        [InlineData(null, true)]
        public void Forval_for_medlemmen_kan_valja_hanger_pa_nyckeln(string? type, bool expected)
            => Assert.Equal(expected, MembershipTypeKeys.DefaultMemberSelectable(type));

        [Fact]
        public void Kategorins_forval_gar_genom_nycklarna()
            => Assert.False(MembershipFeeCategory.DefaultMemberSelectable("Familj"));

        private static string Src(string rel, [CallerFilePath] string thisFile = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "HpskSite", rel)));

        [Fact]
        public void Hushallsdialogen_skriver_Familj_via_konstanten()
        {
            var src = Src(Path.Combine("Controllers", "ClubMembershipController.cs"));
            Assert.DoesNotMatch(new Regex(@"MembershipType\s*=\s*""Familj"""), src);
            Assert.Equal(2, Regex.Matches(src, @"MembershipType = MembershipTypeKeys\.Familj;").Count);
        }
    }
}
