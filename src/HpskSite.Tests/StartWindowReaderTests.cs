using System;
using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Planera dagen steg 1: tolkningen av tider och datum i startlistornas JSON (precisionens skjutlag).</summary>
    public class StartWindowReaderTests
    {
        [Theory]
        [InlineData("09:00", 9, 0)]
        [InlineData("9:05", 9, 5)]
        [InlineData(" 13:30 ", 13, 30)]
        [InlineData("00:00", 0, 0)]
        public void Klockslag_med_och_utan_inledande_nolla_tolkas(string s, int h, int m)
            => StartWindowReader.ParseTime(s).Should().Be(new TimeSpan(h, m, 0));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("25:00")]
        [InlineData("9.00")]
        [InlineData("fm")]
        [InlineData("24:00")]
        public void Okanda_eller_felaktiga_tider_ar_okanda_aldrig_gissade(string? s)
            => StartWindowReader.ParseTime(s).Should().BeNull();

        [Fact]
        public void Datum_ar_aaaa_mm_dd_och_inget_annat()
        {
            StartWindowReader.ParseDate("2027-06-05").Should().Be(new DateTime(2027, 6, 5));
            StartWindowReader.ParseDate("5 juni").Should().BeNull();
            StartWindowReader.ParseDate("2027-6-5").Should().BeNull();
            StartWindowReader.ParseDate("").Should().BeNull();
            StartWindowReader.ParseDate(null).Should().BeNull();
        }
    }
}
