using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>"Visa publikt" (klubbsidorna steg 2d): vilka paneler som får öppnas för alla, golvet i skrivvägen och grindens tolkning av en publicerad sida.</summary>
    public class SitePublicPanelsTests
    {
        private static string Panel(string key, int vis) => "{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":" + vis + ",\"data\":{\"key\":\"" + key + "\",\"zone\":\"full\"}}";
        private static SiteContentResult Parse(string owner, string pageKey, string key, int vis, SiteVisibility page) =>
            SiteBlockSchema.Parse("{\"schema\":2,\"blocks\":[" + Panel(key, vis) + "]}", SiteContentKind.ConvertedPage, page, SiteTemplates.For(owner), pageKey);

        [Fact]
        public void Bara_de_utpekade_panelerna_får_vara_publika_och_aldrig_en_personpanel()
        {
            var allowed = new List<string>();
            foreach (var owner in new[] { "Club", "Region" })
                foreach (var d in PanelCatalog.For(owner).Where(d => d.PublicAllowed)) allowed.Add(owner + "/" + d.Key);
            Assert.Equal(new[] { "Club/about-text", "Club/ranges", "Club/calendar", "Club/competitions", "Club/records", "Region/records" }.OrderBy(x => x), allowed.OrderBy(x => x));
            Assert.All(PanelCatalog.Club.Concat(PanelCatalog.Region).Where(d => d.PublicAllowed), d => { Assert.False(d.PersonalData); Assert.Null(d.HostedBy); });
        }

        [Fact]
        public void Standardnivån_ändras_inte_utan_ett_aktivt_val()
        {
            // Mallens nivåer är orörda: golvet är bara en möjlighet.
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PanelVisibility("Club", "ranges", "ranges"));
            Assert.Equal(SiteVisibility.Alla, SiteConversion.PanelFloor("Club", "ranges", "ranges"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PanelFloor("Club", "documents", "document-archive"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PanelFloor("Club", "members", "member-directory"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PanelFloor("Region", "board", "board"));
            Assert.True(SiteConversion.CanShowPublicly("Club", "calendar", "calendar"));
            Assert.True(SiteConversion.CanShowPublicly("Club", "about", "about-text"));
            Assert.False(SiteConversion.CanShowPublicly("Club", "home", "about-text"));   // mallens utloggad-ruta på Hem
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PanelVisibility("Club", "about", "about-text"));   // standardnivån är oförändrad
            Assert.False(SiteConversion.CanShowPublicly("Region", "calendar", "calendar"));   // redan öppen
            Assert.False(SiteConversion.CanShowPublicly("Club", "documents", "document-archive"));
        }

        [Theory]
        [InlineData("Club", "about", "about-text")]
        [InlineData("Club", "ranges", "ranges")]
        [InlineData("Club", "calendar", "calendar")]
        [InlineData("Club", "competitions", "competitions")]
        [InlineData("Club", "records", "records")]
        [InlineData("Region", "records", "records")]
        public void En_publicerbar_panel_kan_sparas_som_Alla_på_en_öppen_sida_men_inte_på_en_sida_som_inte_är_öppen(string owner, string page, string key)
        {
            Assert.True(Parse(owner, page, key, 0, SiteVisibility.Alla).Ok);
            Assert.False(Parse(owner, page, key, 0, SiteVisibility.Inloggade).Ok);   // aldrig öppnare än sidan
            Assert.True(Parse(owner, page, key, 1, SiteVisibility.Inloggade).Ok);
        }

        [Theory]
        [InlineData("Club", "documents", "document-archive")]
        [InlineData("Club", "members", "member-directory")]
        [InlineData("Club", "members", "instructors")]
        [InlineData("Club", "members", "guldserie")]
        [InlineData("Club", "home", "motions")]
        [InlineData("Club", "home", "about-text")]   // Om-texten på Hem är mallens egen utloggad-ruta: aldrig valbar, aldrig Alla
        [InlineData("Region", "board", "board")]
        [InlineData("Region", "documents", "document-archive")]
        [InlineData("Region", "about", "region-instructors")]
        public void Övriga_paneler_kan_aldrig_vara_öppna_för_alla(string owner, string page, string key) =>
            Assert.False(Parse(owner, page, key, 0, SiteVisibility.Alla).Ok);

        [Fact]
        public void Sidans_golv_är_den_öppnaste_panelens()
        {
            Assert.Equal(SiteVisibility.Alla, SiteConversion.PageFloor("Club", "about"));
            Assert.Equal(SiteVisibility.Alla, SiteConversion.PageFloor("Club", "ranges"));
            Assert.Equal(SiteVisibility.Alla, SiteConversion.PageFloor("Club", "records"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PageFloor("Club", "documents"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PageFloor("Club", "members"));
            Assert.Equal(SiteVisibility.Inloggade, SiteConversion.PageFloor("Region", "board"));
        }

        private static ConvertedPage Converted(string owner, string pageKey, string key, int panelVis, SiteVisibility page, ConvertedState state = ConvertedState.Ok)
        {
            var content = SiteBlockSchema.Parse("{\"schema\":2,\"blocks\":[" + Panel(key, panelVis) + "]}", SiteContentKind.ConvertedPage, page, SiteTemplates.For(owner), pageKey);
            Assert.True(content.Ok, string.Join("; ", content.Errors.Select(e => e.Message)));
            return new ConvertedPage(pageKey, state, page, false, content);
        }

        [Fact]
        public void Grinden_säger_ja_bara_när_sidan_och_panelen_båda_är_öppna_för_alla_och_sidan_går_att_tolka()
        {
            Assert.True(SiteConvertedPages.IsPanelPublic(Converted("Club", "ranges", "ranges", 0, SiteVisibility.Alla), "Club", "ranges"));
            Assert.False(SiteConvertedPages.IsPanelPublic(Converted("Club", "ranges", "ranges", 1, SiteVisibility.Alla), "Club", "ranges"));        // panelen är bara för inloggade
            Assert.False(SiteConvertedPages.IsPanelPublic(Converted("Club", "ranges", "ranges", 1, SiteVisibility.Inloggade), "Club", "ranges"));   // sidan är inte öppen
            Assert.False(SiteConvertedPages.IsPanelPublic(Converted("Club", "ranges", "ranges", 0, SiteVisibility.Alla), "Club", "calendar"));      // fel panel
            Assert.False(SiteConvertedPages.IsPanelPublic(Converted("Club", "ranges", "ranges", 0, SiteVisibility.Alla, ConvertedState.Broken), "Club", "ranges"));
            Assert.False(SiteConvertedPages.IsPanelPublic(new ConvertedPage("ranges", ConvertedState.Ok, SiteVisibility.Alla, false, null), "Club", "ranges"));
        }

        [Fact]
        public void En_publik_sida_ger_utloggad_Serve_och_övriga_sidor_förblir_låsta_i_menyn()
        {
            var pages = new Dictionary<string, ConvertedPage> { ["calendar"] = Converted("Club", "calendar", "calendar", 0, SiteVisibility.Alla) };
            var template = SiteConvertedPages.Apply(SiteTemplates.ClubTemplate, pages);
            var anon = SiteViewer.Anonymous;
            Assert.Equal(SitePageDecision.Serve, SitePageAccessRules.Decide(template, template.PageByKey("calendar")!, anon));
            Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(template, template.PageByKey("ranges")!, anon));
            Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(template, template.PageByKey("records")!, anon));
            // utan omvandling (eller med brytare AV, då ConvertedSource ger tomt) är det som förut
            Assert.Equal(SitePageDecision.Locked, SitePageAccessRules.Decide(SiteTemplates.ClubTemplate, SiteTemplates.ClubTemplate.PageByKey("calendar")!, anon));
        }
    }
}
