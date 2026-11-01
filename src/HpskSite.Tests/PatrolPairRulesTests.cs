using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.ViewModels.Competition;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// De manuella vägarna (flytta, lägg till, efteranmälan) får inte tyst bryta "måste gå i samma patrull som". Varje test kan falla: tas kontrollen bort
    /// (SeparationMessage returnerar alltid null) faller flytt- och nyplaceringstesten; ignoreras vapengruppen faller gruppstesten.
    /// </summary>
    public class PatrolPairRulesTests
    {
        // Nybörjaren 1 följer kamraten 2 i klass C; 3 är en utomstående.
        static CompetitionRegistration Reg(int id, string cls, int? join = null, string? name = null, bool active = true) =>
            new() { MemberId = id, MemberClass = cls, JoinMemberId = join, MemberName = name ?? $"Skytt {id}", IsActive = active };

        static PairPlacement At(int id, string cls, int patrolId, bool dns = false) => new(id, cls, patrolId, patrolId, dns);
        static PairMove To(int id, string cls, int patrolId) => new(id, cls, patrolId, patrolId);

        static readonly List<CompetitionRegistration> Regs = new() { Reg(1, "C1", join: 2, name: "Nybörjaren"), Reg(2, "C2", name: "Kamraten"), Reg(3, "C1", name: "Utomstående") };

        [Fact]
        public void Moving_a_follower_away_from_the_partner_is_reported_with_both_names()
        {
            var before = new[] { At(1, "C1", 10), At(2, "C2", 10), At(3, "C1", 11) };
            var msg = PatrolPairRules.SeparationMessage(Regs, before, new[] { To(1, "C1", 11) });
            Assert.NotNull(msg);
            Assert.Contains("Nybörjaren", msg); Assert.Contains("Kamraten", msg); Assert.Contains("patrull 11", msg); Assert.Contains("patrull 10", msg);
        }

        [Fact]
        public void Moving_the_partner_away_from_a_follower_is_reported_too()
        {
            var before = new[] { At(1, "C1", 10), At(2, "C2", 10) };
            Assert.NotNull(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(2, "C2", 12) }));
        }

        [Fact]
        public void Moving_both_to_the_same_patrol_is_fine()
        {
            var before = new[] { At(1, "C1", 10), At(2, "C2", 10) };
            Assert.Null(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(1, "C1", 12), To(2, "C2", 12) }));
        }

        [Fact]
        public void An_unrelated_move_is_never_reported()
        {
            var before = new[] { At(1, "C1", 10), At(2, "C2", 10), At(3, "C1", 11) };
            Assert.Null(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(3, "C1", 12) }));
        }

        [Fact]
        public void A_pair_that_was_already_apart_is_not_blamed_on_this_move()
        {
            var before = new[] { At(1, "C1", 10), At(2, "C2", 11) };
            Assert.Null(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(1, "C1", 12) }));
        }

        [Fact]
        public void Placing_a_new_follower_in_another_patrol_than_the_partner_is_reported()
        {
            var before = new[] { At(2, "C2", 10) };   // 1 är inte placerad än
            Assert.NotNull(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(1, "C1", 11) }));
            Assert.Null(PatrolPairRules.SeparationMessage(Regs, before, new[] { To(1, "C1", 10) }));
        }

        [Fact]
        public void A_partner_who_is_dns_or_not_registered_leaves_no_requirement()
        {
            var dns = new[] { At(1, "C1", 10), At(2, "C2", 10, dns: true) };
            Assert.Null(PatrolPairRules.SeparationMessage(Regs, dns, new[] { To(1, "C1", 11) }));
            var gone = new List<CompetitionRegistration> { Reg(1, "C1", join: 2), Reg(2, "C2", active: false) };
            Assert.Null(PatrolPairRules.PartnerPlacement(gone, new[] { At(2, "C2", 10) }, 1, "C1"));
        }

        [Fact]
        public void The_requirement_follows_the_weapon_group_not_the_class()
        {
            // Nybörjaren följer kamraten i C; en start i B är en annan sak och rörs inte av kravet.
            var regs = new List<CompetitionRegistration> { Reg(1, "C1", join: 2), Reg(1, "B1"), Reg(2, "C2"), Reg(2, "B1") };
            var before = new[] { At(1, "C1", 10), At(2, "C2", 10), At(1, "B1", 20), At(2, "B1", 21) };
            Assert.Null(PatrolPairRules.SeparationMessage(regs, before, new[] { To(1, "B1", 22) }));
            Assert.NotNull(PatrolPairRules.SeparationMessage(regs, before, new[] { To(1, "C1", 11) }));
        }

        [Fact]
        public void The_partner_patrol_is_found_for_a_follower_and_only_in_the_same_group()
        {
            var placements = new[] { At(2, "C2", 10), At(2, "B1", 20) };
            Assert.Equal(10, PatrolPairRules.PartnerPlacement(Regs, placements, 1, "C1")?.PatrolId);
            Assert.Null(PatrolPairRules.PartnerPlacement(Regs, placements, 3, "C1"));   // 3 följer ingen
            Assert.Null(PatrolPairRules.PartnerPlacement(Regs, new[] { At(2, "B1", 20) }, 1, "C1"));   // kamraten är bara placerad i B
        }
    }
}
