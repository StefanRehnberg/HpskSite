using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Utseendet (grunddesign + grundfärg): paletten är kuraterad och varje färg ska vara läsbar i ljust och mörkt läge,
    /// okända värden blir standard, och CSS:en kommer bara ur vitlistan.
    /// </summary>
    public class SiteAppearanceTests
    {
        public static IEnumerable<object[]> Colors() => SiteAppearance.Palette.Select(p => new object[] { p.Key });

        [Theory, MemberData(nameof(Colors))]
        public void Every_palette_color_is_readable_in_light_and_dark_mode(string key)
        {
            var hex = SiteAppearance.ColorOf(key).Hex;
            var s = SiteAppearance.Scheme.For(hex);
            Assert.True(SiteAppearance.Contrast(hex, SiteAppearance.WhiteHex) >= 4.5, $"{key}: vit text på knappen");
            Assert.True(SiteAppearance.Contrast(s.Hover, SiteAppearance.WhiteHex) >= 4.5, $"{key}: vit text på knappen vid hover");
            Assert.True(SiteAppearance.Contrast(s.LightEmphasis, s.LightSubtle) >= 4.5, $"{key}: text på dämpad bakgrund, ljust");
            Assert.True(SiteAppearance.Contrast(s.DarkLink, SiteAppearance.DarkBodyHex) >= 4.5, $"{key}: länk i mörkt läge");
            Assert.True(SiteAppearance.Contrast(s.DarkLink, s.DarkSubtle) >= 4.5, $"{key}: text på dämpad bakgrund, mörkt");
            Assert.True(SiteAppearance.Contrast(s.DarkHover, SiteAppearance.DarkBodyHex) >= 4.5, $"{key}: länk vid hover i mörkt läge");
        }

        [Fact]
        public void The_default_color_is_blue_and_draws_no_css_of_its_own()
        {
            Assert.Equal("bla", SiteAppearance.DefaultPalette);
            Assert.Equal("", SiteAppearance.Css("bla"));
            Assert.Equal("", SiteAppearance.Css(null));
            Assert.False(SiteAppearance.LookOf(null, null).HasCustomColor);
            Assert.Equal("standard", SiteAppearance.DefaultHeader);
        }

        [Theory]
        [InlineData(null), InlineData(""), InlineData("rosa"), InlineData("#ff0000"), InlineData("bla; } body{display:none"), InlineData("STANDARD")]
        public void Unknown_values_fall_back_to_the_default(string? raw)
        {
            Assert.Equal(SiteAppearance.DefaultPalette, SiteAppearance.NormalizePalette(raw));
            Assert.Equal(SiteAppearance.DefaultHeader, SiteAppearance.NormalizeHeader(raw));
            Assert.False(SiteAppearance.IsValidPalette(raw));
            Assert.False(SiteAppearance.IsValidHeader(raw));
            Assert.Equal("", SiteAppearance.Css(raw));
        }

        [Fact]
        public void Css_is_scoped_to_the_owner_pages_and_built_only_from_the_whitelist()
        {
            foreach (var p in SiteAppearance.Palette.Where(p => p.Key != SiteAppearance.DefaultPalette))
            {
                var css = SiteAppearance.Css(p.Key);
                Assert.Contains(p.Hex, css);
                Assert.DoesNotContain("<", css);
                foreach (var line in css.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Assert.Contains("body." + SiteAppearance.BodyClass, line); // inget som slår igenom på sajtens övriga sidor
            }
        }

        [Fact]
        public void Keys_are_unique_and_the_standard_header_comes_first()
        {
            Assert.Equal(SiteAppearance.Headers.Count, SiteAppearance.Headers.Select(h => h.Key).Distinct().Count());
            Assert.Equal(SiteAppearance.Palette.Count, SiteAppearance.Palette.Select(p => p.Key).Distinct().Count());
            Assert.Equal(SiteAppearance.DefaultHeader, SiteAppearance.Headers[0].Key);
            Assert.Equal(SiteAppearance.DefaultPalette, SiteAppearance.Palette[0].Key);
        }

        [Fact]
        public void Header_fill_keeps_todays_purple_only_for_the_default_color()
        {
            Assert.Contains("#667eea", SiteAppearance.HeaderFill("bla"));
            Assert.DoesNotContain("#667eea", SiteAppearance.HeaderFill("skog"));
            Assert.Contains("#166534", SiteAppearance.HeaderFill("skog"));
        }
    }
}
