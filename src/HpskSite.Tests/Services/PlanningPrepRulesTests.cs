using System;
using FluentAssertions;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>P2 (2026-10-23): förberedelsernas regler i planeringen — sista dag, inklistring, Utförd direkt.</summary>
    public class PlanningPrepRulesTests
    {
        private static PlanningCaller Caller(string access, int pid = 7, bool viaLink = false) =>
            new(new PlanningMemberRow { Id = pid, PlanningId = 1, Access = access }, viaLink);

        [Fact]
        public void DagarFore_raknas_fran_ankaret()
        {
            PlanningTaskRules.DueFromDaysBefore(10, new DateTime(2027, 5, 20)).Should().Be(new DateTime(2027, 5, 10));
            PlanningTaskRules.DueFromDaysBefore(10, null).Should().BeNull();
            PlanningTaskRules.DueFromDaysBefore(null, new DateTime(2027, 5, 20)).Should().BeNull();
        }

        [Fact]
        public void Ankaret_ar_tavlingens_dag_annars_planeringens_forsta()
        {
            PlanningTaskRules.Anchor(new DateTime(2027, 6, 1), new DateTime(2027, 1, 1)).Should().Be(new DateTime(2027, 6, 1));
            PlanningTaskRules.Anchor(null, new DateTime(2027, 1, 1)).Should().Be(new DateTime(2027, 1, 1));
            PlanningTaskRules.Anchor(DateTime.MinValue, null).Should().BeNull();
        }

        [Fact]
        public void Paminnelsefonstret_ar_tre_dagar_fore_till_och_med_sista_dagen()
        {
            var due = new DateTime(2027, 5, 10);
            PlanningTaskRules.DueReminderWindow(due, new DateTime(2027, 5, 6)).Should().BeFalse();
            PlanningTaskRules.DueReminderWindow(due, new DateTime(2027, 5, 7)).Should().BeTrue();
            PlanningTaskRules.DueReminderWindow(due, new DateTime(2027, 5, 10)).Should().BeTrue();
            PlanningTaskRules.DueReminderWindow(due, new DateTime(2027, 5, 11)).Should().BeFalse();
        }

        [Fact]
        public void Inklistring_tabb_eller_semikolon_och_oläsligt_datum_ger_fel()
        {
            var rows = PlanningTaskRules.ParsePaste("Beställ tavlor\tKalle\t2027-05-01\n\nHyr tält;;\nBad\t\t2027-13-45");
            rows.Should().HaveCount(3);
            rows[0].Should().Be(new PlanningTaskRules.PasteRow("Beställ tavlor", "Kalle", new DateTime(2027, 5, 1), null));
            rows[1].Title.Should().Be("Hyr tält"); rows[1].Who.Should().BeNull(); rows[1].Error.Should().BeNull();
            rows[2].Error.Should().NotBeNull();
        }

        [Fact]
        public void Utford_direkt_nar_valet_ar_av()
        {
            var off = new PlanningRow { DoneAtMeetingsOnly = false };
            var on = new PlanningRow { DoneAtMeetingsOnly = true };
            var mine = new PlanningTaskRow { AssigneeParticipantId = 7 };
            var other = new PlanningTaskRow { AssigneeParticipantId = 99 };
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Participant), false, off, mine).Should().BeTrue();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Participant), false, off, other).Should().BeFalse();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Editor), false, off, other).Should().BeTrue();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Editor), false, on, other).Should().BeFalse();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Participant), false, on, mine).Should().BeFalse();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Owner), false, on, other).Should().BeTrue();
            PlanningAccess.CanDecideTask(Caller(PlanningAccessLevel.Participant), true, on, other).Should().BeTrue();
        }

        [Fact]
        public void Dokument_tas_bort_av_den_som_lade_till_eller_Kan_andra()
        {
            var d = new PlanningDocumentRow { AddedBy = 7 };
            PlanningAccess.CanRemoveDocument(Caller(PlanningAccessLevel.Participant), d).Should().BeTrue();
            PlanningAccess.CanRemoveDocument(Caller(PlanningAccessLevel.Participant, pid: 8), d).Should().BeFalse();
            PlanningAccess.CanRemoveDocument(Caller(PlanningAccessLevel.Editor, pid: 8), d).Should().BeTrue();
            PlanningAccess.CanRemoveDocument(Caller(PlanningAccessLevel.Editor, viaLink: true), d).Should().BeFalse();
        }

        [Theory]
        [InlineData(PlanningTaskStatus.Done, "Klar")]
        [InlineData(PlanningTaskStatus.InProgress, "Pagar")]
        [InlineData(PlanningTaskStatus.ReadyForReview, "Pagar")]
        [InlineData(PlanningTaskStatus.NeedsHelp, "Blockerad")]
        [InlineData(PlanningTaskStatus.NotStarted, "Planerad")]
        public void Bemanningens_och_bladets_ord_for_lagena(string status, string legacy) =>
            WorkBreakdownService.LegacyStatus(status).Should().Be(legacy);
    }
}
