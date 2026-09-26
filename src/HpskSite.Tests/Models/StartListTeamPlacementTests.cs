using FluentAssertions;
using HpskSite.CompetitionTypes.Precision.Models;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>
    /// En plats i ett skjutlag är en SKJUTPLATS. Att flytta eller ta bort en skytt får inte
    /// flytta de andra (rapporterat från tävling 5574, 2026-09-26), och en skytt som läggs till
    /// hamnar i första lucka i stället för på "antal + 1", som kan vara upptagen.
    /// </summary>
    public class StartListTeamPlacementTests
    {
        private static StartListTeam Team(params int[] positions) => new()
        {
            TeamNumber = 1,
            Shooters = positions.Select(p => new StartListShooter { Position = p, MemberId = 100 + p, Name = "S" + p }).ToList()
        };

        [Fact]
        public void Lucka_FyllsForst()
        {
            var team = Team(1, 2, 4, 5);          // plats 3 flyttades bort
            var s = new StartListShooter { MemberId = 999 };
            team.PlaceInFirstFreePosition(s, maxPerTeam: 10);

            s.Position.Should().Be(3);
            team.Shooters!.Select(x => x.Position).Should().Equal(1, 2, 3, 4, 5);
        }

        [Fact]
        public void AndraSkyttar_FlyttasInte()
        {
            var team = Team(1, 2, 4, 5);
            team.PlaceInFirstFreePosition(new StartListShooter { MemberId = 999 }, 10);

            team.Shooters!.Single(x => x.MemberId == 104).Position.Should().Be(4);
            team.Shooters!.Single(x => x.MemberId == 105).Position.Should().Be(5);
        }

        [Fact]
        public void UtanLucka_HamnarSist()
        {
            var team = Team(1, 2, 3);
            var s = new StartListShooter { MemberId = 999 };
            team.PlaceInFirstFreePosition(s, 10);
            s.Position.Should().Be(4);
        }

        [Fact]
        public void AntalPlusEtt_AnvandsInte_NarDenArUpptagen()
        {
            // 3 skyttar på plats 1, 2, 4: "antal + 1" = 4 är upptagen.
            var team = Team(1, 2, 4);
            var s = new StartListShooter { MemberId = 999 };
            team.PlaceInFirstFreePosition(s, 10);
            s.Position.Should().Be(3);
            team.Shooters!.Select(x => x.Position).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void FulltSkjutlag_HamnarEfterHogstaPlatsen_AldrigPaUpptagen()
        {
            var team = Team(1, 2, 3);
            var s = new StartListShooter { MemberId = 999 };
            team.PlaceInFirstFreePosition(s, maxPerTeam: 3);
            s.Position.Should().Be(4);
            team.Shooters!.Select(x => x.Position).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void TomtSkjutlag_Plats1()
        {
            var team = new StartListTeam { TeamNumber = 2 };
            var s = new StartListShooter { MemberId = 999 };
            team.PlaceInFirstFreePosition(s, 10);
            s.Position.Should().Be(1);
            team.ShooterCount.Should().Be(1);
        }

        [Fact]
        public void ListanSorteras_EfterPlats()
        {
            var team = Team(1, 5);
            team.PlaceInFirstFreePosition(new StartListShooter { MemberId = 999 }, 10);
            team.Shooters!.Select(x => x.Position).Should().Equal(1, 2, 5);
        }
    }
}
