using FluentAssertions;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// En kopia av en tävling (Kopiera, +1 år) ska ta med klubbens medaljregler: det är en del av tävlingens definition, inte dess
    /// tillstånd. Den gamla boolen medalsPerWeaponGroup är däremot tillstånd och följer inte med (CompetitionFieldTable).
    /// </summary>
    public class MedalRuleCopyTests
    {
        [Fact]
        public void A_copy_carries_the_clubs_medal_rules()
        {
            var source = new CompetitionInfo
            {
                CompetitionType = "Faltskytte",
                MedalGroupingMode = "class", MedalPlaces = 3, MedalTieRule = "shared"
            };
            var copy = new CompetitionInfo();

            CompetitionFieldTable.CopyDefinitionInto(source, copy, shiftYears: 1);

            copy.MedalGroupingMode.Should().Be("class");
            copy.MedalPlaces.Should().Be(3);
            copy.MedalTieRule.Should().Be("shared");
        }

        [Fact]
        public void A_copy_without_rules_stays_without_rules()
        {
            var copy = new CompetitionInfo();

            CompetitionFieldTable.CopyDefinitionInto(new CompetitionInfo { CompetitionType = "Precision" }, copy, shiftYears: 1);

            copy.MedalGroupingMode.Should().BeNull();
            copy.MedalPlaces.Should().Be(0);
            copy.MedalTieRule.Should().BeNull();
        }
    }
}
