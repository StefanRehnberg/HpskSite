using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Planera dagen, Fördela: dagen delas i block som en person kan ta före eller efter sin egen start. Bevisen är att blocken täcker dagen, inte överlappar och att passen går först.</summary>
    public class SlotSlicerTests
    {
        private static readonly DateTime D = new(2027, 6, 5);

        [Theory]
        [InlineData(180, 3)]   // 09 h / 3 h = 3 block
        [InlineData(120, 5)]   // 4,5 → 5
        [InlineData(240, 2)]
        [InlineData(480, 1)]
        [InlineData(60, 9)]
        [InlineData(10, 9)]    // under minimum räknas som 60
        [InlineData(1000, 1)]  // över max räknas som 480
        public void Dagsprogrammet_delas_i_ett_rimligt_antal_block(int blockMinutes, int expected)
        {
            SlotSlicer.Blocks(D, DayProgram.Default, blockMinutes).Should().HaveCount(expected);
        }

        [Fact]
        public void Blocken_ar_sammanhangande_tacker_hela_dagen_och_overlappar_aldrig()
        {
            for (var bm = 30; bm <= 600; bm += 15)
                foreach (var prog in new[] { DayProgram.Default, new DayProgram(TimeSpan.FromHours(7), TimeSpan.FromHours(19)), new DayProgram(TimeSpan.FromHours(9), TimeSpan.FromHours(12)) })
                {
                    var blocks = SlotSlicer.Blocks(D, prog, bm);
                    blocks.Should().NotBeEmpty();
                    blocks[0].Start.Should().Be(D + prog.Start, $"bm {bm}");
                    blocks[^1].End.Should().Be(D + prog.End, $"bm {bm}");
                    for (var i = 1; i < blocks.Count; i++) blocks[i].Start.Should().Be(blocks[i - 1].End, $"bm {bm}: kant i kant");
                    blocks.Should().OnlyContain(b => b.Length > TimeSpan.Zero && !b.AllDay);
                }
        }

        [Fact]
        public void Pass_gar_fore_dagsprogrammet_och_kommer_i_tidsordning()
        {
            var passes = new List<(TimeSpan, TimeSpan)> { (TimeSpan.FromHours(13), TimeSpan.FromHours(17)), (TimeSpan.FromHours(8), TimeSpan.FromHours(12)) };
            var blocks = SlotSlicer.Blocks(D, DayProgram.Default, 180, passes);
            blocks.Select(b => (b.Start.Hour, b.End.Hour)).Should().Equal((8, 12), (13, 17));
        }

        [Fact]
        public void Oanvandbara_pass_faller_tillbaka_pa_dagsprogrammet()
        {
            var bad = new List<(TimeSpan, TimeSpan)> { (TimeSpan.FromHours(9), TimeSpan.FromHours(9)) };   // start = slut är inget intervall
            SlotSlicer.Blocks(D, DayProgram.Default, 180, bad).Should().HaveCount(3);
        }

        [Fact]
        public void Ett_ogiltigt_dagsprogram_ger_forvalet()
        {
            SlotSlicer.Blocks(D, new DayProgram(TimeSpan.FromHours(17), TimeSpan.FromHours(8)), 180).Should().HaveCount(3);
        }
    }
}
