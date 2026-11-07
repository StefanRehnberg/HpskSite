using HpskSite.Services.SitePages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna steg 1: brytaren per klubb och krets. Det säkra utgångsläget är AV (dagens vy): ett fel, en saknad
    /// tabell eller en ägare utan rad får aldrig slå på den nya strukturen. Ingen databas; läsningen och skrivningen är virtuella.
    /// </summary>
    public class SiteOwnerServiceTests
    {
        private sealed class Fake : SiteOwnerService
        {
            public Func<HashSet<string>> Load = () => new HashSet<string>();
            public int Loads;
            public List<(string Type, int Id, bool Enabled, int Version, int? By)> Writes = new();

            public Fake(Func<DateTime> now) : base(NullLogger<SiteOwnerService>.Instance, now) { }

            protected override HashSet<string> LoadEnabled() { Loads++; return Load(); }
            protected override void WriteRow(string ownerType, int ownerId, bool enabled, int templateVersion, int? byMemberId)
                => Writes.Add((ownerType, ownerId, enabled, templateVersion, byMemberId));
        }

        private static (Fake Svc, Action<TimeSpan> Advance) Make(params string[] enabled)
        {
            var clock = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
            var svc = new Fake(() => clock);
            svc.Load = () => new HashSet<string>(enabled, StringComparer.Ordinal);
            return (svc, d => clock += d);
        }

        [Fact]
        public void En_ägare_utan_rad_är_AV()
        {
            var (svc, _) = Make();
            Assert.False(svc.IsEnabled(SiteTemplates.Club, 1234));
        }

        [Fact]
        public void Bara_den_påslagna_ägaren_och_ägartypen_är_PÅ()
        {
            var (svc, _) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            Assert.True(svc.IsEnabled(SiteTemplates.Club, 5));
            Assert.False(svc.IsEnabled(SiteTemplates.Region, 5)); // sorten följer id:t
            Assert.False(svc.IsEnabled(SiteTemplates.Club, 6));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Ogiltigt_id_är_AV_utan_att_läsa(int id)
        {
            var (svc, _) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            Assert.False(svc.IsEnabled(SiteTemplates.Club, id));
            Assert.Equal(0, svc.Loads);
        }

        [Fact]
        public void Ingen_fråga_per_sidvisning_inom_TTL_och_en_ny_efter()
        {
            var (svc, advance) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            for (var i = 0; i < 50; i++) svc.IsEnabled(SiteTemplates.Club, 5);
            Assert.Equal(1, svc.Loads);
            advance(SiteOwnerService.Ttl + TimeSpan.FromSeconds(1));
            svc.IsEnabled(SiteTemplates.Club, 5);
            Assert.Equal(2, svc.Loads);
        }

        [Fact]
        public void ClearLocal_tvingar_en_ny_läsning()
        {
            var (svc, _) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            svc.IsEnabled(SiteTemplates.Club, 5);
            svc.ClearLocal();
            svc.IsEnabled(SiteTemplates.Club, 5);
            Assert.Equal(2, svc.Loads);
        }

        [Fact]
        public void Ett_läsfel_utan_känt_läge_ger_AV_och_kastar_aldrig()
        {
            var (svc, _) = Make();
            svc.Load = () => throw new InvalidOperationException("databasen nere");
            Assert.False(svc.IsEnabled(SiteTemplates.Club, 5));
        }

        [Fact]
        public void Ett_läsfel_efter_ett_lyckat_läge_behåller_det_kända()
        {
            var (svc, advance) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            Assert.True(svc.IsEnabled(SiteTemplates.Club, 5));
            svc.Load = () => throw new InvalidOperationException("kort låsning");
            advance(SiteOwnerService.Ttl + TimeSpan.FromSeconds(1));
            Assert.True(svc.IsEnabled(SiteTemplates.Club, 5)); // senast kända, inte AV
        }

        [Fact]
        public void Ett_fel_hamras_inte_på_varje_begäran()
        {
            var (svc, advance) = Make();
            svc.Load = () => throw new InvalidOperationException("nere");
            for (var i = 0; i < 20; i++) svc.IsEnabled(SiteTemplates.Club, 5);
            Assert.Equal(1, svc.Loads);                       // en kort paus efter ett fel, inte en fråga per sidvisning
            advance(TimeSpan.FromSeconds(11));
            svc.IsEnabled(SiteTemplates.Club, 5);
            Assert.Equal(2, svc.Loads);
        }

        [Fact]
        public void Set_skriver_med_mallens_version_och_tömmer_cachen()
        {
            var (svc, _) = Make();
            Assert.False(svc.IsEnabled(SiteTemplates.Club, 5));
            svc.Load = () => new HashSet<string> { SiteOwnerService.Key(SiteTemplates.Club, 5) };
            svc.Set(SiteTemplates.Club, 5, enabled: true, byMemberId: 99);
            Assert.Single(svc.Writes);
            Assert.Equal((SiteTemplates.Club, 5, true, SiteTemplates.ClubTemplate.Version, (int?)99), svc.Writes[0]);
            Assert.True(svc.IsEnabled(SiteTemplates.Club, 5));  // cachen tömdes: nästa läsning ser den nya mängden
        }

        [Theory]
        [InlineData("Competition", 5)]
        [InlineData("", 5)]
        [InlineData("Club", 0)]
        [InlineData("Club", -3)]
        public void Set_vägrar_okänd_ägartyp_och_ogiltigt_id(string type, int id)
        {
            var (svc, _) = Make();
            Assert.Throws<ArgumentOutOfRangeException>(() => svc.Set(type, id, true, 1));
            Assert.Empty(svc.Writes);
        }
    }
}
