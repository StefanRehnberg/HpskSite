using FluentAssertions;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    public class CompetitionHubWindowTests
    {
        private static readonly DateTime Today = new(2026, 10, 19);

        private static CompetitionInfo C(DateTime? start, DateTime? end = null) =>
            new() { Id = 1, Name = "x", CompetitionDate = start, CompetitionEndDate = end };

        [Fact]
        public void Fonstret_borjar_15_manader_bakat() =>
            CompetitionHubWindow.From(Today).Should().Be(new DateTime(2025, 7, 19));

        [Fact]
        public void Kommande_visas() => CompetitionHubWindow.InWindow(C(Today.AddDays(30)), Today).Should().BeTrue();

        [Fact]
        public void Precis_pa_gransen_visas() =>
            CompetitionHubWindow.InWindow(C(new DateTime(2025, 7, 19)), Today).Should().BeTrue();

        [Fact]
        public void En_dag_fore_gransen_visas_inte() =>
            CompetitionHubWindow.InWindow(C(new DateTime(2025, 7, 18)), Today).Should().BeFalse();

        [Fact]
        public void Forra_arets_motsvarande_tavling_i_juni_visas() =>
            CompetitionHubWindow.InWindow(C(new DateTime(2025, 9, 6)), Today).Should().BeTrue("ett KM i september i fjol ska synas när årets går i oktober");

        [Fact]
        public void En_tavling_som_borjade_fore_men_slutade_inom_fonstret_visas() =>
            CompetitionHubWindow.InWindow(C(new DateTime(2025, 6, 1), new DateTime(2025, 8, 31)), Today).Should().BeTrue("sista tävlingsdagen avgör");

        [Fact]
        public void Utan_datum_visas_alltid() => CompetitionHubWindow.InWindow(C(null), Today).Should().BeTrue();

        [Fact]
        public void Aret_ar_startdatumets_ar()
        {
            CompetitionHubWindow.InYear(C(new DateTime(2024, 12, 30), new DateTime(2025, 1, 2)), 2024).Should().BeTrue();
            CompetitionHubWindow.InYear(C(new DateTime(2024, 12, 30), new DateTime(2025, 1, 2)), 2025).Should().BeFalse();
            CompetitionHubWindow.InYear(C(null), 2025).Should().BeFalse();
        }

        [Theory]
        [InlineData("2025", 2025)]
        [InlineData("1989", null)]
        [InlineData("abc", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Aret_i_adressen(string? value, int? expected) => CompetitionHubWindow.ParseYear(value).Should().Be(expected);

        [Fact]
        public void Sokningen_kraver_alla_ord_i_valfri_ordning()
        {
            const string text = "Kretsmästerskap fält Halland 2025";
            CompetitionHubWindow.Matches(text, "fält halland").Should().BeTrue();
            CompetitionHubWindow.Matches(text, "halland fält").Should().BeTrue();
            CompetitionHubWindow.Matches(text, "KRETSMÄSTERSKAP").Should().BeTrue();
            CompetitionHubWindow.Matches(text, "fält skåne").Should().BeFalse();
            CompetitionHubWindow.Matches(text, "  ").Should().BeFalse();
        }
    }
}
