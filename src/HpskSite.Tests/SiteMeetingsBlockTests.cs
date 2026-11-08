using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2c: händelseblockets typfilter ("Bara möten", mallen Möten och kallelser).</summary>
    public class SiteMeetingsBlockTests
    {
        private static SiteContentResult Parse(string data) =>
            SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"events\",\"visibility\":2,\"data\":" + data + "}]}",
                SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, SiteTemplates.ClubTemplate);

        [Fact]
        public void Typfiltret_Möte_godtas_och_bevaras()
        {
            var c = Parse("{\"count\":3,\"type\":\"Möte\"}");
            Assert.True(c.Ok);
            Assert.Equal("Möte", c.Blocks[0].Data["type"]!.GetValue<string>());
        }

        [Fact]
        public void Utan_typ_visas_alla_händelser_och_ingen_typ_lagras()
        {
            var c = Parse("{\"count\":3}");
            Assert.True(c.Ok); Assert.Null(c.Blocks[0].Data["type"]);
        }

        [Theory]
        [InlineData("{\"type\":\"Tävling\"}")] [InlineData("{\"type\":\"<b>\"}")] [InlineData("{\"type\":\"\"}")] [InlineData("{\"type\":5}")] [InlineData("{\"type\":\"möte\"}")]
        public void En_okänd_eller_felformad_typ_avvisas(string data) => Assert.False(Parse(data).Ok);

        private sealed class Data : ISiteBlockData
        {
            public List<SiteEventInfo> Events = new();
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Events;                    // ignorerar både antal och typ
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count, string? eventType) => Events;  // en dålig implementation: lämnar ut allt
            public SiteContactInfo? Contact() => null;
        }

        private static string Render(JsonObject data, Data d) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "events", SiteVisibility.Klubbmedlemmar, data), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(new SiteViewer(true, true, false, false), "/halland/ankeborg/", SiteTemplates.ClubTemplate, d));

        [Fact]
        public void Renderaren_visar_bara_möten_även_om_lagringen_lämnar_ut_fler_och_respekterar_antalet()
        {
            var d = new Data();
            d.Events.Add(new SiteEventInfo("STÄDDAG", new DateTime(2027, 5, 1), null, "/x/", "Städning"));
            d.Events.Add(new SiteEventInfo("MÖTE-1", new DateTime(2027, 5, 2), null, "/x/", "Möte"));
            d.Events.Add(new SiteEventInfo("MÖTE-2", new DateTime(2027, 5, 3), null, "/x/", "Möte"));
            d.Events.Add(new SiteEventInfo("UTAN-TYP", new DateTime(2027, 5, 4), null, "/x/"));
            var html = Render(new JsonObject { ["count"] = 1, ["type"] = "Möte" }, d);
            Assert.Contains("MÖTE-1", html);
            Assert.DoesNotContain("STÄDDAG", html); Assert.DoesNotContain("UTAN-TYP", html); Assert.DoesNotContain("MÖTE-2", html);
        }

        [Fact]
        public void Utan_typfilter_visas_alla_som_förut()
        {
            var d = new Data(); d.Events.Add(new SiteEventInfo("STÄDDAG", new DateTime(2027, 5, 1), null, "/x/", "Städning"));
            Assert.Contains("STÄDDAG", Render(new JsonObject { ["count"] = 5 }, d));
        }

        [Fact]
        public void Standardimplementationen_gallrar_på_typ_när_en_läsare_saknar_eget_filter()
        {
            ISiteBlockData d = new MinimalData();
            var got = d.UpcomingEvents(2, "Möte");
            Assert.Equal(new[] { "M1", "M2" }, got.Select(e => e.Title).ToArray());
        }

        private sealed class MinimalData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => new[]
            {
                new SiteEventInfo("S", new DateTime(2027, 1, 1), null, "/", "Städning"), new SiteEventInfo("M1", new DateTime(2027, 1, 2), null, "/", "Möte"),
                new SiteEventInfo("M2", new DateTime(2027, 1, 3), null, "/", "Möte"), new SiteEventInfo("M3", new DateTime(2027, 1, 4), null, "/", "Möte"),
            };
            public SiteContactInfo? Contact() => null;
        }
    }
}
