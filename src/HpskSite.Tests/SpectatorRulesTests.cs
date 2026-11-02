using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (A7/H3): vad åskådarlistan får visa.</summary>
    public class SpectatorRulesTests
    {
        [Theory]
        [InlineData("Anna Svensson", "Anna Svensson")]
        [InlineData("  Anna  ", "Anna")]
        [InlineData(null, "Åskådare")]
        [InlineData("", "Åskådare")]
        [InlineData("   ", "Åskådare")]
        [InlineData("\u0007\u0000", "Åskådare")]
        public void CleanName_trimmar_och_tar_bort_styrtecken(string? input, string expected) =>
            Assert.Equal(expected, SpectatorRules.CleanName(input));

        [Fact]
        public void CleanName_kapar_lange_namn()
        {
            var cleaned = SpectatorRules.CleanName(new string('x', 500));
            Assert.Equal(SpectatorRules.MaxNameLength, cleaned.Length);
        }

        [Fact]
        public void CleanName_lamnar_html_som_text_men_klienten_skriver_med_textContent()
        {
            // Servern gör inte om tecknen (HTML-kodning hör till utskriften), men namnet kan aldrig bli längre än taket.
            Assert.Equal("\"><img src=x onerror=alert(1)>", SpectatorRules.CleanName("\"><img src=x onerror=alert(1)>"));
        }

        [Theory]
        [InlineData("/media/abc/foto.jpg", "/media/abc/foto.jpg")]
        [InlineData("https://pistol.nu/media/abc.jpg", "https://pistol.nu/media/abc.jpg")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("javascript:alert(1)", "")]
        [InlineData("http://evil.example/x.png", "")]          // bara https
        [InlineData("//evil.example/x.png", "")]               // protokollrelativ
        [InlineData("/a.jpg\" onerror=\"alert(1)", "")]        // bryter ut ur attributet
        [InlineData("/a.jpg onerror=alert(1)", "")]
        [InlineData("/a.jpg<script>", "")]
        [InlineData("data:image/png;base64,AAAA", "")]
        public void CleanPictureUrl(string? input, string expected) =>
            Assert.Equal(expected, SpectatorRules.CleanPictureUrl(input));

        [Fact]
        public void CleanPictureUrl_nekar_for_langa_adresser() =>
            Assert.Equal("", SpectatorRules.CleanPictureUrl("/" + new string('a', 400)));

        [Fact]
        public void Taken_ar_satta_med_marginal()
        {
            Assert.True(SpectatorRules.MaxSpectatorsPerMatch >= 100);
            Assert.True(SpectatorRules.MaxTrackedMatches >= 500);
        }
    }
}
