using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2b: stödlägets tillstånd (plan v3 B2/H2) mot en riktig SQL Server, plus anledningsreglerna (rena).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteSupportTests
    {
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private const string Club = "Club";
        private const int Owner = 990001;

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("för kort")]
        public void För_kort_eller_tom_anledning_avvisas(string? reason) => Assert.NotNull(SiteSupport.ReasonProblem(reason));

        [Fact]
        public void Tydlig_anledning_godkänns_men_inte_för_lång_eller_med_kontrolltecken()
        {
            Assert.Null(SiteSupport.ReasonProblem("Klubben bad om hjälp med banreglerna"));
            Assert.NotNull(SiteSupport.ReasonProblem(new string('x', SiteSupport.MaxReasonLength + 1)));
            Assert.NotNull(SiteSupport.ReasonProblem("anledning med \u0007 tecken"));
        }

        [SqlFact]
        public void Ett_stödläge_gäller_en_person_en_ägare_och_60_minuter()
        {
            using var f = new SiteSqlFixture();
            SiteSupport.Start(f.Db, 5513, Club, Owner, "Klubben bad om hjälp med regelsidan", Now);
            var g = SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddMinutes(30))!;
            Assert.Equal(Now.AddMinutes(SiteSupport.Minutes), g.ExpiresDate);
            Assert.Null(SiteSupport.Active(f.Db, 5514, Club, Owner, Now.AddMinutes(30)));            // en annan person
            Assert.Null(SiteSupport.Active(f.Db, 5513, Club, Owner + 1, Now.AddMinutes(30)));        // en annan ägare
            Assert.Null(SiteSupport.Active(f.Db, 5513, "Region", Owner, Now.AddMinutes(30)));         // sorten följer id:t
        }

        [SqlFact]
        public void Stödläget_har_gått_ut_efter_60_minuter()
        {
            using var f = new SiteSqlFixture();
            SiteSupport.Start(f.Db, 5513, Club, Owner, "Klubben bad om hjälp med regelsidan", Now);
            Assert.NotNull(SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddMinutes(59)));
            Assert.Null(SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddMinutes(60)));            // utgånget på sekunden
            Assert.Null(SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddHours(5)));
        }

        [SqlFact]
        public void Att_avsluta_i_förtid_gör_stödläget_inaktivt_och_ett_nytt_ersätter_det_gamla()
        {
            using var f = new SiteSqlFixture();
            var first = SiteSupport.Start(f.Db, 5513, Club, Owner, "Första anledningen till stödläge", Now);
            Assert.Equal(1, SiteSupport.End(f.Db, 5513, Club, Owner, Now.AddMinutes(5)));
            Assert.Null(SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddMinutes(6)));
            Assert.Equal(0, SiteSupport.End(f.Db, 5513, Club, Owner, Now.AddMinutes(6)));            // inget kvar att avsluta
            var second = SiteSupport.Start(f.Db, 5513, Club, Owner, "Andra anledningen till stödläge", Now.AddMinutes(10));
            var third = SiteSupport.Start(f.Db, 5513, Club, Owner, "Tredje anledningen till stödläge", Now.AddMinutes(11));
            Assert.NotEqual(first, second);
            Assert.Equal(third, SiteSupport.Active(f.Db, 5513, Club, Owner, Now.AddMinutes(12))!.Id);
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteSupportGrant WHERE MemberId = 5513 AND OwnerId = @0 AND EndedDate IS NULL", Owner));   // bara ett öppet
        }

        [SqlFact]
        public void Anledningen_sparas_trimmad_och_en_ogiltig_anledning_skapar_inget_stödläge()
        {
            using var f = new SiteSqlFixture();
            Assert.Throws<ArgumentException>(() => SiteSupport.Start(f.Db, 5513, Club, Owner, "kort", Now));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteSupportGrant WHERE OwnerId = @0", Owner));
            SiteSupport.Start(f.Db, 5513, Club, Owner, "   Klubben bad om hjälp med regelsidan   ", Now);
            Assert.Equal("Klubben bad om hjälp med regelsidan", SiteSupport.Active(f.Db, 5513, Club, Owner, Now)!.Reason);
        }
    }
}
