using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S4/S5: <see cref="ChampionshipRule.OutOfCompetitionApplies"/> och <see cref="ChampionshipRule.AppliesTo(string?)"/>.
    /// Beteendet gäller ALLTID när tävlingen är ett mästerskap och grenen är fält/MagnumFält — det finns ingen växel per tävling
    /// (Stefan 2026-11-06). Är något av de två falskt gäller alla grupper och ingen flagga — exakt dagens beteende.
    /// </summary>
    public class ChampionshipRuleOutOfCompetitionTests
    {
        private static CompetitionInfo Falt(string? scope = CompetitionScopeHelper.Kretsmasterskap,
            string? groups = "C", string type = "Faltskytte") =>
            new() { CompetitionType = type, CompetitionScope = scope, ChampionshipWeaponGroups = groups };

        [Fact]
        public void On_a_field_championship_the_behaviour_always_applies()
        {
            Assert.True(ChampionshipRule.For(Falt()).OutOfCompetitionApplies);
            Assert.True(ChampionshipRule.For(Falt(type: "MagnumFalt")).OutOfCompetitionApplies);
            // Alla mästerskapsnivåer, utan någon inställning på tävlingen alls.
            foreach (var scope in new[] { CompetitionScopeHelper.Klubbmasterskap, CompetitionScopeHelper.Kretsmasterskap,
                                          CompetitionScopeHelper.Landsdelsmasterskap, CompetitionScopeHelper.SvensktMasterskap })
                Assert.True(ChampionshipRule.For(Falt(scope: scope, groups: null)).OutOfCompetitionApplies);
        }

        [Fact]
        public void With_no_weapon_group_list_every_group_decides_the_championship()
        {
            var rule = ChampionshipRule.For(Falt(groups: null));
            Assert.True(rule.OutOfCompetitionApplies);
            Assert.True(rule.AppliesTo("A"));
            Assert.True(rule.AppliesTo("R"));
        }

        [Theory]
        [InlineData("Magnum Fält")]     // fri text som sajten känner igen (CompetitionTypes.GetFuzzy)
        [InlineData("Faltskytte")]
        [InlineData("MagnumFalt")]
        public void Legacy_spellings_of_the_field_types_count_as_field(string type)
        {
            Assert.True(ChampionshipRule.For(Falt(type: type)).OutOfCompetitionApplies);
        }

        [Theory]
        [InlineData("Precision")]
        [InlineData("Springskytte")]
        [InlineData("Milsnabb")]
        [InlineData("Fältskjutning")]   // lagrat så på pilotkandidaten 7352 i prod-dumpen: sajten känner inte igen värdet som någon gren (ingen Stationer-flik heller) — ett DATAFEL att rätta, inte något regeln ska gissa
        public void Other_disciplines_still_do_not(string type)
        {
            Assert.False(ChampionshipRule.For(Falt(type: type)).OutOfCompetitionApplies);
        }

        [Fact]
        public void Not_a_championship_or_not_field_means_the_list_and_flags_do_nothing()
        {
            Assert.False(ChampionshipRule.For(Falt(scope: null)).OutOfCompetitionApplies);
            Assert.False(ChampionshipRule.For(Falt(scope: "Nationell")).OutOfCompetitionApplies);
            Assert.False(ChampionshipRule.For(Falt(type: "Precision")).OutOfCompetitionApplies);
            Assert.False(ChampionshipRule.For(Falt(type: "Springskytte")).OutOfCompetitionApplies);
            Assert.True(ChampionshipRule.For(Falt(scope: null, type: "Precision")).AppliesTo("A"));
        }

        [Fact]
        public void A_weapon_group_list_limits_which_groups_decide_the_championship()
        {
            var rule = ChampionshipRule.For(Falt(groups: "C"));
            Assert.True(rule.AppliesTo("C"));
            Assert.False(rule.AppliesTo("A"));
            Assert.False(rule.AppliesTo("R"));
            Assert.True(rule.AppliesTo(" c "));
        }

        [Fact]
        public void An_empty_list_means_all_groups()
        {
            var rule = ChampionshipRule.For(Falt(groups: null));
            Assert.True(rule.AppliesTo("A"));
            Assert.True(rule.AppliesTo("C"));
            Assert.True(rule.AppliesTo("R"));
        }

        [Fact]
        public void A_class_resolves_to_its_weapon_group()
        {
            var rule = ChampionshipRule.For(Falt(groups: "C"));
            Assert.True(rule.AppliesToClass("C_Vet_Y"));   // Id-formen
            Assert.True(rule.AppliesToClass("C Vet Y"));   // visningsnamnet
            Assert.False(rule.AppliesToClass("A2"));
            Assert.False(rule.AppliesToClass("R3"));
        }

        [Fact]
        public void The_standard_medal_C_split_follows_the_list_only_when_a_list_is_set()
        {
            // Utan lista, eller med C i listan: som förut (SM och landsdel delar C).
            Assert.True(ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Landsdelsmasterskap, groups: null)).SplitsStandardMedalGroupC);
            Assert.True(ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Landsdelsmasterskap, groups: "A,C")).SplitsStandardMedalGroupC);
            // Mästerskap bara i A/B: C avgör inget mästerskap, så C delas inte för standardmedaljerna.
            Assert.False(ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Landsdelsmasterskap, groups: "A,B")).SplitsStandardMedalGroupC);
            // En gren som inte är fält ignorerar listan: som förut.
            Assert.True(ChampionshipRule.For(Falt(type: "Precision", scope: CompetitionScopeHelper.Landsdelsmasterskap, groups: "A,B")).SplitsStandardMedalGroupC);
            // Krets och klubb delade aldrig C för standardmedaljerna.
            Assert.False(ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Kretsmasterskap, groups: "C")).SplitsStandardMedalGroupC);
        }

        [Fact]
        public void Kind_names_the_championship_level_and_nothing_else_is_a_championship()
        {
            Assert.Equal(ChampionshipScopeKind.Sm, ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.SvensktMasterskap)).Kind);
            Assert.Equal(ChampionshipScopeKind.Landsdel, ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Landsdelsmasterskap)).Kind);
            Assert.Equal(ChampionshipScopeKind.Krets, ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Kretsmasterskap)).Kind);
            Assert.Equal(ChampionshipScopeKind.Klubb, ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.Klubbmasterskap)).Kind);
            Assert.Equal(ChampionshipScopeKind.None, ChampionshipRule.For(Falt(scope: "Nationell")).Kind);
            Assert.Equal(ChampionshipScopeKind.None, ChampionshipRule.For(Falt(scope: null)).Kind);
            // En JSON-array av äldre data normaliseras som förut.
            Assert.Equal(ChampionshipScopeKind.Krets, ChampionshipRule.For(Falt(scope: "[\"" + CompetitionScopeHelper.Kretsmasterskap + "\"]")).Kind);
        }

        [Fact]
        public void The_existing_rule_answers_stay_the_same()
        {
            // S2:s svar rörs inte av de nya fälten.
            var rule = ChampionshipRule.For(Falt(scope: CompetitionScopeHelper.SvensktMasterskap, groups: "C"));
            Assert.True(rule.IsChampionship);
            Assert.True(rule.IsSmOrLandsdel);
            Assert.True(rule.SplitsStandardMedalGroupC);
        }
    }
}
