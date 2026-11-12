using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2a: EN valideringsfunktion för adressen till en egen sida (ren funktion).</summary>
    public class SitePageSlugRulesTests
    {
        private static readonly string[] None = Array.Empty<string>();

        private static SlugCheck Club(string? slug, string[]? taken = null, string[]? children = null) =>
            SitePageSlugRules.Check(slug, SiteTemplates.Club, taken ?? None, children ?? None);

        private static SlugCheck Region(string? slug, string[]? taken = null, string[]? children = null, string[]? clubs = null) =>
            SitePageSlugRules.Check(slug, SiteTemplates.Region, taken ?? None, children ?? None, clubs);

        [Theory]
        [InlineData("Regler för banan", "regler-for-banan")]
        [InlineData("  Årsmöte 2027!  ", "arsmote-2027")]
        [InlineData("Hitta hit & kontakt", "hitta-hit-kontakt")]
        [InlineData("Östra Ö", "ostra-o")]
        [InlineData("a---b", "a-b")]
        [InlineData("---", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("日本語", "")]
        public void SlugFrom_gör_en_titel_till_ett_förslag(string? title, string expected)
            => Assert.Equal(expected, SitePageSlugRules.SlugFrom(title));

        [Fact]
        public void SlugFrom_kortar_och_slutar_aldrig_på_bindestreck()
        {
            var s = SitePageSlugRules.SlugFrom(string.Join(" ", Enumerable.Repeat("ord", 40)));
            Assert.True(s.Length <= SitePageSlugRules.MaxLength);
            Assert.False(s.EndsWith('-'));
        }

        [Fact]
        public void En_fri_adress_är_fri()
        {
            Assert.True(Club("regler-for-banan").Ok);
            Assert.True(Region("regler-for-banan").Ok);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Stora")]
        [InlineData("med mellanslag")]
        [InlineData("-start")]
        [InlineData("slut-")]
        [InlineData("dubbel--streck")]
        [InlineData("åäö")]
        [InlineData("a/b")]
        [InlineData("a.b")]
        [InlineData("..")]
        public void Fel_form_avvisas(string? slug) => Assert.False(Club(slug).Ok);

        [Fact]
        public void För_lång_adress_avvisas()
            => Assert.False(Club(new string('a', SitePageSlugRules.MaxLength + 1)).Ok);

        [Theory]
        [InlineData("klubbar")]
        [InlineData("tavlingar")]
        [InlineData("handelser")]
        [InlineData("serier")]
        [InlineData("2027")]
        [InlineData("hemsidan")]
        [InlineData("admin")]
        [InlineData("media")]
        [InlineData("login-register")]
        public void Reserverade_ord_och_årtal_avvisas_hos_klubb_och_krets(string slug)
        {
            Assert.False(Club(slug).Ok);
            Assert.False(Region(slug).Ok);
        }

        [Fact]
        public void Mallens_sluggar_avvisas_hos_ägaren_vars_mall_de_tillhör()
        {
            foreach (var s in SiteTemplates.Slugs(SiteTemplates.Club)) Assert.False(Club(s).Ok, "klubb: " + s);
            foreach (var s in SiteTemplates.Slugs(SiteTemplates.Region)) Assert.False(Region(s).Ok, "krets: " + s);
        }

        [Fact]
        public void Upptagen_adress_av_vilken_sort_som_helst_avvisas_utan_skiftläge()
        {
            Assert.False(Club("sommarlagret", taken: new[] { "sommarlagret" }).Ok);
            Assert.False(Club("sommarlagret", taken: new[] { "SommarLagret" }).Ok);
            Assert.True(Club("sommarlagret", taken: new[] { "hostlagret" }).Ok);
        }

        [Fact]
        public void Barnnod_i_trädet_avvisas()
            => Assert.False(Club("gammal-handelse", children: new[] { "gammal-handelse" }).Ok);

        [Fact]
        public void Kretsens_klubbar_delar_namnrymd_med_kretsens_sidor_men_inte_med_en_klubbs()
        {
            Assert.False(Region("ankeborg-pistolklubb", clubs: new[] { "ankeborg-pistolklubb" }).Ok);
            Assert.True(Club("ankeborg-pistolklubb").Ok);
            Assert.True(SitePageSlugRules.Check("ankeborg-pistolklubb", SiteTemplates.Club, None, None, new[] { "ankeborg-pistolklubb" }).Ok);
        }

        [Fact]
        public void Suggest_numrerar_en_upptagen_adress()
        {
            Assert.Equal("regler", SitePageSlugRules.Suggest("regler", SiteTemplates.Club, None, None));
            Assert.Equal("regler-2", SitePageSlugRules.Suggest("regler", SiteTemplates.Club, new[] { "regler" }, None));
            Assert.Equal("regler-3", SitePageSlugRules.Suggest("regler", SiteTemplates.Club, new[] { "regler", "regler-2" }, None));
        }

        [Theory]
        [InlineData("kalender")]   // mallens slugg: ett annat namn, inte "kalender-2"
        [InlineData("klubbar")]
        [InlineData("2027")]
        [InlineData("Fel Form")]
        [InlineData("")]
        public void Suggest_numrerar_aldrig_ett_reserverat_ord(string slug)
            => Assert.Null(SitePageSlugRules.Suggest(slug, SiteTemplates.Club, None, None));

        [Fact]
        public void Suggest_håller_sig_inom_maxlängden()
        {
            var longSlug = new string('a', SitePageSlugRules.MaxLength);
            var s = SitePageSlugRules.Suggest(longSlug, SiteTemplates.Club, new[] { longSlug }, None);
            Assert.NotNull(s);
            Assert.True(s!.Length <= SitePageSlugRules.MaxLength);
            Assert.True(SitePageSlugRules.Check(s, SiteTemplates.Club, new[] { longSlug }, None).Ok);
        }
    }
}
