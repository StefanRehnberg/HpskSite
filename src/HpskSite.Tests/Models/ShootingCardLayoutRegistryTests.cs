using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>Layoutregistret och de nya layouterna Skyttblock (blk) och Skyttkort (card), Stefans beslut 2026-11-24.</summary>
    public class ShootingCardLayoutRegistryTests
    {
        // Stora banan ur mocken: stationer med 1, 2 och 6 figurer, poängmål på olika platser.
        static ShootingCardStation S(int n, int f, params int[] p) => new(n, null, f, p.Length) { PoangSlots = p };
        static List<ShootingCardStation> Big() => new()
        {
            S(1, 3), S(2, 1, 1), S(3, 4), S(4, 6, 2, 5), S(5, 3), S(6, 2, 2), S(7, 3), S(8, 3), S(9, 3), S(10, 3, 1, 3)
        };
        static ShootingCardModel Card(int shooters, List<ShootingCardStation>? st = null) => new()
        {
            PatrolNumber = 2, StartTime = "09:42", WeaponGroupText = "A", Stations = st ?? Big(),
            Shooters = Enumerable.Range(1, shooters).Select(i => new ShootingCardShooter { Name = "Skytt " + i, Club = "Klubb", ShootingClass = "A2", Position = i }).ToList()
        };
        static int Count(string s, string needle) => (s.Length - s.Replace(needle, "").Length) / needle.Length;
        static string Doc(string layout, int shooters, Action<ShootingCardOptions>? tweak = null, List<ShootingCardStation>? st = null)
        {
            var o = new ShootingCardOptions { CompetitionName = "T", Layout = layout };
            tweak?.Invoke(o);
            return ShootingCardRenderer.RenderDocument(new[] { Card(shooters, st) }, o);
        }

        [Fact]
        public void The_registry_ids_are_a_contract()
        {
            // Id:n finns i länkar och sparade val: byt aldrig ett id.
            ShootingCardLayouts.All.Select(l => l.Id).Should().Equal("blk", "card", "fig", "tot");
            ShootingCardLayouts.Get("nope").Should().BeNull();
            ShootingCardLayouts.Default.Id.Should().Be("fig");
        }

        [Fact]
        public void Block_and_card_have_locked_paper_and_todays_layouts_keep_all_four_formats()
        {
            ShootingCardLayouts.Get("blk")!.Formats.Should().ContainSingle().Which.Should().Match<ShootingCardFormat>(f => f.Paper == "landscape" && f.PerSheet == 1);
            ShootingCardLayouts.Get("card")!.Formats.Should().ContainSingle().Which.Should().Match<ShootingCardFormat>(f => f.Paper == "portrait" && f.PerSheet == 2);
            ShootingCardLayouts.Get("fig")!.Formats.Should().HaveCount(4);
            ShootingCardLayouts.Get("tot")!.Formats.Should().HaveCount(4);
            // Papperet i länken ignoreras för de låsta layouterna.
            Doc("blk", 4, o => { o.Paper = "portrait"; o.PerSheet = 2; }).Should().Contain("@page{size:A4 landscape");
            Doc("card", 2, o => { o.Paper = "landscape"; o.PerSheet = 1; }).Should().Contain("@page{size:A4 portrait");
        }

        [Theory]
        [InlineData(1, new[] { 1 })]
        [InlineData(4, new[] { 4 })]
        [InlineData(5, new[] { 4, 1 })]
        [InlineData(6, new[] { 4, 2 })]
        [InlineData(8, new[] { 4, 4 })]
        [InlineData(9, new[] { 4, 4, 1 })]
        public void Block_fills_four_per_sheet_and_the_rest_continues(int shooters, int[] expected) =>
            ShootingCardLayouts.Get("blk")!.Split(shooters).Should().Equal(expected);

        [Fact]
        public void Card_is_one_per_shooter_and_todays_layouts_still_split_evenly()
        {
            ShootingCardLayouts.Get("card")!.Split(3).Should().Equal(1, 1, 1);
            ShootingCardLayouts.Get("fig")!.Split(13).Should().Equal(7, 6);
        }

        [Fact]
        public void Block_draws_three_rows_per_station_with_hatched_cells_for_the_missing_figures()
        {
            // En skytt med de tio stationerna: kolumner = ceil(f/3), alltså 3·ceil(f/3) − f spärrade celler per station.
            var st = Big();
            var html = Doc("blk", 1, st: st);
            var spärr = st.Sum(s => 3 * (int)Math.Ceiling(s.Figures / 3.0) - s.Figures);
            // Fyra block ritas alltid (tomma platser kan skrivas för hand), så spärrcellerna finns fyra gånger.
            Count(html, "class=\"c off\"").Should().Be(spärr * 4);
            var writable = st.Sum(s => s.Figures);
            Count(html, "<i class=\"c\"><u>").Should().Be((writable - st.Sum(s => s.PoangmalCount)) * 4);
            Count(html, "class=\"c sp\"").Should().Be(st.Sum(s => s.PoangmalCount) * 4);
        }

        [Fact]
        public void Poang_labels_are_ordinals_in_the_order_of_the_poangmal()
        {
            var html = Doc("blk", 1, st: new() { S(4, 6, 2, 5) });
            html.Should().Contain("<b class=\"pq\">P1</b>").And.Contain("<b class=\"pq\">P2</b>").And.NotContain("P3");
            // P1 sitter på figur 2 och P2 på figur 5.
            html.Should().Contain("<i class=\"c sp\"><u>2</u><b class=\"pq\">P1</b>").And.Contain("<i class=\"c sp\"><u>5</u><b class=\"pq\">P2</b>");
        }

        [Fact]
        public void Poang_on_its_own_row_gives_one_box_per_poangmal_and_hatch_for_stations_without()
        {
            var st = Big();
            var html = Doc("blk", 1, o => o.PoangRow = true, st);
            Count(html, "class=\"c p\"").Should().Be(st.Sum(s => s.PoangmalCount) * 4);
            Count(html, "class=\"c sp\"").Should().Be(0);
            // Stationer utan poängmål får en spärrad rad så att raderna går i linje: 7 stationer × 4 block utöver figurcellerna.
            var figOff = st.Sum(s => 3 * (int)Math.Ceiling(s.Figures / 3.0) - s.Figures) * 4;
            Count(html, "class=\"c off\"").Should().Be(figOff + st.Count(s => s.PoangmalCount == 0) * 4);
        }

        [Fact]
        public void The_optional_sum_is_one_box_per_station_in_a_block_and_one_column_on_a_card()
        {
            var st = Big();
            Count(Doc("blk", 1, st: st), "class=\"c sum\"").Should().Be(0);
            Count(Doc("blk", 1, o => o.SumColumn = true, st), "class=\"c sum\"").Should().Be(st.Count * 4);
            Count(Doc("card", 1, st: st), "class=\"c sum\"").Should().Be(0);
            Count(Doc("card", 1, o => o.SumColumn = true, st), "class=\"c sum\"").Should().Be(st.Count);
        }

        [Fact]
        public void Card_has_one_row_per_station_and_a_column_per_figure_of_the_largest_station()
        {
            var st = Big();
            var html = Doc("card", 1, st: st);
            var maxF = st.Max(s => s.Figures);   // 6
            Count(html, "class=\"sn\"").Should().Be(st.Count);
            Count(html, "class=\"c off\"").Should().Be(st.Sum(s => maxF - s.Figures));
            Count(html, "class=\"c sp\"").Should().Be(st.Sum(s => s.PoangmalCount));
            html.Should().Contain("repeat(6,");
        }

        [Fact]
        public void Card_with_poang_columns_adds_one_column_per_poangmal_of_the_largest_station()
        {
            var st = Big();   // flest poängmål på en station: 2
            var html = Doc("card", 1, o => o.PoangRow = true, st);
            html.Should().Contain("repeat(2,15mm)");
            Count(html, "class=\"c p\"").Should().Be(st.Sum(s => s.PoangmalCount));
            Count(html, "class=\"c sp\"").Should().Be(0);
        }

        [Fact]
        public void Names_are_pre_printed_or_left_blank_and_html_encoded()
        {
            var card = Card(2);
            card.Shooters[0].Name = "<b>Anna</b> & Co";
            var o = new ShootingCardOptions { Layout = "blk" };
            var pre = ShootingCardRenderer.RenderDocument(new[] { card }, o);
            pre.Should().Contain("&lt;b&gt;Anna&lt;/b&gt; &amp; Co").And.NotContain("<b>Anna</b>");
            o.BlankNames = true;
            var blank = ShootingCardRenderer.RenderDocument(new[] { card }, o);
            blank.Should().NotContain("Anna").And.NotContain("Skytt 2");
            Count(blank, "class=\"ln\"").Should().Be(8);   // fyra block × två skrivrader
        }

        [Fact]
        public void Colour_is_an_option_and_black_and_white_is_the_default()
        {
            Doc("blk", 4).Should().NotContain("class=\"card sk col\"");
            Doc("blk", 4, o => o.Color = true).Should().Contain("class=\"card sk col\"");
            Doc("card", 1, o => o.Color = true).Should().Contain("class=\"card kc col\"");
        }

        [Fact]
        public void The_hatch_is_vector_and_defined_once_for_the_new_layouts_only()
        {
            Count(Doc("blk", 4), "id=\"skh\"").Should().Be(1);
            Count(Doc("card", 1), "id=\"skh\"").Should().Be(1);
            Doc("fig", 4).Should().NotContain("skh");
            Doc("blk", 4).Should().NotContain("repeating-linear-gradient").And.NotContain("box-shadow:inset");
        }

        [Fact]
        public void The_fit_report_names_the_limiting_box_and_catches_a_height_that_is_too_small()
        {
            var rect = ShootingCardLayout.Cards("landscape", 1)[0];
            var blk = ShootingCardLayouts.Get("blk")!;
            var plain = blk.Fit(Big(), 4, rect, new ShootingCardOptions());
            plain.Dimensions.Select(d => d.Label).Should().Contain("Träffruta, höjd").And.Contain("Poängruta, bredd");
            // Båda extraraderna (poäng och summa) tar höjd från figurraderna.
            var crowded = blk.Fit(Big(), 4, rect, new ShootingCardOptions { PoangRow = true, SumColumn = true });
            var h = crowded.Dimensions.Single(d => d.Label == "Träffruta, höjd");
            h.Value.Should().BeLessThan(plain.Dimensions.Single(d => d.Label == "Träffruta, höjd").Value);
            crowded.Level.Should().NotBe("ok", "tre extra rader gör figurrutorna för låga");
        }

        [Fact]
        public void The_block_never_runs_past_the_page()
        {
            foreach (var (row, sum) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var p = ShootingCardGeometry.Block(Big(), ShootingCardLayout.Cards("landscape", 1)[0], row, sum);
                var used = p.HeaderH + p.HeaderGap + p.Blocks * p.BlockH + (p.Blocks - 1) * p.Gap;
                used.Should().BeLessThan(p.ContentH, $"row={row} sum={sum}");
                // Innehållet i ett block (rubrik + tre rader + extraraderna + ram) ryms i blockets höjd.
                (p.StationHeaderH + 3 * p.CellH + p.PoangRowH + p.SumRowH + 2 * ShootingCardGeometry.BlockBorder).Should().BeApproximately(p.BlockH, 0.001);
                p.Stations.Sum(s => s.Width + ShootingCardGeometry.StationDivider).Should().BeApproximately(p.ContentW - p.NameW - 2 * ShootingCardGeometry.BlockBorder, 0.01);
            }
        }

        [Fact]
        public void Preview_shows_only_the_first_sheet_without_toolbar_or_auto_print()
        {
            var cards = Enumerable.Range(0, 5).Select(i => Card(2)).ToList();
            var o = new ShootingCardOptions { Layout = "card", AutoPrint = true, Preview = true };
            var html = ShootingCardRenderer.RenderDocument(cards, o);
            Count(html, "class=\"holder\"").Should().Be(1);
            html.Should().NotContain("window.print();},350");
            html.Should().Contain(".bar,.notice{display:none!important}");
        }

        [Fact]
        public void Todays_layouts_use_the_same_fit_numbers_as_before()
        {
            var rect = ShootingCardLayout.Cards("landscape", 1)[0];
            var m = ShootingCardLayout.Measure("fig", Big(), 12, rect, false);
            var expected = ShootingCardLayout.Fit("fig", m.BoxW);
            var r = ShootingCardLayouts.Get("fig")!.Fit(Big(), 12, rect, new ShootingCardOptions());
            r.Level.Should().Be(expected.Level);
            r.Limiting.Shown.Should().Be(expected.BoxW);
        }
    }
}
