using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2a: allt sidstrukturen lagrar per ägare tas bort när ägaren raderas — och inget annat. Mot SQL Server (HPSK_TEST_SQL).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteOwnerDataTests
    {
        private const int Gone = 990001;
        private const int Stays = 990002;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static readonly string Blocks = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":2,\"data\":{\"html\":\"<p>x</p>\"}}]}";

        private sealed class Facts : ISiteOwnerFacts
        {
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => true;
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => Array.Empty<string>();
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => Array.Empty<string>();
        }

        private static int Scalar(IDatabase db, string sql, params object[] a) => db.ExecuteScalar<int>(sql, a);

        /// <summary>Allt en ägare kan ha: brytare, publicerad sida med referens och adress, utkast, menyrad, stödläge, auditrad.</summary>
        private static void Populate(IDatabase db, int owner)
        {
            db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled) VALUES ('Club', @0, 1, 1)", owner);
            var f = new Facts(); var actor = new SiteActor(7);
            var page = SitePageWriter.CreatePage(db, f, "Club", owner, "Regler", null, SiteVisibility.Klubbmedlemmar, actor, Now);
            Assert.True(page.Ok, page.Message);
            var saved = SitePageWriter.SaveDraft(db, f, page.PageId, "Club", owner, 1, "Regler",
                "{\"schema\":1,\"blocks\":[{\"id\":\"abcd2\",\"type\":\"documents\",\"visibility\":2,\"data\":{\"refs\":[\"doc:5\"]}}]}", null, actor, Now);
            Assert.True(saved.Ok, saved.Message);
            Assert.True(SitePageWriter.Publish(db, f, page.PageId, "Club", owner, saved.Revision, actor, Now).Ok);
            SitePageStore.Create(db, "Club", owner, "Utkast", SiteVisibility.Dold, null, null, 7, false, Now);
            db.Execute("IF NOT EXISTS (SELECT 1 FROM dbo.SiteMenuItem WHERE OwnerType = 'Club' AND OwnerId = @0 AND PageId = @1) INSERT INTO dbo.SiteMenuItem (OwnerType, OwnerId, Kind, PageId, Label) VALUES ('Club', @0, 1, @1, N'Regler')", owner, page.PageId);   // en ny sida ligger redan i menyn
            db.Execute("INSERT INTO dbo.SiteSupportGrant (MemberId, OwnerType, OwnerId, Reason, ExpiresDate) VALUES (7, 'Club', @0, N'prov', DATEADD(HOUR, 1, SYSDATETIME()))", owner);
        }

        private static int Rows(IDatabase db, int owner) =>
            Scalar(db, "SELECT COUNT(*) FROM dbo.SiteOwner WHERE OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.SitePageRef r JOIN dbo.SitePage p ON p.Id = r.PageId WHERE p.OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.SiteMenuItem WHERE OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.SiteSupportGrant WHERE OwnerId=@0", owner)
          + Scalar(db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind='sitepage' AND OwnerId=@0", owner);

        [SqlFact]
        public void Radering_tar_bort_allt_ägaren_har_men_ingenting_hos_andra()
        {
            using var f = new SiteSqlFixture();
            Populate(f.Db, Gone); Populate(f.Db, Stays);
            var before = Rows(f.Db, Stays);
            Assert.True(Rows(f.Db, Gone) >= 9, "ägaren ska ha rader i alla tabeller före raderingen");

            Assert.Equal(1, SiteOwnerData.DeleteAll(f.Db, "Club", Gone));

            Assert.Equal(0, Rows(f.Db, Gone));
            Assert.Equal(before, Rows(f.Db, Stays));                                               // den andra ägaren är orörd
        }

        [SqlFact]
        public void Auditloggen_står_kvar_den_bär_bara_hash_och_gallras_av_sitt_eget_jobb()
        {
            using var f = new SiteSqlFixture();
            Populate(f.Db, Gone);
            Assert.True(Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE OwnerId=@0", Gone) > 0);
            SiteOwnerData.DeleteAll(f.Db, "Club", Gone);
            Assert.True(Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE OwnerId=@0", Gone) > 0);
        }

        [SqlFact]
        public void Villkoren_är_inte_accepterade_förrän_ägarens_admin_accepterat_den_aktuella_versionen()
        {
            using var f = new SiteSqlFixture();
            f.Db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled) VALUES ('Club', @0, 1, 1)", Gone);
            Assert.False(SiteOwnerData.TermsAccepted(f.Db, "Club", Gone));
            Assert.True(SiteOwnerData.AcceptTerms(f.Db, "Club", Gone, 7, Now));
            Assert.True(SiteOwnerData.TermsAccepted(f.Db, "Club", Gone));
            Assert.Equal(7, Scalar(f.Db, "SELECT TermsAcceptedByMemberId FROM dbo.SiteOwner WHERE OwnerId=@0", Gone));
            // En äldre version av villkoren räknas inte.
            f.Db.Execute("UPDATE dbo.SiteOwner SET TermsVersion = @1 WHERE OwnerId = @0", Gone, SiteOwnerData.CurrentTermsVersion - 1);
            Assert.False(SiteOwnerData.TermsAccepted(f.Db, "Club", Gone));
        }

        [SqlFact]
        public void Villkoren_hos_en_ägare_utan_rad_ger_false_och_accept_vägras()
        {
            using var f = new SiteSqlFixture();
            Assert.False(SiteOwnerData.TermsAccepted(f.Db, "Club", Gone));
            Assert.False(SiteOwnerData.AcceptTerms(f.Db, "Club", Gone, 7, Now));
        }

        [SqlFact]
        public void Accepten_gäller_bara_den_ägare_och_den_sort_den_gavs_för()
        {
            using var f = new SiteSqlFixture();
            f.Db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", Gone);
            f.Db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Region', @0, 1)", Gone);
            SiteOwnerData.AcceptTerms(f.Db, "Club", Gone, 7, Now);
            Assert.True(SiteOwnerData.TermsAccepted(f.Db, "Club", Gone));
            Assert.False(SiteOwnerData.TermsAccepted(f.Db, "Region", Gone));   // sorten följer id:t
        }

        [SqlFact]
        public void En_ägare_utan_rader_ger_noll_och_kastar_inte()
        {
            using var f = new SiteSqlFixture();
            Assert.Equal(0, SiteOwnerData.DeleteAll(f.Db, "Club", Gone));
        }

        [SqlFact]
        public void Sorten_följer_id_ett_kretsnummer_rör_inte_en_klubb_med_samma_nummer()
        {
            using var f = new SiteSqlFixture();
            Populate(f.Db, Gone);
            f.Db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Region', @0, 1)", Gone);
            Assert.Equal(1, SiteOwnerData.DeleteAll(f.Db, "Region", Gone));
            Assert.True(Rows(f.Db, Gone) >= 8, "klubben med samma nummer ska vara kvar");
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteOwner WHERE OwnerType='Club' AND OwnerId=@0", Gone));
        }

        [SqlFact]
        public void Ett_återanvänt_id_ärver_ingenting()
        {
            using var f = new SiteSqlFixture();
            Populate(f.Db, Gone);
            SiteOwnerData.DeleteAll(f.Db, "Club", Gone);
            // Samma nod-id igen (en ny klubb): ingen brytare, inga sidor, ingen adress som gammal 301 kan peka på.
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteOwner WHERE OwnerType='Club' AND OwnerId=@0", Gone));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE OwnerType='Club' AND OwnerId=@0 AND TargetKind='sitepage'", Gone));
        }
    }
}
