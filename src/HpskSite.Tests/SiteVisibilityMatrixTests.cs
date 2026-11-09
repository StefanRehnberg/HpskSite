using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>"Vem ser sidan?" (tabellen som ersätter förhandsvisningen med namnkort): varje cell kommer ur samma regler som besökarna möter.</summary>
    public class SiteVisibilityMatrixTests
    {
        private static string[] Row(SitePagePreview.VisibilityMatrix m, string label) => m.Rows.Single(r => r.Label == label).States.ToArray();

        [Fact]
        public void Kolumnerna_är_de_fyra_fasta_tittarna_och_kretsen_har_sina_egna_namn()
        {
            var club = SitePagePreview.Matrix("Club", "Medlemmar", "members", SiteConversion.PageVisibility("Club", "members"), SiteConversion.Build("Club", "members", null));
            Assert.Equal(new[] { "anon", "logged", "member", "board" }, club.Columns.Select(c => c.Key));
            var region = SitePagePreview.Matrix("Region", "Hem", "home", SiteConversion.PageVisibility("Region", "home"), SiteConversion.Build("Region", "home", null));
            Assert.Contains(region.Columns, c => c.Label.Contains("krets", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void En_sida_bakom_inloggning_ger_utloggad_ett_hänglås_och_ingenting_av_innehållet()
        {
            var m = SitePagePreview.Matrix("Club", "Medlemmar", "members", SiteConversion.PageVisibility("Club", "members"), SiteConversion.Build("Club", "members", null));
            Assert.Equal("lock", Row(m, "Sidan i menyn")[0]);
            Assert.Equal("no", Row(m, "Medlemskatalogen")[0]);
            Assert.Equal("see", Row(m, "Medlemskatalogen")[1]);   // dagens regel för Medlemmar är inloggning
            Assert.Contains("hänglås", m.Summary);
        }

        [Fact]
        public void Inloggningsrutan_är_bara_för_utloggad_och_kommande_händelser_bara_för_inloggad()
        {
            var m = SitePagePreview.Matrix("Club", "Hem", "home", SiteConversion.PageVisibility("Club", "home"), SiteConversion.Build("Club", "home", null));
            Assert.Equal(new[] { "see", "no", "no", "no" }, Row(m, "Inloggningsruta"));
            Assert.Equal("no", Row(m, "Kommande händelser")[0]);
            Assert.Equal("see", Row(m, "Kommande händelser")[2]);
        }

        private static SiteContentResult Free(string blocksJson, SiteVisibility page) =>
            SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[" + blocksJson + "]}", SiteContentKind.FreePage, page, SiteTemplates.For("Club"), null);

        [Fact]
        public void Ett_block_är_aldrig_öppnare_än_sidan_och_en_fri_sida_ger_blockraderna_med_sin_text()
        {
            var content = Free("{\"id\":\"aaa11111\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"<p>Bara för medlemmar</p>\"}},{\"id\":\"bbb22222\",\"type\":\"heading\",\"visibility\":0,\"data\":{\"text\":\"Öppen rubrik\",\"level\":2}}", SiteVisibility.Alla);
            Assert.True(content.Ok, string.Join("; ", content.Errors.Select(e => e.Message)));
            var m = SitePagePreview.Matrix("Club", "Egen sida", null, SiteVisibility.Alla, content);
            Assert.Equal(new[] { "see", "see", "see", "see" }, Row(m, "Sidan i menyn"));
            var text = m.Rows.Single(r => r.Label == "Text");
            Assert.Equal(new[] { "no", "no", "see", "see" }, text.States);   // medlemmar och styrelse, inte utloggad eller inloggad icke-medlem
            Assert.Contains("Bara för medlemmar", text.Detail);
            Assert.Equal(new[] { "see", "see", "see", "see" }, m.Rows.Single(r => r.Label == "Rubrik").States);
            Assert.Equal("Utloggade kan öppna sidan.", m.Summary);
        }

        [Fact]
        public void En_stängd_sida_visar_inga_blockrader_som_synliga_för_den_som_inte_får_sidan()
        {
            var content = Free("{\"id\":\"ccc33333\",\"type\":\"heading\",\"visibility\":3,\"data\":{\"text\":\"Styrelsens rubrik\",\"level\":2}}", SiteVisibility.Styrelse);
            Assert.True(content.Ok);
            var m = SitePagePreview.Matrix("Club", "Styrelsesida", null, SiteVisibility.Styrelse, content);
            Assert.Equal(new[] { "no", "no", "no", "see" }, Row(m, "Sidan i menyn"));   // en dold sida syns inte ens med hänglås
            Assert.Equal(new[] { "no", "no", "no", "see" }, m.Rows.Single(r => r.Label == "Rubrik").States);
            Assert.Equal("Utloggade ser inte sidan alls.", m.Summary);
        }

        [Fact]
        public void Ett_personblock_blir_aldrig_synligt_för_utloggad_även_om_nivån_skulle_säga_Alla()
        {
            // Parse vägrar Alla på ett personblock; tabellen får ändå inte tro något annat om en sådan rad skulle finnas i lagrad data.
            var block = new SiteBlock("ddd44444", "contact", SiteVisibility.Alla, new System.Text.Json.Nodes.JsonObject());
            Assert.False(SiteBlockRenderer.BlockVisible(block, SiteVisibility.Alla, "Club", SiteViewer.Anonymous));
            Assert.True(SiteBlockRenderer.BlockVisible(block, SiteVisibility.Alla, "Club", new SiteViewer(true, false, false, false)));
        }
    }
}
