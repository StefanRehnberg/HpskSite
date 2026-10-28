using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Kontrollen av den frivilliga e-postadress arrangören skriver för någon utan konto (Stefan 2026-10-28).</summary>
    public class StaffEmailTests
    {
        [Theory]
        [InlineData("anna@example.se", true)]
        [InlineData("  anna.svensson@klubb.example.org ", true)]
        [InlineData("anna@example", false)]        // ingen punkt i domänen
        [InlineData("anna.example.se", false)]     // saknar @
        [InlineData("Anna <anna@example.se>", false)]   // en adress, inte ett visningsnamn
        [InlineData("", false)]
        [InlineData(null, false)]
        public void LooksLikeEmail_godtar_en_riktig_adress_och_inget_annat(string? s, bool expected)
            => StaffingService.LooksLikeEmail(s).Should().Be(expected);
    }
}
