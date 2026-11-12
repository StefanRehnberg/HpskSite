using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Blockbiblioteket, steg 3: tävlingar, rekord och banor. Allt läses live, ingenting lagras i sidan, allt som skrivs ut är kodat och blocken följer sin nivå.</summary>
    public class SiteLibraryBlocksTests
    {
        private static SiteContentResult Parse(string type, string data, int vis = 2, bool region = false)
        {
            var b = "{\"id\":\"abcd1\",\"type\":\"" + type + "\",\"visibility\":" + vis + ",\"data\":" + data + "}";
            return SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[" + b + "]}", SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar,
                region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate);
        }

        [Theory]
        [InlineData("competitions")] [InlineData("records")] [InlineData("ranges")] [InlineData("champions")] [InlineData("stats")]
        public void Blocken_gäller_både_klubb_och_krets_har_bara_rubrik_och_lagrar_inga_id(string type)
        {
            Assert.True(Parse(type, "{}").Ok);
            Assert.True(Parse(type, "{\"title\":\"Rubrik\"}", region: true).Ok);
            Assert.False(Parse(type, "{\"title\":\"x\",\"ids\":[1,2]}").Ok);      // inget fält för id: ägaren kommer ur begäran
            Assert.False(Parse(type, "{\"ownerId\":5}").Ok);
            Assert.False(Parse(type, "{}", vis: 0).Ok);                           // aldrig öppnare än sidan
            Assert.Equal(type == "champions", SiteBlockSchema.Types[type].Personal);   // mästarna är namn på personer: aldrig Alla
            Assert.Empty(SiteBlockSchema.ExtractRefs(Parse(type, "{}")));
        }

        [Theory]
        [InlineData("0")] [InlineData("11")] [InlineData("\"tre\"")]
        public void Antalet_tävlingar_är_1_till_10(string count) => Assert.False(Parse("competitions", "{\"count\":" + count + "}").Ok);

        private sealed class LibData : ISiteBlockData
        {
            public List<SiteCompetitionInfo> Comps = new();
            public List<SiteRecordInfo> Recs = new();
            public List<SiteRangeInfo> Ranges = new();
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public IReadOnlyList<SiteCompetitionInfo> UpcomingCompetitions(int count) => Comps;
            public IReadOnlyList<SiteRecordInfo> Records() => Recs;
            public IReadOnlyList<SiteRangeInfo> OwnerRanges() => Ranges;
            public List<SiteChampionInfo> Champs = new();
            public List<SiteStatItem> StatItems = new();
            public IReadOnlyList<SiteChampionInfo> Champions() => Champs;
            public IReadOnlyList<SiteStatItem> Stats() => StatItems;
        }

        private static readonly SiteViewer Member = new(true, true, false, false);

        private static string Render(string type, JsonObject data, LibData d, SiteViewer? v = null, SiteVisibility blockVis = SiteVisibility.Klubbmedlemmar) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", type, blockVis, data), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(v ?? Member, "/halland/ankeborg/", SiteTemplates.ClubTemplate, d));

        [Fact]
        public void Tävlingarna_ritas_med_kodad_titel_och_datum_och_högst_så_många_som_valts()
        {
            var d = new LibData();
            for (var i = 1; i <= 6; i++) d.Comps.Add(new SiteCompetitionInfo(i == 1 ? "A <b>&</b> B" : "Tävling " + i, new DateTime(2027, 5, i), "/t/" + i + "/"));
            var html = Render("competitions", new JsonObject { ["title"] = "Nästa <x>", ["count"] = 3 }, d);
            Assert.Contains("A &lt;b&gt;&amp;&lt;/b&gt; B", html);
            Assert.Contains("Nästa &lt;x&gt;", html);
            Assert.Contains("href=\"/t/1/\"", html);
            Assert.Contains("Tävling 3", html);
            Assert.DoesNotContain("Tävling 4", html);                                   // antalet gäller även om källan lämnar fler
            Assert.DoesNotContain("<b>", html);
        }

        [Fact]
        public void En_tävling_med_otillåten_adress_blir_text_inte_länk()
        {
            var d = new LibData { Comps = { new SiteCompetitionInfo("Farlig", new DateTime(2027, 5, 1), "javascript:alert(1)") } };
            var html = Render("competitions", new JsonObject { ["count"] = 5 }, d);
            Assert.Contains("Farlig", html);
            Assert.DoesNotContain("javascript:", html);
        }

        [Fact]
        public void Rekorden_ritas_som_tabell_med_kodade_namn_och_lagrekord_märks()
        {
            var d = new LibData
            {
                Recs = { new SiteRecordInfo("Precision", "Dam <1>", false, "289 / 300", "Anna \"A\" Ek", new DateTime(2026, 3, 4)),
                         new SiteRecordInfo("Precision", "Öppen", true, "850 / 900", "Lag & Co", new DateTime(2025, 1, 2)),
                         new SiteRecordInfo("Fältskytte", "Öppen", false, "42 träff <b>", "Fält", new DateTime(2025, 1, 2)) }
            };
            var html = Render("records", new JsonObject { ["title"] = "Rekord" }, d);
            Assert.Contains("<table", html);
            Assert.Contains("Dam &lt;1&gt;", html);
            Assert.Contains("Anna &quot;A&quot; Ek", html);
            Assert.Contains("Lag &amp; Co", html);
            Assert.Contains("(lag)", html);
            Assert.Contains("289 / 300", html);
            Assert.Contains("42 träff &lt;b&gt;", html);                                // enheten följer med talet och är kodad
            Assert.Contains("table-responsive", html);                                  // tabellen går att rulla på telefon
        }

        [Fact]
        public void Banorna_ritas_utan_karta_med_standardrubrik_och_kodade_namn()
        {
            var d = new LibData { Ranges = { new SiteRangeInfo(1, "Banan <1>", "Ankeborg & co", "/skjutbana/1"), new SiteRangeInfo(2, "Utan ort", null, "/skjutbana/2") } };
            var html = Render("ranges", new JsonObject(), d);
            Assert.Contains("<h3>Skjutbanor</h3>", html);
            Assert.Contains("Banan &lt;1&gt;", html);
            Assert.Contains("Ankeborg &amp; co", html);
            Assert.Contains("href=\"/skjutbana/2\"", html);
            Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("competitions")] [InlineData("records")] [InlineData("ranges")]
        public void Inga_poster_ger_tomt_svar_inte_en_tom_ruta_eller_en_ensam_rubrik(string type)
            => Assert.Equal("", Render(type, new JsonObject { ["title"] = "Rubrik" }, new LibData()));

        [Theory]
        [InlineData("competitions")] [InlineData("records")] [InlineData("ranges")]
        public void Blocket_följer_sin_nivå_en_icke_medlem_och_en_utloggad_ser_inget(string type)
        {
            var d = new LibData
            {
                Comps = { new SiteCompetitionInfo("T", new DateTime(2027, 1, 1), "/t/") },
                Recs = { new SiteRecordInfo("Precision", "K", false, "1 / 300", "H", new DateTime(2026, 1, 1)) },
                Ranges = { new SiteRangeInfo(1, "B", null, "/skjutbana/1") },
            };
            Assert.NotEqual("", Render(type, new JsonObject(), d));
            Assert.Equal("", Render(type, new JsonObject(), d, new SiteViewer(true, false, false, false)));
            Assert.Equal("", Render(type, new JsonObject(), d, SiteViewer.Anonymous, SiteVisibility.Inloggade));
        }

        [Fact]
        public void Mästarna_ritas_med_kodade_namn_år_och_enhet_och_aldrig_för_alla()
        {
            var d = new LibData { Champs = { new SiteChampionInfo(2026, "Precision", "C <Dam>", false, "285 / 300", "Eva & Co"),
                                             new SiteChampionInfo(2025, "Fältskytte", "Öppen", true, "42 träff (25 figurer)", "Lag <1>") } };
            var html = Render("champions", new JsonObject { ["title"] = "Mästare" }, d);
            Assert.Contains("<td>2026</td>", html);
            Assert.Contains("C &lt;Dam&gt;", html);
            Assert.Contains("Eva &amp; Co", html);
            Assert.Contains("42 träff (25 figurer)", html);
            Assert.Contains("Lag &lt;1&gt;", html);
            Assert.Contains("(lag)", html);
            // Personblock: även om författaren valt Alla (Dold→Alla-skalan) släpps en utloggad aldrig in.
            var anon = SiteBlockRenderer.Render(new SiteBlock("abcd1", "champions", SiteVisibility.Alla, new JsonObject()), SiteVisibility.Alla,
                new SiteRenderContext(SiteViewer.Anonymous, "/halland/ankeborg/", SiteTemplates.ClubTemplate, d));
            Assert.Equal("", anon);
            Assert.False(Parse("champions", "{}", vis: 0).Ok);
        }

        [Fact]
        public void Siffrorna_ritas_kodade_med_rubrik_och_ger_tomt_svar_utan_siffror()
        {
            var d = new LibData { StatItems = { new SiteStatItem("Medlemmar <x>", 123), new SiteStatItem("Skjutbanor", 2) } };
            var html = Render("stats", new JsonObject { ["title"] = "Vi i siffror" }, d);
            Assert.Contains("Vi i siffror", html);
            Assert.Contains(">123<", html);
            Assert.Contains("Medlemmar &lt;x&gt;", html);
            Assert.Equal("", Render("stats", new JsonObject { ["title"] = "Vi i siffror" }, new LibData()));
        }
    }
}
