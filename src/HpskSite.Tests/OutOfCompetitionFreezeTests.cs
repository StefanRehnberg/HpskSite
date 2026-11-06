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
    /// Stefan 2026-11-06: när en officiell lista publiceras eller räknas om FRYSES vem som tävlar — varje klassrad utan beslut får ett
    /// ("utanför" där det finns ett förslag, annars "tävlar"). Förslaget visas redan live som utanför (det följer av arrangörens
    /// "vilka deltar"), så det som fryses är det som visades. Ett test som inte kan falla är värdelöst: påståendena här faller om
    /// motsvarande rad i <see cref="OutOfCompetitionFlags"/> byts mot det gamla beteendet (stämpla bara "utanför"; ignorera förslag).
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
        public void A_suggestion_already_counts_as_outside_in_live_views_and_a_decision_overrides_it()
        {
            var c = Comp();
            // 7: förslag utan beslut (annan krets). 10: beslut "utanför". 8: beslut "tävlar" trots annan krets. 9: egen krets.
            var (flags, _) = Setup(RegFor(7, 3, ("C2", null)), RegFor(10, 3, ("C2", true)), RegFor(8, 3, ("C2", false)), RegFor(9, 1, ("C2", null)));

            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.Contains(7, "C2"));     // förslaget visas som utanför redan i den preliminära listan och live
            Assert.True(set.Contains(10, "C2"));
            Assert.False(set.Contains(8, "C2"));    // arrangörens beslut "tävlar" vinner över förslaget
            Assert.False(set.Contains(9, "C2"));
        }

        [Fact]
        public void Freezing_stamps_exactly_what_the_live_list_already_showed()
        {
            // Det som frysts vid publicering får inte skilja sig från det som visades live: "utanför" för just de i gällande mängd.
            var c = Comp();
            var (flags, store) = Setup(RegFor(7, 3, ("C2", null)), RegFor(9, 1, ("C2", null)));
            var live = flags.Load(c, ChampionshipRule.For(c));

            flags.FreezeDecisions(c, ChampionshipRule.For(c));

            Assert.True(live.Contains(7, "C2"));
            Assert.False(live.Contains(9, "C2"));
            store.Verify(s => s.StampOutOfCompetition(42, 70, "C2", It.IsAny<string>()), Times.Once);   // 7 var utanför live → fryses utanför
            store.Verify(s => s.StampInCompetition(42, 90, "C2", It.IsAny<string>()), Times.Once);       // 9 tävlade live → fryses som tävlar
        }

        [Fact]
        public void Without_removes_one_shooter_in_one_class_and_leaves_the_original_set_untouched()
        {
            // Rätta listan, "flytta tillbaka in": förhandsvisningen räknar listan med skytten borttagen ur mängden.
            var c = Comp();
            var (flags, _) = Setup(RegFor(10, 3, ("C2", true), ("C1", true)));
            var set = flags.Load(c, ChampionshipRule.For(c));

            var less = set.Without(10, "C2");

            Assert.False(less.Contains(10, "C2"));
            Assert.True(less.Contains(10, "C1"));       // bara den klassen
            Assert.True(set.Contains(10, "C2"));        // originalmängden rörs aldrig
            Assert.True(less.With(10, "C2").Contains(10, "C2"));
        }
    }
}
