using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Hemsidans vy över de vanliga sidorna (K5a): läget per sida (som förut / utkast / egen version), om "Ändra den här sidan" får visas, panelerna ägaren får välja,
    /// och förhandsvisningen av en omgjord sida ("Se som"). Lägesreglerna och förhandsvisningen är rena; SQL-provet visar att läget läses ur sidans riktiga versioner.
    /// </summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteSystemPagesTests
    {
        private sealed class NoData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
        }

        private sealed class Facts : ISiteOwnerFacts
        {
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => false;
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => Array.Empty<string>();
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => Array.Empty<string>();
        }

        private static bool Oracle(int level, SiteViewer v) => level switch
        {
            0 => true, 1 => v.LoggedIn, 2 => v.LoggedIn && (v.IsOwnerMember || v.IsOwnerAdmin), 3 => v.LoggedIn && (v.IsOwnerBoard || v.IsOwnerAdmin), 4 => v.LoggedIn && v.IsOwnerAdmin, _ => false,
        };

        private static SitePageListItem Copy(string key, int? pub, int? draft, int id = 11, int rev = 3) =>
            new() { Id = id, Slug = key, Revision = rev, PublishedSchema = pub, DraftSchema = draft, PublishedVersionId = pub == null ? null : 1, DraftVersionId = draft == null ? null : 2 };

        // ── Läget ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, null, SystemPageMode.AsBefore)]
        [InlineData(1, null, SystemPageMode.AsBefore)]
        [InlineData(1, 1, SystemPageMode.AsBefore)]
        [InlineData(null, 2, SystemPageMode.Draft)]
        [InlineData(1, 2, SystemPageMode.Draft)]
        [InlineData(2, null, SystemPageMode.Converted)]
        [InlineData(2, 2, SystemPageMode.Converted)]
        public void Läget_följer_schemat_i_den_publicerade_och_i_utkastversionen(int? published, int? draft, SystemPageMode want) =>
            Assert.Equal(want, SiteSystemPages.ModeOf(published, draft));

        [Fact]
        public void Läget_har_ett_ord_och_en_kod_som_klienten_kan_lita_på()
        {
            Assert.Equal(new[] { "Som förut", "Utkast, syns inte än", "Egen version" }, new[] { SystemPageMode.AsBefore, SystemPageMode.Draft, SystemPageMode.Converted }.Select(SiteSystemPages.Word));
            Assert.Equal(new[] { "asBefore", "draft", "converted" }, new[] { SystemPageMode.AsBefore, SystemPageMode.Draft, SystemPageMode.Converted }.Select(SiteSystemPages.Code));
        }

        [Theory]
        [InlineData("Club")]
        [InlineData("Region")]
        public void Alla_vanliga_sidor_listas_aldrig_Admin_och_utan_rad_är_de_som_förut(string owner)
        {
            var all = SiteSystemPages.Describe(owner, new List<SitePageListItem>(), convertEnabled: true, canWrite: true);
            var want = SiteTemplates.For(owner).Pages.Where(p => !p.IsAdmin).OrderBy(p => p.Order).Select(p => p.Key).ToList();
            Assert.Equal(want, all.Select(s => s.Key).ToList());
            Assert.DoesNotContain(all, s => SiteTemplates.For(owner).PageByKey(s.Key)!.IsAdmin);
            Assert.All(all, s => { Assert.Equal(SystemPageMode.AsBefore, s.Mode); Assert.Null(s.PageId); Assert.False(s.PendingChanges); Assert.True(s.CanConvert, s.Key); });
        }

        [Theory]
        [InlineData(false, true)]    // brytare 3 AV
        [InlineData(true, false)]    // stödläge / inte ägarens egen admin
        [InlineData(false, false)]
        public void Ändra_den_här_sidan_visas_bara_för_ägarens_egen_admin_med_brytare_3_på(bool convertEnabled, bool canWrite) =>
            Assert.All(SiteSystemPages.Describe("Club", new List<SitePageListItem>(), convertEnabled, canWrite), s => Assert.False(s.CanConvert, s.Key));

        [Fact]
        public void En_sida_som_redan_är_omgjord_eller_ett_utkast_kan_inte_göras_om_igen_och_bär_sitt_id()
        {
            var copies = new List<SitePageListItem> { Copy("home", 2, null, id: 21), Copy("calendar", null, 2, id: 22), Copy("records", 1, null, id: 23), Copy("documents", 2, 2, id: 24, rev: 9) };
            var d = SiteSystemPages.Describe("Club", copies, true, true).ToDictionary(s => s.Key);
            Assert.Equal((SystemPageMode.Converted, 21, false, false), (d["home"].Mode, d["home"].PageId, d["home"].PendingChanges, d["home"].CanConvert));
            Assert.Equal((SystemPageMode.Draft, 22, false, false), (d["calendar"].Mode, d["calendar"].PageId, d["calendar"].PendingChanges, d["calendar"].CanConvert));
            Assert.Equal((SystemPageMode.AsBefore, 23, false, true), (d["records"].Mode, d["records"].PageId, d["records"].PendingChanges, d["records"].CanConvert));   // en sida med egen text är som förut
            Assert.Equal((SystemPageMode.Converted, 24, true, false), (d["documents"].Mode, d["documents"].PageId, d["documents"].PendingChanges, d["documents"].CanConvert));
            Assert.Equal(9, d["documents"].Revision);
            Assert.Equal(SystemPageMode.AsBefore, d["about"].Mode);                                  // utan rad
        }

        [Fact]
        public void Vem_ser_sidan_är_mallens_öppnaste_nivå_och_för_en_omgjord_sida_dess_egen()
        {
            var t = SiteTemplates.For("Club");
            var copy = Copy("records", 2, null, id: 31); copy.Visibility = (byte)SiteVisibility.Styrelse;
            var d = SiteSystemPages.Describe("Club", new List<SitePageListItem> { copy }, true, true).ToDictionary(s => s.Key);
            Assert.Equal((int)SiteVisibility.Styrelse, d["records"].Level);                              // omgjord: sidans egen nivå
            Assert.Equal((int)t.MostOpenLevel("home")!.Value, d["home"].Level);                           // annars mallens öppnaste
            Assert.All(d.Values.Where(s => s.Key != "records"), s => Assert.Equal((int)t.MostOpenLevel(s.Key)!.Value, s.Level));
        }

        // ── Panelerna ägaren får välja ───────────────────────────────────────────

        [Theory]
        [InlineData("Club")]
        [InlineData("Region")]
        public void Panelerna_per_sida_är_registrets_valbara_och_bär_namn_zon_och_obligatorisk(string owner)
        {
            foreach (var page in SiteTemplates.For(owner).Pages.Where(p => !p.IsAdmin))
            {
                var got = SiteSystemPages.PanelsFor(owner, page.Key).Select(o => System.Text.Json.JsonSerializer.SerializeToElement(o)).ToList();
                var want = SiteSystemPages.InPageOrder(owner, page.Key);
                Assert.Equal(PanelCatalog.Selectable(owner, page.Key).Select(d => d.Key).OrderBy(k => k), want.Select(d => d.Key).OrderBy(k => k));   // samma mängd som registrets valbara
                Assert.Equal(want.Select(d => d.Key), got.Select(e => e.GetProperty("key").GetString()));
                foreach (var (d, e) in want.Zip(got))
                {
                    Assert.Equal(d.Name, e.GetProperty("name").GetString());
                    Assert.Equal(d.Required, e.GetProperty("required").GetBoolean());
                    Assert.Equal(d.PersonalData, e.GetProperty("personalData").GetBoolean());
                    Assert.Contains(e.GetProperty("zone").GetString(), new[] { "main", "side", "full" });
                }
            }
            // Inloggningsrutan är mallens egen (bara för utloggade) och får aldrig erbjudas som val.
            Assert.DoesNotContain(SiteSystemPages.PanelsFor("Club", "home").Select(o => System.Text.Json.JsonSerializer.SerializeToElement(o).GetProperty("key").GetString()), k => k == "login-prompt");
        }

        [Fact]
        public void Panelerna_står_i_den_ordning_sidan_visar_dem()
        {
            // Klubbens Hem för en inloggad: Det här händer, Kommande händelser, Motioner, Kontakt, Snabblänkar — sedan det bara utloggad-listan ritar (Dokument med snabblänk).
            var keys = SiteSystemPages.InPageOrder("Club", "home").Select(d => d.Key).ToList();
            Assert.Equal(new[] { "whats-happening", "upcoming-events", "motions", "contact-card", "quicklinks", "quicklink-docs" }, keys);
        }

        // ── Förhandsvisning av en omgjord sida ───────────────────────────────────

        private static SiteContentResult Converted(string owner, string page, SiteVisibility pageVis, string blocksJson)
        {
            var c = SiteBlockSchema.Parse("{\"schema\":2,\"blocks\":[" + blocksJson + "]}", SiteContentKind.ConvertedPage, pageVis, SiteTemplates.For(owner), page);
            Assert.True(c.Ok, string.Join("; ", c.Errors.Select(e => e.Message)));
            return c;
        }

        private static string Text(string id, string canary, int vis) => "{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":" + vis + ",\"data\":{\"html\":\"<p>" + canary + "</p>\"}}";

        [Fact]
        public void Förhandsvisningen_av_en_omgjord_sida_visar_egen_text_och_beslut_precis_som_besökarens_sida()
        {
            var failures = new List<string>();
            foreach (var persona in SitePagePreview.Personas("Club"))
            foreach (var pageVis in Enumerable.Range(0, 5))
            foreach (var blockVis in Enumerable.Range(pageVis, 5 - pageVis))
            {
                var canary = $"KANARIE-{persona.Key}-{pageVis}-{blockVis}";
                var content = Converted("Club", "records", (SiteVisibility)pageVis, Text("abcd1", canary, blockVis));
                var r = SitePagePreview.BuildConverted("Club", "Rekord", "records", (SiteVisibility)pageVis, content, persona, new NoData(), "/k/");
                var ok = Oracle(blockVis, persona.Viewer);          // sidan visas för den som får se minst ett block, blocket för den som får se det
                var want = ok ? SitePageDecision.Serve
                    : persona.Viewer.LoggedIn ? SitePageDecision.NotFound
                    : blockVis is 1 or 2 ? SitePageDecision.LoginRedirect : SitePageDecision.NotFound;
                if (r.Decision != want) failures.Add($"beslut {persona.Key}/sida {pageVis}/block {blockVis}: {r.Decision} (väntade {want})");
                if (r.Document.Contains(canary) != ok) failures.Add($"{(ok ? "SAKNAS" : "LÄCKA")}: {persona.Key}/sida {pageVis}/block {blockVis}");
                if (r.Decision != SitePageDecision.Serve && r.Document.Length != 0) failures.Add($"dokument trots {r.Decision}: {persona.Key}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        }

        [Fact]
        public void En_panel_visas_som_kort_med_namn_bara_för_den_som_får_se_den_och_ritas_aldrig_med_levande_data()
        {
            var content = Converted("Club", "calendar", SiteVisibility.Alla,
                "{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":2,\"data\":{\"key\":\"calendar\"}}," + Text("abcd1", "ÖPPEN-TEXT", 0));
            var anon = SitePagePreview.BuildConverted("Club", "Kalender", "calendar", SiteVisibility.Alla, content, SitePagePreview.Persona("Club", "anon")!, new NoData(), "/k/");
            var member = SitePagePreview.BuildConverted("Club", "Kalender", "calendar", SiteVisibility.Alla, content, SitePagePreview.Persona("Club", "member")!, new NoData(), "/k/");
            Assert.Contains("ÖPPEN-TEXT", anon.Document);
            Assert.DoesNotContain("card mb-3", anon.Document);                                 // panelen är för medlemmar: utloggad ser den inte
            Assert.Contains("ÖPPEN-TEXT", member.Document);
            Assert.Contains("card mb-3", member.Document);
            Assert.Contains(PanelCatalog.Find("Club", "calendar")!.Name, member.Document);
            Assert.Contains("automatiskt innehåll", member.Notice);
        }

        [Fact]
        public void En_omgjord_sida_utan_något_block_för_tittaren_säger_det()
        {
            var content = Converted("Club", "calendar", SiteVisibility.Alla, "{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":3,\"data\":{\"key\":\"calendar\"}}," + Text("abcd1", "STYRELSE-TEXT", 3));
            var logged = SitePagePreview.BuildConverted("Club", "Kalender", "calendar", SiteVisibility.Alla, content, SitePagePreview.Persona("Club", "member")!, new NoData(), "/k/");
            Assert.Equal(SitePageDecision.NotFound, logged.Decision);                          // medlem får inte se något av sidan: den finns inte för hen
            Assert.DoesNotContain("STYRELSE-TEXT", logged.Document);
            var board = SitePagePreview.BuildConverted("Club", "Kalender", "calendar", SiteVisibility.Alla, content, SitePagePreview.Persona("Club", "board")!, new NoData(), "/k/");
            Assert.Equal(SitePageDecision.Serve, board.Decision);
            Assert.Contains("STYRELSE-TEXT", board.Document);
        }

        // ── SQL: läget ur sidans riktiga versioner ───────────────────────────────

        [SqlFact]
        public void Läget_ur_databasen_följer_omvandla_publicera_och_lägg_tillbaka()
        {
            const int owner = 990003;
            using var f = new SiteSqlFixture();
            f.Db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled, ConvertEnabled) VALUES ('Club', @0, 1, 1, 1)", owner);
            var now = new DateTime(2026, 11, 8, 12, 0, 0);
            var actor = new SiteActor(7);
            var facts = new Facts();
            SystemPageState Of(string key) => SiteSystemPages.Describe("Club", SitePageStore.ListTemplateCopies(f.Db, "Club", owner), true, true).Single(s => s.Key == key);

            Assert.Equal(SystemPageMode.AsBefore, Of("calendar").Mode);
            Assert.True(SiteOwnerData.ConvertEnabled(f.Db, "Club", owner));

            var conv = SitePageWriter.ConvertTemplatePage(f.Db, facts, "Club", owner, "calendar", actor, now);
            Assert.True(conv.Ok, conv.Message);
            Assert.Equal((SystemPageMode.Draft, conv.PageId, false, false), (Of("calendar").Mode, Of("calendar").PageId, Of("calendar").PendingChanges, Of("calendar").CanConvert));

            var pub = SitePageWriter.Publish(f.Db, facts, conv.PageId, "Club", owner, conv.Revision, actor, now);
            Assert.True(pub.Ok, pub.Message);
            Assert.Equal((SystemPageMode.Converted, false), (Of("calendar").Mode, Of("calendar").PendingChanges));

            var rev = SitePageWriter.RevertConversion(f.Db, facts, conv.PageId, "Club", owner, pub.Revision, actor, now);
            Assert.True(rev.Ok, rev.Message);
            Assert.Equal((SystemPageMode.AsBefore, true), (Of("calendar").Mode, Of("calendar").CanConvert));   // Lägg tillbaka: som förut, och går att göra om igen

            f.Db.Execute("UPDATE dbo.SiteOwner SET ConvertEnabled = 0 WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
            Assert.False(SiteOwnerData.ConvertEnabled(f.Db, "Club", owner));
            f.Db.Execute("UPDATE dbo.SiteOwner SET ConvertEnabled = 1, EditingEnabled = 0 WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
            Assert.False(SiteOwnerData.ConvertEnabled(f.Db, "Club", owner));                                   // brytare 3 gäller bara med brytare 2
        }
    }
}
