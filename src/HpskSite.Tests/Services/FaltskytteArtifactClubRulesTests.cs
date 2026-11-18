using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Klubbens egna medaljregler i fältets artefakt (plan v2, S1): fast antal platser, delad plats och att artefakten bär reglerna.
    /// Fältets särskjutning, kategoriställningen och uppdelningen i grupper byggs i FaltskytteResultsBuilder och bevisas mot rehearsal
    /// (golden master + en tävling med reglerna påslagna); här provas medaljutdelningen som ren funktion av ställningen.
    /// Förväntade värden är skrivna för hand.
    /// </summary>
    public class FaltskytteArtifactClubRulesTests
    {
        private static FaltskytteShooterResult Shooter(int id, string name, int hits, int figures) => new()
        {
            MemberId = id, Name = name, Club = "K", ShootingClass = "A1", TotalHits = hits, TotalFigures = figures
        };

        private static HpskSite.CompetitionTypes.Precision.Models.PrecisionFinalResults Build(
            CompetitionInfo competition, int participants, params FaltskytteShooterResult[] ordered)
        {
            var build = new FaltskytteResultsBuildResult
            {
                Results = new FaltskylteFinalResults { CompetitionId = 1, ScoringMode = "Normal" },
                CategoryStandings = new List<FaltskytteCategoryStanding>
                {
                    new() { CategoryName = "A1", WeaponGroup = "A", Participants = participants, Ordered = ordered.ToList() }
                }
            };
            return FaltskytteResultArtifactService.BuildArtifact(competition, build);
        }

        private static CompetitionInfo Club(int places = 0, string? tie = null, string? mode = null) => new()
        {
            CompetitionType = "Faltskytte", CompetitionScope = "Klubbmästerskap",
            MedalPlaces = places, MedalTieRule = tie, MedalGroupingMode = mode
        };

        private static readonly FaltskytteShooterResult[] Four =
        {
            Shooter(1, "Anna", 30, 10), Shooter(2, "Bo", 28, 9), Shooter(3, "Cia", 25, 8), Shooter(4, "Dag", 20, 6)
        };

        [Fact]
        public void Without_club_rules_the_shb_reduction_applies_as_before()
        {
            // SHB C.3.4.1: fyra deltagare ger guld + silver.
            var award = Build(Club(), 4, Four).MedalAwards.Single();

            award.MedalCount.Should().Be(2);
            award.Awards.Select(a => a.Medal).Should().Equal("Guld", "Silver");
        }

        [Fact]
        public void Fixed_places_give_the_best_three_regardless_of_the_shb_reduction()
        {
            // Klubbens regel: de tre bästa även med fyra deltagare (SHB hade gett två).
            var award = Build(Club(places: 3), 4, Four).MedalAwards.Single();

            award.MedalCount.Should().Be(3);
            award.Awards.Select(a => (a.Medal, a.Name)).Should().Equal(("Guld", "Anna"), ("Silver", "Bo"), ("Brons", "Cia"));
        }

        [Fact]
        public void Fixed_places_never_exceed_the_number_of_shooters_and_can_be_fewer_than_three()
        {
            Build(Club(places: 3), 2, Four.Take(2).ToArray()).MedalAwards.Single().Awards.Should().HaveCount(2);
            Build(Club(places: 1), 4, Four).MedalAwards.Single().Awards.Select(a => a.Name).Should().Equal("Anna");
        }

        [Fact]
        public void Shared_place_gives_equal_shooters_the_same_medal_and_the_next_place_is_skipped()
        {
            var tied = new[]
            {
                Shooter(1, "Anna", 30, 10), Shooter(2, "Bo", 28, 9), Shooter(3, "Cia", 28, 9), Shooter(4, "Dag", 20, 6)
            };

            var award = Build(Club(places: 3, tie: "shared"), 4, tied).MedalAwards.Single();

            // 1, 2, 2 — och brons delas inte ut, eftersom nästa plats är fjärde.
            award.Awards.Select(a => (a.Place, a.Medal, a.Name)).Should().Equal(
                (1, "Guld", "Anna"), (2, "Silver", "Bo"), (2, "Silver", "Cia"));
            award.Unresolved.Should().BeEmpty();
        }

        [Fact]
        public void Without_shared_place_a_tie_is_not_shared_in_the_award_loop()
        {
            // Samma ställning utan regeln: platsen är positionen (särskjutningen avgör i byggaren, inte här).
            var tied = new[] { Shooter(1, "Anna", 30, 10), Shooter(2, "Bo", 28, 9), Shooter(3, "Cia", 28, 9) };

            Build(Club(places: 3), 3, tied).MedalAwards.Single().Awards.Select(a => a.Place).Should().Equal(1, 2, 3);
        }

        [Fact]
        public void The_artifact_carries_the_rules_only_when_they_apply()
        {
            var plain = Build(Club(), 4, Four);
            plain.MedalGroupingMode.Should().BeNull();
            plain.MedalPlaces.Should().BeNull();
            plain.MedalTieRule.Should().BeNull();

            var custom = Build(Club(places: 3, tie: "shared", mode: "class"), 4, Four);
            custom.MedalGroupingMode.Should().Be("class");
            custom.MedalPlaces.Should().Be(3);
            custom.MedalTieRule.Should().Be("shared");
        }

        [Fact]
        public void At_sm_the_clubs_rules_do_not_apply_and_are_not_written()
        {
            var sm = new CompetitionInfo
            {
                CompetitionType = "Faltskytte", CompetitionScope = "Svenskt Mästerskap",
                MedalPlaces = 3, MedalTieRule = "shared", MedalGroupingMode = "class"
            };

            var artifact = Build(sm, 4, Four);

            artifact.MedalAwards.Single().MedalCount.Should().Be(2);   // SHB:s reducering gäller
            artifact.MedalGroupingMode.Should().BeNull();
            artifact.MedalPlaces.Should().BeNull();
            artifact.MedalTieRule.Should().BeNull();
        }
    }
}
