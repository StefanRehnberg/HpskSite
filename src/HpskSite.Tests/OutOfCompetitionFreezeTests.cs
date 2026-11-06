using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using HpskSite.Models.Competitions;
using HpskSite.Models.Registrations;
using HpskSite.Services.Registrations;
using Moq;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Stefan 2026-11-06: (1) när en officiell lista publiceras eller räknas om FRYSES vem som tävlar — varje klassrad utan beslut får ett
    /// ("utanför" där det finns ett förslag, annars "tävlar"); (2) bara arrangörens BESLUT visas publikt och live (förslag syns bara för
    /// funktionärer). Ett test som inte kan falla är värdelöst: varje påstående här faller om motsvarande rad i
    /// <see cref="OutOfCompetitionFlags"/> byts mot det gamla beteendet (stämpla bara "utanför"; läs förslag som beslut).
    /// </summary>
    public class OutOfCompetitionFreezeTests
    {
        private static CompetitionInfo Comp(string? mode = "on", string? participants = "own") => new()
        {
            Id = 42, CompetitionType = "Faltskytte", CompetitionScope = CompetitionScopeHelper.Kretsmasterskap,
            ChampionshipMode = mode, ChampionshipParticipants = participants
        };

        private static RegistrationRecord RegFor(int memberId, int clubId, params (string cls, bool? decision)[] classes) => new()
        {
            Id = memberId * 10, CompetitionId = 42, MemberId = memberId, IsActive = true, ClubId = clubId,
            Classes = classes.Select(c => new ShootingClassEntry { Class = c.cls, OutOfCompetition = c.decision }).ToList()
        };

        // Klubbar: 1 = Varberg (Halland, värd), 3 = Luleå (Norrbotten, annan krets → förslag "utanför").
        private static (OutOfCompetitionFlags flags, Mock<ICompetitionRegistrationStore> store) Setup(params RegistrationRecord[] regs)
        {
            var store = new Mock<ICompetitionRegistrationStore>();
            store.Setup(s => s.List(42)).Returns(regs);
            store.Setup(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            store.Setup(s => s.StampInCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var regions = new Mock<IClubRegionLookup>();
            regions.Setup(r => r.OriginOf(It.IsAny<CompetitionInfo>())).Returns(new CompetitionOrigin(1, "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(1)).Returns(new ClubOrigin(1, "Varbergs PK", "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(3)).Returns(new ClubOrigin(3, "Luleå PK", "Norrbotten", "Norrbottens krets", "Nord"));
            var memberClubs = new Mock<IMemberClubsLookup>();
            memberClubs.Setup(m => m.ClubsOf(It.IsAny<IEnumerable<int>>())).Returns(new Dictionary<int, IReadOnlyList<int>>());
            return (new OutOfCompetitionFlags(store.Object, regions.Object, memberClubs.Object), store);
        }

        [Fact]
        public void Freezing_gives_every_undecided_row_a_decision_and_never_touches_an_existing_one()
        {
            var c = Comp();
            // 7: annan krets utan beslut → "utanför". 9: egen krets utan beslut → "tävlar". 8: arrangören sa "tävlar" → rörs inte.
            // 10: arrangören sa "utanför" → rörs inte.
            var (flags, store) = Setup(
                RegFor(7, 3, ("C2", null)), RegFor(8, 3, ("C2", false)), RegFor(9, 1, ("C2", null)), RegFor(10, 3, ("C2", true)));

            var (outside, inside) = flags.FreezeDecisions(c, ChampionshipRule.For(c));

            Assert.Equal(1, outside);
            Assert.Equal(1, inside);
            store.Verify(s => s.StampOutOfCompetition(42, 70, "C2", It.IsAny<string>()), Times.Once);
            store.Verify(s => s.StampInCompetition(42, 90, "C2", It.IsAny<string>()), Times.Once);
            store.Verify(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            store.Verify(s => s.StampInCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void Freezing_without_a_participants_setting_still_freezes_everyone_as_competing()
        {
            // Ingen "vilka deltar" → inga förslag, men en senare ändring av inställningen får ändå inte flytta någon: alla frysas som "tävlar".
            var c = Comp(participants: null);
            var (flags, store) = Setup(RegFor(7, 3, ("C2", null)), RegFor(9, 1, ("C2", null), ("C1", null)));

            var (outside, inside) = flags.FreezeDecisions(c, ChampionshipRule.For(c));

            Assert.Equal(0, outside);
            Assert.Equal(3, inside);
            store.Verify(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("off")]
        public void Freezing_does_nothing_when_the_behaviour_does_not_apply(string? mode)
        {
            var c = Comp(mode);
            var (flags, store) = Setup(RegFor(7, 3, ("C2", null)));

            var (outside, inside) = flags.FreezeDecisions(c, ChampionshipRule.For(c));

            Assert.Equal((0, 0), (outside, inside));
            store.Verify(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            store.Verify(s => s.StampInCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Only_decisions_are_public_while_staff_still_see_the_suggestion()
        {
            var c = Comp();
            // 7: förslag utan beslut. 10: beslut "utanför". 8: beslut "tävlar" trots att hen är från annan krets.
            var (flags, _) = Setup(RegFor(7, 3, ("C2", null)), RegFor(10, 3, ("C2", true)), RegFor(8, 3, ("C2", false)));
            var rule = ChampionshipRule.For(c);

            var decided = flags.LoadDecided(c, rule);
            Assert.False(decided.Contains(7, "C2"));    // förslaget visas inte publikt
            Assert.True(decided.Contains(10, "C2"));    // beslutet gör det
            Assert.False(decided.Contains(8, "C2"));

            var effective = flags.Load(c, rule);         // funktionärernas bild: förslaget räknas
            Assert.True(effective.Contains(7, "C2"));
            Assert.True(effective.Contains(10, "C2"));
            Assert.False(effective.Contains(8, "C2"));
        }

        [Fact]
        public void Decided_set_is_empty_when_the_behaviour_does_not_apply()
        {
            var c = Comp(mode: null);
            var (flags, store) = Setup(RegFor(10, 3, ("C2", true)));

            Assert.False(flags.LoadDecided(c, ChampionshipRule.For(c)).Contains(10, "C2"));
            store.Verify(s => s.List(It.IsAny<int>()), Times.Never);   // inget läses alls
        }
    }
}
