using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2c: nyhetsblocket (live-data, lagrar inget). Schema, rendering och att en lagringsimplementation som lämnar ut för mycket inte kan göra blocket öppnare.</summary>
    public class SiteNewsBlockTests
    {
        private static SiteContentResult Parse(string data, int vis = 2, SiteContentKind kind = SiteContentKind.FreePage)
        {
            var b = "{\"id\":\"abcd1\",\"type\":\"news\",\"visibility\":" + vis + ",\"data\":" + data + "}";
            var json = kind == SiteContentKind.FreePage
                ? "{\"schema\":1,\"blocks\":[" + b + "]}"
                : "{\"schema\":1,\"slots\":{\"before\":[" + b + "],\"after\":[]}}";
            return SiteBlockSchema.Parse(json, kind, SiteVisibility.Klubbmedlemmar, SiteTemplates.ClubTemplate);
        }

        [Fact]
        public void Ett_nyhetsblock_får_rubrik_och_antal_och_standardantalet_är_fem()
        {
            var c = Parse("{\"title\":\"Senaste nytt\"}");
            Assert.True(c.Ok);
            Assert.Equal(5, c.Blocks[0].Data["count"]!.GetValue<int>());
            Assert.Equal("Senaste nytt", c.Blocks[0].Data["title"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("{\"count\":0}")] [InlineData("{\"count\":11}")] [InlineData("{\"count\":\"fem\"}")] [InlineData("{\"url\":\"/x\"}")] [InlineData("{\"title\":\"<b>\"}")]
        public void Ett_nyhetsblock_med_fel_form_avvisas(string data) => Assert.False(Parse(data).Ok);

        [Fact]
        public void Nyheter_får_ligga_före_och_efter_en_systemsida_men_lagrar_inga_id_eller_filer()
        {
            var c = Parse("{\"count\":3}", kind: SiteContentKind.TemplateCopy);
            Assert.True(c.Ok, string.Join("; ", c.Errors.Select(e => e.Message)));
            Assert.Empty(SiteBlockSchema.ExtractRefs(c));
        }

        [Fact]
        public void Nyhetsblocket_kan_inte_vara_öppnare_än_sidan() => Assert.False(Parse("{\"count\":3}", vis: 0).Ok);

        private sealed class NewsData : ISiteBlockData
        {
            public List<SiteNewsInfo> Items = new();
            public int AskedFor;
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public IReadOnlyList<SiteNewsInfo> News(int count) { AskedFor = count; return Items; }   // en dålig implementation: ignorerar antalet
        }

        private static string Render(JsonObject data, SiteViewer v, NewsData d) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "news", SiteVisibility.Klubbmedlemmar, data), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(v, "/halland/ankeborg/", SiteTemplates.ClubTemplate, d));

        private static readonly SiteViewer Member = new(true, true, false, false);

        [Fact]
        public void Nyheterna_visas_med_kodad_titel_datum_och_länk_högst_så_många_som_blocket_ber_om()
        {
            var d = new NewsData();
            for (var i = 1; i <= 4; i++) d.Items.Add(new SiteNewsInfo($"Nyhet {i} <b>&</b>", new DateTime(2027, 3, i), $"/halland/ankeborg/nyhet-{i}/"));
            var html = Render(new JsonObject { ["title"] = "Senaste nytt", ["count"] = 2 }, Member, d);
            Assert.Equal(2, d.AskedFor);
            Assert.Contains("Senaste nytt", html);
            Assert.Contains("1 mars 2027", html);
            Assert.Contains("href=\"/halland/ankeborg/nyhet-1/\"", html);
            Assert.DoesNotContain("<b>", html);
            Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", html);
            Assert.DoesNotContain("Nyhet 3", html);    // antalet gäller även om lagringen lämnar ut fler
        }

        [Fact]
        public void En_nyhet_med_en_osäker_adress_visas_som_text_utan_länk()
        {
            var d = new NewsData(); d.Items.Add(new SiteNewsInfo("Farlig", new DateTime(2027, 3, 1), "javascript:alert(1)"));
            var html = Render(new JsonObject { ["count"] = 3 }, Member, d);
            Assert.Contains("Farlig", html); Assert.DoesNotContain("javascript:", html); Assert.DoesNotContain(">Farlig</a>", html);
        }

        [Fact]
        public void Blocket_länkar_till_arkivet_under_ägarens_adress()
        {
            var d = new NewsData(); d.Items.Add(new SiteNewsInfo("Hej", new DateTime(2027, 3, 1), "/halland/ankeborg/nyheter/hej/"));
            Assert.Contains("href=\"/halland/ankeborg/nyheter/\"", Render(new JsonObject { ["count"] = 3 }, Member, d));
        }

        [Fact]
        public void Sammanfattningen_visas_som_förval_kodad_och_kan_stängas_av()
        {
            var d = new NewsData(); d.Items.Add(new SiteNewsInfo("Hej", new DateTime(2027, 3, 1), "/x/", "Kort <i>text</i> & mer"));
            var on = Render(new JsonObject { ["count"] = 3 }, Member, d);
            Assert.Contains("Kort &lt;i&gt;text&lt;/i&gt; &amp; mer", on);
            Assert.DoesNotContain("Kort", Render(new JsonObject { ["count"] = 3, ["showExcerpt"] = false }, Member, d));
        }

        [Fact]
        public void Omslagsbilden_visas_bara_när_blocket_ber_om_den_och_bilden_hör_till_ägaren()
        {
            var d = new NewsData(); d.Items.Add(new SiteNewsInfo("Hej", new DateTime(2027, 3, 1), "/x/", null, 7));
            Assert.DoesNotContain("<img", Render(new JsonObject { ["count"] = 3 }, Member, d));                        // förvalet: ingen bild
            Assert.DoesNotContain("<img", Render(new JsonObject { ["count"] = 3, ["showImage"] = true }, Member, d));  // bilden finns inte hos ägaren (Images() ger inga)
        }

        [Theory]
        [InlineData("{\"showExcerpt\":\"ja\"}")] [InlineData("{\"showImage\":1}")]
        public void Visa_sammanfattning_och_bild_är_ja_eller_nej(string data) => Assert.False(Parse(data).Ok);

        [Fact]
        public void Visa_sammanfattning_och_bild_sparas_som_de_är()
        {
            var c = Parse("{\"showExcerpt\":false,\"showImage\":true}");
            Assert.True(c.Ok);
            Assert.False(c.Blocks[0].Data["showExcerpt"]!.GetValue<bool>());
            Assert.True(c.Blocks[0].Data["showImage"]!.GetValue<bool>());
        }

        [Fact]
        public void Inga_nyheter_ger_tomt_svar_inte_en_tom_ruta()
            => Assert.Equal("", Render(new JsonObject { ["title"] = "Senaste nytt", ["count"] = 3 }, Member, new NewsData()));
    }
}
