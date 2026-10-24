using System;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Omgång 3 steg 9: krockar över hela veckan (familjen).</summary>
    public class FamilyClashRulesTests
    {
        private static readonly DateTime Sat = new(2028, 8, 19);
        private static (TimeSpan, TimeSpan)? S(int a, int b) => (TimeSpan.FromHours(a), TimeSpan.FromHours(b));
        private static FamilyClashRules.Slot Slot(int planning, (TimeSpan, TimeSpan)? span, DateTime? day = null, string who = "m:1") =>
            new(who, "Kalle", planning, day ?? Sat, span, "uppdrag " + planning);

        [Fact]
        public void Tva_uppdrag_i_olika_delar_av_veckan_samtidigt_krockar()
        {
            var r = FamilyClashRules.Find(new[] { Slot(1, S(8, 12)), Slot(2, S(11, 14)) }, Array.Empty<FamilyClashRules.Start>(), Array.Empty<FamilyClashRules.Intent>());
            r.Should().ContainSingle().Which.Kind.Should().Be(FamilyClashRules.KindAssignment);
        }

        [Fact]
        public void Inom_samma_planering_raknar_rutnatet_sjalvt()
        {
            FamilyClashRules.Find(new[] { Slot(1, S(8, 12)), Slot(1, S(11, 14)) }, Array.Empty<FamilyClashRules.Start>(), Array.Empty<FamilyClashRules.Intent>())
                .Should().BeEmpty();
        }

        [Fact]
        public void Kant_i_kant_och_annan_dag_ar_ingen_krock()
        {
            FamilyClashRules.Find(new[] { Slot(1, S(8, 12)), Slot(2, S(12, 16)), Slot(3, S(8, 12), Sat.AddDays(1)) },
                Array.Empty<FamilyClashRules.Start>(), Array.Empty<FamilyClashRules.Intent>()).Should().BeEmpty();
        }

        [Fact]
        public void Heldag_krockar_med_allt_samma_dag()
        {
            FamilyClashRules.Find(new[] { Slot(1, null), Slot(2, S(15, 16)) }, Array.Empty<FamilyClashRules.Start>(), Array.Empty<FamilyClashRules.Intent>())
                .Should().ContainSingle();
        }

        [Fact]
        public void Uppdrag_under_egen_start()
        {
            var r = FamilyClashRules.Find(new[] { Slot(1, S(9, 11)) },
                new[] { new FamilyClashRules.Start("m:1", 77, Sat, S(10, 12), "egen start") }, Array.Empty<FamilyClashRules.Intent>());
            r.Should().ContainSingle().Which.Kind.Should().Be(FamilyClashRules.KindStart);
        }

        [Fact]
        public void Avsikt_att_tavla_samma_dag_varnar_tills_starten_finns()
        {
            var intent = new FamilyClashRules.Intent("m:1", "Kalle", 77, Sat, Sat.AddDays(1), "tänker tävla i Fält");
            FamilyClashRules.Find(new[] { Slot(1, S(8, 12), Sat.AddDays(1)) }, Array.Empty<FamilyClashRules.Start>(), new[] { intent })
                .Should().ContainSingle().Which.Kind.Should().Be(FamilyClashRules.KindIntent);
            // Med en start i tävlingen gäller starten — avsikten är inte längre en gissning.
            FamilyClashRules.Find(new[] { Slot(1, S(8, 12), Sat.AddDays(1)) }, new[] { new FamilyClashRules.Start("m:1", 77, Sat, S(14, 16), "start") }, new[] { intent })
                .Should().BeEmpty();
        }

        [Fact]
        public void En_annan_person_krockar_inte()
        {
            FamilyClashRules.Find(new[] { Slot(1, S(8, 12)), Slot(2, S(8, 12), who: "n:lisa") }, Array.Empty<FamilyClashRules.Start>(), Array.Empty<FamilyClashRules.Intent>())
                .Should().BeEmpty();
        }
    }
}
