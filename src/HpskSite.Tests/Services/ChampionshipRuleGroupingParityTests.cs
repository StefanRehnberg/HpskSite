using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Steg S0 (plan v2): regeln läser det nya fältet <c>medalGroupingMode</c> men ska svara EXAKT som förr för varje tävling som saknar det.
    /// Paritetstestet jämför de gamla statiska formlerna (<see cref="MedalGrouping.PerWeaponGroup(string?, bool)"/>,
    /// <see cref="ChampionshipCategory.SplitsGroupC"/>) mot den nya regeln över alla omfattningar och båda värden på boolen, innan något anrop byts.
    /// </summary>
    public class ChampionshipRuleGroupingParityTests
    {
        public static IEnumerable<object[]> Scopes() => new[]
        {
            new object[] { "Klubbmästerskap" }, new object[] { "Kretsmästerskap" }, new object[] { "Landsdelsmästerskap" },
            new object[] { "Svenskt Mästerskap" }, new object[] { "" }, new object[] { "Ingen" }, new object[] { "[\"Klubbmästerskap\"]" },
        };

        [Theory]
        [MemberData(nameof(Scopes))]
        public void Without_the_new_field_the_rule_answers_as_before(string scope)
        {
            foreach (var stored in new[] { false, true })
            {
                var rule = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalsPerWeaponGroup = stored });

                rule.MedalsPerWeaponGroup.Should().Be(MedalGrouping.PerWeaponGroup(scope, stored), $"{scope} / bool={stored}");
                rule.SplitsChampionshipClassC.Should().Be(ChampionshipCategory.SplitsGroupC(scope, stored), $"{scope} / bool={stored}");
                // ForScope (prisutdelningen, ur artefaktens bool) svarar likadant.
                ChampionshipRule.ForScope(scope, stored).MedalsPerWeaponGroup.Should().Be(rule.MedalsPerWeaponGroup);
                ChampionshipRule.ForScope(scope, stored).SplitsChampionshipClassC.Should().Be(rule.SplitsChampionshipClassC);
            }
        }

        [Theory]
        [InlineData("Klubbmästerskap")]
        [InlineData("Kretsmästerskap")]
        [InlineData("Landsdelsmästerskap")]
        [InlineData("Svenskt Mästerskap")]
        [InlineData("[\"Klubbmästerskap\"]")]
        public void The_backfilled_mode_equals_the_old_bool(string scope)
        {
            // Migreringen sätter weapongroup där boolen var sann OCH tävlingen är ett klubb- eller kretsmästerskap (där boolen gällde; utanför
            // mästerskap lämnas läget tomt, eftersom ett lagrat läge där betyder "klubben har valt"). Regeln ska då svara som boolen gjorde.
            var viaBool = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalsPerWeaponGroup = true });
            var viaMode = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalsPerWeaponGroup = true, MedalGroupingMode = "weapongroup" });

            viaMode.MedalsPerWeaponGroup.Should().Be(viaBool.MedalsPerWeaponGroup);
            viaMode.SplitsChampionshipClassC.Should().Be(viaBool.SplitsChampionshipClassC);
        }

        [Fact]
        public void The_new_field_wins_over_the_old_bool_where_the_organiser_may_choose()
        {
            var rule = ChampionshipRule.For(new CompetitionInfo
            {
                CompetitionScope = "Klubbmästerskap", MedalsPerWeaponGroup = true, MedalGroupingMode = "shb"
            });

            rule.GroupingMode.Should().Be(MedalGroupingKind.Shb);
            rule.MedalsPerWeaponGroup.Should().BeFalse();
            rule.SplitsChampionshipClassC.Should().BeTrue();
        }

        [Theory]
        [InlineData("Landsdelsmästerskap")]
        [InlineData("Svenskt Mästerskap")]
        public void A_stored_mode_never_applies_at_sm_and_landsdel(string scope)
        {
            // Nivåspärren: ett värde som blivit kvar från när tävlingen var något annat slår aldrig igenom på SM och landsdel.
            ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalGroupingMode = "weapongroup" })
                .GroupingMode.Should().Be(MedalGroupingKind.Shb);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Ingen")]
        public void The_old_bool_means_nothing_without_a_championship_but_a_stored_mode_does(string scope)
        {
            // Boolen hade aldrig effekt utanför klubb- och kretsmästerskap och får inte få det nu (8 tävlingar i rehearsal har den kvar).
            ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalsPerWeaponGroup = true })
                .GroupingMode.Should().Be(MedalGroupingKind.Shb);
            // Ett uttryckligt läge är däremot klubbens val.
            ChampionshipRule.For(new CompetitionInfo { CompetitionScope = scope, MedalGroupingMode = "weapongroup" })
                .GroupingMode.Should().Be(MedalGroupingKind.WeaponGroup);
        }

        [Fact]
        public void Class_applies_where_the_organiser_may_choose_and_is_neither_shb_nor_per_weapon_group()
        {
            // S1: per klass gäller (klubb/krets). Det är varken SHB:s indelning eller "per vapengrupp" — och C delas inte i mästerskapsklasser.
            var rule = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = "Klubbmästerskap", MedalGroupingMode = "class" });

            rule.StoredGroupingMode.Should().Be("class");
            rule.GroupingMode.Should().Be(MedalGroupingKind.Class);
            rule.GroupingModeName.Should().Be("class");
            rule.MedalsPerWeaponGroup.Should().BeFalse();
            rule.SplitsChampionshipClassC.Should().BeFalse();
        }

        [Theory]
        [InlineData("Landsdelsmästerskap")]
        [InlineData("Svenskt Mästerskap")]
        public void Class_places_and_tie_rule_never_apply_at_sm_or_landsdel(string scope)
        {
            var rule = ChampionshipRule.For(new CompetitionInfo
            {
                CompetitionScope = scope, MedalGroupingMode = "class", MedalPlaces = 3, MedalTieRule = "shared"
            });

            rule.GroupingMode.Should().Be(MedalGroupingKind.Shb);
            rule.MedalPlaces.Should().Be(0);
            rule.TieRule.Should().Be(MedalTieRules.ShootOff);
        }

        [Fact]
        public void Places_and_tie_rule_apply_at_club_and_region_level_and_default_to_the_old_behaviour()
        {
            var none = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = "Kretsmästerskap" });
            none.MedalPlaces.Should().Be(0);
            none.TieRule.Should().Be(MedalTieRules.ShootOff);

            var set = ChampionshipRule.For(new CompetitionInfo { CompetitionScope = "Kretsmästerskap", MedalPlaces = 2, MedalTieRule = "series" });
            set.MedalPlaces.Should().Be(2);
            set.TieRule.Should().Be(MedalTieRules.Series);

            // En artefakts egna värden (prisutdelningen läser dem därifrån):
            var fromArtifact = ChampionshipRule.ForScope("Klubbmästerskap", false, "class", 3, "shared");
            fromArtifact.GroupingMode.Should().Be(MedalGroupingKind.Class);
            fromArtifact.MedalPlaces.Should().Be(3);
            fromArtifact.TieRule.Should().Be(MedalTieRules.Shared);
        }
    }
}
