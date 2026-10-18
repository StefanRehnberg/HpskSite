using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Omfattningen läses i alla lagrade former — även JSON med escapade tecken, som tävling 3514 i prod
    /// (<c>["Svenskt Mästerskap"]</c>), som förut inte kändes igen som SM och därför saknade adress.
    /// </summary>
    public class CompetitionScopeNormalizeTests
    {
        [Theory]
        [InlineData("Svenskt Mästerskap", "Svenskt Mästerskap")]
        [InlineData("[\"Svenskt Mästerskap\"]", "Svenskt Mästerskap")]
        [InlineData("[\"Svenskt M\\u00E4sterskap\"]", "Svenskt Mästerskap")]
        [InlineData("\"Kretsm\\u00E4sterskap\"", "Kretsmästerskap")]
        [InlineData("[]", "")]
        [InlineData("[Kretsmästerskap]", "Kretsmästerskap")] // inte giltig JSON: skalas av som förut
        [InlineData("  ", "")]
        [InlineData(null, "")]
        public void Omfattningen_lases(string? stored, string expected)
            => CompetitionScopeHelper.NormalizeScopeValue(stored).Should().Be(expected);

        [Fact]
        public void Mastarskapsreglerna_laser_samma_varde()
            => ChampionshipCategory.NormalizeScope("[\"Svenskt M\\u00E4sterskap\"]").Should().Be(CompetitionScopeHelper.SvensktMasterskap);
    }
}
