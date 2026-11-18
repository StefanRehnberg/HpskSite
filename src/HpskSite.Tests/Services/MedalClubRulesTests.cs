using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Klubbens egna regler för medaljer (backlog P1.2, plan v2 S1): per klass (resultatlistans grupper), fast antal platser och
    /// delad plats vid lika resultat. Förväntade värden är skrivna för hand, inte räknade med produktionskoden.
    /// </summary>
    public class MedalClubRulesTests
    {
        // ── Per klass följer resultatlistans grupper ─────────────────────────────

        [Fact]
        public void Per_class_without_merges_gives_every_class_its_own_group()
        {
            var map = MedalGroupMap.PerClass();

            map.GroupFor("A1").Should().Be("A1");
            map.GroupFor("A2").Should().Be("A2");
            map.GroupFor("C1 Dam").Should().Be("C1 Dam");
            map.GroupFor("C_Vet_Y").Should().Be(HpskSite.Models.ShootingClasses.ToCanonicalName("C_Vet_Y"));
            map.GroupFor("").Should().Be("");
        }

        [Fact]
        public void A_merged_class_is_one_group_with_the_class_it_was_merged_into()
        {
            // Resultatlistan visar "C2+Dam" som EN grupp: båda klasserna tävlar om samma medaljer.
            var merges = new Dictionary<string, string> { ["C2"] = "C2+Dam", ["C2 Dam"] = "C2+Dam" };
            var map = MedalGroupMap.PerClass(merges);

            map.GroupFor("C2").Should().Be("C2+Dam");
            map.GroupFor("C2 Dam").Should().Be("C2+Dam");
            map.GroupFor("C3").Should().Be("C3");   // en klass utan sammanslagning är sin egen grupp
        }

        [Fact]
        public void Per_class_is_chosen_by_the_rule_and_the_other_modes_ignore_the_merges()
        {
            var merges = new Dictionary<string, string> { ["C2"] = "C2+Dam" };

            var perClass = ChampionshipRule.ForScope("Klubbmästerskap", false, "class");
            MedalGroupMap.For(perClass, merges).GroupFor("C2").Should().Be("C2+Dam");

            // SHB och per vapengrupp är kategorier, inte resultatlistans grupper: sammanslagningen ändrar dem inte.
            var shb = ChampionshipRule.ForScope("Klubbmästerskap", false);
            MedalGroupMap.For(shb, merges).GroupFor("C2").Should().Be("C");
            var weaponGroup = ChampionshipRule.ForScope("Klubbmästerskap", true);
            MedalGroupMap.For(weaponGroup, merges).GroupFor("C2").Should().Be("C");
        }

        [Fact]
        public void The_lookup_for_per_class_follows_the_merges()
        {
            var merges = new Dictionary<string, string> { ["C2"] = "C2+Dam" };
            var lookup = MedalGroupMap.PerClass(merges).BuildLookup(new[] { "C2", "C3", "A1" });

            lookup["C2"].Should().Be("C2+Dam");
            lookup["C3"].Should().Be("C3");
            lookup["A1"].Should().Be("A1");
        }

        // ── Grinden: vem delar ut placeringsmedaljer ─────────────────────────────

        [Theory]
        [InlineData("Svenskt Mästerskap")]
        [InlineData("Landsdelsmästerskap")]
        [InlineData("Kretsmästerskap")]
        [InlineData("Klubbmästerskap")]
        public void Every_championship_awards_placement_medals_with_or_without_a_stored_choice(string scope)
        {
            ChampionshipRule.ForScope(scope).AwardsPlacementMedals.Should().BeTrue();
            ChampionshipRule.ForScope(scope, false, "class", 3, "shared").AwardsPlacementMedals.Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("Ingen")]
        public void A_competition_without_championship_awards_nothing_until_the_organiser_has_chosen(string scope)
        {
            // Ingen markering, inget lagrat läge: som förr. En gammal bool utan läge betyder inte "klubben har valt".
            ChampionshipRule.ForScope(scope).AwardsPlacementMedals.Should().BeFalse();
            ChampionshipRule.ForScope(scope, storedMedalsPerWeaponGroup: true).AwardsPlacementMedals.Should().BeFalse();
        }

        [Theory]
        [InlineData("shb")]
        [InlineData("weapongroup")]
        [InlineData("class")]
        public void A_competition_without_championship_awards_medals_once_a_mode_is_chosen(string mode)
        {
            ChampionshipRule.ForScope("", false, mode).AwardsPlacementMedals.Should().BeTrue();
        }

        [Fact]
        public void The_clubs_rules_take_effect_without_a_championship_scope()
        {
            var rule = ChampionshipRule.For(new HpskSite.Models.Competitions.CompetitionInfo
            {
                CompetitionScope = null, MedalGroupingMode = "class", MedalPlaces = 3, MedalTieRule = "shared"
            });

            rule.IsChampionship.Should().BeFalse();            // mästerskapets egna regler (Utanför mästerskapet m.m.) gäller inte
            rule.OutOfCompetitionApplies.Should().BeFalse();
            rule.AwardsPlacementMedals.Should().BeTrue();
            rule.GroupingMode.Should().Be(MedalGroupingKind.Class);
            rule.MedalPlaces.Should().Be(3);
            rule.TieRule.Should().Be(MedalTieRules.Shared);
        }

        [Theory]
        [InlineData("Landsdelsmästerskap")]
        [InlineData("Svenskt Mästerskap")]
        public void The_level_check_still_holds_at_sm_and_landsdel(string scope)
        {
            ChampionshipRule.ForScope(scope).MayChooseMedalRules.Should().BeFalse();
            ChampionshipRule.ForScope("Klubbmästerskap").MayChooseMedalRules.Should().BeTrue();
            ChampionshipRule.ForScope("").MayChooseMedalRules.Should().BeTrue();
        }

        // ── Fast antal platser ───────────────────────────────────────────────────

        [Theory]
        [InlineData(10, 3, 3, "Guld, Silver, Brons")]
        [InlineData(5, 3, 3, "Guld, Silver, Brons")]
        [InlineData(3, 3, 3, "Guld, Silver, Brons")]
        [InlineData(2, 3, 2, "Guld + Silver")]     // färre än platser: bara de som skjutit
        [InlineData(1, 3, 1, "Guld")]
        [InlineData(0, 3, 0, "Inga medaljer")]
        [InlineData(10, 1, 1, "Guld")]
        [InlineData(10, 2, 2, "Guld + Silver")]
        [InlineData(4, 2, 2, "Guld + Silver")]
        public void Fixed_places_give_the_best_n_never_more_than_have_shot(int participants, int places, int expectedMedals, string expectedText)
        {
            ChampionshipMedalCount.Fixed(participants, places).Should().Be((expectedMedals, expectedText));
            ChampionshipMedalCount.For(participants, false, places: places).Should().Be((expectedMedals, expectedText));
        }

        [Theory]
        [InlineData(4, "Guld + Silver")]   // SHB: fyra deltagare ger guld + silver
        [InlineData(3, "Enbart Guld")]     // SHB: tre ger bara guld — INTE klubbens regel
        [InlineData(2, "Inga medaljer")]
        public void Places_zero_is_the_shb_reduction_unchanged(int participants, string expectedText)
        {
            ChampionshipMedalCount.For(participants, false, places: 0).Text.Should().Be(expectedText);
        }

        [Fact]
        public void Fixed_places_do_not_use_the_junior_rule()
        {
            // Juniorregeln (SHB) och klubbens fasta antal ger samma siffra men ska inte vara samma kodväg: 1 deltagare, 3 platser.
            ChampionshipMedalCount.For(1, true, places: 3).Should().Be((1, "Guld"));
            ChampionshipMedalCount.For(7, true, places: 2).Should().Be((2, "Guld + Silver"));   // SHB-juniorregeln hade gett tre
        }

        // ── Delad plats ──────────────────────────────────────────────────────────

        [Fact]
        public void Shared_place_gives_equal_shooters_the_best_place_in_the_run()
        {
            var ordered = new List<(int, int)> { (290, 5), (285, 4), (285, 4), (280, 3), (280, 3), (270, 0) };

            Enumerable.Range(0, ordered.Count)
                .Select(i => ChampionshipMedalCount.SharedPlace(ordered, i))
                .Should().Equal(1, 2, 2, 4, 4, 6);
        }

        [Fact]
        public void Shared_place_distinguishes_on_the_secondary_figure()
        {
            // Lika poäng men olika innertior är inte lika: andrahandstalet avgör.
            var ordered = new List<(int, int)> { (285, 5), (285, 4), (285, 4) };

            Enumerable.Range(0, 3).Select(i => ChampionshipMedalCount.SharedPlace(ordered, i)).Should().Equal(1, 2, 2);
        }

        // ── Klartexten ───────────────────────────────────────────────────────────

        [Fact]
        public void The_shb_defaults_are_described_exactly_as_before()
        {
            MedalGrouping.Describe("shb", 0, "shootoff").Should().Be(MedalGrouping.Describe(false));
            MedalGrouping.Describe("weapongroup", 0, null).Should().Be(MedalGrouping.Describe(true));
            MedalGrouping.Describe(null, 0, null).Should().Be(MedalGrouping.Describe(false));
        }

        [Fact]
        public void The_clubs_own_rules_are_described_in_plain_swedish()
        {
            var text = MedalGrouping.Describe("class", 3, "shared");

            text.Should().Contain("per klass");
            text.Should().Contain("De tre bästa i varje grupp får medalj.");
            text.Should().Contain("Lika resultat ger delad plats.");
            MedalGrouping.Describe("class", 1, "series").Should().Contain("Bara den bästa").And.Contain("serieräkningen");
        }
    }
}
