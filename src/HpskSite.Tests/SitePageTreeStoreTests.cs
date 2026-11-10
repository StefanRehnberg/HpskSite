using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Sidor under sidor mot en riktig SQL Server (<c>HPSK_TEST_SQL</c>, annars hoppas de över MED NAMN): skapa undersida, flytta, ordning, nivåregeln, raderings- och menyreglerna,
    /// papperskorgen och städjobbet. Varje prov i en transaktion som rullas tillbaka.
    /// </summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePageTreeStoreTests
    {
        private const string Club = "Club";
        private const int Owner = 990001;
        private const int OtherOwner = 990002;
        private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0);
        private static readonly SiteActor Admin = new(7);

        private sealed class Facts : ISiteOwnerFacts
        {
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => true;
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => new List<string>();
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => new List<string>();
        }
        private static readonly Facts F = new();

        private static void SeedOwners(IDatabase db)
        {
            foreach (var id in new[] { Owner, OtherOwner })
                db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static int New(IDatabase db, string title, SiteVisibility vis = SiteVisibility.Klubbmedlemmar, int? parent = null, int owner = Owner)
        {
            var r = SitePageWriter.CreatePage(db, F, Club, owner, title, null, vis, Admin, Now, parent);
            Assert.True(r.Ok, r.Status + " " + r.Message);
            return r.PageId;
        }

        private static SitePageRow Row(IDatabase db, int id, int owner = Owner) => SitePageStore.GetPage(db, id, Club, owner, includeDeleted: true)!;
        private static List<int> Order(IDatabase db, int? parent = null) => SitePageStore.Siblings(db, Club, Owner, parent).Select(s => s.Id).ToList();

        [SqlFact]
        public void En_ny_undersida_får_sin_förälder_och_läggs_sist_bland_syskonen()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Om oss");
            var a = New(f.Db, "Historia", parent: parent);
            var b = New(f.Db, "Stadgar", parent: parent);
            Assert.Equal(parent, Row(f.Db, a).ParentPageId);
            Assert.Equal(new[] { a, b }, Order(f.Db, parent));
            Assert.True(Row(f.Db, b).SortOrder > Row(f.Db, a).SortOrder);
            Assert.Equal(new[] { parent }, Order(f.Db));   // på egen nivå finns bara föräldern
        }

        [SqlFact]
        public void Adressen_är_platt_och_rörs_inte_av_trädet()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Om oss");
            var child = SitePageWriter.CreatePage(f.Db, F, Club, Owner, "Historia", null, SiteVisibility.Klubbmedlemmar, Admin, Now, parent);
            Assert.Equal("historia", child.Slug);                                   // inte om-oss/historia
            var moved = SitePageWriter.SetParent(f.Db, child.PageId, Club, Owner, null, Admin, Now);
            Assert.True(moved.Ok);
            Assert.Equal("historia", f.Db.ExecuteScalar<string>("SELECT Slug FROM dbo.PageAddress WHERE TargetKind='sitepage' AND TargetId=@0 AND IsCurrent=1", child.PageId));
        }

        [SqlFact]
        public void En_undersida_kan_inte_skapas_öppnare_än_föräldern_eller_under_en_undersida_eller_en_annan_förenings_sida()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Styrelsens sidor", SiteVisibility.Styrelse);
            var child = New(f.Db, "Protokoll", SiteVisibility.Styrelse, parent);
            var tooOpen = SitePageWriter.CreatePage(f.Db, F, Club, Owner, "För öppen", null, SiteVisibility.Alla, Admin, Now, parent);
            Assert.Equal(SiteWriteStatus.Invalid, tooOpen.Status);
            var third = SitePageWriter.CreatePage(f.Db, F, Club, Owner, "Tredje nivån", null, SiteVisibility.Dold, Admin, Now, child);
            Assert.Equal(SiteWriteStatus.Invalid, third.Status);
            var other = New(f.Db, "Annans", SiteVisibility.Dold, owner: OtherOwner);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreatePage(f.Db, F, Club, Owner, "Intrång", null, SiteVisibility.Dold, Admin, Now, other).Status);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreatePage(f.Db, F, Club, Owner, "Hittepå", null, SiteVisibility.Dold, Admin, Now, 987654321).Status);
        }

        [SqlFact]
        public void Att_flytta_höjer_nivån_till_förälderns_och_tar_sidan_ur_menyn_om_föräldern_inte_ligger_där()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Styrelsen", SiteVisibility.Styrelse);
            var page = New(f.Db, "Öppen sida", SiteVisibility.Alla);
            Assert.True(SitePageWriter.SetInMenu(f.Db, page, Club, Owner, true, Admin, Now).Ok);
            var r = SitePageWriter.SetParent(f.Db, page, Club, Owner, parent, Admin, Now);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(2, r.Affected);                                              // nivån höjdes och sidan togs ur menyn
            Assert.Equal((byte)SiteVisibility.Styrelse, Row(f.Db, page).Visibility);
            Assert.False(SitePageStore.IsInMenu(f.Db, Club, Owner, page));
            Assert.Equal(parent, Row(f.Db, page).ParentPageId);
        }

        [SqlFact]
        public void Flytta_prövar_reglerna_en_sida_med_undersidor_kan_inte_bli_en_undersida_och_ingen_sida_blir_sin_egen_förälder()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var a = New(f.Db, "A"); var b = New(f.Db, "B"); var child = New(f.Db, "A1", parent: a);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.SetParent(f.Db, a, Club, Owner, b, Admin, Now).Status);       // A har undersidor
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.SetParent(f.Db, b, Club, Owner, child, Admin, Now).Status);   // föräldern är en undersida
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.SetParent(f.Db, b, Club, Owner, b, Admin, Now).Status);       // sig själv
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SetParent(f.Db, b, Club, OtherOwner, a, Admin, Now).Status); // en annan förenings sida
            Assert.True(SitePageWriter.SetParent(f.Db, child, Club, Owner, b, Admin, Now).Ok);                                 // en undersida byter förälder
            Assert.Equal(b, Row(f.Db, child).ParentPageId);
            Assert.True(SitePageWriter.SetParent(f.Db, a, Club, Owner, b, Admin, Now).Ok);                                      // nu har A inga undersidor och kan flyttas
        }

        [SqlFact]
        public void En_omgjord_Om_sida_kan_ha_undersidor_och_kan_inte_läggas_tillbaka_förrän_de_är_borta()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            f.Db.Execute("UPDATE dbo.SiteOwner SET EditingEnabled = 1, ConvertEnabled = 1 WHERE OwnerType = 'Club' AND OwnerId = @0", Owner);
            var notYet =SitePageStore.GetTemplateCopy(f.Db, Club, Owner, "about");
            Assert.Null(notYet);                                                                                         // ingen rad = inget att peka på
            var conv = SitePageWriter.ConvertTemplatePage(f.Db, F, Club, Owner, "about", Admin, Now);
            Assert.True(conv.Ok, conv.Status + " " + conv.Message);
            var about = SitePageStore.GetTemplateCopy(f.Db, Club, Owner, "about")!;
            var child = SitePageWriter.CreatePage(f.Db, F, Club, Owner, "Historia", null, SiteVisibility.Klubbmedlemmar, Admin, Now, about.Id);
            Assert.True(child.Ok, child.Status + " " + child.Message);
            Assert.Equal(about.Id, Row(f.Db, child.PageId).ParentPageId);
            // Hem kan aldrig vara förälder, en vanlig sida kan flyttas in under Om
            var other = New(f.Db, "Bli medlem");
            Assert.True(SitePageWriter.SetParent(f.Db, other, Club, Owner, about.Id, Admin, Now).Ok);
            // spärren: varken Avbryt eller Lägg tillbaka medan undersidor finns
            var cancel = SitePageWriter.CancelConversion(f.Db, F, about.Id, Club, Owner, Row(f.Db, about.Id).Revision, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, cancel.Status);
            Assert.Contains("undersidor", cancel.Message);
            Assert.True(SitePageWriter.SetParent(f.Db, child.PageId, Club, Owner, null, Admin, Now).Ok);
            Assert.True(SitePageWriter.SetParent(f.Db, other, Club, Owner, null, Admin, Now).Ok);
            var cancelled = SitePageWriter.CancelConversion(f.Db, F, about.Id, Club, Owner, Row(f.Db, about.Id).Revision, Admin, Now);
            Assert.True(cancelled.Ok, cancelled.Status + " " + cancelled.Message);                                        // utan undersidor går det
        }

        [SqlFact]
        public void Taket_på_undersidor_per_sida_gäller()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder");
            for (var i = 0; i < SitePageTree.MaxChildrenPerParent; i++) New(f.Db, "Under " + i, parent: parent);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreatePage(f.Db, F, Club, Owner, "En för mycket", null, SiteVisibility.Klubbmedlemmar, Admin, Now, parent).Status);
        }

        [SqlFact]
        public void Ordningen_ändras_bara_bland_syskon_och_stannar_vid_ändarna()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var a = New(f.Db, "A"); var b = New(f.Db, "B"); var c = New(f.Db, "C");
            var x = New(f.Db, "X", parent: a); var y = New(f.Db, "Y", parent: a);
            Assert.Equal(new[] { a, b, c }, Order(f.Db));
            Assert.True(SitePageWriter.MoveInOrder(f.Db, c, Club, Owner, -1, Admin, Now).Ok);
            Assert.Equal(new[] { a, c, b }, Order(f.Db));
            Assert.True(SitePageWriter.MoveInOrder(f.Db, a, Club, Owner, -1, Admin, Now).Ok);        // först redan: ingen ändring
            Assert.Equal(new[] { a, c, b }, Order(f.Db));
            Assert.True(SitePageWriter.MoveInOrder(f.Db, b, Club, Owner, 1, Admin, Now).Ok);         // sist redan: ingen ändring
            Assert.Equal(new[] { a, c, b }, Order(f.Db));
            Assert.True(SitePageWriter.MoveInOrder(f.Db, y, Club, Owner, -1, Admin, Now).Ok);        // undersidor byter plats bland sina syskon
            Assert.Equal(new[] { y, x }, Order(f.Db, a));
            Assert.Equal(new[] { a, c, b }, Order(f.Db));                                              // utan att röra första nivån
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.MoveInOrder(f.Db, b, Club, OtherOwner, 1, Admin, Now).Status);
        }

        [SqlFact]
        public void Gör_man_en_förälder_strängare_får_undersidorna_samma_nivå_men_aldrig_tvärtom()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder", SiteVisibility.Alla);
            var open = New(f.Db, "Öppen under", SiteVisibility.Alla, parent);
            var strict = New(f.Db, "Strängare under", SiteVisibility.Dold, parent);
            var rev = Row(f.Db, parent).Revision;
            var r = SitePageWriter.SaveDraft(f.Db, F, parent, Club, Owner, rev, "Förälder", "{\"schema\":1,\"blocks\":[]}", SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(1, r.Affected);                                                              // bara den öppna höjdes
            Assert.Equal((byte)SiteVisibility.Klubbmedlemmar, Row(f.Db, open).Visibility);
            Assert.Equal((byte)SiteVisibility.Dold, Row(f.Db, strict).Visibility);                    // en strängare undersida rörs inte
            // öppnar man föräldern igen följer undersidorna inte med (en undersida får vara strängare)
            var again = SitePageWriter.SaveDraft(f.Db, F, parent, Club, Owner, r.Revision, "Förälder", "{\"schema\":1,\"blocks\":[]}", SiteVisibility.Alla, Admin, Now);
            Assert.True(again.Ok); Assert.Equal(0, again.Affected);
            Assert.Equal((byte)SiteVisibility.Klubbmedlemmar, Row(f.Db, open).Visibility);
        }

        [SqlFact]
        public void En_undersida_kan_inte_sparas_öppnare_än_sin_förälder()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder", SiteVisibility.Klubbmedlemmar);
            var child = New(f.Db, "Under", SiteVisibility.Klubbmedlemmar, parent);
            var r = SitePageWriter.SaveDraft(f.Db, F, child, Club, Owner, Row(f.Db, child).Revision, "Under", "{\"schema\":1,\"blocks\":[]}", SiteVisibility.Alla, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, r.Status);
            Assert.Equal((byte)SiteVisibility.Klubbmedlemmar, Row(f.Db, child).Visibility);
        }

        [SqlFact]
        public void En_undersida_ligger_bara_i_menyn_om_föräldern_gör_det_och_följer_med_ut()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var a = New(f.Db, "A", parent: parent); var b = New(f.Db, "B", parent: parent);
            var refused = SitePageWriter.SetInMenu(f.Db, a, Club, Owner, true, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, refused.Status);
            Assert.False(SitePageStore.IsInMenu(f.Db, Club, Owner, a));
            Assert.True(SitePageWriter.SetInMenu(f.Db, parent, Club, Owner, true, Admin, Now).Ok);
            Assert.True(SitePageWriter.SetInMenu(f.Db, a, Club, Owner, true, Admin, Now).Ok);
            Assert.True(SitePageWriter.SetInMenu(f.Db, b, Club, Owner, true, Admin, Now).Ok);
            var off = SitePageWriter.SetInMenu(f.Db, parent, Club, Owner, false, Admin, Now);
            Assert.True(off.Ok); Assert.Equal(2, off.Affected);
            Assert.False(SitePageStore.IsInMenu(f.Db, Club, Owner, a)); Assert.False(SitePageStore.IsInMenu(f.Db, Club, Owner, b));
        }

        [SqlFact]
        public void En_förälder_med_undersidor_kan_inte_raderas_men_en_undersida_kan_det()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var child = New(f.Db, "Under", parent: parent);
            var blocked = SitePageWriter.SoftDelete(f.Db, parent, Club, Owner, Row(f.Db, parent).Revision, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, blocked.Status);
            Assert.Null(Row(f.Db, parent).DeletedDate);
            Assert.True(SitePageWriter.SoftDelete(f.Db, child, Club, Owner, Row(f.Db, child).Revision, Admin, Now).Ok);
            Assert.True(SitePageWriter.SoftDelete(f.Db, parent, Club, Owner, Row(f.Db, parent).Revision, Admin, Now).Ok);   // nu utan undersidor
        }

        [SqlFact]
        public void En_återställd_undersida_vars_förälder_är_raderad_blir_en_sida_på_egen_nivå()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var child = New(f.Db, "Under", parent: parent);
            SitePageWriter.SoftDelete(f.Db, child, Club, Owner, Row(f.Db, child).Revision, Admin, Now);
            SitePageWriter.SoftDelete(f.Db, parent, Club, Owner, Row(f.Db, parent).Revision, Admin, Now);
            var restored = SitePageStore.Restore(f.Db, child, Club, Owner, Row(f.Db, child).Revision, 7, Now);
            Assert.Equal(SiteStoreOutcome.Ok, restored.Outcome);
            Assert.Null(Row(f.Db, child).ParentPageId);                                                // inte under en raderad sida
        }

        [SqlFact]
        public void En_återställd_undersida_vars_förälder_lever_ligger_kvar_under_den()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var child = New(f.Db, "Under", parent: parent);
            SitePageWriter.SoftDelete(f.Db, child, Club, Owner, Row(f.Db, child).Revision, Admin, Now);
            Assert.Equal(SiteStoreOutcome.Ok, SitePageStore.Restore(f.Db, child, Club, Owner, Row(f.Db, child).Revision, 7, Now).Outcome);
            Assert.Equal(parent, Row(f.Db, child).ParentPageId);
        }

        [SqlFact]
        public void Städjobbet_kan_ta_bort_en_raderad_förälder_utan_att_stranda_på_nyckeln()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var child = New(f.Db, "Under", parent: parent);
            SitePageWriter.SoftDelete(f.Db, child, Club, Owner, Row(f.Db, child).Revision, Admin, Now);
            SitePageWriter.SoftDelete(f.Db, parent, Club, Owner, Row(f.Db, parent).Revision, Admin, Now);
            // gamla raderingsdatum, så att båda är mogna för städning
            f.Db.Execute("UPDATE dbo.SitePage SET DeletedDate = @0 WHERE Id IN (@1, @2)", Now.AddDays(-400), parent, child);
            var purged = SiteRetention.Run(f.Db, Now);
            Assert.True(purged.PagesPurged >= 2, "Raderade sidor borttagna: " + purged.PagesPurged);
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePage WHERE Id IN (@0, @1)", parent, child));
        }

        [SqlFact]
        public void Listan_bär_förälder_ordning_och_antal_undersidor_och_menyn_ordnar_efter_sidans_ordning()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var parent = New(f.Db, "Förälder"); var child = New(f.Db, "Under", parent: parent);
            var list = SitePageStore.ListFree(f.Db, Club, Owner);
            Assert.Equal(1, list.Single(p => p.Id == parent).ChildCount);
            Assert.Equal(parent, list.Single(p => p.Id == child).ParentPageId);
            Assert.All(list, p => Assert.True(p.SortOrder > 0));
        }
    }
}
