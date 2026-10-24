using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Uppgifterna, mötets punkter och rollerna (Stefan 2026-10-23).</summary>
    public class PlanningTaskRulesTests
    {
        private static PlanningMemberRow P(int id, string access) => new()
        {
            Id = id, PlanningId = 1, MemberId = 10 + id, DisplayName = "P" + id, Access = access,
        };
        private static PlanningCaller C(PlanningMemberRow? p, bool link = false) => new(p, link);
        private static PlanningTaskRow T(int createdBy, int? assignee, string status = PlanningTaskStatus.NotStarted) => new()
        {
            Id = 1, PlanningId = 1, Title = "Boka marken", CreatedBy = createdBy, AssigneeParticipantId = assignee, Status = status,
        };
        private static readonly DateTime Now = new(2026, 11, 4, 18, 0, 0);
        private static PlanningMeetingRow M(DateTime? start, string status = PlanningMeetingStatus.Scheduled) => new()
        {
            Id = 7, PlanningId = 1, Title = "Avstämning", Status = status, StartsAt = start, EndsAt = start?.AddHours(1),
        };

        // ---------------------------------------------------------------- behörigheten

        [Fact]
        public void Den_som_har_uppgiften_far_rapportera_aven_om_hen_foljer()
        {
            var follower = P(5, PlanningAccessLevel.Follower);
            PlanningAccess.CanReportOnTask(C(follower), T(createdBy: 1, assignee: 5)).Should().BeTrue();
            PlanningAccess.CanReportOnTask(C(follower), T(createdBy: 1, assignee: 6)).Should().BeFalse();
        }

        [Fact]
        public void Deltar_far_inte_rapportera_pa_nagon_annans_uppgift_men_Kan_andra_far()
        {
            PlanningAccess.CanReportOnTask(C(P(2, PlanningAccessLevel.Participant)), T(1, 3)).Should().BeFalse();
            PlanningAccess.CanReportOnTask(C(P(2, PlanningAccessLevel.Editor)), T(1, 3)).Should().BeTrue();
        }

        [Fact]
        public void Uppgiften_andras_av_den_som_skapade_den_eller_av_Kan_andra()
        {
            PlanningAccess.CanEditTask(C(P(1, PlanningAccessLevel.Participant)), T(1, 3)).Should().BeTrue();
            PlanningAccess.CanEditTask(C(P(2, PlanningAccessLevel.Participant)), T(1, 3)).Should().BeFalse();
            PlanningAccess.CanEditTask(C(P(2, PlanningAccessLevel.Editor)), T(1, 3)).Should().BeTrue();
            PlanningAccess.CanEditTask(C(null), T(1, 3)).Should().BeFalse();
        }

        [Fact]
        public void Pa_ett_pagaende_mote_beslutar_alla_som_deltar_annars_bara_den_sammankallande()
        {
            var deltar = C(P(2, PlanningAccessLevel.Participant));
            PlanningAccess.CanDecideTask(deltar, atCurrentMeeting: true).Should().BeTrue();
            PlanningAccess.CanDecideTask(deltar, atCurrentMeeting: false).Should().BeFalse();
            PlanningAccess.CanDecideTask(C(P(3, PlanningAccessLevel.Owner)), atCurrentMeeting: false).Should().BeTrue();
            // Följer beslutar inte ens på mötet, och den sammankallande inte via en mejllänk.
            PlanningAccess.CanDecideTask(C(P(4, PlanningAccessLevel.Follower)), atCurrentMeeting: true).Should().BeFalse();
            PlanningAccess.CanDecideTask(C(P(3, PlanningAccessLevel.Owner), link: true), atCurrentMeeting: false).Should().BeFalse();
        }

        // ---------------------------------------------------------------- omgång 3: mejllänken (Expertgruppen K2)

        [Fact]
        public void Via_mejllanken_andrar_Kan_andra_bara_sitt_eget()
        {
            var editorLink = C(P(2, PlanningAccessLevel.Editor), link: true);
            PlanningAccess.CanEditTask(editorLink, T(createdBy: 1, assignee: 3)).Should().BeFalse();
            PlanningAccess.CanEditTask(editorLink, T(createdBy: 2, assignee: 3)).Should().BeTrue();
            PlanningAccess.CanReportOnTask(editorLink, T(1, 3)).Should().BeFalse();
            PlanningAccess.CanReportOnTask(editorLink, T(1, 2)).Should().BeTrue();
            PlanningAccess.CanEditMeeting(editorLink, new PlanningMeetingRow { CreatedBy = 1 }).Should().BeFalse();
            PlanningAccess.CanEditMeeting(editorLink, new PlanningMeetingRow { CreatedBy = 2 }).Should().BeTrue();
            PlanningAccess.CanRemoveTopic(editorLink, new PlanningMeetingTopicRow { AddedBy = 1 }).Should().BeFalse();
        }

        [Fact]
        public void Pa_motet_via_mejllanken_beslutar_man_bara_om_sin_egen_uppgift()
        {
            var p = new PlanningRow { Id = 1, DoneAtMeetingsOnly = true };
            var guestLink = C(P(5, PlanningAccessLevel.Participant), link: true);
            PlanningAccess.CanDecideTask(guestLink, true, p, T(createdBy: 1, assignee: 6)).Should().BeFalse();
            PlanningAccess.CanDecideTask(guestLink, true, p, T(createdBy: 1, assignee: 5)).Should().BeTrue();
            PlanningAccess.CanDecideTask(C(P(5, PlanningAccessLevel.Participant)), true, p, T(1, 6)).Should().BeTrue();
            PlanningAccess.CanDecideTask(guestLink, atCurrentMeeting: true).Should().BeFalse();
        }

        [Fact]
        public void En_tillagd_medlem_ar_inbjuden_tills_hen_varit_inne()
        {
            var added = new PlanningMemberRow { Id = 3, MemberId = 77, AddedBy = 10 };
            PlanningContactRules.IsPendingInvite(added).Should().BeTrue();
            PlanningContactRules.IsPendingInvite(new PlanningMemberRow { Id = 3, MemberId = 77, AddedBy = 10, LastSeenAt = Now }).Should().BeFalse();
            // Den som skapade planeringen, gästen och den som kom via tävlingens behörighet är aldrig "inbjudna".
            PlanningContactRules.IsPendingInvite(new PlanningMemberRow { MemberId = 77, AddedBy = 77 }).Should().BeFalse();
            PlanningContactRules.IsPendingInvite(new PlanningMemberRow { MemberId = null, AddedBy = 10, GuestEmail = "g@example.org" }).Should().BeFalse();
            PlanningContactRules.IsPendingInvite(new PlanningMemberRow { MemberId = 77, AddedBy = 10, ViaCompetition = true }).Should().BeFalse();
        }

        [Fact]
        public void Roller_ges_bara_av_den_sammankallande()
        {
            PlanningAccess.CanManageRoles(C(P(1, PlanningAccessLevel.Owner))).Should().BeTrue();
            PlanningAccess.CanManageRoles(C(P(2, PlanningAccessLevel.Editor))).Should().BeFalse();
        }

        [Fact]
        public void En_punkt_tas_bort_av_den_som_lade_till_den_eller_av_Kan_andra()
        {
            var topic = new PlanningMeetingTopicRow { Id = 1, AddedBy = 2, Text = "Parkeringen" };
            PlanningAccess.CanRemoveTopic(C(P(2, PlanningAccessLevel.Participant)), topic).Should().BeTrue();
            PlanningAccess.CanRemoveTopic(C(P(3, PlanningAccessLevel.Participant)), topic).Should().BeFalse();
            PlanningAccess.CanRemoveTopic(C(P(3, PlanningAccessLevel.Editor)), topic).Should().BeTrue();
        }

        // ---------------------------------------------------------------- mötet

        [Fact]
        public void Ett_bokat_mote_ar_pagaende_fran_tolv_timmar_fore_till_fjorton_dagar_efter()
        {
            PlanningTaskRules.MeetingIsCurrent(M(Now.AddHours(11)), Now).Should().BeTrue();
            PlanningTaskRules.MeetingIsCurrent(M(Now.AddHours(13)), Now).Should().BeFalse();
            PlanningTaskRules.MeetingIsCurrent(M(Now.AddDays(-13)), Now).Should().BeTrue();
            PlanningTaskRules.MeetingIsCurrent(M(Now.AddDays(-15)), Now).Should().BeFalse();
        }

        [Fact]
        public void Ett_foreslaget_eller_installt_mote_ar_aldrig_pagaende()
        {
            PlanningTaskRules.MeetingIsCurrent(M(Now, PlanningMeetingStatus.Proposed), Now).Should().BeFalse();
            PlanningTaskRules.MeetingIsCurrent(M(Now, PlanningMeetingStatus.Cancelled), Now).Should().BeFalse();
            PlanningTaskRules.MeetingIsCurrent(M(null), Now).Should().BeFalse();
            PlanningTaskRules.MeetingIsCurrent(null, Now).Should().BeFalse();
        }

        [Fact]
        public void Motet_visar_oppna_uppgifter_och_dem_det_sjalv_beslutade_klara_forst_klar_att_prova()
        {
            var tasks = new List<PlanningTaskRow>
            {
                new() { Id = 1, Title = "a", Status = PlanningTaskStatus.NotStarted },
                new() { Id = 2, Title = "b", Status = PlanningTaskStatus.ReadyForReview },
                new() { Id = 3, Title = "c", Status = PlanningTaskStatus.Done, DoneAtMeetingId = 7 },
                new() { Id = 4, Title = "d", Status = PlanningTaskStatus.Done, DoneAtMeetingId = 6 },
                new() { Id = 5, Title = "e", Status = PlanningTaskStatus.NeedsHelp, DeletedAt = Now },
            };
            PlanningTaskRules.ForMeeting(tasks, 7).Select(t => t.Id).Should().Equal(2, 1, 3);
        }

        // ---------------------------------------------------------------- lägena

        [Fact]
        public void Fortsatter_gor_klar_att_prova_till_pagar_och_lamnar_andra_lagen()
        {
            PlanningTaskRules.AfterContinue(PlanningTaskStatus.ReadyForReview).Should().Be(PlanningTaskStatus.InProgress);
            PlanningTaskRules.AfterContinue(PlanningTaskStatus.NeedsHelp).Should().Be(PlanningTaskStatus.NeedsHelp);
            PlanningTaskRules.AfterContinue(PlanningTaskStatus.NotStarted).Should().Be(PlanningTaskStatus.NotStarted);
        }

        [Fact]
        public void Angra_lagger_tillbaka_laget_fore_beslutet()
        {
            PlanningTaskRules.AfterUndo(PlanningTaskStatus.NeedsHelp).Should().Be(PlanningTaskStatus.NeedsHelp);
            PlanningTaskRules.AfterUndo(null).Should().Be(PlanningTaskStatus.ReadyForReview);
            PlanningTaskRules.AfterUndo(PlanningTaskStatus.Done).Should().Be(PlanningTaskStatus.ReadyForReview);
        }

        [Fact]
        public void Den_ansvarige_kan_inte_satta_Utford_sjalv()
        {
            PlanningTaskStatus.AssigneeSettable.Should().NotContain(PlanningTaskStatus.Done);
            PlanningTaskStatus.AssigneeSettable.Should().Contain(PlanningTaskStatus.ReadyForReview);
        }

        [Fact]
        public void Lagenas_lagrade_strangar_byts_aldrig()
        {
            PlanningTaskStatus.All.Should().Equal("NotStarted", "InProgress", "NeedsHelp", "ReadyForReview", "Done");
            PlanningTaskStatus.Label(PlanningTaskStatus.ReadyForReview).Should().Be("Klar att pröva");
            PlanningTaskStatus.Label(PlanningTaskStatus.Done).Should().Be("Utförd");
        }

        // ---------------------------------------------------------------- rollerna

        [Fact]
        public void Rollistan_rensas_fran_dubbletter_och_tomma()
        {
            PlanningTaskRules.CleanRoleNames(new[] { " Banansvarig ", "banansvarig", "", "Kassör" })
                .Should().Equal("Banansvarig", "Kassör");
        }

        [Fact]
        public void Rollistan_kapas_vid_taket()
        {
            PlanningTaskRules.CleanRoleNames(Enumerable.Range(1, 20).Select(i => "Roll " + i))
                .Should().HaveCount(PlanningTaskRules.MaxRolesPerPerson);
        }

        [Fact]
        public void En_roll_ur_bemanningens_katalog_far_katalogens_nyckel_egen_text_far_ingen()
        {
            PlanningTaskRules.RoleKeyFor("Banansvarig").Should().Be("banansvarig");
            PlanningTaskRules.RoleKeyFor("Kontakt med markägaren").Should().BeNull();
            PlanningTaskRules.RoleSuggestions().Select(r => r.Name).Should().Contain(new[] { "Banansvarig", "Kontakt med kommunen" });
        }

        [Fact]
        public void Rubriken_kravs()
        {
            PlanningTaskRules.ValidateTitle("  ").Should().NotBeNull();
            PlanningTaskRules.ValidateTitle("Boka marken").Should().BeNull();
        }
    }
}
