using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Förslag och beslut (P3 steg B, 2026-10-23).</summary>
    public class PlanningDecisionRulesTests
    {
        private static PlanningMemberRow P(int id, string access, bool removed = false) => new()
        {
            Id = id, PlanningId = 1, MemberId = 10 + id, DisplayName = "P" + id, Access = access,
            RemovedAt = removed ? new DateTime(2026, 10, 1) : null,
        };
        private static PlanningCaller C(PlanningMemberRow? p, bool link = false) => new(p, link);
        private static PlanningDecisionRow D(int proposedBy, string status = PlanningDecisionStatus.Open, DateTime? edited = null,
            string decider = PlanningDecisionMaker.Group) => new()
        {
            Id = 3, PlanningId = 1, Title = "Varbergs PK bemannar station 3–5", ProposedBy = proposedBy, Status = status, EditedAt = edited,
            DeciderKind = decider,
        };
        private static PlanningDecisionResponseRow R(int pid, DateTime at, string answer = PlanningDecisionAnswer.Yes) => new()
        {
            DecisionId = 3, PlanningId = 1, ParticipantId = pid, Answer = answer, AnsweredAt = at,
        };

        [Fact]
        public void Den_som_foljer_far_inte_lagga_fram_eller_svara()
        {
            var f = C(P(1, PlanningAccessLevel.Follower));
            PlanningAccess.CanProposeDecision(f).Should().BeFalse();
            PlanningAccess.CanRespondToDecision(f).Should().BeFalse();
            var d = C(P(2, PlanningAccessLevel.Participant));
            PlanningAccess.CanProposeDecision(d).Should().BeTrue();
            PlanningAccess.CanRespondToDecision(d).Should().BeTrue();
        }

        [Fact]
        public void Svar_via_mejllanken_tillats_men_inte_att_registrera_beslutet()
        {
            var viaLink = C(P(2, PlanningAccessLevel.Editor), link: true);
            PlanningAccess.CanRespondToDecision(viaLink).Should().BeTrue();
            PlanningAccess.CanDecideDecision(viaLink).Should().BeFalse();
            PlanningAccess.CanDecideDecision(C(P(2, PlanningAccessLevel.Editor))).Should().BeTrue();
            PlanningAccess.CanDecideDecision(C(P(2, PlanningAccessLevel.Participant))).Should().BeFalse();
        }

        [Fact]
        public void Andra_forslaget_far_den_som_lade_fram_det_eller_Kan_andra_och_bara_medan_det_ar_oppet()
        {
            PlanningAccess.CanEditDecision(C(P(5, PlanningAccessLevel.Participant)), D(proposedBy: 5)).Should().BeTrue();
            PlanningAccess.CanEditDecision(C(P(6, PlanningAccessLevel.Participant)), D(proposedBy: 5)).Should().BeFalse();
            PlanningAccess.CanEditDecision(C(P(6, PlanningAccessLevel.Editor)), D(proposedBy: 5)).Should().BeTrue();
            PlanningAccess.CanEditDecision(C(P(5, PlanningAccessLevel.Participant)), D(5, PlanningDecisionStatus.Decided)).Should().BeFalse();
        }

        [Fact]
        public void Ett_svar_fore_andringen_markas_ut()
        {
            var edited = new DateTime(2026, 10, 23, 12, 0, 0);
            var d = D(1, edited: edited);
            PlanningDecisionRules.AnsweredBeforeEdit(d, R(2, edited.AddMinutes(-5))).Should().BeTrue();
            PlanningDecisionRules.AnsweredBeforeEdit(d, R(2, edited.AddMinutes(5))).Should().BeFalse();
            PlanningDecisionRules.AnsweredBeforeEdit(D(1), R(2, edited)).Should().BeFalse();
        }

        [Fact]
        public void Vi_vantar_bara_pa_dem_som_deltar_och_inte_redan_svarat()
        {
            var people = new List<PlanningMemberRow>
            {
                P(1, PlanningAccessLevel.Owner), P(2, PlanningAccessLevel.Participant), P(3, PlanningAccessLevel.Follower),
                P(4, PlanningAccessLevel.Editor, removed: true), P(5, PlanningAccessLevel.Participant),
            };
            var waiting = PlanningDecisionRules.Waiting(D(1), people, new[] { R(2, DateTime.Now) }).Select(p => p.Id);
            waiting.Should().BeEquivalentTo(new[] { 1, 5 });
        }

        [Fact]
        public void Ett_forslag_till_en_styrelse_vantar_inte_pa_gruppen_och_gruppen_svarar_inte()
        {
            var people = new List<PlanningMemberRow> { P(1, PlanningAccessLevel.Owner), P(2, PlanningAccessLevel.Participant) };
            var board = D(1, decider: PlanningDecisionMaker.Board);
            PlanningDecisionRules.Waiting(board, people, Array.Empty<PlanningDecisionResponseRow>()).Should().BeEmpty();
            PlanningDecisionRules.RespondRefusal(board).Should().Contain("styrelse");
            PlanningDecisionRules.RespondRefusal(D(1)).Should().BeNull();
            PlanningDecisionRules.RespondRefusal(D(1, PlanningDecisionStatus.Decided)).Should().Contain("avgjort");
        }

        [Fact]
        public void Bara_ett_forslag_till_en_styrelse_skickas_och_inte_igen_medan_det_ligger_dar()
        {
            var board = D(1, decider: PlanningDecisionMaker.Board);
            PlanningDecisionRules.SendRefusal(board, atBoard: false).Should().BeNull();
            PlanningDecisionRules.SendRefusal(board, atBoard: true).Should().Contain("redan hos styrelsen");
            PlanningDecisionRules.SendRefusal(D(1, PlanningDecisionStatus.Decided, decider: PlanningDecisionMaker.Board), atBoard: false).Should().Contain("avgjort");
            PlanningDecisionRules.SendRefusal(D(1), atBoard: false).Should().Contain("gruppen");
        }

        [Theory]
        [InlineData("yes", PlanningDecisionAnswer.Yes)]
        [InlineData("No", PlanningDecisionAnswer.No)]
        [InlineData("Can", null)]
        [InlineData("kanske", null)]
        [InlineData(null, null)]
        public void Svaret_ar_bara_Ja_eller_Nej(string? input, string? expected) =>
            PlanningDecisionAnswer.Normalize(input).Should().Be(expected);

        [Fact]
        public void Svarens_etiketter_ar_Ja_och_Nej()
        {
            PlanningDecisionAnswer.Label(PlanningDecisionAnswer.Yes).Should().Be("Ja");
            PlanningDecisionAnswer.Label(PlanningDecisionAnswer.No).Should().Be("Nej");
        }

        [Fact]
        public void Rubriken_kravs_och_har_ett_tak()
        {
            PlanningDecisionRules.ValidateTitle("  ").Should().NotBeNull();
            PlanningDecisionRules.ValidateTitle(new string('x', 301)).Should().NotBeNull();
            PlanningDecisionRules.ValidateTitle("Vi tar Skrea").Should().BeNull();
        }
    }
}
