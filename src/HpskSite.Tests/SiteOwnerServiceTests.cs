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

            public List<(string Type, int Id)> Deletes = new();
            protected override int DeleteRow(string ownerType, int ownerId) { Deletes.Add((ownerType, ownerId)); return 1; }

            public Func<HashSet<string>> LoadEd = () => new HashSet<string>();
            public int EditingRowsAffected = 1;
            public List<(string Type, int Id, bool Enabled)> EditingWrites = new();
            protected override HashSet<string> LoadEditing() => LoadEd();
            protected override int WriteEditing(string ownerType, int ownerId, bool enabled)
            { EditingWrites.Add((ownerType, ownerId, enabled)); return EditingRowsAffected; }

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

        // ── Brytare 2 (egen redigering): effektivt läge = IsEnabled OCH EditingEnabled, på ETT ställe ──────────

        private static string K(string type, int id) => SiteOwnerService.Key(type, id);

        [Fact]
        public void Redigering_kräver_både_brytare_1_och_brytare_2()
        {
            var (svc, _) = Make(K(SiteTemplates.Club, 5), K(SiteTemplates.Club, 6));
            svc.LoadEd = () => new HashSet<string> { K(SiteTemplates.Club, 5), K(SiteTemplates.Club, 7) };
            Assert.True(svc.IsEditingEnabled(SiteTemplates.Club, 5));    // båda
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Club, 6));   // bara 1
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Club, 7));   // bara 2: brytare 1 AV vinner
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Region, 5)); // sorten följer id:t
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Redigering_med_ogiltigt_id_är_AV_utan_att_läsa(int id)
        {
            var (svc, _) = Make();
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Club, id));
            Assert.Equal(0, svc.Loads);
        }

        [Fact]
        public void Att_slå_av_brytare_1_ger_AV_vid_nästa_läsning_och_PÅ_igen_utan_att_brytare_2_rörs()
        {
            var (svc, _) = Make(K(SiteTemplates.Club, 5));
            svc.LoadEd = () => new HashSet<string> { K(SiteTemplates.Club, 5) };
            Assert.True(svc.IsEditingEnabled(SiteTemplates.Club, 5));

            svc.Load = () => new HashSet<string>();                           // brytare 1 av
            svc.Set(SiteTemplates.Club, 5, enabled: false, byMemberId: 1);
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Club, 5));
            Assert.Empty(svc.EditingWrites);                                  // Set rör aldrig EditingEnabled

            svc.Load = () => new HashSet<string> { K(SiteTemplates.Club, 5) }; // på igen: brytare 2 oförändrad i databasen
            svc.Set(SiteTemplates.Club, 5, enabled: true, byMemberId: 1);
            Assert.True(svc.IsEditingEnabled(SiteTemplates.Club, 5));
            Assert.Empty(svc.EditingWrites);
        }

        [Fact]
        public void SetEditing_skriver_och_tömmer_cachen()
        {
            var (svc, _) = Make(K(SiteTemplates.Club, 5));
            Assert.False(svc.IsEditingEnabled(SiteTemplates.Club, 5));
            svc.LoadEd = () => new HashSet<string> { K(SiteTemplates.Club, 5) };
            svc.SetEditing(SiteTemplates.Club, 5, true, byMemberId: 9);
            Assert.Equal((SiteTemplates.Club, 5, true), Assert.Single(svc.EditingWrites));
            Assert.True(svc.IsEditingEnabled(SiteTemplates.Club, 5));
        }

        [Fact]
        public void SetEditing_utan_rad_i_SiteOwner_vägras()
        {
            var (svc, _) = Make();
            svc.EditingRowsAffected = 0;
            Assert.Throws<InvalidOperationException>(() => svc.SetEditing(SiteTemplates.Club, 5, true, 1));
        }

        [Theory]
        [InlineData("Competition", 5)]
        [InlineData("Club", 0)]
        public void SetEditing_vägrar_okänd_ägartyp_och_ogiltigt_id(string type, int id)
        {
            var (svc, _) = Make();
            Assert.Throws<ArgumentOutOfRangeException>(() => svc.SetEditing(type, id, true, 1));
            Assert.Empty(svc.EditingWrites);
        }

        [Fact]
        public void Remove_tar_bort_raden_och_tömmer_cachen()
        {
            var (svc, _) = Make(SiteOwnerService.Key(SiteTemplates.Club, 5));
            Assert.True(svc.IsEnabled(SiteTemplates.Club, 5));
            svc.Load = () => new HashSet<string>();
            Assert.Equal(1, svc.Remove(SiteTemplates.Club, 5));
            Assert.Equal((SiteTemplates.Club, 5), Assert.Single(svc.Deletes));
            Assert.False(svc.IsEnabled(SiteTemplates.Club, 5));
        }

        [Theory]
        [InlineData("Competition", 5)]
        [InlineData("Club", 0)]
        public void Remove_vägrar_okänd_ägartyp_och_ogiltigt_id(string type, int id)
        {
            var (svc, _) = Make();
            Assert.Throws<ArgumentOutOfRangeException>(() => svc.Remove(type, id));
            Assert.Empty(svc.Deletes);
        }

        [Theory]
        [InlineData("club", "Club")]
        [InlineData("regionalPage", "Region")]
        [InlineData("competition", null)]
        [InlineData("clubsPage", null)]
        public void Raderingen_städar_bara_klubbar_och_kretsar(string alias, string? expected)
            => Assert.Equal(expected, SiteOwnerCleanupHandler.OwnerTypeOf(alias));

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
