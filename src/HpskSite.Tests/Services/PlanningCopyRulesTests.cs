using System;
using FluentAssertions;
using HpskSite.Models.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Kopiera planeringen (P3 steg C, 2026-10-23).</summary>
    public class PlanningCopyRulesTests
    {
        private static readonly DateTime Sm2028 = new(2028, 8, 15);   // tisdag
        private static readonly DateTime Sm2029 = new(2029, 8, 14);   // tisdag, 364 dagar senare

        [Fact]
        public void En_frist_ett_ar_senare_behaller_sitt_datum()
        {
            // Förbundets frist 1 oktober året före ska förbli 1 oktober — inte 30 september.
            PlanningCopyRules.ShiftDeadline(new DateTime(2027, 10, 1), Sm2028, Sm2029).Should().Be(new DateTime(2028, 10, 1));
        }

        [Fact]
        public void Bemanningens_dagar_flyttas_i_dagar_och_behaller_veckodagen()
        {
            var off = PlanningCopyRules.Offset(Sm2028, Sm2029);
            off.Should().Be(364);
            PlanningCopyRules.Shift(Sm2028, off)!.Value.DayOfWeek.Should().Be(Sm2028.DayOfWeek);
        }

        [Fact]
        public void En_kopia_nagra_manader_senare_flyttar_fristen_lika_manga_dagar()
        {
            var nov = new DateTime(2028, 11, 15);
            PlanningCopyRules.ShiftDeadline(new DateTime(2028, 6, 1), Sm2028, nov).Should().Be(new DateTime(2028, 6, 1).AddDays(92));
        }

        [Fact]
        public void Utan_dag_att_rakna_fran_foljer_datumet_inte_med()
        {
            PlanningCopyRules.ShiftDeadline(new DateTime(2027, 10, 1), null, Sm2029).Should().BeNull();
            PlanningCopyRules.ShiftDeadline(new DateTime(2027, 10, 1), Sm2028, null).Should().BeNull();
            PlanningCopyRules.Shift(Sm2028, null).Should().BeNull();
        }

        [Fact]
        public void Kopians_namn_har_ett_tak()
        {
            PlanningCopyRules.DefaultName("SM 2028").Should().Be("SM 2028 (kopia)");
            PlanningCopyRules.DefaultName(new string('x', 200)).Length.Should().BeLessThanOrEqualTo(200);
        }
    }
}
