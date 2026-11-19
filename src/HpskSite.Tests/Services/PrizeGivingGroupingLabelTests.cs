using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Prisutdelningens text om hur medaljerna delades ut, och varningen när tävlingens val ändrats efter att listan räknades ut.
    /// Etiketten beskriver ARTEFAKTEN (listan), inte tävlingens nuvarande inställning — annars är det en tyst lögn vid prisbordet.
    /// </summary>
    public class PrizeGivingGroupingLabelTests
    {
        private static ChampionshipRule Current(string scope, string? mode = null, int places = 0, string? tie = null) =>
            ChampionshipRule.ForScope(scope, false, mode, places, tie);

        [Fact]
        public void No_medals_gives_no_text()
        {
            var (text, stale) = PrizeGivingService.BuildGroupingLabels(
                false, true, MedalGroupingModes.Class, 3, MedalTieRules.ShootOff, Current(""));

            text.Should().BeEmpty();
            stale.Should().BeNull();
        }

        [Fact]
        public void Clubs_own_rules_are_described_in_clear_text_and_not_stale_when_unchanged()
        {
            var (text, stale) = PrizeGivingService.BuildGroupingLabels(
                true, false, MedalGroupingModes.Class, 3, MedalTieRules.Shared,
                Current("", MedalGroupingModes.Class, 3, MedalTieRules.Shared));

            text.Should().Contain("per klass");
            text.Should().Contain("tre bästa");
            text.Should().Contain("delad plats");
            stale.Should().BeNull();
        }

        [Fact]
        public void Clubs_own_rules_are_described_even_when_there_is_no_weapon_group_C()
        {
            // Texten om vapengrupp C kräver att C finns; klubbens egna regler gäller alla vapengrupper.
            var (text, _) = PrizeGivingService.BuildGroupingLabels(
                true, false, MedalGroupingModes.WeaponGroup, 1, MedalTieRules.ShootOff,
                Current("", MedalGroupingModes.WeaponGroup, 1));

            text.Should().NotBeEmpty();
            text.Should().Contain("Bara den bästa");
        }

        [Fact]
        public void The_default_rules_without_group_C_say_nothing()
        {
            // Ett KM i bara vapengrupp B: texten om C:s delning beskriver något som inte finns.
            var (text, stale) = PrizeGivingService.BuildGroupingLabels(
                true, false, MedalGroupingModes.Shb, 0, MedalTieRules.ShootOff, Current(CompetitionScopeHelper.Klubbmasterskap));

            text.Should().BeEmpty();
            stale.Should().BeNull();
        }

        [Fact]
        public void The_default_rules_with_group_C_are_described_as_before()
        {
            var (text, _) = PrizeGivingService.BuildGroupingLabels(
                true, true, MedalGroupingModes.Shb, 0, MedalTieRules.ShootOff, Current(CompetitionScopeHelper.Klubbmasterskap));

            text.Should().Be(MedalGrouping.Describe(false));
        }

        [Fact]
        public void A_changed_rule_after_the_list_was_computed_names_both_and_points_to_the_result_tab()
        {
            // Listan räknades per klass med tre platser; tävlingen är nu satt till per vapengrupp, bara guld.
            var (text, stale) = PrizeGivingService.BuildGroupingLabels(
                true, true, MedalGroupingModes.Class, 3, MedalTieRules.ShootOff,
                Current("", MedalGroupingModes.WeaponGroup, 1));

            text.Should().Contain("per klass");
            stale.Should().NotBeNull();
            stale.Should().Contain("per klass").And.Contain("vapengrupp").And.Contain("Räkna om listan");
        }

        [Fact]
        public void A_changed_tie_rule_alone_also_marks_the_list_stale()
        {
            var (_, stale) = PrizeGivingService.BuildGroupingLabels(
                true, true, MedalGroupingModes.Class, 3, MedalTieRules.ShootOff,
                Current("", MedalGroupingModes.Class, 3, MedalTieRules.Series));

            stale.Should().NotBeNull();
        }
    }
}
