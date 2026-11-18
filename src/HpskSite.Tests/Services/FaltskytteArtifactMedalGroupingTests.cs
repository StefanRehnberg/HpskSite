using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Fältets resultatartefakt ska bära medaljindelningen den räknades i, som precisionens gör. Annars jämför prisutdelningen
    /// artefaktens (alltid falska) läge mot tävlingens och larmar om en indelning som aldrig ändrats.
    /// </summary>
    public class FaltskytteArtifactMedalGroupingTests
    {
        private static bool BuiltMode(string scope, bool stored)
        {
            var competition = new CompetitionInfo
            {
                CompetitionType = "Faltskytte",
                CompetitionScope = scope,
                MedalsPerWeaponGroup = stored
            };
            var build = new FaltskytteResultsBuildResult
            {
                Results = new FaltskylteFinalResults { CompetitionId = 1 }
            };
            return FaltskytteResultArtifactService.BuildArtifact(competition, build).MedalsPerWeaponGroup;
        }

        [Theory]
        [InlineData("Klubbmästerskap", true, true)]
        [InlineData("Kretsmästerskap", true, true)]
        [InlineData("Klubbmästerskap", false, false)]
        [InlineData("Landsdelsmästerskap", true, false)]   // nivåspärren: ett lagrat värde slår aldrig igenom på ett LM
        [InlineData("Svenskt Mästerskap", true, false)]    // eller på ett SM
        [InlineData("", true, false)]                      // och inte utan mästerskap alls
        public void The_artifact_carries_the_grouping_that_applies(string scope, bool stored, bool expected)
        {
            BuiltMode(scope, stored).Should().Be(expected);
        }
    }
}
