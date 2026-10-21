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

        private static List<(string, DateTime)> One(string t, int days) => new() { (t, Today.AddDays(days)) };

        [Fact]
        public void Amnesraden_for_en_uppgift_namnger_den_och_sager_forsenat()
        {
            DueReminderRules.Subject(One("Besiktning av Änganäs", 10), Today, "Änganäs").Should().Be("Påminnelse: Besiktning av Änganäs");
            DueReminderRules.Subject(One("Besiktning av Änganäs", -1), Today, "Änganäs").Should().Be("Försenat: Besiktning av Änganäs");
        }

        [Fact]
        public void Flera_uppgifter_blir_ETT_mejl_med_antal_och_lista_i_datumordning()
        {
            var items = new List<(string, DateTime)> { ("Domänavgift", Today.AddDays(71)), ("Webbhotell", Today.AddDays(-6)), ("Mailserver", Today.AddDays(71)) };
            DueReminderRules.Subject(items, Today, "Hallands Pistolskyttekrets")
                .Should().Be("Försenat och kommande: 3 uppgifter för Hallands Pistolskyttekrets");
            var p = DueReminderRules.Paragraphs(items, Today, "årshjulet för X", false);
            p[0].Should().StartWith("Det här ska vara klart:");
            p[0].IndexOf("Webbhotell").Should().BeLessThan(p[0].IndexOf("Domänavgift"), "datumordning, det försenade först");
            p[0].Should().Contain("för 6 dagar sedan").And.Contain("om 71 dagar");
            var later = new List<(string, DateTime)> { ("A", Today.AddDays(5)), ("B", Today.AddDays(6)) };
            DueReminderRules.Subject(later, Today, "X").Should().Be("Påminnelse: 2 uppgifter för X");
        }

        [Fact]
        public void Mejlet_sager_vad_man_gor_nar_det_ar_klart()
        {
            DueReminderRules.Paragraphs(One("Bokslut", 5), Today, "x", false)[1].Should().Contain("Bocka av det i årshjulet");
            DueReminderRules.Paragraphs(One("Besiktning", 5), Today, "x", true)[1].Should().Contain("registrera det på banans sida");
        }

        [Fact]
        public void Texten_sager_nar_och_hur_manga_dagar()
        {
            DueReminderRules.WhenText(Today.AddDays(90), Today).Should().Be("senast 2027-01-19 (om 90 dagar)");
            DueReminderRules.WhenText(Today.AddDays(1), Today).Should().Contain("om 1 dag)");
            DueReminderRules.WhenText(Today, Today).Should().StartWith("i dag");
            DueReminderRules.WhenText(Today.AddDays(-5), Today).Should().Contain("för 5 dagar sedan");
            DueReminderRules.Paragraphs(One("Bokslut", 1), Today, "skjutbanan Änganäs", true)[2].Should().Contain("skjutbanan Änganäs");
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
