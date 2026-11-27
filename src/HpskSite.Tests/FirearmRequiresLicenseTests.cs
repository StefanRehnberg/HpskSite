using System.Runtime.CompilerServices;
using HpskSite.Controllers;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Licenspliktigt/licensfritt vapen (medlemsönskemål 2026-11-25: luftpistoler räknades med på
    /// föreningsintyget). Flaggan är nullbar i skrivbegäran — en begäran som inte nämner den får aldrig
    /// tyst ändra den — och varje räkning som hamnar på ett intyg eller i en licenspåminnelse måste
    /// filtrera på den.
    /// </summary>
    public class FirearmRequiresLicenseTests
    {
        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("true", null)]
        public void Formuläret_mappar_bara_1_och_0_allt_annat_rör_inte_flaggan(string? input, bool? expected)
        {
            var form = new FirearmFormRequest { Alias = "x", RequiresLicense = input };
            Assert.Equal(expected, form.ToWriteRequest().RequiresLicense);
        }

        [Fact]
        public void Intygsräkningen_räknar_bara_licenspliktiga_vapen()
        {
            var sql = ReadSite("Services", "Firearms", "FirearmService.cs");
            var body = Between(sql, "public int CountHeldInFederation", "// ── Etikettens kod");
            Assert.Contains("f.RequiresLicense = 1", body);
        }

        [Fact]
        public void Licenspåminnelserna_gäller_bara_licenspliktiga_vapen()
        {
            var src = ReadSite("Services", "Firearms", "FirearmReminderHostedService.cs");
            var body = Between(src, "private List<DueRow> LoadDue", "return new List<DueRow>();");
            Assert.Contains("RequiresLicense = 1", body);
        }

        [Fact]
        public void Intyg_kan_inte_begäras_för_ett_licensfritt_vapen()
        {
            var src = ReadSite("Services", "Firearms", "ForeningsintygRequestService.cs");
            Assert.Contains("!firearm.RequiresLicense", src);
        }

        [Fact]
        public void Ett_licensfritt_vapen_får_inget_förfallodatum()
        {
            var src = ReadSite("Services", "Firearms", "FirearmService.cs");
            Assert.Contains("(request.RequiresLicense ?? true) ? request.LicenseExpiresOn : null", src);
            Assert.Contains("requiresLicense ? request.LicenseExpiresOn : null", src);
        }

        private static string Between(string src, string from, string to)
        {
            var a = src.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, $"Hittade inte '{from}'.");
            var b = src.IndexOf(to, a, StringComparison.Ordinal);
            Assert.True(b > a, $"Hittade inte '{to}' efter '{from}'.");
            return src[a..b];
        }

        private static string ReadSite(string folder, string sub, string file, [CallerFilePath] string here = "") =>
            File.ReadAllText(Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", folder, sub, file)));
    }
}
