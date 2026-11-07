using HpskSite.Services.Addresses;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna F0-spik b: <c>SetCurrentFlat</c> (adress utan år) mot en riktig SQL Server. Körs när <c>HPSK_TEST_SQL</c> är
    /// satt, annars hoppas de över MED NAMN. Allt i en transaktion som rullas tillbaka.
    /// </summary>
    public class PageAddressFlatTests
    {
        private static readonly PageAddressOwner Club = new("Club", 990001);
        private static readonly PageAddressOwner OtherClub = new("Club", 990002);

        private static int Count(NPoco.IDatabase db, string where, params object[] args) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PageAddress WHERE " + where, args);

        [SqlFact]
        public void Första_adressen_skrivs_som_aktuell_utan_år()
        {
            using var f = new SiteSqlFixture();
            var r = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "regler-for-banan");
            Assert.True(r.Ok); Assert.True(r.Changed);
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501 AND IsCurrent=1 AND UrlYear IS NULL AND Slug='regler-for-banan' AND OwnerType='Club' AND OwnerId=990001"));
        }

        [SqlFact]
        public void Samma_slugg_igen_skriver_ingenting()
        {
            using var f = new SiteSqlFixture();
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "regler");
            var r = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "REGLER");
            Assert.True(r.Ok); Assert.False(r.Changed);
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501"));
        }

        [SqlFact]
        public void Sluggbyte_gör_den_gamla_till_historik_och_numrerar_aldrig()
        {
            using var f = new SiteSqlFixture();
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "regler");
            var r = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "banregler");
            Assert.True(r.Ok); Assert.True(r.Changed);
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501 AND IsCurrent=1 AND Slug='banregler'"));
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501 AND IsCurrent=0 AND Slug='regler'"));
        }

        [SqlFact]
        public void En_adress_sidan_själv_haft_återanvänds()
        {
            using var f = new SiteSqlFixture();
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "a");
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "b");
            var r = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "a");
            Assert.True(r.Ok); Assert.True(r.Changed);
            Assert.Equal(2, Count(f.Db, "TargetKind='sitepage' AND TargetId=501"));
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501 AND IsCurrent=1 AND Slug='a'"));
        }

        [SqlFact]
        public void En_annan_sidas_adress_ger_Taken_och_rör_ingenting()
        {
            using var f = new SiteSqlFixture();
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "regler");
            var r = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 502, Club, "regler");
            Assert.False(r.Ok); Assert.False(r.Changed); Assert.NotNull(r.Conflict);
            Assert.Equal(0, Count(f.Db, "TargetKind='sitepage' AND TargetId=502"));
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND TargetId=501 AND IsCurrent=1"));
        }

        [SqlFact]
        public void Namnrymden_delas_med_händelsens_gamla_trädadress_men_inte_med_en_annan_ägare()
        {
            using var f = new SiteSqlFixture();
            // En händelses gamla trädadress: årslös rad av sorten 'event' hos samma ägare.
            f.Db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES ('Club', 990001, NULL, 'sommarlagret', 'event', 900001, 0)");
            var clash = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "sommarlagret");
            Assert.False(clash.Ok);
            var otherOwner = PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 502, OtherClub, "sommarlagret");
            Assert.True(otherOwner.Ok);
        }

        [SqlFact]
        public void Ett_års_adress_med_samma_slugg_krockar_inte()
        {
            using var f = new SiteSqlFixture();
            f.Db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES ('Club', 990001, 2027, 'sommarlagret', 'event', 900001, 1)");
            Assert.True(PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "sommarlagret").Ok);
        }

        [SqlFact]
        public void Sorten_sitepage_tillåts_av_databasens_villkor()
        {
            using var f = new SiteSqlFixture();
            // CK_PageAddress_Kind måste tillåta 'sitepage' (migreringen). Annars kastar insättningen.
            PageAddressService.SetCurrentFlat(f.Db, PageAddressKinds.SitePage, 501, Club, "x");
            Assert.Equal(1, Count(f.Db, "TargetKind='sitepage' AND OwnerId=990001"));
        }

        [Fact]
        public void Utan_slugg_kastas()
        {
            Assert.Throws<ArgumentException>(() => PageAddressService.SetCurrentFlat(null!, PageAddressKinds.SitePage, 1, Club, " "));
        }
    }
}
