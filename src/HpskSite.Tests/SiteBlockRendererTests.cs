using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2a: blockrenderaren (ren kod). Allt kodat, synligheten prövas vid varje rendering.</summary>
    public class SiteBlockRendererTests
    {
        private sealed class Data : ISiteBlockData
        {
            public List<SiteDocumentInfo> Docs = new();
            public List<SiteEventInfo> Events = new();
            public SiteContactInfo? ContactInfo;
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Docs;   // en dålig implementation: lämnar ut ALLT
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Events;
            public SiteContactInfo? Contact() => ContactInfo;
        }

        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);

        private static SiteRenderContext Ctx(SiteViewer v, Data? d = null) =>
            new(v, "/halland/ankeborg-pistolklubb/", SiteTemplates.ClubTemplate, d ?? new Data());

        private static SiteBlock B(string type, SiteVisibility vis, JsonObject data, string id = "abcd1") => new(id, type, vis, data);

        private static string Render(SiteBlock b, SiteViewer v, SiteVisibility page = SiteVisibility.Klubbmedlemmar, Data? d = null) =>
            SiteBlockRenderer.Render(b, page, Ctx(v, d));

        // ── Text ──────────────────────────────────────────────────────────────

        [Fact]
        public void Text_saneras_om_vid_rendering_även_om_den_lagrade_raden_är_smutsig()
        {
            var b = B("text", SiteVisibility.Klubbmedlemmar, new JsonObject { ["html"] = "<p onclick=\"x()\">Hej</p><script>alert(1)</script><img src=\"/a.jpg\">" });
            var html = Render(b, Member);
            Assert.Contains("Hej", html);
            Assert.DoesNotContain("onclick", html); Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img", html);
        }

        [Fact]
        public void Tabeller_får_ett_responsivt_omslag()
        {
            var b = B("text", SiteVisibility.Klubbmedlemmar, new JsonObject { ["html"] = "<table><tr><td>1</td></tr></table>" });
            var html = Render(b, Member);
            Assert.Contains("<div class=\"table-responsive\"><table", html);
            Assert.Contains("</table></div>", html);
        }

        [Fact]
        public void Tom_text_ger_ingen_ruta()
            => Assert.Equal("", Render(B("text", SiteVisibility.Klubbmedlemmar, new JsonObject { ["html"] = "   " }), Member));

        // ── Rubrik och länkar ─────────────────────────────────────────────────

        [Fact]
        public void Rubriken_kodas_och_nivån_begränsas_till_2_eller_3()
        {
            var html = Render(B("heading", SiteVisibility.Klubbmedlemmar, new JsonObject { ["text"] = "<img src=x onerror=alert(1)>", ["level"] = 9 }), Member);
            Assert.DoesNotContain("<img", html);
            Assert.Contains("&lt;img", html);
            Assert.Contains("<h2>", html);
            Assert.Contains("<h3>", Render(B("heading", SiteVisibility.Klubbmedlemmar, new JsonObject { ["text"] = "x", ["level"] = 3 }), Member));
        }

        [Fact]
        public void Länkar_prövas_igen_och_farliga_utelämnas_hela_blocket_om_inget_återstår()
        {
            var items = new JsonArray
            {
                new JsonObject { ["label"] = "Bra", ["url"] = "https://svenskskyttesport.se" },
                new JsonObject { ["label"] = "Ond", ["url"] = "javascript:alert(1)" },
                new JsonObject { ["label"] = "Annan värd", ["url"] = "//evil.com" },
                new JsonObject { ["label"] = "<b>x</b>", ["url"] = "/halland/" },
            };
            var html = Render(B("links", SiteVisibility.Klubbmedlemmar, new JsonObject { ["items"] = items }), Member);
            Assert.Contains("Bra", html); Assert.DoesNotContain("javascript", html); Assert.DoesNotContain("evil.com", html);
            Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
            Assert.Contains("rel=\"noopener noreferrer nofollow ugc\"", html);

            var onlyBad = new JsonArray { new JsonObject { ["label"] = "Ond", ["url"] = "javascript:alert(1)" } };
            Assert.Equal("", Render(B("links", SiteVisibility.Klubbmedlemmar, new JsonObject { ["items"] = onlyBad }), Member));
        }

        [Fact]
        public void Pagelink_visas_bara_för_en_sida_tittaren_får_se_och_bygger_adressen_ur_ägaren()
        {
            var b = B("pagelink", SiteVisibility.Alla, new JsonObject { ["key"] = "records", ["label"] = "Våra \"rekord\"" });
            Assert.Equal("", Render(b, Anon, SiteVisibility.Alla));                  // Rekord är för inloggade
            var html = Render(b, Logged, SiteVisibility.Alla);
            Assert.Contains("href=\"/halland/ankeborg-pistolklubb/rekord/\"", html);
            Assert.Contains("Våra &quot;rekord&quot;", html);
            Assert.Contains("href=\"/halland/ankeborg-pistolklubb/\"", Render(B("pagelink", SiteVisibility.Alla, new JsonObject { ["key"] = "home" }), Anon, SiteVisibility.Alla));
            Assert.Equal("", Render(B("pagelink", SiteVisibility.Alla, new JsonObject { ["key"] = "admin" }), Logged, SiteVisibility.Alla));
            Assert.Equal("", Render(B("pagelink", SiteVisibility.Alla, new JsonObject { ["key"] = "nope" }), Logged, SiteVisibility.Alla));
        }

        // ── Synlighet ─────────────────────────────────────────────────────────

        [Fact]
        public void Block_som_tittaren_inte_får_se_utelämnas_helt_utan_etikett_eller_tom_ruta()
        {
            var b = B("heading", SiteVisibility.Klubbmedlemmar, new JsonObject { ["text"] = "Hemlig rubrik", ["level"] = 2 });
            Assert.Equal("", Render(b, Anon));
            Assert.Equal("", Render(b, Logged));
            Assert.Contains("Hemlig rubrik", Render(b, Member));
        }

        [Fact]
        public void Ett_block_kan_aldrig_vara_öppnare_än_sidan_även_om_raden_säger_det()
        {
            var b = B("heading", SiteVisibility.Alla, new JsonObject { ["text"] = "Styrelsens", ["level"] = 2 });
            Assert.Equal("", Render(b, Member, SiteVisibility.Styrelse));     // lagrad Alla på en Styrelse-sida: Styrelse gäller
            Assert.Contains("Styrelsens", Render(b, Board, SiteVisibility.Styrelse));
        }

        [Fact]
        public void Personblock_är_aldrig_öppna_för_alla_ens_om_raden_säger_det()
        {
            var d = new Data { ContactInfo = new SiteContactInfo("Anna", "anna@example.se", "0701234567") };
            var b = B("contact", SiteVisibility.Alla, new JsonObject { ["title"] = "Kontakt" });
            Assert.Equal("", Render(b, Anon, SiteVisibility.Alla, d));
            Assert.Contains("anna@example.se", Render(b, Logged, SiteVisibility.Alla, d));
        }

        // ── Dokument ──────────────────────────────────────────────────────────

        private static SiteBlock DocBlock(SiteVisibility vis, params int[] ids) =>
            B("documents", vis, new JsonObject { ["refs"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create("doc:" + i)!).ToArray()) });

        [Fact]
        public void Dokumentets_egen_nivå_gäller_också_Strictest_av_block_och_dokument()
        {
            var d = new Data { Docs =
            {
                new(1, "Öppen blankett", null, 100, SiteVisibility.Alla),
                new(2, "Styrelsepapper", null, 100, SiteVisibility.Styrelse),
            }};
            var b = DocBlock(SiteVisibility.Klubbmedlemmar, 1, 2);
            var memberHtml = Render(b, Member, SiteVisibility.Klubbmedlemmar, d);
            Assert.Contains("Öppen blankett", memberHtml);
            Assert.DoesNotContain("Styrelsepapper", memberHtml);                  // blocket släpper in medlemmen, dokumentet gör det inte
            Assert.Contains("Styrelsepapper", Render(b, Board, SiteVisibility.Klubbmedlemmar, d));
            Assert.Equal("", Render(b, Logged, SiteVisibility.Klubbmedlemmar, d)); // blocket själv stänger en icke-medlem ute
        }

        [Fact]
        public void Ett_dokument_som_inte_står_i_blockets_referenser_visas_aldrig_även_om_datalagret_lämnar_ut_det()
        {
            var d = new Data { Docs = { new(1, "Min", null, 1, SiteVisibility.Alla), new(99, "Någon annans", null, 1, SiteVisibility.Alla) } };
            var html = Render(DocBlock(SiteVisibility.Klubbmedlemmar, 1), Member, SiteVisibility.Klubbmedlemmar, d);
            Assert.Contains("Min", html);
            Assert.DoesNotContain("Någon annans", html);
        }

        [Fact]
        public void Dokumentets_titel_och_beskrivning_kodas_och_länken_går_bara_till_nedladdningen()
        {
            var d = new Data { Docs = { new(1, "<script>x</script>", "<b>besk</b>", 1, SiteVisibility.Alla) } };
            var html = Render(DocBlock(SiteVisibility.Klubbmedlemmar, 1), Member, SiteVisibility.Klubbmedlemmar, d);
            Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<b>", html);
            Assert.Contains("DownloadDocument?id=1", html);
        }

        [Fact]
        public void Inga_synliga_dokument_ger_ingen_ruta()
        {
            var d = new Data { Docs = { new(2, "Styrelsepapper", null, 1, SiteVisibility.Styrelse) } };
            Assert.Equal("", Render(DocBlock(SiteVisibility.Klubbmedlemmar, 2), Member, SiteVisibility.Klubbmedlemmar, d));
        }

        // ── Händelser och kontakt ─────────────────────────────────────────────

        [Fact]
        public void Händelser_kodas_begränsas_till_antalet_och_en_oäkta_länk_blir_text()
        {
            var events = Enumerable.Range(1, 8).Select(i => new SiteEventInfo("H" + i, new DateTime(2027, 5, i, 18, 0, 0), "Banan", "/halland/x/" + i)).ToList();
            events[0] = new SiteEventInfo("<i>Ond</i>", new DateTime(2027, 5, 1, 18, 0, 0), null, "javascript:alert(1)");
            var d = new Data { Events = events };
            var html = Render(B("events", SiteVisibility.Klubbmedlemmar, new JsonObject { ["count"] = 3 }), Member, SiteVisibility.Klubbmedlemmar, d);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<li").Count);
            Assert.DoesNotContain("javascript", html); Assert.DoesNotContain("<i>", html);
            Assert.Contains("&lt;i&gt;Ond&lt;/i&gt;", html);
        }

        [Fact]
        public void Inga_händelser_eller_ingen_kontakt_ger_ingen_ruta()
        {
            Assert.Equal("", Render(B("events", SiteVisibility.Klubbmedlemmar, new JsonObject { ["count"] = 3 }), Member));
            Assert.Equal("", Render(B("contact", SiteVisibility.Klubbmedlemmar, new JsonObject()), Member, SiteVisibility.Klubbmedlemmar, new Data { ContactInfo = new SiteContactInfo(null, " ", null) }));
        }

        [Fact]
        public void Kontaktuppgifterna_kodas_och_blir_mailto_och_tel()
        {
            var d = new Data { ContactInfo = new SiteContactInfo("A\"nna", "a@b.se", "070-123") };
            var html = Render(B("contact", SiteVisibility.Klubbmedlemmar, new JsonObject()), Member, SiteVisibility.Klubbmedlemmar, d);
            Assert.Contains("A&quot;nna", html); Assert.Contains("href=\"mailto:a@b.se\"", html); Assert.Contains("href=\"tel:070-123\"", html);
        }

        [Fact]
        public void Okänd_blocktyp_renderas_aldrig()
            => Assert.Equal("", Render(B("embed", SiteVisibility.Alla, new JsonObject()), Board, SiteVisibility.Alla));

        [Fact]
        public void RenderAll_slår_ihop_bara_det_tittaren_får_se()
        {
            var blocks = new[]
            {
                B("heading", SiteVisibility.Alla, new JsonObject { ["text"] = "Öppen", ["level"] = 2 }, "abcd1"),
                B("heading", SiteVisibility.Styrelse, new JsonObject { ["text"] = "Stängd", ["level"] = 2 }, "abcd2"),
            };
            var html = SiteBlockRenderer.RenderAll(blocks, SiteVisibility.Alla, Ctx(Anon));
            Assert.Contains("Öppen", html); Assert.DoesNotContain("Stängd", html);
        }
    }
}
