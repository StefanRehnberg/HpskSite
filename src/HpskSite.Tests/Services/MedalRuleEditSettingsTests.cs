using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Vad som får sparas som klubbens medaljregler och när en bekräftelse krävs (plan v2, S1). Sparvägen (SetMedalGrouping) och de här
    /// testerna prövar samma kod, <see cref="MedalRuleEditSettings"/>.
    /// </summary>
    public class MedalRuleEditSettingsTests
    {
        private const string Club = "Klubbmästerskap";

        [Fact]
        public void Nothing_sent_is_nothing_to_refuse()
        {
            MedalRuleEditSettings.Refusal("Svenskt Mästerskap", null, null, null, enabled: false).Should().BeNull();
        }

        [Theory]
        [InlineData("klass")]
        [InlineData("SHB-klasser")]
        public void An_unknown_mode_is_refused_and_named(string mode)
        {
            MedalRuleEditSettings.Refusal(Club, mode, null, null, enabled: true)
                .Should().Contain("Okänd medaljindelning").And.Contain(mode);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(99)]
        public void Places_outside_zero_to_three_are_refused(int places)
        {
            MedalRuleEditSettings.Refusal(Club, null, places, null, enabled: true).Should().Contain("0–3");
        }

        [Fact]
        public void An_unknown_tie_rule_is_refused()
        {
            MedalRuleEditSettings.Refusal(Club, null, null, "lottning", enabled: true).Should().Contain("Okänd regel för lika resultat");
        }

        [Theory]
        [InlineData("class", null, null)]
        [InlineData(null, 3, null)]
        [InlineData(null, null, "shared")]
        [InlineData(null, null, "series")]
        public void The_clubs_own_rules_are_refused_while_the_setting_is_off(string? mode, int? places, string? tie)
        {
            MedalRuleEditSettings.Refusal(Club, mode, places, tie, enabled: false).Should().Contain("inte påslagna");
            MedalRuleEditSettings.Refusal(Club, mode, places, tie, enabled: true).Should().BeNull();
        }

        [Theory]
        [InlineData("shb")]
        [InlineData("weapongroup")]
        [InlineData("")]
        public void The_old_choices_need_no_setting(string mode)
        {
            // SHB och "per vapengrupp" är redan idag valbara för klubb och krets — de kräver inte den nya inställningen.
            MedalRuleEditSettings.Refusal(Club, mode, null, null, enabled: false).Should().BeNull();
            MedalRuleEditSettings.Refusal(Club, null, 0, "shootoff", enabled: false).Should().BeNull();
        }

        [Theory]
        [InlineData("Svenskt Mästerskap")]
        [InlineData("Landsdelsmästerskap")]
        public void The_level_check_refuses_every_value_at_sm_and_landsdel(string scope)
        {
            MedalRuleEditSettings.Refusal(scope, "class", null, null, enabled: true).Should().Contain("landsdels- och svenskt mästerskap");
            MedalRuleEditSettings.Refusal(scope, "weapongroup", null, null, enabled: true).Should().Contain("landsdels- och svenskt mästerskap");
            MedalRuleEditSettings.Refusal(scope, null, 3, null, enabled: true).Should().Contain("landsdels- och svenskt mästerskap");
        }

        [Theory]
        [InlineData("Klubbmästerskap")]
        [InlineData("Kretsmästerskap")]
        [InlineData("")]            // klubbens egna regler (backlog P1.2): en tävling utan mästerskapsomfattning får välja
        [InlineData("Ingen")]
        public void Club_and_region_championships_and_non_championships_may_choose(string scope)
        {
            MedalRuleEditSettings.Refusal(scope, "class", 3, "shared", enabled: true).Should().BeNull();
        }

        // ── Bekräftelsen ────────────────────────────────────────────────────────

        [Fact]
        public void No_confirmation_when_nothing_is_published_shot_off_or_customised()
        {
            MedalRuleEditSettings.ConfirmationNeeded(false, 0, false).Should().BeNull();
        }

        [Fact]
        public void Each_reason_is_named_and_they_add_up()
        {
            MedalRuleEditSettings.ConfirmationNeeded(true, 0, false).Should().Contain("publicerad");
            MedalRuleEditSettings.ConfirmationNeeded(false, 4, false).Should().Contain("4 noterade särskjutningsrundor");
            MedalRuleEditSettings.ConfirmationNeeded(false, 0, true).Should().Contain("Hederspriserna");

            var all = MedalRuleEditSettings.ConfirmationNeeded(true, 2, true)!;
            all.Should().Contain("publicerad").And.Contain("2 noterade").And.Contain("Hederspriserna");
        }

        // ── Fältpåsen skriver aldrig reglerna ────────────────────────────────────

        [Fact]
        public void The_form_bag_never_writes_the_medal_rules()
        {
            var target = new CompetitionInfo();
            var result = CompetitionFieldBag.Apply(target, new Dictionary<string, object>
            {
                ["medalGroupingMode"] = "class", ["medalPlaces"] = 3, ["medalTieRule"] = "shared", ["venue"] = "Banan"
            });

            target.MedalGroupingMode.Should().BeNull();
            target.MedalPlaces.Should().Be(0);
            target.MedalTieRule.Should().BeNull();
            target.Venue.Should().Be("Banan");   // övriga fält skrivs som vanligt
            result.Ignored.Should().BeEquivalentTo(new[] { "medalGroupingMode", "medalPlaces", "medalTieRule" });
        }
    }
}
