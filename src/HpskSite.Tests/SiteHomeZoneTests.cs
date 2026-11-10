using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Zon (huvud- eller sidokolumn) på ägarens egna block på en omgjord Hem. Zonen är ren layout: den ändrar aldrig vem som ser blocket,
    /// finns bara på Hem, och "hela bredden" lagras aldrig, så en sida som inte valt kolumner har exakt samma JSON som förut.
    /// </summary>
    public class SiteHomeZoneTests
    {
        private sealed class NoData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
        }

        private static string Text(string id, int vis, string? zone) =>
            "{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":" + vis + ",\"data\":{\"html\":\"<p>Hej</p>\"" + (zone == null ? "" : ",\"zone\":\"" + zone + "\"") + "}}";

        private static SiteContentResult Parse(string owner, string page, SiteContentKind kind, params string[] blocks) =>
            SiteBlockSchema.Parse("{\"schema\":" + (kind == SiteContentKind.ConvertedPage ? 2 : 1) + ",\"blocks\":[" + string.Join(",", blocks) + "]}",
                kind, SiteVisibility.Alla, SiteTemplates.For(owner), kind == SiteContentKind.ConvertedPage ? page : null);

        public static IEnumerable<object[]> Owners() => new[] { SiteTemplates.Club, SiteTemplates.Region }.Select(o => new object[] { o });

        [Theory, MemberData(nameof(Owners))]
        public void Ett_eget_block_på_Hem_kan_ligga_i_huvud_eller_sidokolumnen(string owner)
        {
            var r = Parse(owner, "home", SiteContentKind.ConvertedPage, Text("aaaa1", 0, "main"), Text("bbbb2", 0, "side"));
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
            Assert.Equal("main", r.Blocks[0].Data["zone"]!.GetValue<string>());
            Assert.Equal("side", r.Blocks[1].Data["zone"]!.GetValue<string>());
        }

        [Theory, MemberData(nameof(Owners))]
        public void Planen_ritar_blocken_i_sin_zon(string owner)
        {
            var r = Parse(owner, "home", SiteContentKind.ConvertedPage, Text("aaaa1", 0, "main"), Text("bbbb2", 0, "side"), Text("cccc3", 0, null));
            var page = new ConvertedPage("home", ConvertedState.Ok, SiteVisibility.Alla, false, r);
            var plan = SiteConvertedPages.Plan(page, new SiteRenderContext(SiteViewer.Anonymous, "/x/", SiteTemplates.For(owner), new NoData()));
            Assert.Equal(new[] { PanelZone.Main, PanelZone.Side, PanelZone.Full }, plan.Items.Select(i => i.Zone).ToArray());
        }

        [Theory, MemberData(nameof(Owners))]
        public void Hela_bredden_lagras_aldrig_så_en_sida_utan_kolumner_har_samma_JSON_som_förut(string owner)
        {
            var withFull = Parse(owner, "home", SiteContentKind.ConvertedPage, Text("aaaa1", 0, "full"));
            var without = Parse(owner, "home", SiteContentKind.ConvertedPage, Text("aaaa1", 0, null));
            Assert.True(withFull.Ok && without.Ok);
            Assert.Null(withFull.Blocks[0].Data["zone"]);
            Assert.Equal(without.Blocks[0].Data.ToJsonString(), withFull.Blocks[0].Data.ToJsonString());
        }

        [Theory, MemberData(nameof(Owners))]
        public void Zon_vägras_på_en_egen_sida_och_på_andra_omgjorda_sidor_än_Hem(string owner)
        {
            var free = Parse(owner, "", SiteContentKind.FreePage, Text("aaaa1", 0, "side"));
            Assert.False(free.Ok);
            Assert.Contains(free.Errors, e => e.Message.Contains("Hem"));
            foreach (var page in SiteTemplates.For(owner).Pages.Where(p => !p.IsAdmin && p.Key != "home"))
            {
                var r = Parse(owner, page.Key, SiteContentKind.ConvertedPage, Text("aaaa1", 0, "side"));
                Assert.False(r.Ok, $"{owner}/{page.Key} släppte igenom en zon");
            }
        }

        [Theory, MemberData(nameof(Owners))]
        public void Okänd_zon_vägras(string owner)
        {
            foreach (var z in new[] { "left", "MAIN", "", "main; x" })
                Assert.False(Parse(owner, "home", SiteContentKind.ConvertedPage, Text("aaaa1", 0, z)).Ok, $"zon \"{z}\" släpptes igenom");
        }

        [Fact]
        public void Zonen_ändrar_aldrig_vem_som_ser_blocket()
        {
            // Ett block på nivån Medlemmar i sidokolumnen ritas för en medlem men aldrig för en utloggad, precis som utan zon.
            var r = Parse(SiteTemplates.Club, "home", SiteContentKind.ConvertedPage, Text("aaaa1", (int)SiteVisibility.Klubbmedlemmar, "side"));
            Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
            var page = new ConvertedPage("home", ConvertedState.Ok, SiteVisibility.Alla, false, r);
            SiteRenderContext Ctx(SiteViewer v) => new(v, "/x/", SiteTemplates.For(SiteTemplates.Club), new NoData());
            Assert.Empty(SiteConvertedPages.Plan(page, Ctx(SiteViewer.Anonymous)).Items);
            Assert.Single(SiteConvertedPages.Plan(page, Ctx(new SiteViewer(true, true, false, false))).Items);
        }
    }
}
