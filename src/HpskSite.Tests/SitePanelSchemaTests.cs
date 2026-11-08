using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Schema 2 och panelblocket (konvertera-planen v2, K1): bara på en omvandlad systemsida, bara på sin egen sida, högst en gång, personnivån ur registret.</summary>
    public class SitePanelSchemaTests
    {
        private static readonly SiteTemplate Club = SiteTemplates.ClubTemplate;
        private static readonly SiteTemplate Region = SiteTemplates.RegionTemplate;

        private static string Panel(string id, string key, int vis, string? zone = null) =>
            "{\"id\":\"" + id + "\",\"type\":\"panel\",\"visibility\":" + vis + ",\"data\":{\"key\":\"" + key + "\"" + (zone == null ? "" : ",\"zone\":\"" + zone + "\"") + "}}";

        private static string Page(int schema, params string[] blocks) => "{\"schema\":" + schema + ",\"blocks\":[" + string.Join(",", blocks) + "]}";

        private static SiteContentResult Converted(string json, string pageKey, SiteVisibility page = SiteVisibility.Alla, SiteTemplate? t = null) =>
            SiteBlockSchema.Parse(json, SiteContentKind.ConvertedPage, page, t ?? Club, pageKey);

        [Fact]
        public void En_omvandlad_sida_med_paneler_och_egna_block_läses_och_lagras_som_schema_2()
        {
            var json = Page(2, Panel("abcd1", "calendar", 2), "{\"id\":\"abcd2\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"<p>Hej</p>\"}}");
            var r = Converted(json, "calendar", SiteVisibility.Klubbmedlemmar);
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
            Assert.Equal(2, r.Blocks.Count);
            var stored = SiteBlockSchema.ToJson(r);
            Assert.Contains("\"schema\":2", stored);
            Assert.Equal(stored, SiteBlockSchema.ToJson(Converted(stored, "calendar", SiteVisibility.Klubbmedlemmar)));   // en omläsning ger samma form
            Assert.Equal(2, SiteBlockSchema.SchemaNumberOf(stored));
        }

        [Fact]
        public void Panelens_zon_får_sitt_förval_ur_registret_och_en_okänd_zon_avvisas()
        {
            var r = Converted(Page(2, Panel("abcd1", "upcoming-events", 2)), "home", SiteVisibility.Klubbmedlemmar);
            Assert.True(r.Ok);
            Assert.Equal("main", r.Blocks[0].Data["zone"]!.GetValue<string>());
            Assert.False(Converted(Page(2, Panel("abcd1", "upcoming-events", 2, "mitten")), "home", SiteVisibility.Klubbmedlemmar).Ok);
        }

        [Fact]
        public void Ett_panelblock_finns_bara_på_en_omvandlad_sida_inte_på_en_fri_sida_och_inte_i_en_plats()
        {
            Assert.False(SiteBlockSchema.Parse(Page(1, Panel("abcd1", "calendar", 2)), SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, Club).Ok);
            var slots = "{\"schema\":1,\"slots\":{\"before\":[" + Panel("abcd1", "calendar", 2) + "],\"after\":[]}}";
            Assert.False(SiteBlockSchema.Parse(slots, SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, Club, "calendar").Ok);
            Assert.False(SiteBlockSchema.Parse(Page(2, Panel("abcd1", "calendar", 2)), SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, Club).Ok);   // schema 2 på en fri sida
            // Även med en giltig sidnyckel och rätt schemanummer: typen själv är bara för omvandlade sidor (inte bara en bieffekt av andra regler).
            var forced = SiteBlockSchema.Parse(Page(1, Panel("abcd1", "calendar", 2)), SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, Club, "calendar");
            Assert.False(forced.Ok);
            Assert.Contains(forced.Errors, e => e.Message.Contains("omvandlad"));
        }

        [Fact]
        public void En_panel_hör_bara_hemma_på_sin_egen_sida_och_okända_paneler_avvisas()
        {
            Assert.False(Converted(Page(2, Panel("abcd1", "calendar", 2)), "about", SiteVisibility.Klubbmedlemmar).Ok);          // Kalender på Om klubben
            Assert.False(Converted(Page(2, Panel("abcd1", "nonexistent", 2)), "calendar", SiteVisibility.Klubbmedlemmar).Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "stats-card", 2)), "home", SiteVisibility.Klubbmedlemmar).Ok);         // kretsens panel på en klubb
            Assert.True(Converted(Page(2, Panel("abcd1", "stats-card", 2)), "home", SiteVisibility.Klubbmedlemmar, Region).Ok);
            Assert.True(Converted(Page(2, Panel("abcd1", "calendar", 2)), "calendar", SiteVisibility.Klubbmedlemmar, Region).Ok);   // kretsen har en egen kalenderpanel
            Assert.False(Converted(Page(2, Panel("abcd1", "calendar", 2)), "admin", SiteVisibility.Klubbmedlemmar).Ok);          // Admin är ingen omvandlingsbar sida
            Assert.False(Converted(Page(2, Panel("abcd1", "calendar", 2)), null!, SiteVisibility.Klubbmedlemmar).Ok);           // sidan måste vara känd
        }

        [Fact]
        public void En_panel_får_ligga_högst_en_gång_på_sidan_men_egna_block_får_upprepas_i_sort()
        {
            Assert.False(Converted(Page(2, Panel("abcd1", "calendar", 2), Panel("abcd2", "calendar", 2)), "calendar", SiteVisibility.Klubbmedlemmar).Ok);
            var two = Page(2, "{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"a\"}}", "{\"id\":\"abcd2\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"b\"}}");
            Assert.True(Converted(two, "calendar", SiteVisibility.Klubbmedlemmar).Ok);
        }

        [Fact]
        public void En_personpanel_kan_aldrig_vara_öppen_för_alla_och_nivån_kommer_ur_registret()
        {
            Assert.False(Converted(Page(2, Panel("abcd1", "member-directory", 0)), "members", SiteVisibility.Alla).Ok);
            Assert.True(Converted(Page(2, Panel("abcd1", "member-directory", 1)), "members", SiteVisibility.Alla).Ok);
            // Kalendern är inte en personpanel (den tas inte ner av personuppgiftstaket), men den får aldrig vara öppnare än mallens nivå (K5c).
            var calendarFloor = (int)SiteConversion.PanelVisibility("Club", "calendar", "calendar");
            Assert.True(Converted(Page(2, Panel("abcd1", "calendar", calendarFloor)), "calendar", SiteVisibility.Alla).Ok);
            Assert.False(calendarFloor > 0 && Converted(Page(2, Panel("abcd1", "calendar", 0)), "calendar", SiteVisibility.Alla).Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "board", 0)), "board", SiteVisibility.Alla, Region).Ok);
        }

        [Fact]
        public void Ett_block_kan_inte_vara_öppnare_än_den_omvandlade_sidan()
        {
            Assert.False(Converted(Page(2, Panel("abcd1", "calendar", 0)), "calendar", SiteVisibility.Klubbmedlemmar).Ok);
        }

        [Fact]
        public void Mallen_avgör_vilka_paneler_som_bara_finns_för_utloggade_och_de_lagras_som_Utloggad_och_inget_annat()
        {
            Assert.True(Converted(Page(2, Panel("abcd1", "login-prompt", 5)), "home").Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "login-prompt", 0)), "home").Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "login-prompt", 1)), "home").Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "contact-card", 5)), "home").Ok);
            // Om-texten är utloggad-bara på Hem men en vanlig inloggad panel på Om klubben: samma panel, olika regel per sida.
            Assert.True(Converted(Page(2, Panel("abcd1", "about-text", 5)), "home").Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "about-text", 1)), "home").Ok);
            Assert.False(Converted(Page(2, Panel("abcd1", "about-text", 5)), "about").Ok);
            Assert.True(Converted(Page(2, Panel("abcd1", "about-text", 1)), "about").Ok);
            Assert.False(Converted(Page(2, "{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":5,\"data\":{\"html\":\"x\"}}"), "home").Ok);   // ett vanligt block är aldrig Utloggad
        }

        [Fact]
        public void Inloggningsrutan_kan_aldrig_väljas_av_en_klubb()
        {
            Assert.DoesNotContain(PanelCatalog.Selectable(SiteTemplates.Club, "home"), d => d.Key == "login-prompt");
            Assert.DoesNotContain(PanelCatalog.Selectable(SiteTemplates.Club, "home"), d => d.Key == "about-text");   // utloggad-bara på Hem
            Assert.Contains(PanelCatalog.Selectable(SiteTemplates.Club, "about"), d => d.Key == "about-text");        // men valbar på Om klubben
            Assert.Contains(PanelCatalog.Selectable(SiteTemplates.Club, "home"), d => d.Key == "contact-card");
        }

        [Fact]
        public void KindOf_väljer_formen_ur_sidan_och_versionens_schema()
        {
            Assert.Equal(SiteContentKind.FreePage, SiteBlockSchema.KindOf(true, Page(1)));
            Assert.Equal(SiteContentKind.FreePage, SiteBlockSchema.KindOf(true, Page(2)));                       // en fri sida blir aldrig omvandlad
            Assert.Equal(SiteContentKind.TemplateCopy, SiteBlockSchema.KindOf(false, "{\"schema\":1,\"slots\":{}}"));
            Assert.Equal(SiteContentKind.ConvertedPage, SiteBlockSchema.KindOf(false, Page(2)));
            Assert.Equal(SiteContentKind.TemplateCopy, SiteBlockSchema.KindOf(false, "inte json"));
            Assert.Equal(SiteContentKind.TemplateCopy, SiteBlockSchema.KindOf(false, null));
            Assert.Equal(SiteContentKind.TemplateCopy, SiteBlockSchema.KindOf(false, "{\"schema\":3}"));         // okänt schema avvisas av Parse, inte tolkas som omvandlat
        }

        [Fact]
        public void Varje_form_kräver_sitt_eget_schemanummer()
        {
            Assert.False(SiteBlockSchema.Parse(Page(1), SiteContentKind.ConvertedPage, SiteVisibility.Alla, Club, "calendar").Ok);
            Assert.True(SiteBlockSchema.Parse(Page(2), SiteContentKind.ConvertedPage, SiteVisibility.Alla, Club, "calendar").Ok);
            Assert.False(SiteBlockSchema.Parse(Page(2), SiteContentKind.FreePage, SiteVisibility.Alla, Club).Ok);
            Assert.False(SiteBlockSchema.Parse("{\"schema\":2,\"slots\":{\"before\":[],\"after\":[]}}", SiteContentKind.TemplateCopy, SiteVisibility.Alla, Club, "calendar").Ok);
            Assert.False(SiteBlockSchema.Parse("{\"schema\":2,\"slots\":{\"before\":[],\"after\":[]}}", SiteContentKind.ConvertedPage, SiteVisibility.Alla, Club, "calendar").Ok);   // platser finns inte i schema 2
        }
    }
}
