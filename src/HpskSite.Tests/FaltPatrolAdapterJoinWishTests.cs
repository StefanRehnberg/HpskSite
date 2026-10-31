using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Staffing;
using HpskSite.Models.ViewModels.Competition;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Anmälningarna → motorn: "gå i samma patrull som" (hårt, per klassanmälan, vapengruppen) och önskemål om tidig/sen start (mjukt).</summary>
    public class FaltPatrolAdapterJoinWishTests
    {
        static readonly DateTime Day = new(2027, 6, 12);

        static CompetitionRegistration Reg(int id, string cls, int? join = null, string pref = "Inget") =>
            new() { MemberId = id, MemberClass = cls, MemberName = "Skytt " + id, MemberClub = "Klubb", JoinMemberId = join, StartPreference = pref };

        static PatrolPlanSettings Settings() => new() { PatrolSize = 4, IntervalMinutes = 15, FirstStart = Day.AddHours(9), WeaponGrouping = "Separate", MultiClassGapMinutes = 30 };

        static PatrolPlan Run(List<CompetitionRegistration> regs, IReadOnlyCollection<LockedPlacement>? locked = null, ISet<int>? reserves = null) =>
            FaltPatrolAdapter.Plan(regs, locked ?? new List<LockedPlacement>(), new PatrolConstraints(), reserves ?? new HashSet<int>(), Settings(), null, null, Day, out _);

        static List<CompetitionRegistration> Twelve(Action<List<CompetitionRegistration>>? tweak = null)
        {
            var regs = Enumerable.Range(1, 16).Select(i => Reg(i, "C1")).ToList();
            tweak?.Invoke(regs);
            return regs;
        }

        static int PatrolOf(PatrolPlan plan, int member, string group = "C") =>
            plan.Patrols.First(p => p.Members.Any(m => m.MemberId == member && FaltPatrolAdapter.GroupOf(m.ShootingClass) == group)).PatrolNumber;

        [Fact]
        public void A_registration_that_must_join_someone_is_placed_in_that_persons_patrol()
        {
            var without = Run(Twelve());
            Assert.NotEqual(PatrolOf(without, 1), PatrolOf(without, 14));   // utan kravet ligger de i olika patruller (fyra per patrull)
            var regs = Twelve(r => r[0] = Reg(1, "C1", join: 14));
            var plan = Run(regs);
            Assert.Equal(PatrolOf(plan, 1), PatrolOf(plan, 14));
            Assert.Equal(1, plan.LinksTotal); Assert.Equal(1, plan.LinksMet); Assert.Empty(plan.LinkIssues);
        }

        [Fact]
        public void The_requirement_is_per_class_entry_so_different_people_can_join_different_classes()
        {
            var regs = Twelve();
            regs.Add(Reg(1, "A1", join: 9));       // 1 skjuter också A och ska där gå med 9
            regs.Add(Reg(9, "A1"));
            regs.Add(Reg(1, "C1", join: 14)); regs.RemoveAll(r => r.MemberId == 1 && r.MemberClass == "C1" && r.JoinMemberId == null);
            var plan = Run(regs);
            Assert.Equal(PatrolOf(plan, 1, "C"), PatrolOf(plan, 14, "C"));
            Assert.Equal(PatrolOf(plan, 1, "A"), PatrolOf(plan, 9, "A"));
            Assert.Equal(2, plan.LinksTotal); Assert.Equal(2, plan.LinksMet);
        }

        [Fact]
        public void Any_C_class_counts_as_the_same_weapon_group()
        {
            var regs = Twelve(r => { r[0] = Reg(1, "C1", join: 14); r[13] = Reg(14, "C2"); });
            var plan = Run(regs);
            Assert.Equal(PatrolOf(plan, 1), PatrolOf(plan, 14));
        }

        [Fact]
        public void A_partner_who_is_not_registered_in_the_group_is_reported_by_name_and_the_plan_still_works()
        {
            var regs = Twelve(r => r[0] = Reg(1, "C1", join: 99));   // 99 finns inte
            regs.Add(Reg(98, "A1")); regs[0] = Reg(1, "C1", join: 98);   // 98 är bara anmäld i A
            var plan = Run(regs);
            Assert.Equal(0, plan.LinksTotal);
            var issue = Assert.Single(plan.LinkIssues);
            Assert.Contains("Skytt 1", issue); Assert.Contains("Skytt 98", issue); Assert.Contains("inte anmäld", issue);
            Assert.Equal(regs.Count, plan.Patrols.SelectMany(p => p.Members).Count());
        }

        [Fact]
        public void A_partner_who_is_a_reserve_or_dns_is_reported_and_ignored()
        {
            var regs = Twelve(r => r[0] = Reg(1, "C1", join: 14));
            var asReserve = Run(regs, reserves: new HashSet<int> { 14 });
            Assert.Contains(asReserve.LinkIssues, x => x.Contains("reserv") && x.Contains("Skytt 14"));
            var dns = new[] { new LockedPlacement(14, "Skytt 14", "Klubb", "C1", Day.AddHours(9), "C", 0, true) };
            var asDns = Run(regs, locked: dns);
            Assert.Contains(asDns.LinkIssues, x => x.Contains("DNS") && x.Contains("Skytt 14"));
        }

        [Fact]
        public void Wishes_for_an_early_start_are_counted_only_on_the_first_start_and_reported_with_names()
        {
            var regs = Twelve(r => { foreach (var i in new[] { 5, 6, 7, 8 }) r[i - 1] = Reg(i, "C1", pref: "Tidig Start"); });
            regs.Add(Reg(5, "B1", pref: "Tidig Start"));   // 5 skjuter också B: önskemålet på B räknas inte (det är inte första starten)
            var plan = Run(regs);
            Assert.InRange(plan.WishesTotal, 1, 4);
            Assert.True(plan.WishesMet <= plan.WishesTotal);
            Assert.Equal(plan.WishesTotal - plan.WishesMet, plan.WishesMissed.Count);
            Assert.All(plan.WishesMissed, x => Assert.Contains("ville starta tidigt", x));
        }

        [Fact]
        public void A_wish_never_adds_hard_failures()
        {
            var plain = Twelve();
            var wished = Twelve(r => { foreach (var i in new[] { 2, 3, 4, 5, 6 }) r[i - 1] = Reg(i, "C1", pref: "Sen Start"); });
            Assert.True(Run(wished).Metrics!.Hard <= Run(plain).Metrics!.Hard);
        }
    }
}
