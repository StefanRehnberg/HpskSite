using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>
    /// Klasslistan på en anmälan finns i databasen i båda skiftlägena. Läsaren som byggde
    /// laganmälans urval gjorde GetProperty("class") och tappade tyst varje skytt vars klass
    /// bytts i startlisteredigeraren (tävling 5574, 2026-09-26).
    /// </summary>
    public class RegistrationClassIdsTests
    {
        [Fact]
        public void CamelCase_Lases()
        {
            CompetitionRegistrationDocument.ReadClassIds("[{\"class\":\"A1\",\"startPreference\":\"Inget\"}]")
                .Should().Equal("A1");
        }

        [Fact]
        public void PascalCase_Lases()
        {
            CompetitionRegistrationDocument.ReadClassIds("[{\"Class\":\"A3\",\"StartPreference\":\"Inget\",\"TeamNumber\":null}]")
                .Should().Equal("A3");
        }

        [Fact]
        public void NumerisktTeamNumber_FallerInte()
        {
            CompetitionRegistrationDocument.ReadClassIds("[{\"class\":\"R2\",\"startPreference\":\"Inget\",\"teamNumber\":3}]")
                .Should().Equal("R2");
        }

        [Fact]
        public void Strangarray_Lases()
        {
            CompetitionRegistrationDocument.ReadClassIds("[\"C-H 50\",\"A-D 21\"]")
                .Should().Equal("C-H 50", "A-D 21");
        }

        [Fact]
        public void BlandadeFormer_FlerKlasser()
        {
            CompetitionRegistrationDocument.ReadClassIds("[{\"class\":\"A1\"},{\"Class\":\"R3\"}]")
                .Should().Equal("A1", "R3");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("inte json")]
        [InlineData("{\"class\":\"A1\"}")]
        public void TomEllerTrasig_GerTomLista(string? json)
        {
            CompetitionRegistrationDocument.ReadClassIds(json).Should().BeEmpty();
        }

        [Fact]
        public void KlassbytetsSkrivvag_GerCamelCase()
        {
            var json = CompetitionRegistrationDocument.SerializeShootingClasses(
                new() { new ShootingClassEntry { Class = "A3", StartPreference = "Inget" } });
            json.Should().Contain("\"class\":\"A3\"").And.NotContain("\"Class\"");
        }
    }
}
