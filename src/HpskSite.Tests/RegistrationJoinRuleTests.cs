using HpskSite.Models;
using HpskSite.Models.Registrations;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>"Måste gå i samma patrull som": reglerna som servern prövar vid varje skrivväg (anmälan, anmälan åt någon annan, adminens redigering).</summary>
    public class RegistrationJoinRuleTests
    {
        static RegistrationRecord Reg(int member, int club, params (string cls, int? join)[] classes) => new()
        {
            CompetitionId = 1, MemberId = member, ClubId = club, IsActive = true,
            Classes = classes.Select(c => new ShootingClassEntry { Class = c.cls, JoinMemberId = c.join }).ToList()
        };

        static string? Check(RegistrationRecord self, string cls, int join, params RegistrationRecord[] all)
            => RegistrationJoinRule.Check(self, cls, join, all, r => r.ClubId, id => "Medlem " + id);

        [Fact]
        public void A_club_mate_registered_in_the_same_weapon_group_is_accepted()
        {
            var self = Reg(1, 10, ("C1", null));
            var mate = Reg(2, 10, ("C2", null));   // annan C-klass: samma vapengrupp
            Assert.Null(Check(self, "C1", 2, self, mate));
        }

        [Fact]
        public void Yourself_is_refused() => Assert.Contains("dig själv", Check(Reg(1, 10, ("C1", null)), "C1", 1)!);

        [Fact]
        public void Someone_who_is_not_registered_in_the_group_is_refused_by_name()
        {
            var self = Reg(1, 10, ("C1", null));
            var onlyA = Reg(2, 10, ("A1", null));
            var msg = Check(self, "C1", 2, self, onlyA)!;
            Assert.Contains("Medlem 2 är inte anmäld i klass C", msg);
            Assert.Contains("inte anmäld", Check(self, "C1", 3, self)!);   // ingen anmälan alls
        }

        [Fact]
        public void Someone_who_has_withdrawn_does_not_count()
        {
            var self = Reg(1, 10, ("C1", null));
            var gone = Reg(2, 10, ("C1", null)); gone.IsActive = false;
            Assert.NotNull(Check(self, "C1", 2, self, gone));
        }

        [Fact]
        public void The_club_is_the_one_the_registration_is_for_not_the_members_home_club()
        {
            var self = Reg(1, 10, ("C1", null));
            var other = Reg(2, 11, ("C1", null));
            Assert.Contains("annan klubb", Check(self, "C1", 2, self, other)!);
        }

        [Fact]
        public void At_most_two_can_follow_the_same_person_in_a_weapon_group()
        {
            var mate = Reg(9, 10, ("C1", null));
            var f1 = Reg(1, 10, ("C1", 9)); var f2 = Reg(2, 10, ("C2", 9));
            var third = Reg(3, 10, ("C1", null));
            Assert.Contains("går redan med 2", Check(third, "C1", 9, mate, f1, f2, third)!);
            // ... men en uppföljare i en ANNAN grupp räknas inte
            var mateA = Reg(9, 10, ("C1", null), ("A1", null));
            var fA = Reg(1, 10, ("A1", 9)); var fA2 = Reg(2, 10, ("A1", 9));
            Assert.Null(Check(third, "C1", 9, mateA, fA, fA2, third));
        }

        [Fact]
        public void The_same_person_can_change_their_own_choice_without_counting_against_themselves()
        {
            var mate = Reg(9, 10, ("C1", null));
            var self = Reg(1, 10, ("C1", 9)); var other = Reg(2, 10, ("C1", 9));
            Assert.Null(Check(self, "C1", 9, mate, self, other));   // den egna posten räknas inte, så 1 annan + jag = 2 är tillåtet
        }

        [Fact]
        public void Chains_and_cycles_are_allowed()
        {
            // A vill med B och B vill med A (cykel), och B vill med C (kedja): motorn bygger en grupp av dem.
            var a = Reg(1, 10, ("C1", 2)); var b = Reg(2, 10, ("C1", 1)); var c = Reg(3, 10, ("C1", null));
            Assert.Null(Check(a, "C1", 2, a, b, c));
            Assert.Null(Check(b, "C1", 3, a, b, c));
        }
    }
}
