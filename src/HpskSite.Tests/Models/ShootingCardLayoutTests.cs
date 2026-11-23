using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>Skjutkortens geometri (Stefan 2026-11-23): två layouter, A4 stående/liggande, 1 eller 2 kort per sida.</summary>
    public class ShootingCardLayoutTests
    {
        // Typisk bana: 8 stationer, 23 figurer. Största: 10 stationer, 31 figurer. Station 2 har ett poängmål.
        static List<ShootingCardStation> Stations(params int[] figures) =>
            figures.Select((f, i) => new ShootingCardStation(i + 1, null, f, i == 1 ? 1 : 0)).ToList();
        static readonly List<ShootingCardStation> Typical = Stations(3, 4, 3, 4, 6, 1, 1, 1);
        static readonly List<ShootingCardStation> Large = Stations(3, 4, 3, 4, 6, 1, 1, 3, 4, 2);

        static double Box(string layout, List<ShootingCardStation> st, string paper, int per, bool sum = false) =>
            ShootingCardLayout.Measure(layout, st, 12, ShootingCardLayout.Cards(paper, per)[0], sum).BoxW;

        [Fact]
        public void Two_cards_per_sheet_are_two_landscape_A5_on_portrait_and_two_portrait_A5_on_landscape()
        {
            var p = ShootingCardLayout.Cards("portrait", 2);
            p.Should().HaveCount(2);
            (p[0].W, p[0].H).Should().Be((210.0, 148.5));
            p[1].Y.Should().Be(148.5);
            var l = ShootingCardLayout.Cards("landscape", 2);
            (l[0].W, l[0].H).Should().Be((148.5, 210.0));
            l[1].X.Should().Be(148.5);
            ShootingCardLayout.Cards("landscape", 1).Single().Should().Be(new ShootingCardRect(0, 0, 297, 210));
        }

        [Fact]
        public void Per_figure_boxes_for_the_largest_course_need_A4_landscape_with_one_card()
        {
            // Det Stefan sa: vill man ha rutor för alla figurer måste liggande A4 tillåtas.
            ShootingCardLayout.Fit("fig", Box("fig", Large, "landscape", 1)).Level.Should().Be("ok");
            ShootingCardLayout.Fit("fig", Box("fig", Large, "portrait", 2)).Level.Should().Be("bad");   // A5 liggande
            ShootingCardLayout.Fit("fig", Box("fig", Large, "landscape", 2)).Level.Should().Be("bad");  // A5 stående
        }

        [Fact]
        public void A_portrait_A4_with_one_card_gives_no_extra_width_over_a_landscape_A5()
        {
            Box("fig", Large, "portrait", 1).Should().BeApproximately(Box("fig", Large, "portrait", 2), 0.001);
        }

        [Fact]
        public void Only_hits_over_figures_fits_ten_stations_on_a_landscape_A5()
        {
            // "Då får vi plats med 8-10 stationer på ett liggande A5."
            ShootingCardLayout.Fit("tot", Box("tot", Large, "portrait", 2)).Level.Should().Be("ok");
            ShootingCardLayout.Fit("tot", Box("tot", Typical, "portrait", 2)).Level.Should().Be("ok");
            ShootingCardLayout.Fit("tot", Box("tot", Large, "landscape", 2)).Level.Should().NotBe("bad"); // A5 stående
        }

        [Fact]
        public void The_sum_column_takes_room_from_the_boxes()
        {
            Box("fig", Typical, "landscape", 1, sum: true).Should().BeLessThan(Box("fig", Typical, "landscape", 1, sum: false));
        }

        [Fact]
        public void Fit_levels_use_the_value_as_it_is_shown_rounded_to_one_decimal()
        {
            ShootingCardLayout.Fit("fig", 3.96).Level.Should().Be("ok");      // visas som 4,0
            ShootingCardLayout.Fit("fig", 3.94).Level.Should().Be("warn");    // visas som 3,9
            ShootingCardLayout.Fit("fig", 3.44).Level.Should().Be("bad");
            ShootingCardLayout.Fit("tot", 9.0).Level.Should().Be("ok");
            ShootingCardLayout.Fit("tot", 8.94).Level.Should().Be("warn");   // visas som 8,9: under gränsen 9
            ShootingCardLayout.Fit("tot", 8.5).Level.Should().Be("warn");
            ShootingCardLayout.Fit("tot", 7.0).Level.Should().Be("warn");
            ShootingCardLayout.Fit("tot", 6.9).Level.Should().Be("bad");
        }

        [Fact]
        public void Rows_adapt_to_the_shooter_count_and_never_get_taller_than_the_cap()
        {
            var card = ShootingCardLayout.Cards("portrait", 2)[0];
            var twelve = ShootingCardLayout.Measure("fig", Typical, 12, card, false);
            var six = ShootingCardLayout.Measure("fig", Typical, 6, card, false);
            six.RowH.Should().BeGreaterThan(twelve.RowH);
            six.RowH.Should().Be(13.5);
            six.NotesH.Should().BeGreaterThan(8);          // utrymmet som blir över blir en anteckningsruta
            twelve.NotesH.Should().BeApproximately(0, 0.001); // tolv skyttar fyller kortet
            ShootingCardLayout.Measure("fig", Typical, 6, ShootingCardLayout.Cards("landscape", 1)[0], false).RowH.Should().Be(15);
        }

        [Theory]
        [InlineData(0, new[] { 0 })]
        [InlineData(1, new[] { 1 })]
        [InlineData(12, new[] { 12 })]
        [InlineData(13, new[] { 7, 6 })]
        [InlineData(24, new[] { 12, 12 })]
        [InlineData(25, new[] { 9, 8, 8 })]
        public void More_than_twelve_shooters_are_split_evenly_never_twelve_plus_one(int shooters, int[] expected)
        {
            ShootingCardLayout.SplitSizes(shooters).Should().Equal(expected);
        }

        [Fact]
        public void Cards_are_paired_in_order_and_an_odd_count_leaves_the_last_place_empty()
        {
            var cards = new List<string> { "a", "b", "c" };
            var s = ShootingCardLayout.Sheets(cards, 2);
            s.Should().HaveCount(2);
            s[0].Should().Equal("a", "b");
            s[1][0].Should().Be("c");
            s[1][1].Should().BeNull();
            ShootingCardLayout.Sheets(cards, 1).Should().HaveCount(3);
        }

        [Fact]
        public void A_station_with_scored_targets_gets_one_box_per_target()
        {
            var one = new ShootingCardStation(1, null, 3, 1);
            var three = new ShootingCardStation(1, null, 3, 3);
            (ShootingCardLayout.StationUnits("fig", three) - ShootingCardLayout.StationUnits("fig", one)).Should().BeApproximately(2 * ShootingCardLayout.PoangBoxUnitsFig, 0.0001);
            ShootingCardLayout.StationUnits("tot", new ShootingCardStation(1, null, 6, 0)).Should().Be(ShootingCardLayout.TotalBoxUnits); // figurerna räknas inte i tot-layouten
        }

        [Theory]
        [InlineData("1-4, 7", new[] { 1, 2, 3, 4, 7 })]
        [InlineData("7;3", new[] { 3, 7 })]
        [InlineData("5-3", new[] { 3, 4, 5 })]
        [InlineData("x, 2", new[] { 2 })]
        public void Patrol_numbers_accept_lists_and_ranges(string text, int[] expected)
        {
            ShootingCardBuilder.ParsePatrolNumbers(text)!.OrderBy(x => x).Should().Equal(expected);
        }

        [Fact]
        public void An_empty_selection_means_all_and_an_unreadable_one_means_none()
        {
            ShootingCardBuilder.ParsePatrolNumbers(null).Should().BeNull();
            ShootingCardBuilder.ParsePatrolNumbers("  ").Should().BeNull();
            ShootingCardBuilder.ParsePatrolNumbers("abc")!.Should().BeEmpty();
        }
    }
}
