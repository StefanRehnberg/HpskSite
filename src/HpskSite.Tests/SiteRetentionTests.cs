using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2b: gallringen (plan v3 A6/A8) mot en riktig SQL Server. Hela gallringen är global, men provet körs i en transaktion som rullas tillbaka,
    /// så inget riktigt data påverkas. Varje regel har sitt prov med rader på båda sidor om gränsen.
    /// </summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteRetentionTests
    {
        private const string Club = "Club";
        private const int Owner = 990001;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static int Scalar(IDatabase db, string sql, params object[] a) => db.ExecuteScalar<int>(sql, a);

        private static int NewPage(IDatabase db, string title = "Sida")
        {
            db.Execute("IF NOT EXISTS (SELECT 1 FROM dbo.SiteOwner WHERE OwnerType='Club' AND OwnerId=@0) INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", Owner);
            return db.ExecuteScalar<int>("INSERT INTO dbo.SitePage (OwnerType, OwnerId, Title, Visibility) VALUES ('Club', @0, @1, 2); SELECT CAST(SCOPE_IDENTITY() AS int)", Owner, title);
        }

        private static int AddVersion(IDatabase db, int pageId, DateTime saved, bool publish = false, bool baseline = false) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SitePageVersion (PageId, OwnerType, OwnerId, Revision, Title, BlocksJson, ContentHash, IsPublish, IsBaseline, SavedDate)
                  VALUES (@0, 'Club', @1, 1, N'v', N'{""schema"":1,""blocks"":[]}', 0x00, @2, @3, @4); SELECT CAST(SCOPE_IDENTITY() AS int)",
                pageId, Owner, publish, baseline, saved);

        private static bool Exists(IDatabase db, int versionId) => Scalar(db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE Id = @0", versionId) == 1;

        // ── Raderade sidor ────────────────────────────────────────────────────────

        [SqlFact]
        public void En_sida_raderad_för_mer_än_30_dagar_sedan_tas_bort_helt_men_inte_en_yngre()
        {
            using var f = new SiteSqlFixture();
            var old = NewPage(f.Db, "Gammal"); var recent = NewPage(f.Db, "Ny");
            foreach (var (id, days) in new[] { (old, 31), (recent, 29) })
            {
                f.Db.Execute("UPDATE dbo.SitePage SET DeletedDate = @1 WHERE Id = @0", id, Now.AddDays(-days));
                f.Db.Execute("IF NOT EXISTS (SELECT 1 FROM dbo.SiteMenuItem WHERE OwnerType = 'Club' AND OwnerId = @0 AND PageId = @1) INSERT INTO dbo.SiteMenuItem (OwnerType, OwnerId, Kind, PageId, Label) VALUES ('Club', @0, 1, @1, N'x')", Owner, id);
                f.Db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES ('Club', @0, NULL, @1, 'sitepage', @2, 1)", Owner, "s" + id, id);
                var v = AddVersion(f.Db, id, Now.AddDays(-40));
                f.Db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1 WHERE Id = @0", id, v);
                f.Db.Execute("INSERT INTO dbo.SitePageRef (PageId, RefKind, RefId) VALUES (@0, 'doc', 5)", id);
            }
            var r = SiteRetention.Run(f.Db, Now);
            Assert.Equal(1, r.PagesPurged);
            foreach (var t in new[] { "SitePage WHERE Id", "SiteMenuItem WHERE PageId", "SitePageVersion WHERE PageId", "SitePageRef WHERE PageId" })
                Assert.Equal(0, Scalar(f.Db, $"SELECT COUNT(*) FROM dbo.{t} = @0", old));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind = 'sitepage' AND TargetId = @0", old));   // adressen följde sidan, ingen 301 till en borttagen sida
            foreach (var t in new[] { "SitePage WHERE Id", "SiteMenuItem WHERE PageId", "SitePageVersion WHERE PageId", "SitePageRef WHERE PageId" })
                Assert.Equal(1, Scalar(f.Db, $"SELECT COUNT(*) FROM dbo.{t} = @0", recent));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind = 'sitepage' AND TargetId = @0", recent));
        }

        [SqlFact]
        public void En_levande_sida_gallras_aldrig_som_sida()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            f.Db.Execute("UPDATE dbo.SitePage SET CreatedDate = @1, UpdatedDate = @1 WHERE Id = @0", p, Now.AddYears(-5));
            SiteRetention.Run(f.Db, Now);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE Id = @0", p));
        }

        // ── Versioner ─────────────────────────────────────────────────────────────

        [SqlFact]
        public void Pekade_versioner_och_baslinjen_raderas_aldrig_av_åldersregeln()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            var baseline = AddVersion(f.Db, p, Now.AddYears(-3), baseline: true);
            var published = AddVersion(f.Db, p, Now.AddYears(-2), publish: true);
            var draft = AddVersion(f.Db, p, Now.AddDays(-1));                        // färskt utkast (ett gammalt utkast på en publicerad sida kastas av sin egen regel)
            f.Db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1, DraftVersionId = @2 WHERE Id = @0", p, published, draft);
            var stray = AddVersion(f.Db, p, Now.AddYears(-2));                       // en vanlig gammal version: ska bort
            SiteRetention.Run(f.Db, Now);
            Assert.True(Exists(f.Db, baseline)); Assert.True(Exists(f.Db, published)); Assert.True(Exists(f.Db, draft));
            Assert.False(Exists(f.Db, stray));
        }

        [SqlFact]
        public void De_senaste_25_behålls_äldre_än_90_dagar_utanför_dem_raderas_men_nyare_behålls_över_25()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            var oldOnes = Enumerable.Range(0, 5).Select(_ => AddVersion(f.Db, p, Now.AddDays(-100))).ToList();    // lägst id = äldst rang
            var recent = Enumerable.Range(0, 30).Select(_ => AddVersion(f.Db, p, Now.AddDays(-10))).ToList();    // 30 nyare: fler än 25 men yngre än 90 dagar
            SiteRetention.Run(f.Db, Now);
            Assert.All(oldOnes, id => Assert.False(Exists(f.Db, id)));            // rang 31–35 och äldre än 90 dagar
            Assert.All(recent, id => Assert.True(Exists(f.Db, id)));              // över 25 men inte äldre än 90 dagar: kvar
        }

        [SqlFact]
        public void En_gammal_version_inom_de_25_senaste_behålls_tills_den_är_12_månader()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            var d100 = AddVersion(f.Db, p, Now.AddDays(-100));                    // rang 3 av 3: inom 25, äldre än 90 dagar: KVAR
            var d200 = AddVersion(f.Db, p, Now.AddDays(-200));
            var m13 = AddVersion(f.Db, p, Now.AddMonths(-13));                    // äldre än 12 månader: bort oavsett rang
            SiteRetention.Run(f.Db, Now);
            Assert.True(Exists(f.Db, d100)); Assert.True(Exists(f.Db, d200));
            Assert.False(Exists(f.Db, m13));
        }

        [SqlFact]
        public void Taket_100_per_sida_tar_den_äldsta_först_och_aldrig_en_pekad()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            var published = AddVersion(f.Db, p, Now.AddDays(-1), publish: true);
            f.Db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1 WHERE Id = @0", p, published);
            var ids = Enumerable.Range(0, 105).Select(_ => AddVersion(f.Db, p, Now.AddDays(-1))).ToList();      // alla yngre än 90 dagar
            SiteRetention.Run(f.Db, Now);
            Assert.Equal(100, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId = @0 AND Id <> @1", p, published));
            Assert.All(ids.Take(5), id => Assert.False(Exists(f.Db, id)));        // de fem äldsta
            Assert.All(ids.Skip(5), id => Assert.True(Exists(f.Db, id)));
            Assert.True(Exists(f.Db, published));
        }

        [SqlFact]
        public void Versioner_hos_en_annan_sida_rangordnas_var_för_sig()
        {
            using var f = new SiteSqlFixture();
            var a = NewPage(f.Db, "A"); var b = NewPage(f.Db, "B");
            var aOld = Enumerable.Range(0, 10).Select(_ => AddVersion(f.Db, a, Now.AddDays(-100))).ToList();
            var bOld = AddVersion(f.Db, b, Now.AddDays(-100));                    // bara en version på B: rang 1, inom 25
            SiteRetention.Run(f.Db, Now);
            Assert.All(aOld, id => Assert.True(Exists(f.Db, id)));                // A har 10 (<=25): ingen rang över 25
            Assert.True(Exists(f.Db, bOld));
        }

        // ── Utkast ────────────────────────────────────────────────────────────────

        [SqlFact]
        public void Ett_utkast_orört_i_90_dagar_kastas_men_bara_på_en_publicerad_sida()
        {
            using var f = new SiteSqlFixture();
            var published = NewPage(f.Db, "Publicerad"); var neverPublished = NewPage(f.Db, "Utkast"); var fresh = NewPage(f.Db, "Färsk");
            foreach (var (id, days, publish) in new[] { (published, 100, true), (neverPublished, 100, false), (fresh, 10, true) })
            {
                var d = AddVersion(f.Db, id, Now.AddDays(-days));
                f.Db.Execute("UPDATE dbo.SitePage SET DraftVersionId = @1 WHERE Id = @0", id, d);
                if (publish) { var pv = AddVersion(f.Db, id, Now.AddDays(-days), publish: true); f.Db.Execute("UPDATE dbo.SitePage SET PublishedVersionId = @1 WHERE Id = @0", id, pv); }
            }
            var r = SiteRetention.Run(f.Db, Now);
            Assert.Equal(1, r.DraftsDiscarded);
            Assert.Null(f.Db.ExecuteScalar<int?>("SELECT DraftVersionId FROM dbo.SitePage WHERE Id = @0", published));           // kastat
            Assert.NotNull(f.Db.ExecuteScalar<int?>("SELECT DraftVersionId FROM dbo.SitePage WHERE Id = @0", neverPublished));   // enda innehållet: kvar
            Assert.NotNull(f.Db.ExecuteScalar<int?>("SELECT DraftVersionId FROM dbo.SitePage WHERE Id = @0", fresh));            // färskt: kvar
            Assert.NotNull(f.Db.ExecuteScalar<int?>("SELECT PublishedVersionId FROM dbo.SitePage WHERE Id = @0", published));    // det publicerade orört
        }

        // ── Stödläge och audit ────────────────────────────────────────────────────

        [SqlFact]
        public void Stödlägen_audit_och_ip_hashar_gallras_efter_sina_frister()
        {
            using var f = new SiteSqlFixture();
            NewPage(f.Db);
            f.Db.Execute("INSERT INTO dbo.SiteSupportGrant (MemberId, OwnerType, OwnerId, Reason, ExpiresDate) VALUES (7, 'Club', @0, N'gammal', @1)", Owner, Now.AddDays(-91));
            f.Db.Execute("INSERT INTO dbo.SiteSupportGrant (MemberId, OwnerType, OwnerId, Reason, ExpiresDate) VALUES (7, 'Club', @0, N'ny', @1)", Owner, Now.AddDays(-89));
            f.Db.Execute("INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ObjectKind, Action, CreatedDate) VALUES ('Club', @0, 'page', 'old', @1)", Owner, Now.AddMonths(-25));
            f.Db.Execute("INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ObjectKind, Action, CreatedDate, IpHash) VALUES ('Club', @0, 'page', 'mid', @1, 0x01)", Owner, Now.AddDays(-100));
            f.Db.Execute("INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ObjectKind, Action, CreatedDate, IpHash) VALUES ('Club', @0, 'page', 'new', @1, 0x01)", Owner, Now.AddDays(-5));
            var r = SiteRetention.Run(f.Db, Now);
            Assert.Equal(1, r.GrantsDeleted); Assert.Equal(1, r.AuditDeleted);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteSupportGrant WHERE OwnerId = @0", Owner));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE Action = 'old' AND OwnerId = @0", Owner));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE Action = 'mid' AND IpHash IS NULL AND OwnerId = @0", Owner));    // raden finns kvar, IP-hashen är borta
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE Action = 'new' AND IpHash IS NOT NULL AND OwnerId = @0", Owner)); // 5 dagar: hashen kvar
        }

        [SqlFact]
        public void En_andra_körning_gör_ingenting()
        {
            using var f = new SiteSqlFixture();
            var p = NewPage(f.Db);
            for (var i = 0; i < 6; i++) AddVersion(f.Db, p, Now.AddMonths(-14));
            Assert.True(SiteRetention.Run(f.Db, Now).Total >= 6);
            Assert.Equal(0, SiteRetention.Run(f.Db, Now).Total);
        }
    }
}
