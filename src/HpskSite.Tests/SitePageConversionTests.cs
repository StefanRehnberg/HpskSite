using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>"Ändra den här sidan" (konvertera-planen v2, K2): standardsidan i schema 2 (rena prov) och skrivlagret mot en riktig SQL Server (transaktion som rullas tillbaka).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePageConversionTests
    {
        private const string Club = "Club";
        private const int Owner = 990001;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static readonly SiteActor Admin = new(7);

        private sealed class Facts : ISiteOwnerFacts
        {
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => false;
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => new List<string>();
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => new List<string>();
        }

        private static readonly SiteViewer[] Viewers =
        {
            SiteViewer.Anonymous, new(true, false, false, false), new(true, true, false, false), new(true, true, true, false), new(true, false, false, true),
        };

        // ── Rena prov: standardsidan ──────────────────────────────────────────────

        public static IEnumerable<object[]> ConvertiblePages() =>
            new[] { SiteTemplates.Club, SiteTemplates.Region }.SelectMany(o => SiteTemplates.For(o).Pages.Where(p => !p.IsAdmin).Select(p => new object[] { o, p.Key }));

        [Theory]
        [MemberData(nameof(ConvertiblePages))]
        public void Varje_systemsida_går_att_bygga_som_giltig_schema_2_med_nivåer_som_aldrig_är_öppnare_än_mallens(string owner, string page)
        {
            var c = SiteConversion.Build(owner, page, null);
            Assert.Equal(SiteContentKind.ConvertedPage, c.Kind);
            Assert.NotEmpty(c.Blocks);
            var pageVis = SiteConversion.PageVisibility(owner, page);
            foreach (var b in c.Blocks)
            {
                var key = b.Data["key"]!.GetValue<string>();
                Assert.Equal(SiteConversion.PanelVisibility(owner, page, key), b.Visibility);
                if (b.Visibility != SiteVisibility.Utloggad) Assert.True(b.Visibility >= pageVis, $"{owner}/{page}/{key} är öppnare än sidan");
            }
            // Varje panel som INTE får tas bort ligger kvar, och varje obligatorisk panel i någon av standardlistorna är med.
            var keys = c.Blocks.Select(b => b.Data["key"]!.GetValue<string>()).ToHashSet();
            foreach (var p in PanelCatalog.Standard(owner, page, true).Concat(PanelCatalog.Standard(owner, page, false)))
                if (PanelCatalog.Find(owner, p.Key)!.Required) Assert.Contains(p.Key, keys);
        }

        [Fact]
        public void Klubbens_Hem_bevarar_både_inloggad_och_utloggad_sida_i_rätt_ordning()
        {
            var keys = SiteConversion.Build(Club, "home", null).Blocks.Select(b => b.Data["key"]!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "about-text", "whats-happening", "upcoming-events", "motions", "contact-card", "quicklink-docs", "login-prompt", "quicklinks" }, keys);
            // Kretsens Hem är oförändrad ordning (utloggad och inloggad har samma lista).
            Assert.Equal(PanelCatalog.Standard(SiteTemplates.Region, "home", true).Select(p => p.Key),
                SiteConversion.Build(SiteTemplates.Region, "home", null).Blocks.Select(b => b.Data["key"]!.GetValue<string>()));
        }

        [Theory]
        [MemberData(nameof(ConvertiblePages))]
        public void Ingen_tittare_ser_en_panel_mer_eller_mindre_än_förut_utom_snabblänkdokumenten_som_ritas_i_Snabblänkar(string owner, string page)
        {
            var template = SiteTemplates.For(owner);
            var converted = SiteConversion.Build(owner, page, null);
            foreach (var viewer in Viewers)
            {
                var ctx = new SiteRenderContext(viewer, "/x/", template, null!);
                var seen = converted.Blocks.Where(b => SiteBlockRenderer.PanelVisible(b, SiteConversion.PageVisibility(owner, page), ctx))
                    .Select(b => b.Data["key"]!.GetValue<string>()).ToHashSet();
                var before = PanelCatalog.Standard(owner, page, viewer.LoggedIn)
                    .Where(p => SiteAccess.CanSee(SiteConversion.PanelVisibility(owner, page, p.Key), viewer)).Select(p => p.Key).ToHashSet();
                var extra = seen.Except(before).ToList();
                var missing = before.Except(seen).ToList();
                Assert.Empty(missing);
                // Det enda tillägget: klubbens snabblänkdokument, som en inloggad redan ser inuti Snabblänkar (K3 ritar dem inte två gånger).
                Assert.All(extra, k => Assert.Equal("quicklink-docs", k));
            }
        }

        [Theory]
        [MemberData(nameof(ConvertiblePages))]
        public void Registrets_personpaneler_är_samma_som_mallens_på_varje_sida(string owner, string page)
        {
            foreach (var p in SiteConversion.Panels(owner, page))
            {
                var def = SiteTemplates.For(owner).PageByKey(page)!.Blocks.FirstOrDefault(b => b.Key == p.Key);
                Assert.NotNull(def);   // varje panel har en mallnivå (annars lagras den som Dold)
                Assert.Equal(def!.PersonalData, PanelCatalog.Find(owner, p.Key)!.PersonalData);
            }
        }

        [Fact]
        public void Egen_text_före_och_efter_behåller_sina_id_och_ett_krockande_id_byts()
        {
            var slots = SiteBlockSchema.Parse("{\"schema\":1,\"slots\":{\"before\":[{\"id\":\"pnl01\",\"type\":\"text\",\"visibility\":1,\"data\":{\"html\":\"<p>FÖRE</p>\"}}],\"after\":[{\"id\":\"efter\",\"type\":\"text\",\"visibility\":1,\"data\":{\"html\":\"<p>EFTER</p>\"}}]}}",
                SiteContentKind.TemplateCopy, SiteVisibility.Inloggade, SiteTemplates.ClubTemplate);
            Assert.True(slots.Ok);
            var c = SiteConversion.Build(Club, "calendar", slots);
            Assert.Equal(new[] { "text", "panel", "text" }, c.Blocks.Select(b => b.Type));
            Assert.Equal(3, c.Blocks.Select(b => b.Id).Distinct().Count());
            Assert.Contains("FÖRE", c.Blocks[0].Data["html"]!.GetValue<string>());
            Assert.Equal("efter", c.Blocks[2].Id);
            Assert.NotEqual("pnl01", c.Blocks[0].Id);
        }

        // ── SQL: skrivlagret ──────────────────────────────────────────────────────

        private static void Seed(IDatabase db, bool editing = true, bool convert = true) =>
            db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled, ConvertEnabled) VALUES ('Club', @0, 1, @1, @2)", Owner, editing, convert);

        private static int Scalar(IDatabase db, string sql, params object[] a) => db.ExecuteScalar<int>(sql, a);

        private static string Slots(string before = "", string after = "") => "{\"schema\":1,\"slots\":{\"before\":[" + before + "],\"after\":[" + after + "]}}";
        private static string SlotText(string id, string html) => "{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":1,\"data\":{\"html\":\"<p>" + html + "</p>\"}}";

        [SqlFact]
        public void Omvandling_kräver_brytare_1_2_och_3_och_ger_annars_ConvertOff_utan_att_skriva_något()
        {
            foreach (var (editing, convert) in new[] { (true, false), (false, true), (false, false) })
            {
                using var f = new SiteSqlFixture(); Seed(f.Db, editing, convert);
                var r = SitePageWriter.ConvertTemplatePage(f.Db, new Facts(), Club, Owner, "calendar", Admin, Now);
                Assert.Equal(SiteWriteStatus.ConvertOff, r.Status);
                Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", Owner));
            }
            using var g = new SiteSqlFixture();
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.ConvertTemplatePage(g.Db, new Facts(), Club, Owner, "calendar", Admin, Now).Status);   // ägaren saknar rad
        }

        [SqlFact]
        public void Omvandling_ger_ett_utkast_i_schema_2_och_lämnar_den_levande_sidan_orörd()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "calendar", SiteVisibility.Inloggade, Admin, Now);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Kalender", Slots(SlotText("abcd1", "FÖRE")), null, Admin, Now);
            var p = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, s.Revision, Admin, Now);
            Assert.True(p.Ok, p.Message);
            var liveBefore = SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.Id;

            var r = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, Now);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(c.PageId, r.PageId);
            var page = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal(liveBefore, page.PublishedVersionId);                                           // den levande sidan är oförändrad
            Assert.True(SitePageStore.IsConverted(f.Db, c.PageId));
            var draft = SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal(2, draft.SchemaVersion);
            Assert.Equal(SiteContentKind.ConvertedPage, SiteBlockSchema.KindOf(page.IsFree, draft.BlocksJson));
            var parsed = SiteBlockSchema.Parse(draft.BlocksJson, SiteContentKind.ConvertedPage, page.VisibilityLevel, SiteTemplates.ClubTemplate, "calendar");
            Assert.True(parsed.Ok);
            Assert.Equal(new[] { "text", "panel" }, parsed.Blocks.Select(b => b.Type));                    // ägarens text före, sedan panelen
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-convert'", c.PageId));
            // LoadSlots-vägen (läsvägen) får aldrig tolka en omgjord sida som platser: kvar är den publicerade schema-1-versionen.
            Assert.Equal(1, SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.SchemaVersion);
        }

        [SqlFact]
        public void Omvandling_är_idempotent_dubbelklick_ger_inte_två_utkast()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var a = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "home", Admin, Now);
            var b = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "home", Admin, Now);
            Assert.True(a.Ok && b.Ok);
            Assert.Equal(a.PageId, b.PageId);
            Assert.Equal(a.Revision, b.Revision);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0 AND TemplateKey='home'", Owner));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId=@0 AND SchemaVersion=2", a.PageId));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-convert'", a.PageId));
        }

        [SqlFact]
        public void Omvandling_vägras_när_sidan_har_opublicerade_ändringar_och_för_Admin_och_okända_sidor()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "calendar", SiteVisibility.Inloggade, Admin, Now);
            Assert.True(SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Kalender", Slots(SlotText("abcd1", "OPUBLICERAD")), null, Admin, Now).Ok);
            var r = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, r.Status);
            Assert.False(SitePageStore.IsConverted(f.Db, c.PageId));
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "admin", Admin, Now).Status);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "finns-inte", Admin, Now).Status);
        }

        [SqlFact]
        public void Avbryt_kastar_utkastet_utan_att_röra_den_levande_sidan_och_omvandlingen_går_att_göra_igen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            // Sida som aldrig publicerats: Avbryt går tillbaka till baslinjen.
            var a = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "records", Admin, Now);
            Assert.True(a.Ok, a.Message);
            var cancelled = SitePageWriter.CancelConversion(f.Db, facts, a.PageId, Club, Owner, a.Revision, Admin, Now);
            Assert.True(cancelled.Ok, cancelled.Message);
            Assert.False(SitePageStore.IsConverted(f.Db, a.PageId));
            var again = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "records", Admin, Now);
            Assert.True(again.Ok, again.Message);
            Assert.True(SitePageStore.IsConverted(f.Db, a.PageId));

            // Sida med publicerad text: Avbryt kastar bara utkastet.
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "calendar", SiteVisibility.Inloggade, Admin, Now);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Kalender", Slots(SlotText("abcd1", "LIVE")), null, Admin, Now);
            Assert.True(SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, s.Revision, Admin, Now).Ok);
            var conv = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, Now);
            var live = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.PublishedVersionId;
            var cc = SitePageWriter.CancelConversion(f.Db, facts, c.PageId, Club, Owner, conv.Revision, Admin, Now);
            Assert.True(cc.Ok, cc.Message);
            var after = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal(live, after.PublishedVersionId);
            Assert.Null(after.DraftVersionId);
            Assert.False(SitePageStore.IsConverted(f.Db, c.PageId));
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CancelConversion(f.Db, facts, c.PageId, Club, Owner, cc.Revision, Admin, Now).Status);   // ingen omvandling pågår
        }

        [SqlFact]
        public void En_gammal_flik_med_fel_revision_får_Conflict_och_ingenting_ändras()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var a = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "records", Admin, Now);
            var stale = SitePageWriter.CancelConversion(f.Db, facts, a.PageId, Club, Owner, a.Revision - 1, Admin, Now);
            Assert.Equal(SiteWriteStatus.Conflict, stale.Status);
            Assert.True(SitePageStore.IsConverted(f.Db, a.PageId));
        }

        [SqlFact]
        public void Publicerad_omvandling_går_att_redigera_men_inte_att_skriva_tillbaka_som_schema_1_med_en_vanlig_sparning()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var a = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "records", Admin, Now);
            var p = SitePageWriter.Publish(f.Db, facts, a.PageId, Club, Owner, a.Revision, Admin, Now);
            Assert.True(p.Ok, p.Message);
            var page = SitePageStore.GetPage(f.Db, a.PageId, Club, Owner)!;
            var pub = SitePageStore.GetPublished(f.Db, a.PageId, Club, Owner)!;
            Assert.Equal(2, pub.SchemaVersion);
            // Redigering i schema 2 går.
            var edited = pub.BlocksJson.Replace("}]}", "},{\"id\":\"egen1\",\"type\":\"text\",\"visibility\":1,\"data\":{\"html\":\"<p>Ny text</p>\"}}]}");
            Assert.True(SitePageWriter.SaveDraft(f.Db, facts, a.PageId, Club, Owner, p.Revision, "Rekord", edited, null, Admin, Now).Ok);
            // En vanlig sparning kan inte tyst göra tillbaka sidan.
            var silent = SitePageWriter.SaveDraft(f.Db, facts, a.PageId, Club, Owner, p.Revision + 1, "Rekord", Slots(), null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, silent.Status);
            Assert.Contains("Gå tillbaka till standardsidan", silent.Message);
        }

        [SqlFact]
        public void Lägg_tillbaka_publicerar_den_tidigare_versionen_och_efteråt_är_sidan_vanlig_igen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "calendar", SiteVisibility.Inloggade, Admin, Now);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Kalender", Slots(SlotText("abcd1", "URSPRUNGLIG")), null, Admin, Now);
            var p1 = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, s.Revision, Admin, Now);
            var conv = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, Now);
            var p2 = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, conv.Revision, Admin, Now);
            Assert.True(p2.Ok, p2.Message);
            Assert.Equal(2, SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.SchemaVersion);

            var r = SitePageWriter.RevertConversion(f.Db, facts, c.PageId, Club, Owner, p2.Revision, Admin, Now);
            Assert.True(r.Ok, r.Message);
            Assert.False(SitePageStore.IsConverted(f.Db, c.PageId));
            var pub = SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal(1, pub.SchemaVersion);
            Assert.Contains("URSPRUNGLIG", pub.BlocksJson);                                                // ägarens text finns kvar
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.DraftVersionId);
            Assert.True(Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId=@0 AND SchemaVersion=2", c.PageId) >= 1);   // historiken finns kvar
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-convert-revert'", c.PageId));
            // Efteråt kan schema 2 inte skrivas med en vanlig sparning (grinden gäller igen), men en ny omvandling går.
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, r.Revision, "Kalender",
                "{\"schema\":2,\"blocks\":[]}", null, Admin, Now).Status);
            Assert.True(SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, Now).Ok);
            // Lägg tillbaka på en sida som inte är omgjord ger ett vanligt fel.
            var plain = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "records", SiteVisibility.Inloggade, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.RevertConversion(f.Db, facts, plain.PageId, Club, Owner, plain.Revision, Admin, Now).Status);
        }

        [SqlFact]
        public void Lägg_tillbaka_på_en_sida_som_aldrig_haft_egen_text_ger_en_tom_vanlig_sida()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var a = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "records", Admin, Now);
            var p = SitePageWriter.Publish(f.Db, facts, a.PageId, Club, Owner, a.Revision, Admin, Now);
            var r = SitePageWriter.RevertConversion(f.Db, facts, a.PageId, Club, Owner, p.Revision, Admin, Now);
            Assert.True(r.Ok, r.Message);
            var pub = SitePageStore.GetPublished(f.Db, a.PageId, Club, Owner)!;
            var c = SiteBlockSchema.Parse(pub.BlocksJson, SiteContentKind.TemplateCopy, SiteVisibility.Inloggade, SiteTemplates.ClubTemplate);
            Assert.True(c.Ok); Assert.Empty(c.AllBlocks);
        }

        [SqlFact]
        public void Gallringen_skyddar_den_senaste_schema_1_versionen_på_en_omgjord_sida_men_ingen_annan_gammal_version()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var old = Now.AddYears(-3);
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "calendar", SiteVisibility.Inloggade, Admin, old);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Kalender", Slots(SlotText("abcd1", "SKYDDAD")), null, Admin, old);
            var p1 = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, s.Revision, Admin, old);
            var protectedId = SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.Id;
            var conv = SitePageWriter.ConvertTemplatePage(f.Db, facts, Club, Owner, "calendar", Admin, old);
            var p2 = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, conv.Revision, Admin, old);
            Assert.True(p2.Ok, p2.Message);
            var oldSchema2 = new List<int>();
            for (var i = 0; i < 40; i++)   // gamla återställningspunkter i schema 2, nyare än den skyddade
                oldSchema2.Add(f.Db.ExecuteScalar<int>(
                    @"INSERT INTO dbo.SitePageVersion (PageId, OwnerType, OwnerId, Revision, Title, BlocksJson, SchemaVersion, ContentHash, SavedDate)
                      VALUES (@0, 'Club', @1, 1, N'v', N'{""schema"":2,""blocks"":[]}', 2, 0x00, @2); SELECT CAST(SCOPE_IDENTITY() AS int)", c.PageId, Owner, old));

            SiteRetention.Run(f.Db, Now);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE Id=@0", protectedId));          // skyddad trots ålder och >25 nyare
            Assert.True(oldSchema2.Count(id => Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE Id=@0", id) == 1) < 40);   // övriga gamla versioner gallras som vanligt
            // Efter Lägg tillbaka är sidan vanlig igen och skyddet släpper (den publicerade versionen är nu en ny, pekad).
            var r = SitePageWriter.RevertConversion(f.Db, facts, c.PageId, Club, Owner, p2.Revision, Admin, Now);
            Assert.True(r.Ok, r.Message);
            SiteRetention.Run(f.Db, Now);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE Id=@0", protectedId));
        }

        [SqlFact]
        public void En_stor_sida_nära_appens_gräns_går_att_spara_och_en_över_gränsen_ger_ett_vanligt_fel_inte_ett_SQL_undantag()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var page = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "Stor", null, SiteVisibility.Inloggade, Admin, Now);
            string Big(int blocks) => "{\"schema\":1,\"blocks\":[" + string.Join(",", Enumerable.Range(0, blocks).Select(i =>
                "{\"id\":\"big" + i + "x\",\"type\":\"text\",\"visibility\":1,\"data\":{\"html\":\"<p>" + new string('a', 90_000) + "</p>\"}}")) + "]}";
            // 4 × 90 000 tecken ≈ 360 000 byte: under appens 400 000 men 720 000 byte i kolumnen (2 byte per tecken) — över det gamla villkoret.
            var ok = SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, page.Revision, "Stor", Big(4), null, Admin, Now);
            Assert.True(ok.Ok, ok.Message);
            var tooBig = SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, ok.Revision, "Stor", Big(5), null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, tooBig.Status);
        }
    }
}
