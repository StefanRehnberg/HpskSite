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
    /// Planera dagen steg 3b: startlistegeneratorn bygger runt uppdragen. Egenskapstester över slumpade men deterministiska instanser (frö):
    /// ingen skytts startfönster överlappar ett eget uppdrag (annat än det som redovisas som oundvikligt), alla anmälda placeras exakt en gång,
    /// de som ska gå tidigt hamnar före övriga i sin grupp, och sökningen tar slut även när uppdraget täcker hela dagen.
    /// </summary>
    public class FaltskyttePatrolGeneratorTests
    {
        private static readonly DateTime D = new(2027, 6, 5);
        private static readonly DateTime First = D.AddHours(9);
        private const int Interval = 15;
        private const int PatrolSize = 4;

        private static CompetitionRegistration Reg(int id, string cls = "C1") =>
            new() { MemberId = id, MemberName = $"Skytt {id:000}", MemberClass = cls, MemberClub = "Klubb" };

        private static StaffInterval Iv(int fromHour, int toHour) => new(D.AddHours(fromHour), D.AddHours(toHour), false);

        private sealed record Instance(List<CompetitionRegistration> Regs, PatrolConstraints Constraints, int WithJobs, int Early, int AllDay);

        private static Instance Make(int seed)
        {
            var r = new Random(seed);
            var n = r.Next(20, 61);
            var regs = Enumerable.Range(1, n).Select(i => Reg(i, r.Next(0, 3) == 0 ? "B1" : "C1")).ToList();
            var jobs = new Dictionary<int, List<StaffInterval>>();
            var early = new HashSet<int>();
            var allDay = 0;
            foreach (var reg in regs)
            {
                if (r.NextDouble() < 0.4)
                {
                    var from = r.Next(8, 13);
                    jobs[reg.MemberId] = new List<StaffInterval> { Iv(from, from + r.Next(1, 4)) };
                }
                if (r.NextDouble() < 0.03) { jobs[reg.MemberId] = new List<StaffInterval> { Iv(0, 23) }; allDay++; }
                if (r.NextDouble() < 0.1) early.Add(reg.MemberId);
            }
            return new Instance(regs, new PatrolConstraints { Jobs = jobs, Early = early }, jobs.Count, early.Count, allDay);
        }

        private static FaltskyttePatrolGenerationResult Run(Instance i) =>
            new FaltskyttePatrolGenerator().Generate(i.Regs, PatrolSize, Interval, First, "Separate", 0, null, i.Constraints);

        [Fact]
        public void Forkontroll_instanserna_ar_inte_for_snalla()
        {
            // Faller om slumpen slutar ge uppdrag, tidiga eller heldagsuppdrag: då bevisar egenskapstesterna ingenting.
            var all = Enumerable.Range(1, 60).Select(Make).ToList();
            all.Sum(i => i.WithJobs).Should().BeGreaterThan(300);
            all.Sum(i => i.Early).Should().BeGreaterThan(40);
            all.Sum(i => i.AllDay).Should().BeGreaterThan(5);
            all.Select(i => Run(i)).Sum(r => r.Unavoidable.Count).Should().BeGreaterThan(0);
        }

        [Fact]
        public void Ingen_start_krockar_med_eget_uppdrag_utom_det_som_redovisas()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var res = Run(inst);
                var unavoidable = res.Unavoidable.Select(u => u.MemberId).ToHashSet();
                foreach (var p in res.Patrols)
                    foreach (var m in p.Members)
                    {
                        if (unavoidable.Contains(m.MemberId)) continue;
                        inst.Constraints.Blocks(m.MemberId, p.StartTime!.Value).Should().BeFalse($"seed {seed}: {m.Name} startar {p.StartTime:HH:mm} mitt i ett eget uppdrag");
                    }
            }
        }

        [Fact]
        public void Alla_anmalda_placeras_exakt_en_gang_och_ingen_patrull_ar_for_stor()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var inst = Make(seed);
                var res = Run(inst);
                res.Patrols.SelectMany(p => p.Members).Select(m => m.MemberId).OrderBy(x => x)
                    .Should().Equal(inst.Regs.Select(r => r.MemberId).OrderBy(x => x), $"seed {seed}");
                res.Patrols.Should().OnlyContain(p => p.Members.Count <= PatrolSize);
            }
        }

        [Fact]
        public void Heldagsuppdrag_tar_slut_och_redovisas_som_oundvikligt()
        {
            var regs = new List<CompetitionRegistration> { Reg(1), Reg(2) };
            var c = new PatrolConstraints { Jobs = new Dictionary<int, List<StaffInterval>> { [1] = new() { Iv(0, 23) } } };
            var res = new FaltskyttePatrolGenerator().Generate(regs, PatrolSize, Interval, First, "Separate", 0, null, c);
            res.Unavoidable.Select(u => u.MemberId).Should().Equal(1);
            res.Patrols.SelectMany(p => p.Members).Select(m => m.MemberId).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Fact]
        public void De_som_ska_ga_tidigt_hamnar_forst_i_sin_grupp()
        {
            var regs = Enumerable.Range(1, 12).Select(i => Reg(i)).ToList();
            var c = new PatrolConstraints { Early = new HashSet<int> { 11, 12 } };
            var res = new FaltskyttePatrolGenerator().Generate(regs, PatrolSize, Interval, First, "Separate", 0, null, c);
            var first = res.Patrols.OrderBy(p => p.StartTime).First();
            first.StartTime.Should().Be(First);
            first.Members.Select(m => m.MemberId).Should().Contain(new[] { 11, 12 });
        }

        [Fact]
        public void Utan_begransningar_beter_sig_som_forr()
        {
            var regs = Enumerable.Range(1, 10).Select(i => Reg(i)).ToList();
            var a = new FaltskyttePatrolGenerator().Generate(regs, PatrolSize, Interval, First);
            var b = new FaltskyttePatrolGenerator().Generate(regs, PatrolSize, Interval, First, "Separate", 0, null, new PatrolConstraints());
            b.Patrols.Select(p => (p.StartTime, string.Join(",", p.Members.Select(m => m.MemberId))))
                .Should().Equal(a.Patrols.Select(p => (p.StartTime, string.Join(",", p.Members.Select(m => m.MemberId)))));
            b.Unavoidable.Should().BeEmpty();
        }
    }
}
