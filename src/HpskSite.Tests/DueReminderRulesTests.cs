using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Påminnelser 90 dagar före (2026-10-21): fönstret och texten.</summary>
    public class DueReminderRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 21);

        [Theory]
        [InlineData(91, false)]
        [InlineData(90, true)]
        [InlineData(20, true, "läggs något in med 20 dagar kvar går påminnelsen ut direkt")]
        [InlineData(0, true)]
        [InlineData(-30, true)]
        [InlineData(-31, false, "äldre historik mejlas aldrig ut")]
        public void Fonstret_ar_90_dagar_fore_till_30_dagar_efter(int days, bool expected, string because = "")
            => DueReminderRules.InWindow(Today.AddDays(days), Today).Should().Be(expected, because);

        [Fact]
        public void Amnesraden_sager_forsenat_nar_datumet_passerat()
        {
            DueReminderRules.Subject("Besiktning av Änganäs", Today.AddDays(10), Today).Should().Be("Påminnelse: Besiktning av Änganäs");
            DueReminderRules.Subject("Besiktning av Änganäs", Today.AddDays(-1), Today).Should().Be("Försenat: Besiktning av Änganäs");
        }

        [Fact]
        public void Texten_sager_nar_och_hur_manga_dagar()
        {
            DueReminderRules.Paragraphs("Bokslut", Today.AddDays(90), Today, "årshjulet för X")[0]
                .Should().Contain("senast 2027-01-19").And.Contain("om 90 dagar");
            DueReminderRules.Paragraphs("Bokslut", Today.AddDays(1), Today, "x")[0].Should().Contain("om 1 dag").And.NotContain("dagar");
            DueReminderRules.Paragraphs("Bokslut", Today, Today, "x")[0].Should().Contain("i dag");
            DueReminderRules.Paragraphs("Bokslut", Today.AddDays(-5), Today, "x")[0].Should().Contain("för 5 dagar sedan");
            DueReminderRules.Paragraphs("Bokslut", Today, Today, "skjutbanan Änganäs")[1].Should().Contain("skjutbanan Änganäs");
        }

        [Fact]
        public void Banans_sorter_far_egen_kalla_i_loggen()
        {
            DueReminderSource.ForRangeKind(RangeRequirement.Inspection).Should().Be("range-inspection");
            DueReminderSource.ForRangeKind(RangeRequirement.Permit).Should().Be("range-permit");
            DueReminderSource.ForRangeKind(RangeRequirement.Document).Should().Be("range-document");
        }
    }
}
