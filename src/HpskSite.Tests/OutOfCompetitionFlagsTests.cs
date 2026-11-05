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
        private static CompetitionInfo Comp(string? mode = "on", string? groups = null) => new()
        {
            Id = 42, CompetitionType = "Faltskytte", CompetitionScope = CompetitionScopeHelper.Kretsmasterskap,
            ChampionshipMode = mode, ChampionshipWeaponGroups = groups
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
            return (new OutOfCompetitionFlags(store.Object), store);
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

        [Theory]
        [InlineData(null)]
        [InlineData("off")]
        public void Without_the_switch_nothing_is_read_at_all(string? mode)
        {
            var (flags, store) = Flags(Reg(7, true, ("C2", true)));
            var c = Comp(mode);
            var set = flags.Load(c, ChampionshipRule.For(c));

            Assert.Same(OutOfCompetitionSet.None, set);
            Assert.False(set.Contains(7, "C2"));
            store.Verify(s => s.List(It.IsAny<int>()), Times.Never);
        }
    }
}
