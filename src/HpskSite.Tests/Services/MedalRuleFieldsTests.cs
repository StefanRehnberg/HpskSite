using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// De tre medaljfälten på tävlingen (add-competition-medal-rules.sql): arrangörens indelning, antal platser och hur oavgjort avgörs.
    /// Kolumnerna har CHECK-villkor, så ett värde utanför dem ska bli "som idag" i sättaren och aldrig nå databasen.
    /// </summary>
    public class MedalRuleFieldsTests
    {
        [Theory]
        [InlineData("shb", "shb")]
        [InlineData("weapongroup", "weapongroup")]
        [InlineData("class", "class")]
        [InlineData(" Class ", "class")]
        [InlineData("WEAPONGROUP", "weapongroup")]
        [InlineData("klass", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void The_grouping_mode_is_normalised_or_dropped(string? input, string? expected)
        {
            MedalGroupingModes.Normalize(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("shootoff", "shootoff")]
        [InlineData("Shared", "shared")]
        [InlineData(" series", "series")]
        [InlineData("lottning", null)]
        [InlineData(null, null)]
        public void The_tie_rule_is_normalised_or_dropped(string? input, string? expected)
        {
            MedalTieRules.Normalize(input).Should().Be(expected);
        }

        [Fact]
        public void The_field_setters_keep_the_values_inside_the_database_checks()
        {
            var c = new CompetitionInfo();
            CompetitionFieldTable.ForAlias("medalGroupingMode")!.Set(c, "bogus");
            CompetitionFieldTable.ForAlias("medalTieRule")!.Set(c, "bogus");
            CompetitionFieldTable.ForAlias("medalPlaces")!.Set(c, 99);

            c.MedalGroupingMode.Should().BeNull();
            c.MedalTieRule.Should().BeNull();
            c.MedalPlaces.Should().Be(3);   // CHECK 0..3: klämd, inte ett 500

            CompetitionFieldTable.ForAlias("medalPlaces")!.Set(c, -4);
            c.MedalPlaces.Should().Be(0);
        }

        [Fact]
        public void A_copy_carries_the_rules_of_the_club_but_not_the_old_bool()
        {
            // Klubbens regler gäller år efter år: de tre nya fälten är Definition och följer med en kopia.
            // Den gamla boolen är State och följer inte med (oförändrat), så en kopia läser läget ur det nya fältet.
            var source = new CompetitionInfo
            {
                MedalGroupingMode = "class", MedalPlaces = 3, MedalTieRule = "shared", MedalsPerWeaponGroup = true
            };

            var copy = CompetitionFieldTable.CopyDefinition(source);

            copy.MedalGroupingMode.Should().Be("class");
            copy.MedalPlaces.Should().Be(3);
            copy.MedalTieRule.Should().Be("shared");
            copy.MedalsPerWeaponGroup.Should().BeFalse();
        }

        [Fact]
        public void The_new_fields_exist_in_the_field_table_as_definition()
        {
            foreach (var alias in new[] { "medalGroupingMode", "medalPlaces", "medalTieRule" })
            {
                var f = CompetitionFieldTable.ForAlias(alias);
                f.Should().NotBeNull(alias);
                f!.Kind.Should().Be(CompetitionFieldKind.Definition, alias);
            }
        }
    }
}
