using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Ikonbiblioteket för egna sidor: nyckeln hamnar i en CSS-klass, så bara nycklar ur listan får passera.</summary>
    public class SitePageIconsTests
    {
        [Fact]
        public void Every_key_is_a_bootstrap_icon_class_and_unique()
        {
            Assert.NotEmpty(SitePageIcons.All);
            Assert.All(SitePageIcons.All, i => Assert.Matches("^bi-[a-z0-9-]+$", i.Key));
            Assert.Equal(SitePageIcons.All.Count, SitePageIcons.All.Select(i => i.Key).Distinct().Count());
            Assert.Contains(SitePageIcons.All, i => i.Key == SitePageIcons.Default);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("  ", null)]
        [InlineData("bi-file-earmark-text", null)]            // standardikonen lagras som "ingen vald"
        [InlineData("bi-trophy", "bi-trophy")]
        [InlineData(" bi-trophy ", "bi-trophy")]
        [InlineData("bi-trophy\" onclick=\"x", null)]         // fri text släpps aldrig igenom
        [InlineData("bi-finns-inte", null)]
        public void Normalize_keeps_only_known_keys(string? input, string? expected) =>
            Assert.Equal(expected, SitePageIcons.Normalize(input));

        [Fact]
        public void Resolve_falls_back_to_the_default_icon()
        {
            Assert.Equal(SitePageIcons.Default, SitePageIcons.Resolve(null));
            Assert.Equal(SitePageIcons.Default, SitePageIcons.Resolve("<script>"));
            Assert.Equal("bi-trophy", SitePageIcons.Resolve("bi-trophy"));
        }
    }
}
