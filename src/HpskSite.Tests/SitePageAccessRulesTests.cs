using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// F0-spik a: beslutet efter inloggningen. Utloggad på en stängd sida → inloggning; inloggad utan rätt → 404
    /// (Styrelse- och Dold-sidor avslöjas inte); med rätt → visa. Ren funktion, ingen databas.
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
        public void Utloggad_på_en_inloggningssida_skickas_till_inloggning()
            => Assert.Equal(SitePageDecision.LoginRedirect, SitePageAccessRules.Decide(T, Page(SiteVisibility.Inloggade), Anon));

        [Fact]
        public void Utloggad_på_en_medlemssida_skickas_till_inloggning()
            => Assert.Equal(SitePageDecision.LoginRedirect, SitePageAccessRules.Decide(T, Page(SiteVisibility.Klubbmedlemmar), Anon));

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
        public void Inloggad_som_inte_är_medlem_får_404_på_medlemssida()
            => Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(T, Page(SiteVisibility.Klubbmedlemmar), Logged));

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
