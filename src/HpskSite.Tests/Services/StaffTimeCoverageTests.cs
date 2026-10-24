using System;
using System.Collections.Generic;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Omgång 3 (Expertgruppen 3.2): tiden är sanningen, och behovet för en station och ett pass.</summary>
    public class StaffTimeCoverageTests
    {
        private static (TimeSpan, TimeSpan)? S(int from, int to) => (TimeSpan.FromHours(from), TimeSpan.FromHours(to));

        [Fact]
        public void Tva_skift_om_fem_ar_fem_pa_plats_inte_tio()
        {
            var spans = new List<(TimeSpan, TimeSpan)?>();
            for (var i = 0; i < 5; i++) spans.Add(S(8, 12));
            for (var i = 0; i < 5; i++) spans.Add(S(12, 16));
            StaffTimeCoverage.Peak(spans).Should().Be(5);
        }

        [Fact]
        public void En_overlappning_raknas()
        {
            StaffTimeCoverage.Peak(new[] { S(8, 12), S(11, 14), S(13, 15) }).Should().Be(2);
        }

        [Fact]
        public void Utan_tid_ar_med_hela_dagen()
        {
            StaffTimeCoverage.Peak(new[] { null, S(8, 12), S(12, 16) }).Should().Be(2);
        }

        [Fact]
        public void I_ett_pass_raknas_bara_det_som_overlappar_passet()
        {
            var spans = new[] { S(8, 12), S(8, 12), S(12, 16), null };
            StaffTimeCoverage.Peak(spans, S(8, 12)).Should().Be(3);   // två fm + en heldag; em börjar när fm slutar
            StaffTimeCoverage.Peak(spans, S(12, 16)).Should().Be(2);  // em + heldag
        }

        [Fact]
        public void Lasbara_och_olasbara_tider()
        {
            StaffTimeCoverage.ParseRange("08:00–12:00").Should().Be(S(8, 12));
            StaffTimeCoverage.ParseRange("8-12").Should().Be(S(8, 12));
            StaffTimeCoverage.ParseRange("Lör FM 06–13").Should().BeNull();
            StaffTimeCoverage.ParseRange("12-8").Should().BeNull();
        }

        private static StaffCrewNeed N(string kind, int count, string? key = null, int? pass = null, DateTime? day = null) =>
            new() { ScopeKind = kind, RoleKey = "markor", Count = count, ScopeKey = key, PassId = pass, DayDate = day };

        private static readonly DateTime Sat = new(2028, 7, 15);

        [Fact]
        public void Det_mest_bestamda_behovet_galler()
        {
            var rows = new[]
            {
                N(CrewNeedScope.Station, 2),
                N(CrewNeedScope.Station, 3, key: "5"),
                N(CrewNeedScope.Station, 4, pass: 9, day: Sat),
                N(CrewNeedScope.Station, 1, key: "5", pass: 9, day: Sat),
            };
            CrewNeedRules.For(rows, CrewNeedScope.Station, "markor", Sat, "5", 9).Should().Be(1);
            CrewNeedRules.For(rows, CrewNeedScope.Station, "markor", Sat, "5", null).Should().Be(3);
            CrewNeedRules.For(rows, CrewNeedScope.Station, "markor", Sat, "4", 9).Should().Be(4);
            CrewNeedRules.For(rows, CrewNeedScope.Station, "markor", Sat, "4", null).Should().Be(2);
        }

        [Fact]
        public void Dagens_lasning_ser_inte_stationens_och_passets_rader()
        {
            var rows = new[] { N(CrewNeedScope.All, 2), N(CrewNeedScope.All, 7, pass: 9, day: Sat), N(CrewNeedScope.All, 9, key: "3") };
            CrewNeedRules.ForDay(rows, CrewNeedScope.All, "markor", Sat).Should().Be(2);
            CrewNeedRules.EffectiveFor(rows, CrewNeedScope.All, Sat).Should().ContainSingle().Which.Count.Should().Be(2);
        }

        [Fact]
        public void Bara_pass_som_avviker_fran_sin_dag_lagras()
        {
            var plan = CrewNeedRules.PlanPasses(2, new[] { (Sat, 3) }, new[] { (9, Sat, 3), (10, Sat, 5), (11, Sat.AddDays(1), 2) });
            plan.Should().ContainSingle().Which.PassId.Should().Be(10);
        }
    }
}
