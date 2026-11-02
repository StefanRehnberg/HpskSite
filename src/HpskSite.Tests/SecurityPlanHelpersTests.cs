using HpskSite.Services.Hosting;
using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02: de små rena hjälparna i hotfix 1a.</summary>
    public class SecurityPlanHelpersTests
    {
        [Theory]
        [InlineData("anna.svensson@gmail.com", "a***@gmail.com")]
        [InlineData("x@y.se", "x***@y.se")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("ingen-snabela", "***")]
        public void MaskedEmail_doljer_det_mesta(string? input, string expected) =>
            Assert.Equal(expected, MaskedEmail.Mask(input));

        [Theory]
        [InlineData("https://pistol.nu", "https://pistol.nu")]
        [InlineData("https://pistol.nu/", "https://pistol.nu")]
        [InlineData("http://localhost:18233", "http://localhost:18233")]
        [InlineData("https://your-domain.com", null)]   // mallens platshållare
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("inte-en-adress", null)]
        [InlineData("ftp://pistol.nu", null)]
        public void PublicUrls_UsableSiteUrl(string? input, string? expected) =>
            Assert.Equal(expected, PublicUrls.UsableSiteUrl(input));
    }
}
