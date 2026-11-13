using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Snabblänkar och Kretsrekord-genvägen är borttagna (Stefan 2026-11-13). En redan sparad omvandlad Hem som har dem ska läsas som förut, utan dem — aldrig bli "kan inte visas".</summary>
    public class SiteRetiredPanelTests
    {
        private const string ClubHemMedSnabblankar =
            "{\"schema\":2,\"blocks\":[" +
            "{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":5,\"data\":{\"key\":\"about-text\",\"zone\":\"main\"}}," +
            "{\"id\":\"pnl08\",\"type\":\"panel\",\"visibility\":1,\"data\":{\"key\":\"quicklinks\",\"zone\":\"side\"}}," +
            "{\"id\":\"pnl03\",\"type\":\"panel\",\"visibility\":1,\"data\":{\"key\":\"upcoming-events\",\"zone\":\"main\"}}]}";

        [Fact]
        public void En_sparad_Hem_med_Snabblänkar_läses_utan_panelen_och_är_giltig()
        {
            var c = SiteBlockSchema.Parse(ClubHemMedSnabblankar, SiteContentKind.ConvertedPage, SiteVisibility.Alla, SiteTemplates.ClubTemplate, "home");
            Assert.True(c.Ok, string.Join("; ", c.Errors.Select(e => e.Message)));
            var keys = c.Blocks.Select(b => b.Data["key"]!.GetValue<string>()).ToArray();
            Assert.Equal(new[] { "about-text", "upcoming-events" }, keys);
        }

        [Fact]
        public void Editorn_får_sidans_JSON_utan_de_borttagna_panelerna_och_orörd_text_förblir_orörd()
        {
            var cleaned = SiteBlockSchema.DropRetiredPanels(ClubHemMedSnabblankar)!;
            Assert.DoesNotContain("quicklinks", cleaned);
            Assert.Contains("about-text", cleaned);
            Assert.Contains("upcoming-events", cleaned);
            var untouched = "{\"schema\":2,\"blocks\":[{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":5,\"data\":{\"key\":\"about-text\",\"zone\":\"main\"}}]}";
            Assert.Same(untouched, SiteBlockSchema.DropRetiredPanels(untouched));
            Assert.Null(SiteBlockSchema.DropRetiredPanels((string?)null));
        }

        [Fact]
        public void Snabblänkar_finns_inte_i_registret_men_en_okänd_panel_avvisas_fortfarande()
        {
            Assert.Null(PanelCatalog.Find(SiteTemplates.Club, "quicklinks"));
            Assert.Null(PanelCatalog.Find(SiteTemplates.Region, "quicklinks"));
            Assert.Null(PanelCatalog.Find(SiteTemplates.Region, "quicklink-records"));
            Assert.NotNull(PanelCatalog.Find(SiteTemplates.Club, "quicklink-docs"));       // dokumentgenvägarna lever kvar som eget kort
            Assert.NotNull(PanelCatalog.Find(SiteTemplates.Region, "quicklink-docs"));
            var typo = ClubHemMedSnabblankar.Replace("\"quicklinks\"", "\"quicklinkz\"");
            Assert.False(SiteBlockSchema.Parse(typo, SiteContentKind.ConvertedPage, SiteVisibility.Alla, SiteTemplates.ClubTemplate, "home").Ok);
        }
    }
}
