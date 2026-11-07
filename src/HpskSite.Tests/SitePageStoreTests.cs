using HpskSite.Services.SitePages;
using Microsoft.Data.SqlClient;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2a: SQL-lagret för egna sidor mot en riktig SQL Server (<c>HPSK_TEST_SQL</c>, annars hoppas de över MED NAMN).
    /// Varje prov körs i en transaktion som rullas tillbaka; samtidighetsprovet använder två riktiga anslutningar och städar efter sig.
    /// </summary>
    public class SitePageStoreTests
    {
        private const string Club = "Club";
        private const int Owner = 990001;
        private const int OtherOwner = 990002;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static readonly string Blocks1 = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"<p>Ett</p>\"}}]}";
        private static readonly string Blocks2 = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"<p>Två</p>\"}}]}";

        private static void SeedOwners(IDatabase db)
        {
            foreach (var id in new[] { Owner, OtherOwner })
                db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static (SiteStoreResult Created, SitePageRow Page) NewPage(IDatabase db, string title = "Regler", SiteVisibility vis = SiteVisibility.Klubbmedlemmar)
        {
            var r = SitePageStore.Create(db, Club, Owner, title, vis, null, null, byMemberId: 7, asSupport: false, Now);
            Assert.Equal(SiteStoreOutcome.Ok, r.Outcome);
            return (r, SitePageStore.GetPage(db, r.PageId, Club, Owner)!);
        }

        [SqlFact]
        public void Skapa_ger_en_sida_med_tom_baslinje_som_utkast_och_ingen_publicering()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (r, page) = NewPage(f.Db);
            Assert.Equal(1, r.Revision);
            Assert.Equal((byte)SiteVisibility.Klubbmedlemmar, page.Visibility);
            Assert.Null(page.PublishedVersionId);
            Assert.NotNull(page.DraftVersionId);
            Assert.True(page.HasUnpublishedChanges);
            var draft = SitePageStore.GetDraft(f.Db, r.PageId, Club, Owner)!;
            Assert.True(draft.IsBaseline);
            Assert.Equal(SitePageStore.EmptyContent(false), draft.BlocksJson);
            Assert.Null(SitePageStore.GetPublished(f.Db, r.PageId, Club, Owner));   // aldrig publicerad = inget publikt
        }

        [SqlFact]
        public void Ägare_utan_rad_i_SiteOwner_ger_NotFound()
        {
            using var f = new SiteSqlFixture();
            var r = SitePageStore.Create(f.Db, Club, Owner, "X", SiteVisibility.Dold, null, null, 1, false, Now);
            Assert.Equal(SiteStoreOutcome.NotFound, r.Outcome);
        }

        [SqlFact]
        public void Ogiltig_synlighet_vägras_och_Utloggad_kan_aldrig_väljas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            Assert.Throws<ArgumentOutOfRangeException>(() => SitePageStore.Create(f.Db, Club, Owner, "X", SiteVisibility.Utloggad, null, null, 1, false, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => SitePageStore.Create(f.Db, Club, Owner, "X", (SiteVisibility)9, null, null, 1, false, Now));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId = @0", Owner));
        }

        [SqlFact]
        public void Taket_på_fria_sidor_gäller_inte_mallkopior_eller_andra_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            for (var i = 0; i < SitePageStore.MaxFreePagesPerOwner; i++) NewPage(f.Db, "Sida " + i);
            Assert.Equal(SiteStoreOutcome.TooManyPages, SitePageStore.Create(f.Db, Club, Owner, "En till", SiteVisibility.Dold, null, null, 1, false, Now).Outcome);
            Assert.Equal(SiteStoreOutcome.Ok, SitePageStore.Create(f.Db, Club, Owner, "Kalender", SiteVisibility.Inloggade, "calendar", 1, 1, false, Now).Outcome);
            Assert.Equal(SiteStoreOutcome.Ok, SitePageStore.Create(f.Db, Club, OtherOwner, "Första", SiteVisibility.Dold, null, null, 1, false, Now).Outcome);
        }

        [SqlFact]
        public void En_raderad_sida_räknas_inte_mot_taket()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var first = NewPage(f.Db, "Sida 0");
            for (var i = 1; i < SitePageStore.MaxFreePagesPerOwner; i++) NewPage(f.Db, "Sida " + i);
            Assert.Equal(SiteStoreOutcome.Ok, SitePageStore.SoftDelete(f.Db, first.Page.Id, Club, Owner, 1, 7, Now).Outcome);
            Assert.Equal(SiteStoreOutcome.Ok, SitePageStore.Create(f.Db, Club, Owner, "Ny", SiteVisibility.Dold, null, null, 1, false, Now).Outcome);
        }

        [SqlFact]
        public void Autospar_skriver_utkastet_på_plats_och_höjer_revisionen_men_rör_inte_baslinjen()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            var s1 = SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Regler", Blocks1, null, 7, false, Now);
            Assert.Equal((SiteStoreOutcome.Ok, 2), (s1.Outcome, s1.Revision));
            var s2 = SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 2, "Regler", Blocks2, null, 7, false, Now.AddSeconds(30));
            Assert.Equal(3, s2.Revision);
            Assert.Equal(2, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId = @0", c.PageId)); // baslinje + EN utkastrad
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId = @0 AND IsBaseline = 1 AND BlocksJson = @1", c.PageId, SitePageStore.EmptyContent(false)));
            Assert.Equal(Blocks2, SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!.BlocksJson);
        }

        [SqlFact]
        public void Oförändrat_innehåll_skriver_inget_och_höjer_inte_revisionen()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Regler", Blocks1, null, 7, false, Now);
            var again = SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 2, "Regler", Blocks1, null, 7, false, Now.AddSeconds(30));
            Assert.Equal((SiteStoreOutcome.Ok, 2), (again.Outcome, again.Revision));
            Assert.Equal(2, SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Revision);
        }

        [SqlFact]
        public void Gammal_revision_ger_Conflict_och_ingenting_skrivs()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Regler", Blocks1, null, 7, false, Now);
            var stale = SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Annan titel", Blocks2, null, 8, false, Now);
            Assert.Equal(SiteStoreOutcome.Conflict, stale.Outcome);
            Assert.Equal(2, stale.Revision);                                     // klienten får veta den nuvarande
            var page = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal("Regler", page.Title);
            Assert.Equal(Blocks1, SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!.BlocksJson);
        }

        [SqlFact]
        public void Ett_sid_id_prövas_mot_radens_egen_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, Club, OtherOwner));
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, "Region", Owner));
            Assert.Equal(SiteStoreOutcome.NotFound, SitePageStore.SaveDraft(f.Db, c.PageId, Club, OtherOwner, 1, "Kapad", Blocks1, null, 9, false, Now).Outcome);
            Assert.Equal(SiteStoreOutcome.NotFound, SitePageStore.Publish(f.Db, c.PageId, Club, OtherOwner, 1, 9, false, Now).Outcome);
            Assert.Equal(SiteStoreOutcome.NotFound, SitePageStore.SoftDelete(f.Db, c.PageId, Club, OtherOwner, 1, 9, Now).Outcome);
            Assert.Equal("Regler", SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Title);
        }

        [SqlFact]
        public void Publicera_gör_utkastet_publikt_med_ny_versionsrad_och_utan_osparade_ändringar()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Banregler", Blocks1, null, 7, false, Now);
            var p = SitePageStore.Publish(f.Db, c.PageId, Club, Owner, 2, 7, false, Now);
            Assert.Equal((SiteStoreOutcome.Ok, 3), (p.Outcome, p.Revision));
            var page = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.Equal("Banregler", page.PublishedTitle);
            Assert.Null(page.DraftVersionId);
            Assert.False(page.HasUnpublishedChanges);
            var pub = SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!;
            Assert.True(pub.IsPublish);
            Assert.Equal(Blocks1, pub.BlocksJson);
            Assert.Equal(SiteStoreOutcome.NothingToPublish, SitePageStore.Publish(f.Db, c.PageId, Club, Owner, 3, 7, false, Now).Outcome);
        }

        [SqlFact]
        public void Publicera_med_gammal_revision_ger_Conflict()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "R", Blocks1, null, 7, false, Now);
            Assert.Equal(SiteStoreOutcome.Conflict, SitePageStore.Publish(f.Db, c.PageId, Club, Owner, 1, 7, false, Now).Outcome);
            Assert.Null(SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner));
        }

        [SqlFact]
        public void Ett_utkast_efter_publicering_påverkar_inte_det_publicerade_förrän_det_publiceras()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "Gammal titel", Blocks1, null, 7, false, Now);
            SitePageStore.Publish(f.Db, c.PageId, Club, Owner, 2, 7, false, Now);                      // rev 3
            var s = SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 3, "Ny titel", Blocks2, null, 7, false, Now);   // rev 4
            Assert.Equal(4, s.Revision);
            var page = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.True(page.HasUnpublishedChanges);
            Assert.Equal("Gammal titel", page.PublishedTitle);
            Assert.Equal(Blocks1, SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.BlocksJson);
            Assert.Equal(Blocks2, SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!.BlocksJson);

            var d = SitePageStore.DiscardDraft(f.Db, c.PageId, Club, Owner, 4, 7, Now);                // kasta ändringarna
            Assert.Equal((SiteStoreOutcome.Ok, 5), (d.Outcome, d.Revision));
            Assert.False(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.HasUnpublishedChanges);
            Assert.Equal(Blocks1, SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.BlocksJson);
        }

        [SqlFact]
        public void Att_kasta_ändringar_på_en_aldrig_publicerad_sida_gör_inget()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db);
            Assert.Equal(SiteStoreOutcome.NothingToPublish, SitePageStore.DiscardDraft(f.Db, c.PageId, Club, Owner, 1, 7, Now).Outcome);
            Assert.NotNull(SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner));
        }

        [SqlFact]
        public void Mjukradering_sparar_senaste_synligheten_och_döljer_sidan_och_kan_ångras()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (c, _) = NewPage(f.Db, vis: SiteVisibility.Inloggade);
            SitePageStore.SaveDraft(f.Db, c.PageId, Club, Owner, 1, "R", Blocks1, null, 7, false, Now);
            SitePageStore.Publish(f.Db, c.PageId, Club, Owner, 2, 7, false, Now);                      // rev 3
            var d = SitePageStore.SoftDelete(f.Db, c.PageId, Club, Owner, 3, 7, Now);
            Assert.Equal(SiteStoreOutcome.Ok, d.Outcome);
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner));
            Assert.Null(SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner));                      // raderad = inget publikt
            Assert.Empty(SitePageStore.List(f.Db, Club, Owner));
            var trash = SitePageStore.List(f.Db, Club, Owner, deleted: true).Single();
            Assert.Equal((byte)SiteVisibility.Inloggade, trash.LastVisibleLevel);

            var r = SitePageStore.Restore(f.Db, c.PageId, Club, Owner, 4, 7, Now);
            Assert.Equal(SiteStoreOutcome.Ok, r.Outcome);
            var back = SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!;
            Assert.Null(back.LastVisibleLevel); Assert.Null(back.DeletedDate);
            Assert.Equal(SiteStoreOutcome.NotFound, SitePageStore.Restore(f.Db, c.PageId, Club, Owner, 5, 7, Now).Outcome); // inte raderad
        }

        [SqlFact]
        public void Listan_läser_aldrig_innehåll_och_är_per_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            NewPage(f.Db, "B"); NewPage(f.Db, "A");
            f.Db.Execute("INSERT INTO dbo.SitePage (OwnerType, OwnerId, Title) VALUES ('Club', @0, 'Annans')", OtherOwner);
            Assert.Equal(new[] { "A", "B" }, SitePageStore.List(f.Db, Club, Owner).Select(p => p.Title));
        }

        [SqlFact]
        public void Pekarna_kan_inte_peka_på_en_annan_sidas_version()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var (a, _) = NewPage(f.Db, "A"); var (b, _) = NewPage(f.Db, "B");
            var otherVersion = SitePageStore.GetPage(f.Db, b.PageId, Club, Owner)!.DraftVersionId;
            Assert.ThrowsAny<Exception>(() => f.Db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1 WHERE Id = @0", a.PageId, otherVersion));
        }

        [SqlFact]
        public void Två_samtidiga_sparningar_med_samma_revision_ger_exakt_en_vinnare()
        {
            var cs = Environment.GetEnvironmentVariable("HPSK_TEST_SQL")!;
            const int owner = 990003;
            using var setup = new Database(cs, DatabaseType.SqlServer2012, SqlClientFactory.Instance);
            int pageId;
            try
            {
                setup.Execute("DELETE FROM dbo.SiteOwner WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
                setup.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", owner);
                pageId = SitePageStore.Create(setup, Club, owner, "Race", SiteVisibility.Dold, null, null, 1, false, Now).PageId;
                SitePageStore.SaveDraft(setup, pageId, Club, owner, 1, "Race", Blocks1, null, 1, false, Now);   // rev 2, utkast finns

                // Två transaktioner läser båda revision 2 innan någon skriver; den andra UPDATE väntar på den första och ska då se 0 rader.
                using var db1 = new Database(cs, DatabaseType.SqlServer2012, SqlClientFactory.Instance);
                using var db2 = new Database(cs, DatabaseType.SqlServer2012, SqlClientFactory.Instance);
                db1.BeginTransaction(); db2.BeginTransaction();
                var p1 = SitePageStore.GetPage(db1, pageId, Club, owner)!;
                var p2 = SitePageStore.GetPage(db2, pageId, Club, owner)!;
                Assert.Equal(2, p1.Revision); Assert.Equal(2, p2.Revision);

                // Båda går genom SitePageStore.SaveDraft. db2 passerar förkontrollen (revision 2), och i kroken före dess UPDATE hinner db1
                // spara och checka in. Förkontrollen kan alltså inte rädda db2: bara UPDATE-villkoret "Revision = @expected" gör det.
                SiteStoreResult r1 = default, r2 = default;
                var hookRan = false;
                var t = new Thread(() =>
                {
                    SitePageStore.TestHookBeforeCas = () =>
                    {
                        SitePageStore.TestHookBeforeCas = null;   // db1:s egen SaveDraft på samma tråd ska inte köra kroken igen
                        hookRan = true;
                        r1 = SitePageStore.SaveDraft(db1, pageId, Club, owner, 2, "Vinnare", Blocks2, null, 1, false, Now);
                        db1.CompleteTransaction();
                    };
                    try { r2 = SitePageStore.SaveDraft(db2, pageId, Club, owner, 2, "Förlorare", Blocks1, null, 2, false, Now); }
                    finally { SitePageStore.TestHookBeforeCas = null; }
                });
                t.Start(); Assert.True(t.Join(TimeSpan.FromSeconds(30)), "samtidighetsprovet hängde sig");
                db2.CompleteTransaction();

                Assert.True(hookRan);
                Assert.Equal(SiteStoreOutcome.Ok, r1.Outcome);
                Assert.Equal(SiteStoreOutcome.Conflict, r2.Outcome);
                Assert.Equal("Vinnare", setup.ExecuteScalar<string>("SELECT Title FROM dbo.SitePage WHERE Id = @0", pageId));
                Assert.Equal(3, setup.ExecuteScalar<int>("SELECT Revision FROM dbo.SitePage WHERE Id = @0", pageId));
                Assert.Equal(Blocks2, SitePageStore.GetDraft(setup, pageId, Club, owner)!.BlocksJson);   // förlorarens text skrevs aldrig
            }
            finally
            {
                setup.Execute("UPDATE dbo.SitePage SET PublishedVersionId = NULL, DraftVersionId = NULL WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
                setup.Execute("DELETE FROM dbo.SitePage WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
                setup.Execute("DELETE FROM dbo.SiteOwner WHERE OwnerType = 'Club' AND OwnerId = @0", owner);
            }
        }
    }
}
