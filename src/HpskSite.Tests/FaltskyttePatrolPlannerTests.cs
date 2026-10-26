using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Staffing;
using HpskSite.Models.ViewModels.Competition;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Planera dagen steg 3b: den rena planeraren runt generatorn — lås, reserver, numrering. Egenskapstester över slumpade men deterministiska instanser.
    /// </summary>
    public class FaltskyttePatrolPlannerTests
    {
        private static readonly DateTime D = new(2027, 6, 5);
        private static readonly PatrolPlanSettings S = new() { PatrolSize = 4, IntervalMinutes = 15, FirstStart = D.AddHours(9), WeaponGrouping = "Separate" };

        private static CompetitionRegistration Reg(int id, string cls = "C1") =>
            new() { MemberId = id, MemberName = $"Skytt {id:000}", MemberClass = cls, MemberClub = "Klubb" };

        private static StaffInterval Iv(int fromHour, int toHour) => new(D.AddHours(fromHour), D.AddHours(toHour), false);

        private sealed record Inst(List<CompetitionRegistration> Regs, List<LockedPlacement> Locked, PatrolConstraints C, HashSet<int> Reserves);

        private static Inst Make(int seed)
        {
            var r = new Random(seed);
            var n = r.Next(20, 61);
            var regs = Enumerable.Range(1, n).Select(i => Reg(i, r.Next(0, 3) == 0 ? "B1" : "C1")).ToList();
            var jobs = new Dictionary<int, List<StaffInterval>>();
            var early = new HashSet<int>();
            var reserves = new HashSet<int>();
            var locked = new List<LockedPlacement>();
            foreach (var reg in regs)
            {
                var roll = r.NextDouble();
                if (roll < 0.1) reserves.Add(reg.MemberId);
                else if (roll < 0.25) locked.Add(new LockedPlacement(reg.MemberId, reg.MemberName!, "Klubb", reg.MemberClass, D.AddHours(9).AddMinutes(15 * r.Next(0, 16)), null));
                if (r.NextDouble() < 0.4) { var f = r.Next(8, 13); jobs[reg.MemberId] = new List<StaffInterval> { Iv(f, f + r.Next(1, 4)) }; }
                if (r.NextDouble() < 0.1) early.Add(reg.MemberId);
            }
            // En låst som inte längre är anmäld, och en extra låst vid samma tid som många andra (tvingar fram överflöd).
            locked.Add(new LockedPlacement(9999, "Avanmäld", "Klubb", "C1", D.AddHours(9), null));
            return new Inst(regs, locked, new PatrolConstraints { Jobs = jobs, Early = early }, reserves);
        }

        private static PatrolPlan Run(Inst i) => FaltskyttePatrolPlanner.Plan(i.Regs, i.Locked, i.C, i.Reserves, S);

        [Fact]
        public void Forkontroll_instanserna_har_lasta_reserver_och_avanmalda()
        {
            var all = Enumerable.Range(1, 60).Select(Make).ToList();
            all.Sum(i => i.Locked.Count).Should().BeGreaterThan(300);
            all.Sum(i => i.Reserves.Count).Should().BeGreaterThan(80);
            all.Select(Run).Sum(p => p.LockedDropped.Count).Should().Be(60);
        }

        [Fact]
        public void Lasta_skyttar_star_kvar_pa_sin_tid()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var plan = Run(inst);
                foreach (var l in inst.Locked.Where(l => l.MemberId != 9999))
                {
                    var hit = plan.Patrols.Where(p => p.Members.Any(m => m.MemberId == l.MemberId && m.ShootingClass == l.ShootingClass)).ToList();
                    hit.Should().ContainSingle($"seed {seed}: {l.Name} ska vara i exakt en patrull");
                    hit[0].StartTime.Should().Be(l.StartTime, $"seed {seed}: {l.Name} flyttades");
                }
            }
        }

        [Fact]
        public void Reserver_far_ingen_start_och_alla_ovriga_far_exakt_en()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var plan = Run(inst);
                var placed = plan.Patrols.SelectMany(p => p.Members).Select(m => m.MemberId).OrderBy(x => x).ToList();
                var expected = inst.Regs.Select(r => r.MemberId).Where(id => !inst.Reserves.Contains(id) || inst.Locked.Any(l => l.MemberId == id)).OrderBy(x => x).ToList();
                placed.Should().Equal(expected, $"seed {seed}");
                plan.ReservesLeftOut.Count.Should().Be(inst.Regs.Count(r => inst.Reserves.Contains(r.MemberId) && inst.Locked.All(l => l.MemberId != r.MemberId)), $"seed {seed}");
            }
        }

        [Fact]
        public void Patrullerna_ar_numrerade_i_tidsordning_och_ingen_ofrivillig_krock_finns()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var plan = Run(inst);
                plan.Patrols.Select(p => p.PatrolNumber).Should().Equal(Enumerable.Range(1, plan.Patrols.Count), $"seed {seed}");
                plan.Patrols.Select(p => p.StartTime!.Value).Should().BeInAscendingOrder($"seed {seed}");
                var lockedIds = inst.Locked.Select(l => l.MemberId).ToHashSet();
                var unavoidable = plan.Unavoidable.Select(u => u.MemberId).ToHashSet();
                foreach (var p in plan.Patrols)
                    foreach (var m in p.Members.Where(m => !lockedIds.Contains(m.MemberId) && !unavoidable.Contains(m.MemberId)))
                        inst.C.Blocks(m.MemberId, p.StartTime!.Value).Should().BeFalse($"seed {seed}: {m.Name} {p.StartTime:HH:mm}");
            }
        }

        [Fact]
        public void Ingen_patrull_blir_for_stor_och_de_lasta_tar_plats_i_sin_patrull()
        {
            for (var seed = 1; seed <= 60; seed++)
                Run(Make(seed)).Patrols.Should().OnlyContain(p => p.Members.Count <= S.PatrolSize, $"seed {seed}");

            // Fyra låsta kl 09:00 i en patrull om fyra: de fria fyller inte upp samma patrull, så det blir ingen extra patrull vid samma tid.
            var regs = Enumerable.Range(1, 8).Select(i => Reg(i)).ToList();
            var t = D.AddHours(9);
            var locked = Enumerable.Range(1, 4).Select(i => new LockedPlacement(i, $"Skytt {i:000}", "Klubb", "C1", t, null)).ToList();
            var plan = FaltskyttePatrolPlanner.Plan(regs, locked, new PatrolConstraints(), new HashSet<int>(), S);
            plan.Patrols.Where(p => p.StartTime == t).Should().ContainSingle();
            plan.LockedOverflow.Should().BeEmpty();
            plan.Patrols.SelectMany(p => p.Members).Should().HaveCount(8);
        }

        [Fact]
        public void Last_skytt_i_full_patrull_far_en_egen_patrull_vid_samma_tid_och_redovisas()
        {
            var regs = Enumerable.Range(1, 6).Select(i => Reg(i)).ToList();
            var t = D.AddHours(9);
            var locked = Enumerable.Range(1, 5).Select(i => new LockedPlacement(i, $"Skytt {i:000}", "Klubb", "C1", t, null)).ToList();
            var plan = FaltskyttePatrolPlanner.Plan(regs, locked, new PatrolConstraints(), new HashSet<int>(), S);
            plan.Patrols.Where(p => p.StartTime == t).Should().HaveCount(2);
            plan.LockedOverflow.Should().HaveCount(1);
            plan.Patrols.SelectMany(p => p.Members).Should().HaveCount(6);
        }
    }
}
