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

        /// <summary>
        /// Den publicerade nodens RÅVÄRDE (det som de gamla adressernas upplösning läser) — tävling 3514 i prod.
        /// </summary>
        [Fact]
        public void Publicerad_nods_ravarde_med_escape_kanns_igen()
        {
            var prop = new Moq.Mock<Umbraco.Cms.Core.Models.PublishedContent.IPublishedProperty>();
            prop.Setup(x => x.GetSourceValue(Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>())).Returns("[\"Svenskt M\\u00E4sterskap\"]");
            var node = new Moq.Mock<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();
            node.Setup(x => x.GetProperty("competitionScope")).Returns(prop.Object);
            CompetitionScopeHelper.ReadScope(node.Object).Should().Be(CompetitionScopeHelper.SvensktMasterskap);
        }

        [Fact]
        public void Mastarskapsreglerna_laser_samma_varde()
            => ChampionshipCategory.NormalizeScope("[\"Svenskt M\\u00E4sterskap\"]").Should().Be(CompetitionScopeHelper.SvensktMasterskap);
    }
}
