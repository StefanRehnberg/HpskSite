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
    /// S6: <see cref="OutOfCompetitionFlags"/> är EN läsare av vilka som skjuter utanför mästerskapet. Tom när beteendet inte gäller tävlingen
    /// (då läses inga anmälningar alls), och bara ett beslut på en klass som mästerskapet omfattar räknas.
    /// </summary>
    public class OutOfCompetitionFlagsTests
    {
        // type = grenen; en annan gren än fält gör att beteendet inte gäller tävlingen.
        private static CompetitionInfo Comp(string type = "Faltskytte", string? groups = null) => new()
        {
            Id = 42, CompetitionType = type, CompetitionScope = CompetitionScopeHelper.Kretsmasterskap,
            ChampionshipWeaponGroups = groups
        };

        private static RegistrationRecord Reg(int memberId, bool active, params (string cls, bool? outside)[] classes) => new()
        {
            Id = memberId * 10, CompetitionId = 42, MemberId = memberId, IsActive = active,
            Classes = classes.Select(c => new ShootingClassEntry { Class = c.cls, OutOfCompetition = c.outside }).ToList()
        };

        private static (OutOfCompetitionFlags flags, Mock<ICompetitionRegistrationStore> store) Flags(params RegistrationRecord[] regs)
        {
            var store = new Mock<ICompetitionRegistrationStore>();
            store.Setup(s => s.List(42)).Returns(regs);
            // Utan "vilka deltar" läses varken kretsar eller medlemmars klubbar (Mock utan uppsättning ger tomt svar).
            return (new OutOfCompetitionFlags(store.Object, new Mock<IClubRegionLookup>().Object, new Mock<IMemberClubsLookup>().Object), store);
        }

        // Klubbar: 1 = Varberg (Halland, värd), 3 = Luleå (Norrbotten).
        private static OutOfCompetitionFlags FlagsWithRegions(params RegistrationRecord[] regs)
        {
            var store = new Mock<ICompetitionRegistrationStore>();
            store.Setup(s => s.List(42)).Returns(regs);
            var regions = new Mock<IClubRegionLookup>();
            regions.Setup(r => r.OriginOf(It.IsAny<CompetitionInfo>())).Returns(new CompetitionOrigin(1, "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(1)).Returns(new ClubOrigin(1, "Varbergs PK", "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(3)).Returns(new ClubOrigin(3, "Luleå PK", "Norrbotten", "Norrbottens krets", "Nord"));
            var memberClubs = new Mock<IMemberClubsLookup>();
            memberClubs.Setup(m => m.ClubsOf(It.IsAny<IEnumerable<int>>())).Returns(new Dictionary<int, IReadOnlyList<int>>());
            return new OutOfCompetitionFlags(store.Object, regions.Object, memberClubs.Object);
        }

        private static RegistrationRecord RegFor(int memberId, int clubId, params (string cls, bool? outside)[] classes)
        {
            var r = Reg(memberId, true, classes);
            r.ClubId = clubId;
            return r;
        }

        [Fact]
        public void The_effective_state_is_the_decision_and_otherwise_the_suggestion()
        {
            var c = Comp();
            c.ChampionshipParticipants = "own";
            // 7: tävlar för Luleå, inget beslut → föreslås utanför. 8: samma klubb men arrangören sa "i mästerskapet". 9: Varberg → inget.
            var flags = FlagsWithRegions(RegFor(7, 3, ("C2", null)), RegFor(8, 3, ("C2", false)), RegFor(9, 1, ("C2", null)));
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.Contains(7, "C2"));    // förslaget gäller
            Assert.False(set.Contains(8, "C2"));   // beslutet vinner över förslaget
            Assert.False(set.Contains(9, "C2"));   // ingen anledning
        }

        [Fact]
        public void A_decision_outside_counts_even_when_the_suggestion_says_nothing()
        {
            var c = Comp();
            c.ChampionshipParticipants = "own";
            var flags = FlagsWithRegions(RegFor(9, 1, ("C2", true)));
            Assert.True(flags.Load(c, ChampionshipRule.For(c)).Contains(9, "C2"));
        }

        [Fact]
        public void Without_participants_setting_there_are_no_suggestions()
        {
            var c = Comp();   // ChampionshipParticipants = null → alla deltar
            var flags = FlagsWithRegions(RegFor(7, 3, ("C2", null)));
            Assert.False(flags.Load(c, ChampionshipRule.For(c)).Contains(7, "C2"));
        }

        [Fact]
        public void The_states_expose_decision_and_suggestion_separately_for_the_screens()
        {
            var c = Comp();
            c.ChampionshipParticipants = "own";
            var flags = FlagsWithRegions(RegFor(7, 3, ("C2", null)));
            var s = Assert.Single(flags.States(c, ChampionshipRule.For(c)));
            Assert.Null(s.Decision);
            Assert.NotNull(s.Suggestion);
            Assert.Equal("region", s.Suggestion!.Kind);
            Assert.True(s.Effective);
        }

        [Fact]
        public void A_decision_marks_the_shooter_in_that_class_only()
        {
            var (flags, _) = Flags(Reg(7, true, ("C2", true), ("A1", false)), Reg(8, true, ("C2", null)));
            var c = Comp();
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.Contains(7, "C2"));
            Assert.False(set.Contains(7, "A1"));     // beslutet "i mästerskapet" gör ingen utanför
            Assert.False(set.Contains(8, "C2"));     // inget beslut
            Assert.Equal(1, set.Count);
        }

        [Fact]
        public void A_shooter_outside_in_any_class_cannot_join_a_team()
        {
            // Lagbildningen frågar per skytt (ett lag är klubbens, inte klassens).
            var (flags, _) = Flags(Reg(7, true, ("C2", true), ("A1", false)), Reg(8, true, ("C2", false)));
            var c = Comp();
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.HasMember(7));
            Assert.False(set.HasMember(8));
            Assert.False(set.HasMember(99));
        }

        [Fact]
        public void The_class_matches_in_id_and_display_name_form()
        {
            var (flags, _) = Flags(Reg(7, true, ("C_Vet_Y", true)));
            var c = Comp();
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.Contains(7, "C_Vet_Y"));
            Assert.True(set.Contains(7, "C Vet Y"));   // resultatlistans form
        }

        [Fact]
        public void An_inactive_registration_does_not_count()
        {
            var (flags, _) = Flags(Reg(7, false, ("C2", true)));
            var c = Comp();
            Assert.Equal(0, flags.Load(c, ChampionshipRule.For(c)).Count);
        }

        [Fact]
        public void A_decision_in_a_group_the_championship_does_not_cover_is_ignored()
        {
            var (flags, _) = Flags(Reg(7, true, ("C2", true), ("A1", true)));
            var c = Comp(groups: "C");
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.True(set.Contains(7, "C2"));
            Assert.False(set.Contains(7, "A1"));
        }

        private static (OutOfCompetitionFlags flags, Mock<ICompetitionRegistrationStore> store) FlagsWithRegionsAndStore(params RegistrationRecord[] regs)
        {
            var store = new Mock<ICompetitionRegistrationStore>();
            store.Setup(s => s.List(42)).Returns(regs);
            store.Setup(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            var regions = new Mock<IClubRegionLookup>();
            regions.Setup(r => r.OriginOf(It.IsAny<CompetitionInfo>())).Returns(new CompetitionOrigin(1, "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(1)).Returns(new ClubOrigin(1, "Varbergs PK", "Halland", "Hallands krets", "Vast"));
            regions.Setup(r => r.OriginOf(3)).Returns(new ClubOrigin(3, "Luleå PK", "Norrbotten", "Norrbottens krets", "Nord"));
            var memberClubs = new Mock<IMemberClubsLookup>();
            memberClubs.Setup(m => m.ClubsOf(It.IsAny<IEnumerable<int>>())).Returns(new Dictionary<int, IReadOnlyList<int>>());
            return (new OutOfCompetitionFlags(store.Object, regions.Object, memberClubs.Object), store);
        }

        [Fact]
        public void Stamping_turns_only_undecided_suggestions_into_decisions()
        {
            var c = Comp();
            c.ChampionshipParticipants = "own";
            // 7: förslag utan beslut → stämplas. 8: arrangören sa "i mästerskapet" → rörs inte. 9: ingen anledning → rörs inte.
            // 10: redan beslutat "utanför" (ingen förslagsrad) → rörs inte.
            var (flags, store) = FlagsWithRegionsAndStore(
                RegFor(7, 3, ("C2", null)), RegFor(8, 3, ("C2", false)), RegFor(9, 1, ("C2", null)), RegFor(10, 3, ("C2", true)));

            var n = flags.StampSuggestions(c, ChampionshipRule.For(c));

            Assert.Equal(1, n);
            store.Verify(s => s.StampOutOfCompetition(42, 70, "C2", It.IsAny<string>()), Times.Once);
            store.Verify(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void Stamping_does_nothing_without_participants_setting_or_with_the_switch_off()
        {
            var (flags, store) = FlagsWithRegionsAndStore(RegFor(7, 3, ("C2", null)));
            var noSetting = Comp();                    // alla deltar → inga förslag
            Assert.Equal(0, flags.StampSuggestions(noSetting, ChampionshipRule.For(noSetting)));

            var notField = Comp("Precision");          // en gren utan beteendet
            notField.ChampionshipParticipants = "own";
            Assert.Equal(0, flags.StampSuggestions(notField, ChampionshipRule.For(notField)));

            store.Verify(s => s.StampOutOfCompetition(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("Precision")]
        [InlineData("Springskytte")]
        public void In_a_discipline_without_the_behaviour_nothing_is_read_at_all(string type)
        {
            var (flags, store) = Flags(Reg(7, true, ("C2", true)));
            var c = Comp(type);
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.Same(OutOfCompetitionSet.None, set);
            Assert.False(set.Contains(7, "C2"));
            store.Verify(s => s.List(It.IsAny<int>()), Times.Never);
        }
    }
}
