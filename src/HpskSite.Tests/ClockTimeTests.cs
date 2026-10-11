using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klockslag utan kolon (2026-10-11). Samma fall finns i wwwroot/js/time-input.js — ändras en
    /// regel här, ändra den där.
    /// </summary>
    public class ClockTimeTests
    {
        [Theory]
        [InlineData("11", "11:00")]
        [InlineData("9", "09:00")]
        [InlineData("0", "00:00")]
        [InlineData("930", "09:30")]
        [InlineData("1130", "11:30")]
        [InlineData("0905", "09:05")]
        [InlineData("11:30", "11:30")]
        [InlineData("11.30", "11:30")]
        [InlineData("11,30", "11:30")]
        [InlineData("11 30", "11:30")]
        [InlineData("9:05", "09:05")]
        [InlineData(" 18:00 ", "18:00")]
        [InlineData("23:59", "23:59")]
        public void Godtas(string input, string expected)
        {
            ClockTime.TryNormalize(input, out var t).Should().BeTrue();
            t.Should().Be(expected);
        }

        [Theory]
        [InlineData("24")]
        [InlineData("2400")]
        [InlineData("11:60")]
        [InlineData("11:5")]      // 11:05 eller 11:50? Hellre en fråga än en gissning.
        [InlineData("12345")]
        [InlineData("elva")]
        [InlineData("11:30:00")]
        [InlineData("-1")]
        public void Avvisas(string input)
            => ClockTime.TryNormalize(input, out _).Should().BeFalse();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Tomt_ar_inget_klockslag_och_giltigt(string? input)
        {
            ClockTime.TryNormalize(input, out var t).Should().BeTrue();
            t.Should().BeNull();
        }

        [Fact]
        public void Elva_ar_elva_klockan_inte_elva_dagar()
            => ClockTime.ToTimeSpan("11").Should().Be(new TimeSpan(11, 0, 0));
    }
}
