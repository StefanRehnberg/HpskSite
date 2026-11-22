using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    public class FaltskytteConfigCompatibilityTests
    {
        private const string Falt = "{\"A\":{\"stations\":[]},\"B\":{\"stations\":[]},\"C\":{\"stations\":[]},\"R\":{\"stations\":[]},\"_scoringMode\":\"Normal\"}";
        private const string Mag = "{\"M\":{\"stations\":[]},\"_scoringMode\":\"Normal\"}";
        private const string Mixed = "{\"A\":{\"stations\":[]},\"M\":{\"stations\":[]}}";

        [Fact]
        public void Vapengrupper_ignorerar_metanycklar() =>
            FaltskytteConfigCompatibility.WeaponGroupsOf(Falt).Should().Equal("A", "B", "C", "R");

        [Fact]
        public void Vapengrupper_klarar_den_inslagna_formen() =>
            FaltskytteConfigCompatibility.WeaponGroupsOf("{\"WeaponConfigs\":{\"M\":{},\"A\":{}}}").Should().Equal("M", "A");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("inte json")]
        [InlineData("[1,2]")]
        public void Trasig_eller_tom_blob_ger_inga_grupper(string? blob) =>
            FaltskytteConfigCompatibility.WeaponGroupsOf(blob).Should().BeEmpty();

        [Fact]
        public void Magnumtavling_med_konfiguration_utan_M_far_ett_problem()
        {
            var p = FaltskytteConfigCompatibility.Problems(Falt, "MagnumFalt");
            p.Should().Contain(x => x.Contains("saknar vapengrupp M"));
            p.Should().Contain(x => x.Contains("A, B, C, R"));
        }

        [Fact]
        public void Magnumtavling_med_bara_M_ar_i_ordning() =>
            FaltskytteConfigCompatibility.Problems(Mag, "MagnumFalt").Should().BeEmpty();

        [Fact]
        public void Vanlig_falt_med_vanlig_konfiguration_ar_i_ordning() =>
            FaltskytteConfigCompatibility.Problems(Falt, "Faltskytte").Should().BeEmpty();

        [Fact]
        public void Vanlig_falt_med_M_far_en_varning() =>
            FaltskytteConfigCompatibility.Problems(Mixed, "Faltskytte").Should().ContainSingle(x => x.Contains("vapengrupp M"));

        [Theory]
        [InlineData("Precision")]
        [InlineData("Springskytte")]
        [InlineData(null)]
        public void Andra_grenar_kontrolleras_inte(string? type) =>
            FaltskytteConfigCompatibility.Problems(Falt, type).Should().BeEmpty();

        [Fact]
        public void Tom_konfiguration_sager_det() =>
            FaltskytteConfigCompatibility.Problems("{}", "Faltskytte").Should().Contain(x => x.Contains("inga vapengrupper"));

        [Fact]
        public void Typen_matchas_utan_hansyn_till_versaler() =>
            FaltskytteConfigCompatibility.Problems(Falt, "magnumfalt").Should().NotBeEmpty();
    }
}
