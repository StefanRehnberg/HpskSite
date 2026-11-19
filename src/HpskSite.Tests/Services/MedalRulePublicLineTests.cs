using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Raden på tävlingssidan om hur medaljerna delas ut. Den visas bara när klubbens regler avviker från förbundets och funktionen är
    /// påslagen — ett vanligt mästerskap eller en tävling utan val får ingen rad, och SM/landsdel aldrig.
    /// </summary>
    public class MedalRulePublicLineTests
    {
        private static CompetitionInfo Comp(string? scope = null, string? mode = null, int places = 0, string? tie = null, string type = "Faltskytte") =>
            new() { CompetitionType = type, CompetitionScope = scope, MedalGroupingMode = mode, MedalPlaces = places, MedalTieRule = tie };

        [Fact]
        public void A_competition_outside_the_championships_with_a_choice_explains_it()
        {
            var line = MedalRulePublicLine.For(Comp(null, "class", 3, "shared"));

            line.Should().NotBeNull();
            line.Should().Contain("per klass").And.Contain("tre bästa").And.Contain("delad plats");
        }

        [Fact]
        public void A_competition_without_a_choice_has_no_line()
        {
            MedalRulePublicLine.For(Comp(null)).Should().BeNull();
        }

        [Fact]
        public void A_club_championship_with_the_federations_rules_has_no_line()
        {
            MedalRulePublicLine.For(Comp(CompetitionScopeHelper.Klubbmasterskap)).Should().BeNull();
        }

        [Fact]
        public void A_club_championship_that_deviates_gets_a_line()
        {
            MedalRulePublicLine.For(Comp(CompetitionScopeHelper.Klubbmasterskap, "weapongroup", 1))
                .Should().NotBeNull().And.Contain("vapengrupp");
            MedalRulePublicLine.For(Comp(CompetitionScopeHelper.Kretsmasterskap, null, 0, "series"))
                .Should().NotBeNull().And.Contain("serieräkningen");
        }

        [Theory]
        [InlineData("Svenskt mästerskap")]
        [InlineData("Landsdelsmästerskap")]
        public void SM_and_landsdel_never_get_a_line_even_with_a_stored_choice(string scope)
        {
            // En lagrad klubbregel på ett SM ignoreras (nivåspärren) — sidan får inte påstå något förbundet inte gör.
            var scopeValue = scope.StartsWith("Svenskt") ? CompetitionScopeHelper.SvensktMasterskap : CompetitionScopeHelper.Landsdelsmasterskap;

            MedalRulePublicLine.For(Comp(scopeValue, "class", 3)).Should().BeNull();
        }

        [Theory]
        [InlineData("Springskytte")]
        [InlineData("Skidskytte")]
        public void Disciplines_without_the_surface_have_no_line(string type)
        {
            MedalRulePublicLine.For(Comp(null, "class", 3, type: type)).Should().BeNull();
        }

        [Fact]
        public void A_missing_competition_gives_no_line()
        {
            MedalRulePublicLine.For(null).Should().BeNull();
        }

        [Fact]
        public void The_line_is_the_same_text_as_the_prize_giving_uses()
        {
            var c = Comp(null, "class", 2, "shared");

            MedalRulePublicLine.For(c).Should().Be(MedalGrouping.Describe("class", 2, "shared"));
        }
    }
}
