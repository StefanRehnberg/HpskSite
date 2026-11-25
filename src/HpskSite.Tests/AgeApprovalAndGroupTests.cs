using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 3b C: godkännandelistans tolkning och åldersgruppens beskrivning (texten medlemmen får när grinden nekar).</summary>
    public class AgeApprovalAndGroupTests
    {
        private static readonly AgeStep[] Ladder =
        {
            new("Junior", 0), new("Senior", 21), new("Veteran", 60)
        };

        [Theory]
        [InlineData("Junior", "0–20 år")]
        [InlineData("Senior", "21–59 år")]
        [InlineData("Veteran", "60 år och äldre")]
        [InlineData("veteran", "60 år och äldre")]
        public void Gruppen_beskrivs_med_sina_gränser(string key, string expected)
            => AgeTypeRules.DescribeGroup(key, Ladder).Should().Be(expected);

        [Fact]
        public void En_typ_utan_gräns_har_ingen_grupp()
            => AgeTypeRules.DescribeGroup("Stödjande", Ladder).Should().BeNull();

        [Fact]
        public void Godkännandet_tolkas_med_typen_efter_första_kolon_och_tom_typ_betyder_ingen()
        {
            var items = AgeApprovalItem.Parse(new[] { "12:Senior", "13:", "14", " 15 : x-abc " });
            items.Should().Equal(
                new AgeApprovalItem(12, "Senior"), new AgeApprovalItem(13, ""),
                new AgeApprovalItem(14, ""), new AgeApprovalItem(15, "x-abc"));
        }

        [Fact]
        public void Ogiltiga_rader_och_dubbletter_hoppas_över()
        {
            var items = AgeApprovalItem.Parse(new[] { "x:Senior", "0:Senior", "-3:Junior", "", null, "7:Senior", "7:Junior" });
            items.Should().Equal(new AgeApprovalItem(7, "Senior"));
        }

        [Fact]
        public void Inget_att_tolka_ger_tom_lista()
        {
            AgeApprovalItem.Parse(null).Should().BeEmpty();
            AgeApprovalItem.Parse(Array.Empty<string>()).Should().BeEmpty();
        }
    }
}
