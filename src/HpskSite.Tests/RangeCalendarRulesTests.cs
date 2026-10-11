using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Banans kalender (2026-10-11): vall, tid och fasta tider som rena funktioner.
    /// Krocken är en VARNING — men en varning som lyser fel slutar läsas, så reglerna prövas här.
    /// </summary>
    public class RangeCalendarRulesTests
    {
        // ---- Vall ---------------------------------------------------------------------------

        [Theory]
        [InlineData(null, null, true)]   // hela banan mot hela banan
        [InlineData(null, 3, true)]      // hela banan berör varje vall
        [InlineData(3, null, true)]
        [InlineData(3, 3, true)]
        [InlineData(3, 4, false)]        // två vallar — två klubbar kan skjuta samtidigt
        [InlineData(0, 4, true)]         // 0 läses som hela banan, aldrig som en egen vall
        public void SectionsCollide(int? a, int? b, bool expected)
            => RangeCalendarRules.SectionsCollide(a, b).Should().Be(expected);

        // ---- Tid ----------------------------------------------------------------------------

        [Theory]
        [InlineData("18:00", "20:00", "19:00", "21:00", TimeRelation.Overlap)]
        [InlineData("18:00", "20:00", "20:00", "22:00", TimeRelation.None)]   // kant i kant är ingen krock
        [InlineData("18:00", "20:00", "08:00", "12:00", TimeRelation.None)]
        [InlineData("18:00", "20:00", "18:00", null, TimeRelation.Overlap)]   // samma start
        [InlineData("18:00", "20:00", "19:30", null, TimeRelation.Overlap)]   // öppen start inne i intervallet
        [InlineData("18:00", "20:00", "21:00", null, TimeRelation.None)]      // öppen start efter
        [InlineData("19:30", null, "18:00", "20:00", TimeRelation.Overlap)]   // symmetriskt
        [InlineData("18:00", null, "19:00", null, TimeRelation.SameDay)]      // två öppna: vi vet inte
        [InlineData(null, null, "19:00", "21:00", TimeRelation.SameDay)]      // utan klockslag: samma dag
        [InlineData("18:00", "20:00", null, null, TimeRelation.SameDay)]
        public void Relate(string? aS, string? aE, string? bS, string? bE, TimeRelation expected)
            => RangeCalendarRules.Relate(aS, aE, bS, bE).Should().Be(expected);

        [Fact]
        public void Relate_hittar_inte_pa_en_langd_for_en_oppen_post()
        {
            // En post utan sluttid får inte antas pågå "en timme": 18:00 öppen mot 18:30–19:00 vet vi
            // inget om — bara om den andra STARTAR inne i ett känt intervall blir det en krock.
            RangeCalendarRules.Relate("18:00", null, "18:30", "19:00").Should().Be(TimeRelation.None);
        }

        [Theory]
        [InlineData("18:00", 1080)]
        [InlineData("8:05", 485)]
        [InlineData(" 07:30 ", 450)]
        [InlineData("", null)]
        [InlineData("kväll", null)]
        [InlineData(null, null)]
        public void Minutes(string? s, int? expected) => RangeCalendarRules.Minutes(s).Should().Be(expected);

        // ---- Fasta tider --------------------------------------------------------------------

        private static ClubRangeAllocation Slot(int id, byte iso, DateTime? from = null, DateTime? to = null) => new()
        {
            Id = id, ClubRangeLinkId = 1, DayOfWeek = iso,
            StartTime = new TimeSpan(18, 0, 0), EndTime = new TimeSpan(21, 0, 0), ValidFrom = from, ValidTo = to
        };

        [Fact]
        public void Expand_lagger_ut_tiden_pa_ratt_veckodag()
        {
            // 2026-10-12 är en måndag; tisdagar (2) och torsdagar (4) under två veckor.
            var days = RangeCalendarRules.Expand(new[] { Slot(1, 2), Slot(2, 4) }, new DateTime(2026, 10, 12), new DateTime(2026, 10, 25))
                .Select(x => (x.Date.ToString("yyyy-MM-dd"), x.Slot.Id)).ToList();
            days.Should().Equal(("2026-10-13", 1), ("2026-10-15", 2), ("2026-10-20", 1), ("2026-10-22", 2));
        }

        [Fact]
        public void Expand_soendag_ar_dag_sju()
        {
            var days = RangeCalendarRules.Expand(new[] { Slot(1, 7) }, new DateTime(2026, 10, 12), new DateTime(2026, 10, 18)).ToList();
            days.Should().ContainSingle().Which.Date.Should().Be(new DateTime(2026, 10, 18));
        }

        [Fact]
        public void Expand_respekterar_giltigheten_och_raknar_med_kantdagarna()
        {
            var slot = Slot(1, 2, from: new DateTime(2026, 10, 20), to: new DateTime(2026, 11, 3));
            var days = RangeCalendarRules.Expand(new[] { slot }, new DateTime(2026, 10, 1), new DateTime(2026, 11, 30))
                .Select(x => x.Date.ToString("yyyy-MM-dd")).ToList();
            days.Should().Equal("2026-10-20", "2026-10-27", "2026-11-03");
        }

        [Fact]
        public void Hhmm_ar_alltid_tva_siffror()
            => RangeCalendarRules.Hhmm(new TimeSpan(8, 5, 0)).Should().Be("08:05");
    }
}
