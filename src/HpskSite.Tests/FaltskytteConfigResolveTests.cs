using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Vilken klass konfiguration en skytt får (Magnum-omtaget 2026-11-22): en Magnumskytt får aldrig en annan klass tider och
    /// figurer, men övriga klasser beter sig som förut (okänd grupp → första konfigurationen).
    /// </summary>
    public class FaltskytteConfigResolveTests
    {
        private static FaltskytteWeaponClassConfig Cfg(int stations) =>
            new() { Stations = Enumerable.Range(1, stations).Select(i => new FaltskytteStationConfig { Station = i, ShootingTimeSec = 30 }).ToList() };

        private static FaltskytteCompetitionConfig Make(params (string key, int stations)[] classes)
        {
            var c = new FaltskytteCompetitionConfig();
            foreach (var (key, n) in classes) c.WeaponConfigs[key] = Cfg(n);
            return c;
        }

        [Theory]
        [InlineData("M")]
        [InlineData("M1")]
        [InlineData("M3")]
        [InlineData("M7")]
        public void Magnum_utan_nyckeln_M_ger_null_aldrig_en_annan_klass(string cls)
        {
            var cfg = Make(("A", 6), ("B", 7), ("C", 8), ("R", 9));
            cfg.GetForWeaponClass(cls).Should().BeNull();
        }

        [Theory]
        [InlineData("M")]
        [InlineData("M1")]
        [InlineData("M5")]
        [InlineData("M9")]
        public void Magnum_med_nyckeln_M_far_den(string cls)
        {
            var cfg = Make(("A", 6), ("M", 8));
            cfg.GetForWeaponClass(cls)!.Stations.Count.Should().Be(8);
        }

        [Fact]
        public void Magnum_med_egen_klassnyckel_traffar_exakt_forst()
        {
            var cfg = Make(("M", 8), ("M6", 5));
            cfg.GetForWeaponClass("M6")!.Stations.Count.Should().Be(5);
            cfg.GetForWeaponClass("M1")!.Stations.Count.Should().Be(8);
        }

        [Theory]
        [InlineData("A1", "A", 6)]
        [InlineData("B2", "B", 7)]
        [InlineData("C3", "C", 8)]
        public void Vanliga_klasser_traffar_sin_grupp(string cls, string key, int expected)
        {
            var cfg = Make(("A", 6), ("B", 7), ("C", 8), ("M", 9));
            cfg.GetForWeaponClass(cls)!.Stations.Count.Should().Be(expected, key);
        }

        [Fact]
        public void Okand_grupp_i_vanlig_falt_faller_som_forut_pa_forsta_konfigurationen()
        {
            // oförändrat beteende för vanlig fält: A_Opt-skytt i en konfiguration utan A_Opt får första klassen
            var cfg = Make(("A", 6), ("C", 8));
            cfg.GetForWeaponClass("A_opt_1")!.Stations.Count.Should().Be(6);
        }
    }
}
