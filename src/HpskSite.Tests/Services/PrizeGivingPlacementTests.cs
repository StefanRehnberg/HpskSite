using FluentAssertions;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Läslistan på prisutdelningssidan för en tävling som inte är mästerskap (hotfix 2026-11-18, backlog P1.2 alt B):
    /// de tre bästa i varje resultatlistegrupp, utan medaljer, utan reducering och utan att namnge fel person vid lika resultat.
    /// Förväntade värden är skrivna för hand ur fixturen, inte räknade med produktionskoden.
    /// </summary>
    public class PrizeGivingPlacementTests
    {
        private static PrecisionShooterResult Shooter(int id, string name, string cls, int total, int x,
            string? status = null, bool outside = false) => new()
        {
            MemberId = id, Name = name, Club = "Klubben", ShootingClass = cls,
            ScoreOverride = total, XCountOverride = x, ParticipationStatus = status, OutOfCompetition = outside
        };

        private static PrecisionClassGroup Group(string name, params PrecisionShooterResult[] shooters) =>
            new() { ClassName = name, Shooters = shooters.ToList() };

        [Fact]
        public void Top_three_per_group_in_the_lists_own_order()
        {
            var lists = PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("A1", Shooter(1, "Anna", "A1", 290, 5), Shooter(2, "Bo", "A1", 288, 4),
                            Shooter(3, "Cia", "A1", 280, 1), Shooter(4, "Dag", "A1", 270, 0)),
                Group("A2", Shooter(5, "Eva", "A2", 250, 2))
            });

            lists.Should().HaveCount(2);
            lists[0].CategoryName.Should().Be("A1");
            lists[0].Awards.Select(a => (a.Place, a.Medal, a.Name)).Should().Equal(
                (1, "1:a", "Anna"), (2, "2:a", "Bo"), (3, "3:e", "Cia"));
            lists[0].Participants.Should().Be(4);          // alla som skjutit, inte bara de tre
            lists[0].Unresolved.Should().BeEmpty();
            lists[1].Awards.Select(a => a.Name).Should().Equal("Eva");
            lists[1].WeaponGroup.Should().Be("A");
        }

        [Fact]
        public void A_merged_group_is_one_group()
        {
            // "C2+Dam" är EN grupp i resultatlistan och därmed EN läslista, inte två.
            var lists = PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("C2+Dam", Shooter(1, "Anna", "C2", 300, 9), Shooter(2, "Bo", "C2 Dam", 299, 3), Shooter(3, "Cia", "C2", 250, 0))
            });

            lists.Should().ContainSingle();
            lists[0].CategoryName.Should().Be("C2+Dam");
            lists[0].Awards.Should().HaveCount(3);
        }

        [Fact]
        public void The_display_name_set_by_an_admin_wins()
        {
            var g = Group("B1", Shooter(1, "Anna", "B1", 100, 0));
            g.DisplayClassName = "B Allmänt";
            PrizeGivingService.BuildPlacementLists(new[] { g })[0].CategoryName.Should().Be("B Allmänt");
        }

        [Fact]
        public void Did_not_start_and_outside_shooters_are_left_out_and_places_close_up()
        {
            var lists = PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("A1", Shooter(1, "Anna", "A1", 290, 5, status: "DNS"), Shooter(2, "Bo", "A1", 280, 4),
                            Shooter(3, "Cia", "A1", 270, 3, outside: true), Shooter(4, "Dag", "A1", 260, 0))
            });

            lists[0].Awards.Select(a => (a.Medal, a.Name)).Should().Equal(("1:a", "Bo"), ("2:a", "Dag"));
            lists[0].Participants.Should().Be(2);
        }

        [Fact]
        public void A_group_with_only_non_starters_is_skipped()
        {
            PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("A3", Shooter(1, "Anna", "A3", 0, 0, status: "DNS"))
            }).Should().BeEmpty();
        }

        [Fact]
        public void A_tie_names_nobody_and_says_so()
        {
            // Bo och Cia står exakt lika på plats 2–3: ingen av dem får läsas upp som 2:a.
            var lists = PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("A1", Shooter(1, "Anna", "A1", 290, 5), Shooter(2, "Bo", "A1", 280, 4),
                            Shooter(3, "Cia", "A1", 280, 4), Shooter(4, "Dag", "A1", 270, 0))
            });

            lists[0].Awards.Select(a => a.Name).Should().Equal("Anna");
            lists[0].Unresolved.Should().ContainSingle()
                .Which.Should().Be("2:a–3:e — lika resultat mellan Bo och Cia");
        }

        [Fact]
        public void A_tie_at_the_border_names_nobody_for_the_contested_place()
        {
            // Tre står lika på 3:e plats (index 2–4): platsen är oavgjord, även om bara en av dem får en plats.
            var lists = PrizeGivingService.BuildPlacementLists(new[]
            {
                Group("A1", Shooter(1, "Anna", "A1", 290, 5), Shooter(2, "Bo", "A1", 285, 4),
                            Shooter(3, "Cia", "A1", 280, 3), Shooter(4, "Dag", "A1", 280, 3), Shooter(5, "Eli", "A1", 280, 3))
            });

            lists[0].Awards.Select(a => a.Name).Should().Equal("Anna", "Bo");
            lists[0].Unresolved.Should().ContainSingle()
                .Which.Should().Be("3:e — lika resultat mellan Cia, Dag och Eli");
        }

        [Fact]
        public void No_groups_gives_no_lists()
        {
            PrizeGivingService.BuildPlacementLists(Array.Empty<PrecisionClassGroup>()).Should().BeEmpty();
        }
    }
}
