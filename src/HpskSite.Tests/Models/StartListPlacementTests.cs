using FluentAssertions;
using HpskSite.CompetitionTypes.Precision.Models;
using Xunit;
using static HpskSite.CompetitionTypes.Precision.Models.StartListPlacement;

namespace HpskSite.Tests.Models
{
    /// <summary>
    /// Välj skjutplats: byt plats / flytta ner fram till närmaste lucka, trasiga banor hoppas
    /// över. Önskemål från tävling 5574 (2026-09-26).
    /// </summary>
    public class StartListPlacementTests
    {
        private static StartListTeam Team(int number, params int[] positions) => new()
        {
            TeamNumber = number,
            Shooters = positions.Select(p => new StartListShooter { Position = p, MemberId = number * 100 + p, Name = $"T{number}P{p}" }).ToList()
        };

        private static StartListShooter At(StartListTeam t, int memberId) => t.Shooters!.Single(s => s.MemberId == memberId);
        private static Dictionary<int, int> Lanes(StartListTeam t) => t.Shooters!.ToDictionary(s => s.MemberId, s => s.Position);

        [Fact]
        public void LedigBana_FlyttasDirekt()
        {
            var t = Team(1, 1, 2, 3, 5);
            var r = MoveTo(t, At(t, 105), t, 4, null, 10, null);
            r.Outcome.Should().Be(Outcome.Moved);
            At(t, 105).Position.Should().Be(4);
            At(t, 101).Position.Should().Be(1);
        }

        [Fact]
        public void UpptagenBana_UtanVal_FragarOchAndrarInget()
        {
            var t = Team(1, 1, 2, 3);
            var before = Lanes(t);
            var r = MoveTo(t, At(t, 103), t, 1, null, 10, null);
            r.Outcome.Should().Be(Outcome.NeedsChoice);
            r.Occupant!.MemberId.Should().Be(101);
            Lanes(t).Should().Equal(before);
        }

        [Fact]
        public void Byt_InomSkjutlag()
        {
            var t = Team(1, 1, 2, 3);
            MoveTo(t, At(t, 103), t, 1, ModeSwap, 10, null).Outcome.Should().Be(Outcome.Moved);
            At(t, 103).Position.Should().Be(1);
            At(t, 101).Position.Should().Be(3);
            At(t, 102).Position.Should().Be(2);
        }

        [Fact]
        public void FlyttaNer_SistaTillBana1_AllaFlyttasEttSteg()
        {
            // Michaels exempel: sista skytten till bana 1, alla andra flyttas nedåt.
            var t = Team(1, 1, 2, 3, 4, 5);
            MoveTo(t, At(t, 105), t, 1, ModeShift, 10, null).Outcome.Should().Be(Outcome.Moved);
            At(t, 105).Position.Should().Be(1);
            At(t, 101).Position.Should().Be(2);
            At(t, 104).Position.Should().Be(5);
            t.Shooters!.Select(s => s.Position).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void FlyttaNer_StannarVidForstaLucka()
        {
            // Lucka på bana 3: bara skytten på bana 2 behöver flytta när någon läggs på 2.
            var t = Team(1, 1, 2, 4, 5);
            var other = Team(2, 1);
            MoveTo(other, At(other, 201), t, 2, ModeShift, 10, null).Outcome.Should().Be(Outcome.Moved);
            At(t, 102).Position.Should().Be(3);
            At(t, 104).Position.Should().Be(4);
            At(t, 105).Position.Should().Be(5);
            At(t, 201).Position.Should().Be(2);
            other.Shooters.Should().BeEmpty();
        }

        [Fact]
        public void FlyttaNer_HopparOverTrasigBana()
        {
            var t = Team(1, 1, 2, 3, 5);            // bana 4 trasig
            var broken = new HashSet<int> { 4 };
            var other = Team(2, 1);
            MoveTo(other, At(other, 201), t, 3, ModeShift, 10, broken).Outcome.Should().Be(Outcome.Moved);
            // Kedjan: bana 3 (upptagen) → hoppar trasiga 4 → bana 5 (upptagen) → bana 6 (ledig).
            At(t, 201).Position.Should().Be(3);
            At(t, 103).Position.Should().Be(5);
            At(t, 105).Position.Should().Be(6);
            At(t, 102).Position.Should().Be(2);
            t.Shooters!.Select(s => s.Position).Should().NotContain(4).And.OnlyHaveUniqueItems();
        }

        [Fact]
        public void TrasigBana_KanInteValjas()
        {
            var t = Team(1, 1, 2);
            var r = MoveTo(t, At(t, 102), t, 5, null, 10, new HashSet<int> { 5 });
            r.Outcome.Should().Be(Outcome.Refused);
            At(t, 102).Position.Should().Be(2);
        }

        [Fact]
        public void BanaUtanforSkjutlaget_Vagras()
        {
            var t = Team(1, 1);
            MoveTo(t, At(t, 101), t, 11, null, 10, null).Outcome.Should().Be(Outcome.Refused);
        }

        [Fact]
        public void Byt_MellanSkjutlag_BytarSkjutlagOchPlats()
        {
            var a = Team(1, 1, 2, 3);
            var b = Team(2, 1, 2);
            MoveTo(a, At(a, 103), b, 1, ModeSwap, 10, null).Outcome.Should().Be(Outcome.Moved);
            b.Shooters!.Single(s => s.Position == 1).MemberId.Should().Be(103);
            a.Shooters!.Single(s => s.Position == 3).MemberId.Should().Be(201);
            a.Shooters!.Should().HaveCount(3);
            b.Shooters!.Should().HaveCount(2);
        }

        [Fact]
        public void FlyttaNer_FulltSkjutlag_MellanSkjutlag_Vagras()
        {
            var a = Team(1, 1);
            var b = Team(2, 1, 2, 3);
            var before = Lanes(b);
            var r = MoveTo(a, At(a, 101), b, 2, ModeShift, 3, null);
            r.Outcome.Should().Be(Outcome.Refused);
            Lanes(b).Should().Equal(before);
            a.Shooters.Should().HaveCount(1);
        }

        [Fact]
        public void FlyttaNer_InomFulltSkjutlag_NedatIListan()
        {
            // Skytten på bana 1 läggs på bana 3 i ett fullt skjutlag: 2 och 3 flyttar upp ett steg.
            var t = Team(1, 1, 2, 3);
            MoveTo(t, At(t, 101), t, 3, ModeShift, 3, null).Outcome.Should().Be(Outcome.Moved);
            At(t, 101).Position.Should().Be(3);
            At(t, 102).Position.Should().Be(1);
            At(t, 103).Position.Should().Be(2);
        }

        [Fact]
        public void PlaceInFirstFree_HopparOverTrasigBana()
        {
            var t = Team(1, 1, 2);
            var s = new StartListShooter { MemberId = 999 };
            t.PlaceInFirstFreePosition(s, 10, new HashSet<int> { 3 });
            s.Position.Should().Be(4);
        }

        [Fact]
        public void MoveOffBrokenLanes_EfterGenerering()
        {
            var t = Team(1, 1, 2, 3, 4);
            t.MoveOffBrokenLanes(new HashSet<int> { 2 });
            t.Shooters!.Select(s => s.Position).Should().Equal(1, 3, 4, 5);
            At(t, 102).Position.Should().Be(3);   // ordningen behålls
        }

        [Fact]
        public void UsableLanes_RaknarBaraTrasigaInomSkjutlaget()
        {
            StartListTeam.UsableLanes(10, new HashSet<int> { 3, 12 }).Should().Be(9);
            StartListTeam.UsableLanes(10, null).Should().Be(10);
        }
    }
}
