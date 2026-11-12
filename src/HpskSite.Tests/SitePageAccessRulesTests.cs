using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Beslutet per begäran (Besökarens vy). En sida en vanlig medlem får se men inte tittaren (utloggad, eller inloggad utan medlemskap) → Locked (hänglås
    /// i menyn + en vänlig ruta); Styrelse- och Dold-sidor → 404 åt alla utan rätt (de avslöjas inte); med rätt → visa. Ren funktion, ingen databas.
    /// </summary>
    public class SitePageAccessRulesTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);

        private static SitePageDef Page(SiteVisibility v) =>
            new("x", "x", "X", 1, new[] { new SiteBlockDef("b", v) });

        private static SiteTemplate T => SiteTemplates.ClubTemplate;

        [Fact]
        public void Öppen_sida_visas_för_alla_inklusive_utloggad()
            => Assert.Equal(SitePageDecision.Serve, SitePageAccessRules.Decide(T, Page(SiteVisibility.Alla), Anon));

        [Fact]
        public void Utloggad_på_en_inloggningssida_får_en_låst_sida()
            => Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(T, Page(SiteVisibility.Inloggade), Anon));

        [Fact]
        public void Utloggad_på_en_medlemssida_får_en_låst_sida()
            => Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(T, Page(SiteVisibility.Klubbmedlemmar), Anon));

        // Styrelse och Dold ger 404 åt ALLA utan rätt, även utloggade: en inloggningsomdirigering på just den adressen
        // skulle avslöja att en dold sida finns (plan v2 §4).
        [Theory]
        [InlineData(SiteVisibility.Styrelse)]
        [InlineData(SiteVisibility.Dold)]
        public void Utloggad_på_en_styrelse_eller_dold_sida_får_404_inte_inloggning(SiteVisibility v)
            => Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(T, Page(v), Anon));

        [Theory]
        [InlineData(SiteVisibility.Styrelse)]
        [InlineData(SiteVisibility.Dold)]
        public void Inloggad_utan_rätt_får_404(SiteVisibility v)
            => Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(T, Page(v), Logged));

        [Fact]
        public void Inloggad_som_inte_är_medlem_får_en_låst_sida_på_medlemssida()
            => Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(T, Page(SiteVisibility.Klubbmedlemmar), Logged));

        [Fact]
        public void En_låst_sida_kräver_medlemskap_bara_när_inloggning_inte_räcker_och_Admin_är_aldrig_låst()
        {
            Assert.False(SitePageAccessRules.NeedsMembership(T, Page(SiteVisibility.Inloggade)));      // räcker med inloggning: texten "kräver inloggning"
            Assert.True(SitePageAccessRules.NeedsMembership(T, Page(SiteVisibility.Klubbmedlemmar)));  // texten "för medlemmar"
            Assert.True(SitePageAccessRules.IsLockedFor(T, Page(SiteVisibility.Klubbmedlemmar), Logged));
            Assert.False(SitePageAccessRules.IsLockedFor(T, Page(SiteVisibility.Styrelse), Anon));      // hänglåset avslöjar aldrig en dold sida
            Assert.False(SitePageAccessRules.IsLockedFor(T, Page(SiteVisibility.Dold), Logged));
            var admin = T.Pages.First(p => p.IsAdmin);
            Assert.False(SitePageAccessRules.IsLockedFor(T, admin, Anon));                              // Admin har ingen egen adress: aldrig en låst post
        }

        [Fact]
        public void Medlem_ser_medlemssida_men_inte_styrelsesida()
        {
            Assert.Equal(SitePageDecision.Serve, SitePageAccessRules.Decide(T, Page(SiteVisibility.Klubbmedlemmar), Member));
            Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(T, Page(SiteVisibility.Styrelse), Member));
        }

        [Theory]
        [InlineData(SiteVisibility.Klubbmedlemmar)]
        [InlineData(SiteVisibility.Styrelse)]
        [InlineData(SiteVisibility.Dold)]
        public void Admin_ser_allt(SiteVisibility v)
            => Assert.Equal(SitePageDecision.Serve, SitePageAccessRules.Decide(T, Page(v), Admin));

        [Fact]
        public void Okänd_synlighet_är_aldrig_synlig()
            => Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(T, Page((SiteVisibility)99), Admin));
    }
}
