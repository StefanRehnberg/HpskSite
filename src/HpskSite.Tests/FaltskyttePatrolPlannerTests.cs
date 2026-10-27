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

        // ── Grupper: fältets startlista skapas en grupp i taget med egen första start (B+C kl 09, A kl 13). Tiderna per patrull är uppskattningar. ──
        private static readonly List<PatrolGroupSpec> Groups = new()
        {
            new("B+C", new[] { "B", "C" }, D.AddHours(9), 10, 5, 4),
            new("A", new[] { "A" }, D.AddHours(13), 15, 4, 3),
        };

        private static List<CompetitionRegistration> Mixed(int bc, int a) =>
            Enumerable.Range(1, bc).Select(i => Reg(i, i % 2 == 0 ? "B1" : "C2")).Concat(Enumerable.Range(100, a).Select(i => Reg(i, "A2"))).ToList();

        [Fact]
        public void Varje_grupp_byggs_med_sin_egen_forsta_start_och_inget_flyttar_mellan_grupper()
        {
            var plan = FaltskyttePatrolPlanner.Plan(Mixed(12, 7), new List<LockedPlacement>(), new PatrolConstraints(), new HashSet<int>(), S, Groups);
            var bc = plan.Patrols.Where(p => p.WeaponGroup == "B+C").ToList();
            var a = plan.Patrols.Where(p => p.WeaponGroup == "A").ToList();
            bc.Min(p => p.StartTime).Should().Be(D.AddHours(9));
            a.Min(p => p.StartTime).Should().Be(D.AddHours(13));
            bc.Should().OnlyContain(p => p.Members.Count <= 5);
            a.Should().OnlyContain(p => p.Members.Count <= 4);
            bc.SelectMany(p => p.Members).Select(m => m.MemberId).Should().OnlyContain(id => id < 100);
            a.SelectMany(p => p.Members).Select(m => m.MemberId).Should().OnlyContain(id => id >= 100);
            plan.Patrols.SelectMany(p => p.Members).Should().HaveCount(19);
            plan.Ungrouped.Should().BeEmpty();
        }

        [Fact]
        public void Den_som_kroockar_flyttas_inom_sin_grupp_och_ingen_hamnar_i_en_annan()
        {
            // Skytt 1 (grupp B+C) har ett uppdrag 08–15: gruppens första start 09:00 krockar, så hen får en senare patrull i SAMMA grupp, aldrig i A.
            var c = new PatrolConstraints { Jobs = new Dictionary<int, List<StaffInterval>> { [1] = new() { Iv(8, 12) } } };
            var plan = FaltskyttePatrolPlanner.Plan(Mixed(12, 7), new List<LockedPlacement>(), c, new HashSet<int>(), S, Groups);
            var p1 = plan.Patrols.Single(p => p.Members.Any(m => m.MemberId == 1));
            p1.WeaponGroup.Should().Be("B+C");
            c.Blocks(1, p1.StartTime!.Value).Should().BeFalse();
            p1.StartTime!.Value.Should().BeAfter(D.AddHours(9));
        }

        [Fact]
        public void Anmalda_utan_grupp_placeras_inte_utan_redovisas_med_namn_och_klass()
        {
            var onlyBc = new List<PatrolGroupSpec> { Groups[0] };
            var plan = FaltskyttePatrolPlanner.Plan(Mixed(4, 2), new List<LockedPlacement>(), new PatrolConstraints(), new HashSet<int>(), S, onlyBc);
            plan.Patrols.SelectMany(p => p.Members).Should().HaveCount(4);
            plan.Ungrouped.Should().HaveCount(2);
            plan.Ungrouped.Should().OnlyContain(s => s.Contains("Skytt 10"));
        }

        [Fact]
        public void Lasta_ligger_kvar_i_sin_grupp_pa_sin_tid_och_tar_plats_i_patrullen()
        {
            var t = D.AddHours(13).AddMinutes(15);
            var locked = new List<LockedPlacement> { new(100, "Skytt 100", "Klubb", "A2", t, "A"), new(101, "Skytt 101", "Klubb", "A2", t, "A") };
            var plan = FaltskyttePatrolPlanner.Plan(Mixed(0, 8), locked, new PatrolConstraints(), new HashSet<int>(), S, new List<PatrolGroupSpec> { Groups[1] });
            plan.Patrols.Where(p => p.StartTime == t).Should().ContainSingle().Which.Members.Select(m => m.MemberId).Should().Contain(new[] { 100, 101 });
            plan.Patrols.Should().OnlyContain(p => p.Members.Count <= 4);
            plan.Patrols.SelectMany(p => p.Members).Should().HaveCount(8);
        }

        [Fact]
        public void Egenskap_grupper_slumpade_instanser_ingen_hamnar_i_fel_grupp_eller_krockar()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var plan = FaltskyttePatrolPlanner.Plan(inst.Regs, new List<LockedPlacement>(), inst.C, inst.Reserves, S,
                    new List<PatrolGroupSpec> { new("B+C", new[] { "B", "C" }, D.AddHours(9), 10, 5, 1) });
                var placed = plan.Patrols.SelectMany(p => p.Members).Select(m => m.MemberId).OrderBy(x => x).ToList();
                placed.Should().Equal(inst.Regs.Where(r => !inst.Reserves.Contains(r.MemberId)).Select(r => r.MemberId).OrderBy(x => x), $"seed {seed}");
                plan.Patrols.Should().OnlyContain(p => p.WeaponGroup == "B+C" && p.Members.Count <= 5, $"seed {seed}");
                var unavoidable = plan.Unavoidable.Select(u => u.MemberId).ToHashSet();
                foreach (var p in plan.Patrols)
                    foreach (var m in p.Members.Where(m => !unavoidable.Contains(m.MemberId)))
                        inst.C.Blocks(m.MemberId, p.StartTime!.Value).Should().BeFalse($"seed {seed}: {m.Name}");
            }
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

        // 9ccd668 (main): platserna i en patrull är fasta. En ombyggnad får inte numrera om dem som står kvar, och DNS har plats 0.
        [Fact]
        public void Den_som_star_kvar_behaller_sin_plats_och_nya_tar_lagsta_lediga()
        {
            var t = D.AddHours(9);
            var regs = Enumerable.Range(1, 4).Select(i => Reg(i)).ToList();
            // 2 och 4 är låsta på plats 2 och 4 i sin patrull (1 och 3 har lämnat), de nya 1 och 3 ska fylla luckorna.
            var locked = new List<LockedPlacement>
            {
                new(2, "Skytt 002", "Klubb", "C1", t, null, Position: 2),
                new(4, "Skytt 004", "Klubb", "C1", t, null, Position: 4),
            };
            var plan = FaltskyttePatrolPlanner.Plan(regs, locked, new PatrolConstraints(), new HashSet<int>(), S);
            var p = plan.Patrols.Single(x => x.StartTime == t);
            p.Members.Single(m => m.MemberId == 2).Position.Should().Be(2);
            p.Members.Single(m => m.MemberId == 4).Position.Should().Be(4);
            p.Members.Where(m => m.MemberId is 1 or 3).Select(m => m.Position).OrderBy(x => x).Should().Equal(1, 3);
            p.Members.Select(m => m.Position).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void En_DNS_skytt_stannar_i_sin_patrull_utan_plats_och_tar_ingen_plats_i_storleken()
        {
            var t = D.AddHours(9);
            // Patrullstorlek 4: tre låsta + en DNS + en skytt till får plats i EN patrull, eftersom DNS inte räknas.
            var regs = Enumerable.Range(1, 5).Select(i => Reg(i)).ToList();
            var locked = new List<LockedPlacement>
            {
                new(1, "Skytt 001", "Klubb", "C1", t, null, Position: 1), new(2, "Skytt 002", "Klubb", "C1", t, null, Position: 2), new(3, "Skytt 003", "Klubb", "C1", t, null, Position: 3),
                new(4, "Skytt 004", "Klubb", "C1", t, null, IsDns: true),
            };
            var plan = FaltskyttePatrolPlanner.Plan(regs, locked, new PatrolConstraints(), new HashSet<int>(), S);
            var p = plan.Patrols.Single(x => x.StartTime == t && x.Members.Any(m => m.MemberId == 4));
            var dns = p.Members.Single(m => m.MemberId == 4);
            dns.IsDns.Should().BeTrue();
            dns.Position.Should().Be(0);
            p.Members.Where(m => !m.IsDns).Select(m => m.Position).Should().OnlyHaveUniqueItems().And.OnlyContain(x => x >= 1);
            p.Members.Count(m => !m.IsDns).Should().BeLessThanOrEqualTo(S.PatrolSize);
            p.Members.Should().Contain(m => m.MemberId == 5, "tre skyttar + en DNS lämnar en plats ledig, och den femte ska få den — DNS räknas inte mot storleken");
            plan.Patrols.SelectMany(x => x.Members).Where(m => m.MemberId == 4).Should().HaveCount(1, "DNS-skytten placeras inte en gång till som en vanlig skytt");
        }

        [Fact]
        public void Seat_loser_dubbletter_och_lamnar_DNS_pa_noll()
        {
            var p = new FaltskytteGeneratedPatrol { StartTime = D.AddHours(9) };
            p.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = 1, Position = 2 });
            p.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = 2, Position = 2 });
            p.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = 3, IsDns = true, Position = 5 });
            p.Members.Add(new FaltskytteGeneratedPatrolMember { MemberId = 4 });
            FaltskyttePatrolPlanner.Seat(p);
            p.Members.Single(m => m.MemberId == 3).Position.Should().Be(0);
            p.Members.Where(m => !m.IsDns).Select(m => m.Position).Should().OnlyHaveUniqueItems().And.OnlyContain(x => x >= 1);
            p.Members.Single(m => m.MemberId == 1).Position.Should().Be(2, "den som kom först behåller platsen");
        }
    }
}
