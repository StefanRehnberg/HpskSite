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

        [Theory]
        [InlineData("Helfigur \"mini\"", 7, "M3", 70)]   // 1/1 miniatyr: gruppens 80, egen gräns 70
        [InlineData("Ovalen nr 3", 9, "M3", 50)]         // gruppens 60, egen gräns 50
        [InlineData("ovalen nr 3", 9, "M", 50)]          // skiftlägesokänsligt
        [InlineData("Helfigur \"mini\"", 7, "A", 30)]    // vanlig fält: oförändrat gruppens tak
        [InlineData("C 30", 7, "M3", 80)]                // annan figur i grupp 7: gruppens
        public void Magnum_figurer_med_eget_tak(string name, int sg, string wc, int expected)
        {
            var f = new Figure(sg, 1, name);
            FigureMaxDistance(f, wc, supportHandAllowed: false, morker: false).Should().Be(expected);
            GroupMaxDistance(new TargetGroup(null, new[] { f }), wc, new Station(20, null, null, 0, 6, Array.Empty<TargetGroup>()), false).Should().Be(expected);
        }

        [Fact]
        public void Magnum_figurens_tak_slar_igenom_i_skjuttiden()
        {
            // Ovalen nr 3 (grupp 9) på 50 m: tiden räknas mot 50 m, inte gruppens 60 m (20 s vid taket => 6 skott x 20 = 120 s).
            var oval = new Station(20, null, null, 0, 6, new[] { new TargetGroup(50, new[] { new Figure(9, 1, "Ovalen nr 3") }) });
            MinimumShootingTime(oval, "M3", false, false).Should().Be(120);
            var other = new Station(20, null, null, 0, 6, new[] { new TargetGroup(50, new[] { new Figure(9, 1, "L1 Vertikal") }) });
            MinimumShootingTime(other, "M3", false, false).Should().Be((int)Math.Ceiling(6 * (50 / 60.0 * 20)));   // 50/60 x 20 x 6 (samma flyttalsräkning som koden)
            MinimumShootingTime(oval, "M3", false, false)!.Value.Should().BeGreaterThan(MinimumShootingTime(other, "M3", false, false)!.Value);
        }

        [Fact]
        public void Magnum_omriktning_ar_en_och_en_halv_sekund_annars_tva()
        {
            // Två målgrupper, grupp 8 (68 m) på 34 m = 10 s/skott x 6 = 60 s + omriktning.
            Station Two(string? w = null) => new(20, null, w, 0, 6, new[] { new TargetGroup(34, new[] { new Figure(8, 1) }), new TargetGroup(34, new[] { new Figure(8, 1) }) });
            MinimumShootingTime(Two(), "M3", false, false).Should().Be(62);   // 60 + 1,5 = 61,5 -> 62
            // Tre målgrupper: 60 + 3 = 63.
            var three = new Station(20, null, null, 0, 6, Enumerable.Range(0, 3).Select(_ => new TargetGroup(34, new[] { new Figure(8, 1) })).ToList());
            MinimumShootingTime(three, "M3", false, false).Should().Be(63);
            // Vanlig fält är oförändrat 2 s per byte: C grupp 8 (36 m) på 18 m = 0,75 x 1,5... kontrollerat som skillnaden mot en grupp.
            var c2 = new Station(20, null, null, 0, 6, new[] { new TargetGroup(18, new[] { new Figure(8, 1) }), new TargetGroup(18, new[] { new Figure(8, 1) }) });
            var c1 = new Station(20, null, null, 0, 6, new[] { new TargetGroup(18, new[] { new Figure(8, 1) }) });
            (MinimumShootingTime(c2, "C", false, false) - MinimumShootingTime(c1, "C", false, false)).Should().Be(2);
        }

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

        [Theory]
        [InlineData("M1")]
        [InlineData("M")]
        [InlineData("M3")]
        public void Magnum_FlyttasInteAvStodhandEllerMorker(string wc)
        {
            LookupSizeGroup(5, true, false, wc).Should().Be(5);
            LookupSizeGroup(5, false, true, wc).Should().Be(5);
            LookupSizeGroup(5, true, true, wc).Should().Be(5);
            // samma anrop för A flyttar fortfarande
            LookupSizeGroup(5, true, false, "A").Should().Be(4);
        }

        [Fact]
        public void Magnum_TaketHojsInteAvStodhand()
        {
            var station = new Station(30, "Stödhand tillåten", null, 0, 6, new[] { new TargetGroup(null, new[] { new Figure(5, 1) }) });
            GroupMaxDistance(station.Groups[0], "M1", station, morker: false).Should().Be(100); // grupp 5, inte grupp 4 (110)
            GroupMaxDistance(station.Groups[0], "A", station, morker: false).Should().Be(45);   // A: stödhand lyfter till grupp 4
        }

        [Theory]
        [InlineData("M")]
        [InlineData("M1")]
        [InlineData("A")]
        [InlineData("C")]
        public void HandsUp_HarIngetSHBMinimum(string wc)
        {
            // SHB E.10.5.1: Hands-up är "det enda undantaget från de allmänna reglerna om skjuttider och skjutavstånd".
            MinimumShootingTime(One(5, 25, weapon: "Hands-up"), wc, morker: false, poangMode: false).Should().BeNull();
            MinimumShootingTime(One(5, 25, weapon: "Hands-up"), wc, morker: true, poangMode: true).Should().BeNull();
            // samma station med annat vapenläge har ett minimum
            MinimumShootingTime(One(5, 25, weapon: "Riktning tillåten"), wc, false, false).Should().NotBeNull();
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
