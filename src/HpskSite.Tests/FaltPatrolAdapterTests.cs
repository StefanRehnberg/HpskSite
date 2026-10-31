using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Staffing;
using HpskSite.Models.ViewModels.Competition;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Adaptern databas → motor → PatrolPlan: ren funktion, så den testas utan databas. Fixturen har dubbelskyttar, lås (med sekunder), DNS, reserv och ett uppdrag.</summary>
    public class FaltPatrolAdapterTests
    {
        static readonly DateTime Day = new(2027, 6, 12);

        static CompetitionRegistration Reg(int id, string cls) => new() { MemberId = id, MemberClass = cls, MemberName = "Skytt " + id, MemberClub = "Klubb" };

        static (List<CompetitionRegistration> Regs, PatrolPlanSettings Settings) Fixture()
        {
            var regs = new List<CompetitionRegistration>();
            for (var i = 1; i <= 14; i++) regs.Add(Reg(i, "Klass C 1"));
            for (var i = 1; i <= 4; i++) regs.Add(Reg(i, "Klass B 1"));   // dubbelskyttar
            var s = new PatrolPlanSettings { PatrolSize = 4, IntervalMinutes = 15, FirstStart = Day.AddHours(9), WeaponGrouping = "Separate", MultiClassGapMinutes = 30 };
            return (regs, s);
        }

        static PatrolConstraints NoConstraints() => new();

        static PatrolPlan Run(List<CompetitionRegistration> regs, PatrolPlanSettings s, IReadOnlyCollection<LockedPlacement>? locked = null, ISet<int>? reserves = null, PatrolConstraints? c = null, FaltPlanOptions? o = null) =>
            FaltPatrolAdapter.Plan(regs, locked ?? new List<LockedPlacement>(), c ?? NoConstraints(), reserves ?? new HashSet<int>(), s, null, o, Day, out _);

        [Fact]
        public void Every_registration_is_placed_exactly_once_within_the_cap()
        {
            var (regs, s) = Fixture();
            var plan = Run(regs, s);
            var placed = plan.Patrols.SelectMany(p => p.Members.Select(m => m.MemberId + "|" + m.ShootingClass)).ToList();
            Assert.Equal(regs.Count, placed.Count);
            Assert.Equal(regs.Count, placed.Distinct().Count());
            Assert.All(plan.Patrols, p => Assert.InRange(p.Members.Count(m => !m.IsDns), 1, s.PatrolSize));
            Assert.All(plan.Patrols.SelectMany(p => p.Members), m => Assert.True(m.Position >= 1));
        }

        [Fact]
        public void Double_shooters_get_the_minimum_rest()
        {
            var (regs, s) = Fixture();
            var plan = Run(regs, s);
            foreach (var id in new[] { 1, 2, 3, 4 })
            {
                var times = plan.Patrols.Where(p => p.Members.Any(m => m.MemberId == id)).Select(p => p.StartTime!.Value).OrderBy(t => t).ToList();
                Assert.Equal(2, times.Count);
                Assert.True((times[1] - times[0]).TotalMinutes >= 30, $"skytt {id}: {(times[1] - times[0]).TotalMinutes} min mellan starterna");
            }
        }

        [Fact]
        public void A_locked_shooter_keeps_time_seconds_and_seat()
        {
            var (regs, s) = Fixture();
            var t = Day.AddHours(10).AddMinutes(7).AddSeconds(30);
            var locked = new[] { new LockedPlacement(5, "Skytt 5", "Klubb", "Klass C 1", t, "C", 3) };
            var plan = Run(regs, s, locked);
            var p = plan.Patrols.Single(x => x.Members.Any(m => m.MemberId == 5));
            Assert.Equal(t, p.StartTime);
            Assert.Equal(3, p.Members.Single(m => m.MemberId == 5).Position);
            Assert.Equal(1, plan.LockedKept);
        }

        [Fact]
        public void A_dns_shooter_stays_without_a_seat_and_does_not_count()
        {
            var (regs, s) = Fixture();
            var t = Day.AddHours(9);
            var locked = new[] { new LockedPlacement(6, "Skytt 6", "Klubb", "Klass C 1", t, "C", 0, true) };
            var plan = Run(regs, s, locked);
            var m = plan.Patrols.SelectMany(p => p.Members).Single(x => x.MemberId == 6 && x.ShootingClass == "Klass C 1");
            Assert.True(m.IsDns); Assert.Equal(0, m.Position);
            var p = plan.Patrols.Single(x => x.Members.Contains(m));
            Assert.Equal(t, p.StartTime);
            Assert.All(plan.Patrols, q => Assert.True(q.Members.Count(x => !x.IsDns) <= s.PatrolSize));
        }

        [Fact]
        public void A_reserve_gets_no_fixed_start_and_is_reported()
        {
            var (regs, s) = Fixture();
            var plan = Run(regs, s, reserves: new HashSet<int> { 14 });
            Assert.DoesNotContain(plan.Patrols.SelectMany(p => p.Members), m => m.MemberId == 14);
            Assert.Contains("Skytt 14", plan.ReservesLeftOut);
        }

        [Fact]
        public void A_job_over_the_natural_start_moves_the_shooter()
        {
            var (regs, s) = Fixture();
            var jobs = new Dictionary<int, List<StaffInterval>> { [1] = new() { new StaffInterval(Day.AddHours(9), Day.AddHours(9).AddMinutes(10), false) } };
            var c = new PatrolConstraints { Jobs = jobs };
            var plan = Run(regs, s, c: c);
            foreach (var p in plan.Patrols.Where(p => p.Members.Any(m => m.MemberId == 1)))
                Assert.False(c.Blocks(1, p.StartTime!.Value), $"skytt 1 startar {p.StartTime:HH:mm} mitt i sitt uppdrag");
            Assert.Empty(plan.Unavoidable);
        }

        [Fact]
        public void The_plan_does_not_depend_on_registration_order()
        {
            var (regs, s) = Fixture();
            var a = Run(regs, s);
            var shuffled = regs.OrderByDescending(r => r.MemberClass).ThenByDescending(r => r.MemberId).ToList();
            var b = Run(shuffled, s);
            string H(PatrolPlan p) => string.Join(";", p.Patrols.Select(x => x.StartTime + ":" + string.Join(",", x.Members.OrderBy(m => m.MemberId).Select(m => m.MemberId + m.ShootingClass))));
            Assert.Equal(H(a), H(b));
        }

        [Fact]
        public void Empty_registrations_give_an_empty_plan_not_an_exception()
        {
            var (_, s) = Fixture();
            var plan = Run(new List<CompetitionRegistration>(), s);
            Assert.Empty(plan.Patrols);
        }

        [Fact]
        public void An_overfull_lock_is_reported_not_hidden()
        {
            var (regs, s) = Fixture();
            var t = Day.AddHours(11);
            var locked = Enumerable.Range(1, 6).Select(i => new LockedPlacement(i, "Skytt " + i, "Klubb", "Klass C 1", t, "C", i)).ToList();
            var plan = Run(regs, s, locked);
            Assert.NotEmpty(plan.LockedOverflow);
        }

        [Fact]
        public void A_mixed_cbar_block_gets_spare_seats_so_double_shooters_can_rest()
        {
            // Alla tolv skyttar skjuter både C och B: i ett blandat block (cbar) med fast antal rutor finns annars ingen plats för andra starten med rätt vila.
            var regs = new List<CompetitionRegistration>();
            for (var i = 1; i <= 12; i++) regs.Add(Reg(i, "Klass C 1"));
            for (var i = 1; i <= 6; i++) regs.Add(Reg(i, "Klass B 1"));
            var s = new PatrolPlanSettings { PatrolSize = 4, IntervalMinutes = 15, FirstStart = Day.AddHours(9), WeaponGrouping = "Separate", MultiClassGapMinutes = 30 };
            var o = new FaltPlanOptions { Order = "cbar", MinRest = 30, WishRest = 20, CloseMinutes = 16 * 60, Priority = 0, RoundMinutes = 20 };
            var plan = FaltPatrolAdapter.Plan(regs, new List<LockedPlacement>(), NoConstraints(), new HashSet<int>(), s, null, o, Day, out var res);
            Assert.NotNull(res);
            Assert.False(res!.Infeasible, $"Hard={res.Q!.Hard} Under={res.Q.Under} Coll={res.Q.Coll} Cap={res.Q.Cap} Over={res.Q.Over} Staff={res.Q.StaffBad} Blocked={res.Blocked}");
            Assert.Equal(0, res.Q!.Hard);
            foreach (var id in Enumerable.Range(1, 6))
            {
                var times = plan.Patrols.Where(p => p.Members.Any(m => m.MemberId == id)).Select(p => p.StartTime!.Value).OrderBy(t => t).ToList();
                Assert.Equal(2, times.Count);
                Assert.True((times[1] - times[0]).TotalMinutes >= 30, $"skytt {id}: {(times[1] - times[0]).TotalMinutes} min");
            }
        }
    }
}
