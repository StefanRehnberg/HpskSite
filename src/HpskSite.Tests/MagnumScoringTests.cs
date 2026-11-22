using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Magnumfält räknar träff först (Stefan 2026-11-22; SHB E.6.11 och C.5.1.2.2): träff → träffade figurer → poäng i poängmålen →
    /// poäng på sista stationen, näst sista osv. → träff på sista stationen, näst sista osv. EN avgörare,
    /// <see cref="FaltskytteScoringMode.ResolveFor"/>, som listan, laget, prisutdelningen, artefakten och särskjutningen frågar.
    /// </summary>
    public class MagnumScoringTests
    {
        private static FaltskytteCompetitionConfig Cfg(string? mode) => new() { ScoringMode = mode };

        [Theory]
        [InlineData("MagnumFalt", "Poang", "Normal")]
        [InlineData("MagnumFalt", "Normal", "Normal")]
        [InlineData("MagnumFalt", null, "Normal")]
        [InlineData("magnumfalt", "Poang", "Normal")]
        public void Magnum_ar_alltid_Normal_oavsett_konfigurationens_lage(string type, string? cfgMode, string expected) =>
            FaltskytteScoringMode.ResolveFor(type, Cfg(cfgMode), "Poang").Should().Be(expected);

        [Theory]
        [InlineData("Faltskytte", "Poang", "Poang")]
        [InlineData("Faltskytte", "Normal", "Normal")]
        [InlineData("Faltskytte", null, "Normal")]
        public void Vanlig_falt_foljer_konfigurationen_som_forut(string type, string? cfgMode, string expected) =>
            FaltskytteScoringMode.ResolveFor(type, Cfg(cfgMode), null).Should().Be(expected);

        [Fact]
        public void Vanlig_falt_utan_konfigurationslage_foljer_tavlingens_egenskap() =>
            FaltskytteScoringMode.ResolveFor("Faltskytte", Cfg(null), "Poang").Should().Be("Poang");

        [Theory]
        [InlineData("MagnumFalt", "Poang", "Normal")]
        [InlineData("Faltskytte", "Poang", "Poang")]
        [InlineData("Faltskytte", "Normal", "Normal")]
        [InlineData("Faltskytte", "skräp", "Normal")]
        public void EffectiveFor_for_ett_redan_last_lage(string type, string mode, string expected) =>
            FaltskytteScoringMode.EffectiveFor(type, mode).Should().Be(expected);

        private static FaltskytteShooterResult Shooter(int id, int hits, int figs, int pm, params (int station, int hits, int pm)[] stations) => new()
        {
            MemberId = id,
            TotalHits = hits,
            TotalFigures = figs,
            TotalTiebreakerScore = pm,
            Stations = stations.Select(s => new FaltskytteStationResult { StationNumber = s.station, Hits = s.hits, TiebreakerScore = s.pm }).ToList()
        };

        private static int Cmp(FaltskytteShooterResult a, FaltskytteShooterResult b) => new FaltskylteTieBreaker(false, magnum: true).Compare(a, b);

        [Fact]
        public void Traff_forst()
        {
            Cmp(Shooter(1, 41, 30, 10), Shooter(2, 40, 35, 99)).Should().BePositive();
            Cmp(Shooter(2, 40, 35, 99), Shooter(1, 41, 30, 10)).Should().BeNegative();
        }

        [Fact]
        public void Lika_traff_avgors_av_figurer_sedan_poang()
        {
            Cmp(Shooter(1, 40, 31, 0), Shooter(2, 40, 30, 99)).Should().BePositive();
            Cmp(Shooter(1, 40, 30, 20), Shooter(2, 40, 30, 19)).Should().BePositive();
        }

        [Fact]
        public void Lika_overallt_avgors_av_poang_pa_sista_stationen()
        {
            var a = Shooter(1, 40, 30, 20, (7, 3, 4), (8, 4, 6));
            var b = Shooter(2, 40, 30, 20, (7, 5, 9), (8, 2, 5));
            Cmp(a, b).Should().BePositive(); // station 8: 6 mot 5 poäng, trots färre träff
        }

        [Fact]
        public void Lika_poang_pa_sista_stationen_gar_vidare_till_nast_sista()
        {
            var a = Shooter(1, 40, 30, 20, (7, 3, 9), (8, 4, 5));
            var b = Shooter(2, 40, 30, 20, (7, 5, 8), (8, 2, 5));
            Cmp(a, b).Should().BePositive(); // station 8 lika (5-5), station 7: 9 mot 8
        }

        [Fact]
        public void Lika_poang_overallt_avgors_av_traff_pa_sista_stationen_sedan_nast_sista()
        {
            var a = Shooter(1, 40, 30, 20, (7, 3, 5), (8, 5, 5));
            var b = Shooter(2, 40, 30, 20, (7, 4, 5), (8, 4, 5));
            Cmp(a, b).Should().BePositive(); // alla poäng lika; träff på station 8: 5 mot 4
            var c = Shooter(3, 40, 30, 20, (7, 6, 5), (8, 4, 5));
            Cmp(c, b).Should().BePositive(); // station 8 lika, station 7: 6 mot 4
        }

        [Fact]
        public void Helt_lika_ger_noll() =>
            Cmp(Shooter(1, 40, 30, 20, (7, 3, 5), (8, 4, 5)), Shooter(2, 40, 30, 20, (7, 3, 5), (8, 4, 5))).Should().Be(0);

        [Fact]
        public void Vanlig_falt_har_samma_tiebreaker_som_forut()
        {
            // Normal: hits → figures → poängmål → träff på sista stationen. Poäng på sista stationen räknas INTE (oförändrat).
            var normal = new FaltskylteTieBreaker(false);
            var a = Shooter(1, 40, 30, 20, (7, 3, 4), (8, 4, 6));
            var b = Shooter(2, 40, 30, 20, (7, 5, 9), (8, 4, 5));
            normal.Compare(a, b).Should().Be(0);                    // Normal ser inte poängen på sista stationen
            new FaltskylteTieBreaker(false, magnum: true).Compare(a, b).Should().BePositive();
        }

        [Fact]
        public void Poangfalt_ignorerar_magnumflaggan()
        {
            var a = Shooter(1, 40, 30, 20); a.TotalPoints = 70;
            var b = Shooter(2, 50, 30, 20); b.TotalPoints = 69;
            new FaltskylteTieBreaker(true, magnum: true).Compare(a, b).Should().BePositive(); // poängläget: poäng först
        }
    }
}
