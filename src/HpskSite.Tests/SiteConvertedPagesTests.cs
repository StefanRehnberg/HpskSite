using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Omvandlade sidor i vyerna (konvertera-planen v2, K3/K4): vad en omvandlad sida ritar, och att menyn, flikarna och åtkomsten följer DESS block.
    /// En omvandlad standardsida ska visa exakt samma paneler som standardsidan för varje tittare (ingenting öppnas, ingenting försvinner).
    /// </summary>
    public class SiteConvertedPagesTests
    {
        private sealed class NoData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
        }

        private static readonly SiteViewer[] Viewers =
        {
            SiteViewer.Anonymous,
            new(true, false, false, false),   // inloggad, inte medlem
            new(true, true, false, false),    // medlem
            new(true, true, true, false),     // styrelse
            new(true, true, true, true),      // admin
        };

        private static ConvertedPage Standard(string owner, string page, bool suspended = false)
        {
            var content = SiteConversion.Build(owner, page, null);
            return new ConvertedPage(page, ConvertedState.Ok, SiteConversion.PageVisibility(owner, page), suspended, content);
        }

        private static ConvertedPage Own(string owner, string page, string blocksJson, SiteVisibility pageVis, bool suspended = false)
        {
            var t = SiteTemplates.For(owner);
            var content = SiteBlockSchema.Parse("{\"schema\":2,\"blocks\":[" + blocksJson + "]}", SiteContentKind.ConvertedPage, pageVis, t, page);
            Assert.True(content.Ok, string.Join("; ", content.Errors.Select(e => e.Message)));
            return new ConvertedPage(page, ConvertedState.Ok, pageVis, suspended, content);
        }

        private static string PanelJson(string id, string key, int vis) =>
            "{\"id\":\"" + id + "\",\"type\":\"panel\",\"visibility\":" + vis + ",\"data\":{\"key\":\"" + key + "\"}}";

        private static string TextJson(string id, string html, int vis) =>
            "{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":" + vis + ",\"data\":{\"html\":\"" + html + "\"}}";

        private static SiteRenderContext Ctx(string owner, SiteViewer v) => new(v, "/x/", SiteTemplates.For(owner), new NoData());

        private static IEnumerable<string> PanelKeys(ConvertedPlan p) => p.Items.Where(i => i.PanelKey != null).Select(i => i.PanelKey!);

        public static IEnumerable<object[]> AllPages() =>
            new[] { SiteTemplates.Club, SiteTemplates.Region }
                .SelectMany(o => SiteTemplates.For(o).Pages.Where(p => !p.IsAdmin).Select(p => new object[] { o, p.Key }));

        [Theory]
        [MemberData(nameof(AllPages))]
        public void En_omvandlad_standardsida_visar_samma_paneler_som_standardsidan_för_varje_tittare(string owner, string page)
        {
            var template = SiteTemplates.For(owner);
            var converted = Standard(owner, page);
            foreach (var v in Viewers)
            {
                var plan = SiteConvertedPages.Plan(converted, Ctx(owner, v));
                Assert.False(plan.Broken);
                // Standardsidans paneler för tittaren: standardlistan (inloggad eller utloggad), mallens nivå per panel.
                var pageDef = template.PageByKey(page)!;
                var expected = PanelCatalog.Standard(owner, page, v.LoggedIn).Select(p => p.Key)
                    .Where(k => pageDef.Blocks.FirstOrDefault(b => b.Key == k && !b.Retired) is { } d && SiteAccess.CanSee(SiteTemplate.EffectiveVisibility(d), v))
                    .ToList();
                var actual = PanelKeys(plan).ToList();
                // Allt standardsidan visar visas, i samma ordning ...
                Assert.Equal(expected, actual.Where(expected.Contains).ToList());
                // ... och det enda tillägget är de fristående snabblänkdokumenten för den som inte ser Snabblänkar (testet i läcktestmotorn tillåter exakt det).
                var extra = actual.Except(expected).ToList();
                Assert.True(extra.All(k => k == "quicklink-docs") && (extra.Count == 0 || !actual.Contains("quicklinks")),
                    $"{owner}/{page} {(v.LoggedIn ? "inloggad" : "utloggad")}: extra {string.Join(",", extra)}");
            }
        }

        [Fact]
        public void Ingen_panel_ritas_för_någon_som_inte_får_se_den()
        {
            // Medlemskatalogen (personuppgifter) lagrad som Alla: aldrig öppen för en utloggad, aldrig öppnare än Inloggade.
            var block = new SiteBlock("abcd1", "panel", SiteVisibility.Alla, new JsonObject { ["key"] = "member-directory", ["zone"] = "full" });
            Assert.Equal(SiteVisibility.Inloggade, SiteConvertedPages.Effective(block, SiteVisibility.Alla, SiteTemplates.Club));
            Assert.False(SiteBlockRenderer.PanelVisible(block, SiteVisibility.Alla, Ctx(SiteTemplates.Club, SiteViewer.Anonymous)));
            Assert.True(SiteBlockRenderer.PanelVisible(block, SiteVisibility.Alla, Ctx(SiteTemplates.Club, new SiteViewer(true, false, false, false))));
        }

        [Fact]
        public void Nödbromsen_stoppar_ägarens_egna_block_men_inte_panelerna()
        {
            var json = PanelJson("abcd1", "calendar", 1) + "," + TextJson("abcd2", "<p>Egen text</p>", 1);
            var running = Own(SiteTemplates.Club, "calendar", json, SiteVisibility.Inloggade);
            var braked = Own(SiteTemplates.Club, "calendar", json, SiteVisibility.Inloggade, suspended: true);
            var v = new SiteViewer(true, true, false, false);
            Assert.Contains(SiteConvertedPages.Plan(running, Ctx(SiteTemplates.Club, v)).Items, i => i.PanelKey == null && i.Html.Contains("Egen text"));
            var plan = SiteConvertedPages.Plan(braked, Ctx(SiteTemplates.Club, v));
            Assert.DoesNotContain(plan.Items, i => i.PanelKey == null);
            Assert.Equal(new[] { "calendar" }, PanelKeys(plan));
            // Sidans synlighet följer samma regel: utan de egna blocken räknas bara panelerna.
            var def = SiteConvertedPages.EffectiveDef(SiteTemplates.ClubTemplate.PageByKey("calendar")!, braked, SiteTemplates.Club);
            Assert.Equal(new[] { "abcd1" }, def.Blocks.Select(b => b.Key));
        }

        [Fact]
        public void Ett_tolkningsfel_ger_felkort_aldrig_standardsidan_och_sidan_behåller_mallens_synlighet()
        {
            var broken = new ConvertedPage("calendar", ConvertedState.Broken, SiteVisibility.Inloggade, false, null);
            var plan = SiteConvertedPages.Plan(broken, Ctx(SiteTemplates.Club, Viewers[2]));
            Assert.True(plan.Broken);
            Assert.Empty(plan.Items);
            var def = SiteTemplates.ClubTemplate.PageByKey("calendar")!;
            Assert.Same(def, SiteConvertedPages.EffectiveDef(def, broken, SiteTemplates.Club));
        }

        [Fact]
        public void Menyn_och_åtkomsten_följer_den_omvandlade_sidans_block_inte_mallens()
        {
            // Kalendern omvandlad till BARA en text för styrelsen: en vanlig medlem ska varken se den i menyn eller komma in (mallen släppte in medlemmar).
            var page = Own(SiteTemplates.Club, "calendar", TextJson("abcd1", "<p>Bara styrelsen</p>", 3), SiteVisibility.Styrelse);
            var template = SiteConvertedPages.Apply(SiteTemplates.ClubTemplate, new Dictionary<string, ConvertedPage> { ["calendar"] = page });
            var calendar = template.PageByKey("calendar")!;
            var member = new SiteViewer(true, true, false, false);
            var board = new SiteViewer(true, true, true, false);
            Assert.True(SiteTemplates.ClubTemplate.PageVisible(SiteTemplates.ClubTemplate.PageByKey("calendar")!, member));   // mallens: medlemmar fick se den
            Assert.False(template.PageVisible(calendar, member));
            Assert.True(template.PageVisible(calendar, board));
            Assert.DoesNotContain(template.MenuFor(member), p => p.Key == "calendar");
            Assert.Contains(template.MenuFor(board), p => p.Key == "calendar");
            Assert.Equal(SitePageDecision.NotFound, SitePageAccessRules.Decide(template, calendar, member));
            Assert.Equal(SitePageDecision.Serve, SitePageAccessRules.Decide(template, calendar, board));
            // Andra sidor är orörda.
            Assert.Same(SiteTemplates.ClubTemplate.PageByKey("about"), template.PageByKey("about"));
        }

        [Fact]
        public void Inga_omvandlade_sidor_ger_samma_mall()
        {
            Assert.Same(SiteTemplates.ClubTemplate, SiteConvertedPages.Apply(SiteTemplates.ClubTemplate, new Dictionary<string, ConvertedPage>()));
        }
    }
}
