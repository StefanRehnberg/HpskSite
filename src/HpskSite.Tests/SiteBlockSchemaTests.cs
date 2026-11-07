using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2a: serverns schemavalidering av sidornas block (ren funktion, egen svit).</summary>
    public class SiteBlockSchemaTests
    {
        private static readonly SiteTemplate T = SiteTemplates.ClubTemplate;

        private static string Free(params string[] blocks) => "{\"schema\":1,\"blocks\":[" + string.Join(",", blocks) + "]}";
        private static string Slots(string[] before, string[] after) =>
            "{\"schema\":1,\"slots\":{\"before\":[" + string.Join(",", before) + "],\"after\":[" + string.Join(",", after) + "]}}";
        private static string B(string id, string type, int vis, string data) =>
            $"{{\"id\":\"{id}\",\"type\":\"{type}\",\"visibility\":{vis},\"data\":{data}}}";
        private static string Text(string id = "abcd1", int vis = 2, string html = "<p>Hej</p>") => B(id, "text", vis, "{\"html\":\"" + html.Replace("\"", "\\\"") + "\"}");

        private static SiteContentResult ParseFree(string json, SiteVisibility page = SiteVisibility.Klubbmedlemmar) =>
            SiteBlockSchema.Parse(json, SiteContentKind.FreePage, page, T);

        private static void AssertRejected(SiteContentResult r, string pathContains)
        {
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Path.Contains(pathContains, StringComparison.Ordinal));
        }

        [Fact]
        public void Giltig_fri_sida_går_igenom()
        {
            var r = ParseFree(Free(Text(), B("abcd2", "heading", 2, "{\"text\":\"Regler\",\"level\":2}")));
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Path + " " + e.Message)));
            Assert.Equal(2, r.Blocks.Count);
        }

        [Fact]
        public void Tom_sida_utan_block_är_giltig()
            => Assert.True(ParseFree("{\"schema\":1,\"blocks\":[]}").Ok);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("inte json")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("{\"schema\":2,\"blocks\":[]}")]
        [InlineData("{\"blocks\":[]}")]
        [InlineData("{\"schema\":1.5,\"blocks\":[]}")]
        [InlineData("{\"schema\":\"1\",\"blocks\":[]}")]
        public void Trasig_eller_okänd_form_avvisas(string? json)
            => Assert.False(ParseFree(json).Ok);

        [Fact]
        public void Okänt_fält_i_roten_avvisas_och_slots_hör_inte_hemma_på_en_fri_sida()
        {
            AssertRejected(ParseFree("{\"schema\":1,\"blocks\":[],\"style\":\"x\"}"), "style");
            AssertRejected(ParseFree("{\"schema\":1,\"slots\":{}}"), "slots");
        }

        [Fact]
        public void Okänd_blocktyp_avvisas_även_embed_och_nyheter_som_inte_finns_än()
        {
            AssertRejected(ParseFree(Free(B("abcd1", "embed", 2, "{\"provider\":\"youtube\",\"id\":\"x\"}"))), "type");
            AssertRejected(ParseFree(Free(B("abcd1", "news", 2, "{}"))), "type");
            AssertRejected(ParseFree(Free(B("abcd1", "image", 2, "{}"))), "type");
            AssertRejected(ParseFree(Free(B("abcd1", "script", 2, "{}"))), "type");
        }

        [Fact]
        public void Okända_fält_i_block_och_data_avvisas_även_style()
        {
            AssertRejected(ParseFree(Free("{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":2,\"style\":\"x\",\"data\":{\"html\":\"a\"}}")), "style");
            AssertRejected(ParseFree(Free(B("abcd1", "text", 2, "{\"html\":\"a\",\"style\":\"color:red\"}"))), "data.style");
            AssertRejected(ParseFree(Free(B("abcd1", "text", 2, "{\"html\":\"a\",\"class\":\"x\"}"))), "data.class");
        }

        [Theory]
        [InlineData("")]
        [InlineData("ab")]
        [InlineData("ABCD1")]
        [InlineData("abcd-1")]
        [InlineData("abcdefghijklm")]
        public void Ogiltigt_block_id_avvisas(string id)
            => AssertRejected(ParseFree(Free(Text(id))), ".id");

        [Fact]
        public void Två_block_med_samma_id_avvisas()
            => AssertRejected(ParseFree(Free(Text("abcd1"), Text("abcd1"))), ".id");

        [Fact]
        public void Script_i_text_avvisas_och_html_saneras_vid_skrivning()
        {
            AssertRejected(ParseFree(Free(Text(html: "<script>alert(1)</script>"))), "html");
            AssertRejected(ParseFree(Free(Text(html: "<a href=\"javascript:alert(1)\">x</a>"))), "html");
            var r = ParseFree(Free(Text(html: "<p class=\"x\" onclick=\"y()\">Hej <strong>du</strong></p>")));
            Assert.True(r.Ok);
            var html = r.Blocks[0].Data["html"]!.GetValue<string>();
            Assert.DoesNotContain("onclick", html);
            Assert.DoesNotContain("class", html);
            Assert.Contains("<strong>du</strong>", html);
        }

        [Fact]
        public void Block_öppnare_än_sidan_avvisas_men_lika_eller_stängdare_går()
        {
            AssertRejected(ParseFree(Free(Text(vis: 0)), SiteVisibility.Klubbmedlemmar), "visibility");
            AssertRejected(ParseFree(Free(Text(vis: 1)), SiteVisibility.Styrelse), "visibility");
            Assert.True(ParseFree(Free(Text(vis: 2)), SiteVisibility.Klubbmedlemmar).Ok);
            Assert.True(ParseFree(Free(Text(vis: 3)), SiteVisibility.Klubbmedlemmar).Ok);
            Assert.True(ParseFree(Free(Text(vis: 4)), SiteVisibility.Klubbmedlemmar).Ok);
        }

        [Theory]
        [InlineData(5)]   // Utloggad: bara systemblock
        [InlineData(6)]
        [InlineData(-1)]
        public void Ogiltig_synlighet_avvisas(int vis)
            => AssertRejected(ParseFree(Free(Text(vis: vis)), SiteVisibility.Alla), "visibility");

        [Fact]
        public void Synlighet_måste_vara_ett_heltal()
        {
            AssertRejected(ParseFree(Free("{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":1.5,\"data\":{\"html\":\"a\"}}")), "visibility");
            AssertRejected(ParseFree(Free("{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":\"2\",\"data\":{\"html\":\"a\"}}")), "visibility");
            AssertRejected(ParseFree(Free("{\"id\":\"abcd1\",\"type\":\"text\",\"data\":{\"html\":\"a\"}}")), "visibility");
        }

        [Fact]
        public void Personblock_kan_aldrig_vara_Alla()
        {
            AssertRejected(ParseFree(Free(B("abcd1", "contact", 0, "{}")), SiteVisibility.Alla), "visibility");
            Assert.True(ParseFree(Free(B("abcd1", "contact", 1, "{}")), SiteVisibility.Alla).Ok);
        }

        [Fact]
        public void Länkar_prövas_som_i_saneraren()
        {
            string L(string url) => Free(B("abcd1", "links", 2, "{\"items\":[{\"label\":\"x\",\"url\":\"" + url + "\"}]}"));
            Assert.True(ParseFree(L("https://svenskskyttesport.se")).Ok);
            Assert.True(ParseFree(L("/halland/")).Ok);
            Assert.True(ParseFree(L("mailto:a@b.se")).Ok);
            AssertRejected(ParseFree(L("javascript:alert(1)")), "url");
            AssertRejected(ParseFree(L("//evil.com")), "url");
            AssertRejected(ParseFree(L("http://evil.com")), "url");
            AssertRejected(ParseFree(L("https://pistol.nu@evil.com/")), "url");
            AssertRejected(ParseFree(L("/\\\\evil.com")), "url");
        }

        [Fact]
        public void Länkar_tomma_eller_för_många_avvisas()
        {
            AssertRejected(ParseFree(Free(B("abcd1", "links", 2, "{\"items\":[]}"))), "items");
            AssertRejected(ParseFree(Free(B("abcd1", "links", 2, "{\"items\":[{\"label\":\"\",\"url\":\"/x\"}]}"))), "label");
            var many = string.Join(",", Enumerable.Range(0, 21).Select(i => "{\"label\":\"l\",\"url\":\"/x\"}"));
            AssertRejected(ParseFree(Free(B("abcd1", "links", 2, "{\"items\":[" + many + "]}"))), "items");
        }

        [Fact]
        public void Etiketter_med_kontrolltecken_bidi_eller_html_avvisas()
        {
            AssertRejected(ParseFree(Free(B("abcd1", "heading", 2, "{\"text\":\"a\\u202Eb\",\"level\":2}"))), "text");
            AssertRejected(ParseFree(Free(B("abcd1", "heading", 2, "{\"text\":\"a\\u0007b\",\"level\":2}"))), "text");
            AssertRejected(ParseFree(Free(B("abcd1", "heading", 2, "{\"text\":\"<b>x</b>\",\"level\":2}"))), "text");
            AssertRejected(ParseFree(Free(B("abcd1", "heading", 2, "{\"text\":\"" + new string('x', 121) + "\",\"level\":2}"))), "text");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(0)]
        public void Rubriknivå_är_2_eller_3(int level)
            => AssertRejected(ParseFree(Free(B("abcd1", "heading", 2, "{\"text\":\"x\",\"level\":" + level + "}"))), "level");

        [Fact]
        public void Dokumentreferenser_har_sort_och_dubbletter_slås_ihop()
        {
            var r = ParseFree(Free(B("abcd1", "documents", 2, "{\"refs\":[\"doc:5\",\"doc:5\",\"doc:7\"]}")));
            Assert.True(r.Ok);
            Assert.Equal(2, r.Blocks[0].Data["refs"]!.AsArray().Count);
            Assert.Equal(new[] { ("doc", 5), ("doc", 7) }, SiteBlockSchema.ExtractRefs(r));
        }

        [Theory]
        [InlineData("5")]            // ett tal utan sort är en bugg (id-lagen)
        [InlineData("doc:0")]
        [InlineData("doc:-1")]
        [InlineData("doc:5; DROP")]
        [InlineData("file:5")]
        [InlineData("doc:1234567890")]
        public void Dokumentreferens_utan_sort_eller_med_skräp_avvisas(string rf)
            => AssertRejected(ParseFree(Free(B("abcd1", "documents", 2, "{\"refs\":[\"" + rf + "\"]}"))), "refs");

        [Fact]
        public void Händelseantal_och_pagelink_prövas()
        {
            AssertRejected(ParseFree(Free(B("abcd1", "events", 2, "{\"count\":0}"))), "count");
            AssertRejected(ParseFree(Free(B("abcd1", "events", 2, "{\"count\":11}"))), "count");
            Assert.True(ParseFree(Free(B("abcd1", "events", 2, "{}"))).Ok);
            Assert.True(ParseFree(Free(B("abcd1", "pagelink", 2, "{\"key\":\"calendar\"}"))).Ok);
            AssertRejected(ParseFree(Free(B("abcd1", "pagelink", 2, "{\"key\":\"admin\"}"))), "key");      // Admin har ingen adress
            AssertRejected(ParseFree(Free(B("abcd1", "pagelink", 2, "{\"key\":\"board\"}"))), "key");      // finns bara i kretsens mall
            AssertRejected(ParseFree(Free(B("abcd1", "pagelink", 2, "{\"key\":\"nope\"}"))), "key");
        }

        [Fact]
        public void För_många_block_eller_för_stor_sida_avvisas()
        {
            var many = Enumerable.Range(0, 41).Select(i => Text("a" + i.ToString("D3"))).ToArray();
            Assert.False(ParseFree(Free(many)).Ok);
            Assert.True(ParseFree(Free(many.Take(40).ToArray())).Ok);
            var big = Text(html: new string('x', 100_001));
            AssertRejected(ParseFree(Free(big)), "html");
            var huge = "{\"schema\":1,\"blocks\":[],\"pad\":\"" + new string('x', 400_001) + "\"}";
            Assert.False(ParseFree(huge).Ok);
        }

        [Fact]
        public void För_djup_json_avvisas_utan_att_kasta()
        {
            var deep = new string('[', 500) + new string(']', 500);
            Assert.False(ParseFree("{\"schema\":1,\"blocks\":" + deep + "}").Ok);
        }

        // ── Mallkopia (slots) ─────────────────────────────────────────────────

        [Fact]
        public void Mallkopia_tillåter_text_och_länkar_men_inget_annat_i_slots()
        {
            var ok = SiteBlockSchema.Parse(Slots(new[] { Text("abcd1") }, new[] { B("abcd2", "links", 2, "{\"items\":[{\"label\":\"x\",\"url\":\"/x\"}]}") }),
                SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, T);
            Assert.True(ok.Ok, string.Join("; ", ok.Errors.Select(e => e.Path + " " + e.Message)));
            Assert.Single(ok.Before); Assert.Single(ok.After);

            foreach (var type in new[] { "contact", "documents", "events", "heading", "pagelink" })
            {
                var data = type switch
                {
                    "contact" => "{}",
                    "documents" => "{\"refs\":[\"doc:1\"]}",
                    "events" => "{}",
                    "heading" => "{\"text\":\"x\"}",
                    _ => "{\"key\":\"home\"}"
                };
                var r = SiteBlockSchema.Parse(Slots(new[] { B("abcd1", type, 2, data) }, Array.Empty<string>()),
                    SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, T);
                AssertRejected(r, "slots.before[0].type");
            }
        }

        [Fact]
        public void Mallkopia_med_blocks_eller_okänd_plats_avvisas()
        {
            AssertRejected(SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[]}", SiteContentKind.TemplateCopy, SiteVisibility.Alla, T), "blocks");
            AssertRejected(SiteBlockSchema.Parse("{\"schema\":1,\"slots\":{\"middle\":[]}}", SiteContentKind.TemplateCopy, SiteVisibility.Alla, T), "slots.middle");
            AssertRejected(SiteBlockSchema.Parse("{\"schema\":1}", SiteContentKind.TemplateCopy, SiteVisibility.Alla, T), "slots");
        }

        [Fact]
        public void Block_id_är_unika_över_före_och_efter()
        {
            var r = SiteBlockSchema.Parse(Slots(new[] { Text("abcd1") }, new[] { Text("abcd1") }), SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, T);
            AssertRejected(r, ".id");
        }

        // ── Rundtur ───────────────────────────────────────────────────────────

        [Fact]
        public void Det_som_lagras_läses_tillbaka_oförändrat()
        {
            var first = ParseFree(Free(Text(html: "<p>Hej <a href=\"https://pistol.nu/x\">länk</a></p>"),
                B("abcd2", "documents", 2, "{\"title\":\"Blanketter\",\"refs\":[\"doc:3\"]}"),
                B("abcd3", "events", 3, "{\"count\":3}")));
            Assert.True(first.Ok);
            var stored = SiteBlockSchema.ToJson(first);
            var again = ParseFree(stored);
            Assert.True(again.Ok);
            Assert.Equal(stored, SiteBlockSchema.ToJson(again));
        }

        [Fact]
        public void Mallkopians_rundtur_bevarar_före_och_efter()
        {
            var first = SiteBlockSchema.Parse(Slots(new[] { Text("abcd1") }, new[] { Text("abcd2") }), SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, T);
            var again = SiteBlockSchema.Parse(SiteBlockSchema.ToJson(first), SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, T);
            Assert.True(again.Ok);
            Assert.Equal("abcd1", again.Before.Single().Id);
            Assert.Equal("abcd2", again.After.Single().Id);
        }

        [Fact]
        public void Ett_lagrat_innehåll_prövas_igen_vid_rendering()
        {
            // En rad från en tidigare kodversion: blocket är öppnare än sidan har blivit. Renderingen kör samma Parse.
            var stored = Free(Text(vis: 1));
            Assert.True(ParseFree(stored, SiteVisibility.Inloggade).Ok);
            Assert.False(ParseFree(stored, SiteVisibility.Styrelse).Ok);
        }
    }
}
