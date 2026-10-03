using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests
{
    public class SkjutlagTimingTests
    {
        [Theory]
        [InlineData(7, 90)]   // 70 + 15 = 85 → 90
        [InlineData(6, 75)]
        [InlineData(10, 120)] // 115 → 120
        [InlineData(3, 45)]
        public void Skjutlagets_tid_avrundas_upp_till_kvart(int series, int minutes) =>
            Assert.Equal(minutes, SkjutlagTiming.DurationMinutes(series));

        [Fact] public void Finalen_borjar_nar_sista_skjutlaget_ar_klart() =>
            Assert.Equal("12:00", SkjutlagTiming.FinalsStartAfter(new[] { "09:00", "10:30", "" }, 7));

        [Fact] public void Utan_lasbar_tid_inget_forslag() =>
            Assert.Null(SkjutlagTiming.FinalsStartAfter(new[] { "", null, "kl nio" }, 7));

        [Fact] public void Samma_ordning_skjuter_finalen_direkt_efter_egen_grundomgang() =>
            Assert.Equal("10:30", SkjutlagTiming.ShiftAfterOwnQualification("09:00", 7));

        [Fact] public void Olasbar_tid_lamnas_orord() =>
            Assert.Equal("kl nio", SkjutlagTiming.ShiftAfterOwnQualification("kl nio", 7));

        [Fact] public void Intervallet_i_h_mm() =>
            Assert.Equal("0:45", SkjutlagTiming.FormatInterval(SkjutlagTiming.DurationMinutes(3)));

        [Fact] public void Klockan_gar_inte_over_midnatt() =>
            Assert.Equal("23:59", SkjutlagTiming.FormatClock(25 * 60));
    }
}
