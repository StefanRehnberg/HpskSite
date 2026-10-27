using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;
using Span = HpskSite.Models.Staffing.ShiftScheduleRules.Span;

namespace HpskSite.Tests
{
    /// <summary>Skiftschemat (Stefan 2026-10-27): hålen mot skjuttiden och uppdelningen i lika långa skift.</summary>
    public class ShiftScheduleRulesTests
    {
        private static TimeSpan T(int h, int m = 0) => new(h, m, 0);
        private static Span S(int h1, int h2) => new(T(h1), T(h2));
        private static readonly Span Day = S(8, 17);

        [Fact]
        public void Holes_inget_skift_ger_hela_fönstret()
            => ShiftScheduleRules.Holes(new Span?[0], Day).Should().Equal(Day);

        [Fact]
        public void Holes_ett_skift_som_slutar_före_dagens_slut_ger_resten()
            => ShiftScheduleRules.Holes(new Span?[] { S(8, 11) }, Day).Should().Equal(S(11, 17));

        [Fact]
        public void Holes_lyckat_skiftschema_utan_hål()
            => ShiftScheduleRules.Holes(new Span?[] { S(8, 11), S(11, 14), S(14, 17) }, Day).Should().BeEmpty();

        [Fact]
        public void Holes_hål_mellan_två_skift_och_i_början()
            => ShiftScheduleRules.Holes(new Span?[] { S(9, 11), S(13, 17) }, Day).Should().Equal(S(8, 9), S(11, 13));

        [Fact]
        public void Holes_en_post_utan_tider_är_på_plats_hela_dagen()
            => ShiftScheduleRules.Holes(new Span?[] { null }, Day).Should().BeEmpty();

        [Fact]
        public void Holes_två_samtidiga_behövs_och_bara_en_står_på_plats()
            => ShiftScheduleRules.Holes(new Span?[] { S(8, 17), S(8, 12) }, Day, need: 2).Should().Equal(S(12, 17));

        [Fact]
        public void Holes_skift_utanför_fönstret_räknas_inte_och_klipps()
            => ShiftScheduleRules.Holes(new Span?[] { S(6, 10), S(16, 20) }, Day).Should().Equal(S(10, 16));

        [Fact]
        public void Holes_behov_noll_eller_tomt_fönster_ger_inga_hål()
        {
            ShiftScheduleRules.Holes(new Span?[0], Day, need: 0).Should().BeEmpty();
            ShiftScheduleRules.Holes(new Span?[0], S(9, 9)).Should().BeEmpty();
        }

        [Fact]
        public void Holes_ihopslagna_hål_blir_ett()
            => ShiftScheduleRules.Holes(new Span?[] { S(10, 11), S(10, 11) }, Day).Should().Equal(S(8, 10), S(11, 17));

        [Fact]
        public void Split_tre_lika_skift_på_hela_kvartar()
            => ShiftScheduleRules.Split(S(8, 17), 3).Should().Equal(S(8, 11), S(11, 14), S(14, 17));

        [Fact]
        public void Split_avrundar_till_kvartar_och_sista_tar_resten()
        {
            var r = ShiftScheduleRules.Split(new Span(T(9), T(15, 30)), 3);   // 6,5 h / 3 = 2 h 10 min
            r.Should().HaveCount(3);
            r[0].Start.Should().Be(T(9));
            r[^1].End.Should().Be(T(15, 30));
            r.Take(2).Select(x => x.End.Minutes % 15).Should().OnlyContain(m => m == 0);
            for (var i = 1; i < r.Count; i++) r[i].Start.Should().Be(r[i - 1].End);
        }

        [Fact]
        public void Split_ett_skift_är_hela_fönstret_och_noll_skift_ger_inget()
        {
            ShiftScheduleRules.Split(Day, 1).Should().Equal(Day);
            ShiftScheduleRules.Split(Day, 0).Should().BeEmpty();
        }

        [Fact]
        public void Split_för_kort_fönster_ger_aldrig_tomma_skift()
        {
            var r = ShiftScheduleRules.Split(new Span(T(9), T(9, 10)), 5);
            r.Should().HaveCount(5);
            r.Should().OnlyContain(x => x.End > x.Start);
            r[^1].End.Should().Be(T(9, 10));
        }

        [Fact]
        public void RoleSet_och_WithRole_slår_på_och_av_utan_dubbletter()
        {
            var on = ShiftScheduleRules.WithRole(null, "stationschef", true);
            on.Should().Be("stationschef");
            ShiftScheduleRules.WithRole(on, "STATIONSCHEF", true).Should().Be("stationschef");
            ShiftScheduleRules.WithRole(on, "sekretariat", true).Should().Be("sekretariat,stationschef");
            ShiftScheduleRules.WithRole("sekretariat,stationschef", "stationschef", false).Should().Be("sekretariat");
            ShiftScheduleRules.WithRole("sekretariat", "sekretariat", false).Should().BeNull();
            ShiftScheduleRules.RoleSet("a, b,,C").Should().BeEquivalentTo("a", "b", "C");
        }

        [Fact]
        public void WithRole_vägrar_nyckel_med_komma()
            => ShiftScheduleRules.WithRole("a", "b,c", true).Should().Be("a");

        [Fact]
        public void Hm_formaterar_klockslag()
            => ShiftScheduleRules.Hm(T(8)).Should().Be("08:00");
    }
}
