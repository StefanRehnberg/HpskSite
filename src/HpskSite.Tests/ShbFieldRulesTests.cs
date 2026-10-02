using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;
using static HpskSite.CompetitionTypes.Faltskytte.Services.ShbFieldRules;

namespace HpskSite.Tests
{
    /// <summary>
    /// Pinnar SHB-reglerna på servern mot samma räkning som konfiguratorns JavaScript
    /// (faltSuggestShootingTime m.fl.). Faller ett test här: kontrollera konfiguratorn FÖRST — de två
    /// ska säga samma sak om samma station.
    /// </summary>
    public class ShbFieldRulesTests
    {
        private static Station One(int sizeGroup, int? distance, string? support = null, string? weapon = null, int maxShots = 6, int targets = 1) =>
            new(20, support, weapon, 0, maxShots, new[] { new TargetGroup(distance, new[] { new Figure(sizeGroup, targets) }) });

        [Theory]
        [InlineData(5, "A", 40)]
        [InlineData(5, "B", 45)]
        [InlineData(5, "C", 50)]
        [InlineData(5, "M3", 100)]
        [InlineData(14, "R", 7)]
        public void Maxavstand_PerVapengrupp(int sg, string wc, int expected) => MaxDistance(sg, wc).Should().Be(expected);

        [Fact]
        public void EjGrupperad_HarIngetMaxavstand() => MaxDistance(15, "C").Should().BeNull();

        [Fact]
        public void Stodhand_LattarEttSteg_MorkerSkarper_TarUtVarandra()
        {
            LookupSizeGroup(5, supportHandAllowed: true, morker: false).Should().Be(4);
            LookupSizeGroup(5, false, true).Should().Be(6);
            LookupSizeGroup(5, true, true).Should().Be(5);
            LookupSizeGroup(1, true, false).Should().Be(1);   // taket
        }

        [Fact]
        public void Skjuttid_Normal_SexSkottPaSammaFigur() =>
            // 40 m av 40 m max → 2,0 s/skott × 6 = 12 s
            MinimumShootingTime(One(5, 40), "A", morker: false, poangMode: false).Should().Be(12);

        [Fact]
        public void Skjuttid_45GraderGerTvaSekunder_UtomMagnum()
        {
            MinimumShootingTime(One(5, 40, weapon: "45 grader"), "A", false, false).Should().Be(14);
            MinimumShootingTime(One(5, 40, weapon: "45 grader"), "M1", false, false).Should().Be(MinimumShootingTime(One(5, 40), "M1", false, false));
        }

        [Fact]
        public void Skjuttid_VapengruppC_AvrundasUppat() =>
            // 40 / 50 × 1,5 = 1,2 s × 6 = 7,2 → 8
            MinimumShootingTime(One(5, 40), "C", false, false).Should().Be(8);

        [Fact]
        public void Skjuttid_StodhandGerLagreMinimum() =>
            // grupp 4: 40 / 45 × 2,0 × 6 = 10,67 → 11
            MinimumShootingTime(One(5, 40, support: SupportHandAllowed), "A", false, false).Should().Be(11);

        [Fact]
        public void Skjuttid_MorkerTrettioProcentPaToppen() =>
            // grupp 6 i mörker: 40/35 × 2 × 6 = 13,71 → 14; × 1,3 = 18,2 → 19
            MinimumShootingTime(One(5, 40), "A", morker: true, poangMode: false).Should().Be(19);

        [Fact]
        public void Skjuttid_OmriktningTvaSekunderPerExtraMalgrupp()
        {
            var s = new Station(20, null, null, 0, 3, new[]
            {
                new TargetGroup(40, new[] { new Figure(5, 1) }),
                new TargetGroup(40, new[] { new Figure(5, 1) }),
            });
            // 3 + 3 skott à 2,0 = 12 s + 1 omriktning à 2 s
            MinimumShootingTime(s, "A", false, false).Should().Be(14);
        }

        [Fact]
        public void Skjuttid_Poang_OverskottPaEnklaste_Normal_PaSvaraste()
        {
            var s = new Station(20, null, null, 0, 6, new[]
            {
                new TargetGroup(40, new[] { new Figure(5, 1), new Figure(1, 1) }),   // 2,0 s resp 40/70×2 = 1,14 s
            });
            // Poäng: 1 skott på var, 4 extra på den enklaste: 2,0 + 5 × 1,143 = 7,71 → 8
            MinimumShootingTime(s, "A", false, poangMode: true).Should().Be(8);
            // Normal: alla 6 på den svåraste: 12
            MinimumShootingTime(s, "A", false, poangMode: false).Should().Be(12);
        }

        [Fact]
        public void Skjuttid_IngenStorleksgrupp_IngetMinimum() =>
            MinimumShootingTime(One(0, 40), "A", false, false).Should().BeNull();

        [Fact]
        public void Malgruppens_Maxavstand_ArDenSnavasteFiguren()
        {
            var g = new TargetGroup(30, new[] { new Figure(3, 1), new Figure(9, 1) });
            GroupMaxDistance(g, "A", One(3, 30), false).Should().Be(24);
        }
    }
}
