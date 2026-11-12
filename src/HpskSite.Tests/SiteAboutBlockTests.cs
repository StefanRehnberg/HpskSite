using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Blockbiblioteket, första blocket (Om klubben / Om kretsen): presentationstexten läses live, lagras aldrig i sidan, saneras igen vid ritning och följer blockets nivå.</summary>
    public class SiteAboutBlockTests
    {
        private static SiteContentResult Parse(string data, int vis = 2, SiteContentKind kind = SiteContentKind.FreePage, bool region = false)
        {
            var b = "{\"id\":\"abcd1\",\"type\":\"about\",\"visibility\":" + vis + ",\"data\":" + data + "}";
            var json = kind == SiteContentKind.FreePage
                ? "{\"schema\":1,\"blocks\":[" + b + "]}"
                : "{\"schema\":1,\"slots\":{\"before\":[" + b + "],\"after\":[]}}";
            return SiteBlockSchema.Parse(json, kind, SiteVisibility.Klubbmedlemmar, region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate);
        }

        [Fact]
        public void Ett_Om_block_har_bara_en_valfri_rubrik_och_gäller_både_klubb_och_krets()
        {
            Assert.True(Parse("{}").Ok);
            Assert.True(Parse("{\"title\":\"Om oss\"}").Ok);
            Assert.True(Parse("{\"title\":\"Om oss\"}", region: true).Ok);
            Assert.False(SiteBlockSchema.Types["about"].Personal);
        }

        [Theory]
        [InlineData("{\"html\":\"<p>kopia</p>\"}")] [InlineData("{\"text\":\"kopia\"}")] [InlineData("{\"title\":\"<b>\"}")] [InlineData("{\"title\":\"" + "x" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" + "\"}")]
        public void Ett_Om_block_lagrar_ingen_text_själv_och_ogiltiga_fält_avvisas(string data) => Assert.False(Parse(data).Ok);

        [Fact]
        public void Om_blocket_får_inte_vara_öppnare_än_sidan_och_lagrar_inga_id_eller_filer()
        {
            Assert.False(Parse("{}", vis: 0).Ok);
            var c = Parse("{}");
            Assert.Empty(SiteBlockSchema.ExtractRefs(c));
            Assert.False(Parse("{}", kind: SiteContentKind.TemplateCopy).Ok);   // inte före/efter en systemsida: Om-panelen finns redan där
        }

        private sealed class AboutData : ISiteBlockData
        {
            public string? Html;
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public string? AboutHtml() => Html;
        }

        private static string Render(JsonObject data, SiteViewer v, string? html, SiteVisibility blockVis = SiteVisibility.Klubbmedlemmar) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "about", blockVis, data), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(v, "/halland/ankeborg/", SiteTemplates.ClubTemplate, new AboutData { Html = html }));

        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Outsider = new(true, false, false, false);

        [Fact]
        public void Texten_ritas_med_kodad_rubrik_och_ändras_när_noden_ändras_utan_att_blocket_ändras()
        {
            var block = new JsonObject { ["title"] = "Om <oss> & vi" };
            var a = Render(block, Member, "<p>Vi är en klubb.</p>");
            var b = Render(block, Member, "<p>Nu är vi större.</p>");
            Assert.Contains("Vi är en klubb.", a); Assert.Contains("Om &lt;oss&gt; &amp; vi", a);
            Assert.Contains("Nu är vi större.", b); Assert.DoesNotContain("Vi är en klubb.", b);
        }

        [Fact]
        public void Farlig_html_i_presentationstexten_saneras_även_om_källan_är_osanerad()
        {
            var html = Render(new JsonObject(), Member, "<p>Hej</p><script>alert(1)</script><img src=x onerror=alert(2)><a href=\"javascript:alert(3)\">klick</a>");
            Assert.Contains("Hej", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("<p> </p>")]
        public void Ingen_text_ger_tomt_svar_inte_en_tom_ruta_och_inte_en_ensam_rubrik(string? html)
            => Assert.Equal("", Render(new JsonObject { ["title"] = "Om oss" }, Member, html));

        [Fact]
        public void En_medlem_ser_blocket_men_inte_den_som_blocket_är_stängt_för()
        {
            Assert.Contains("Text", Render(new JsonObject(), Member, "<p>Text</p>"));
            Assert.Equal("", Render(new JsonObject(), Outsider, "<p>Text</p>"));                       // Klubbmedlemmar: en inloggad icke-medlem ser inget
            Assert.Equal("", Render(new JsonObject(), SiteViewer.Anonymous, "<p>Text</p>", SiteVisibility.Inloggade));
        }
    }
}
