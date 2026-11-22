using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Magnum: medaljer och lag går per magnumklass (M1–M7 vid SM; SHB C.3.6.2.3, C.3.6.5.2, C.5.1.1.3), inte som en poolad grupp "M"
    /// (Stefan 2026-11-22; backlog P3.13). Vapengrupps-AXELN (finalen, "vilka grupper avgör mästerskapet") förblir "M". Vanlig fält oförändrad.
    /// </summary>
    public class MagnumMedalGroupingTests
    {
        [Theory]
        [InlineData("M1")]
        [InlineData("M3")]
        [InlineData("M7")]
        public void Mastarskapskategorin_for_en_magnumklass_ar_klassen_sjalv(string cls) =>
            ChampionshipCategory.For(cls, splitGroupC: true).Should().Be(cls);

        [Fact]
        public void Valjer_arrangoren_bort_delningen_poolas_Magnum_som_forut() =>
            ChampionshipCategory.For("M3", splitGroupC: false).Should().Be("M");

        [Fact]
        public void Vapengruppsaxeln_ar_fortfarande_M() =>
            ChampionshipCategory.WeaponGroupFor("M3").Should().Be("M");

        [Fact]
        public void Medaljgruppskartan_ger_klass_for_SHB_och_M_for_vapengrupp()
        {
            MedalGroupMap.Shb.GroupFor("M2").Should().Be("M2");
            MedalGroupMap.WeaponGroup.GroupFor("M2").Should().Be("M");
        }

        [Theory]
        [InlineData("C1", "C")]
        [InlineData("C_Vet_Y", "C Vet Y")]
        [InlineData("A2", "A")]
        [InlineData("B3", "B")]
        [InlineData("R1", "R")]
        public void Vanliga_klasser_ar_oforandrade(string cls, string expected) =>
            ChampionshipCategory.For(cls, splitGroupC: true).Should().Be(expected);

        [Fact]
        public void Lagklasserna_ar_en_per_magnumklass()
        {
            var info = TeamClassHelper.GetTeamClasses(new[] { "M1", "M2", "M6", "A1" }, isSpringskytte: false);
            var names = info.Select(i => i.TeamClass).ToList();
            names.Should().Contain(new[] { "M1", "M2", "M6", "A" });
            names.Should().NotContain("M");
            names.Should().NotContain("M3");   // ingen M3-skytt anmäld
            info.Single(i => i.TeamClass == "M2").CompatibleClasses.Should().Equal("M2");
            info.Single(i => i.TeamClass == "M6").CompatibleClasses.Should().Equal("M6");
        }

        [Fact]
        public void Lagets_vapenfamilj_ar_fortfarande_M()
        {
            TeamClassHelper.GetStandardWeaponFamily("M4").Should().Be("M");
            TeamClassHelper.GetTeamSize("M4").Should().Be((3, 1));
        }

        private static FaltskytteShooterResult Shooter(int id, string cls, int hits) => new()
        {
            MemberId = id, Name = "S" + id, ShootingClass = cls, TotalHits = hits, TotalFigures = hits / 2,
        };

        [Fact]
        public void Standardmedaljen_raknas_per_magnumklass_inte_over_alla()
        {
            // Nio skyttar i M1 (50..42 träff) och nio i M2 (30..22): per klass får bästa skytten i VARJE klass silver (1/9).
            // Poolat (förut) hade 1/9 av alla 18 = 2 silver gått till de två bästa i M1, och ingen i M2.
            var shooters = new List<FaltskytteShooterResult>();
            for (var i = 0; i < 9; i++) shooters.Add(Shooter(100 + i, "M1", 50 - i));
            for (var i = 0; i < 9; i++) shooters.Add(Shooter(200 + i, "M2", 30 - i));
            new FaltskytteStandardMedalService().CalculateStandardMedals(shooters, "Normal", stationCount: 8);

            shooters.Single(s => s.MemberId == 100).StandardMedal.Should().Be("S");   // bästa i M1
            shooters.Single(s => s.MemberId == 101).StandardMedal.Should().Be("B");   // inte silver: bara 1/9 i klassen
            shooters.Single(s => s.MemberId == 200).StandardMedal.Should().Be("S");   // bästa i M2: silver trots lägre träff
            shooters.Count(s => s.StandardMedal == "S").Should().Be(2);
            shooters.Count(s => s.StandardMedal == "B").Should().Be(4); // 1/3 av 9 = 3 per klass, minus silvret = 2 brons per klass
        }

        [Fact]
        public void Standardmedaljen_for_vanlig_falt_ar_oforandrad_pa_vapengrupp()
        {
            // C1 och C2 delar vapengrupp C: 18 skyttar i en grupp ger 2 silver (1/9), inte ett per klass.
            var shooters = new List<FaltskytteShooterResult>();
            for (var i = 0; i < 9; i++) shooters.Add(Shooter(100 + i, "C1", 50 - i));
            for (var i = 0; i < 9; i++) shooters.Add(Shooter(200 + i, "C2", 30 - i));
            new FaltskytteStandardMedalService().CalculateStandardMedals(shooters, "Normal", stationCount: 8);
            shooters.Count(s => s.StandardMedal == "S").Should().Be(2);
            shooters.Single(s => s.MemberId == 200).StandardMedal.Should().NotBe("S");
        }
    }
}
