using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Tävlingsplaneringens rena regler (P1, 2026-10-21).</summary>
    public class PlanningRulesTests
    {
        private static PlanningMemberRow P(int id, string access, bool removed = false, int? memberId = 10) => new()
        {
            Id = id, PlanningId = 1, MemberId = memberId, DisplayName = "P" + id, Access = access,
            RemovedAt = removed ? DateTime.Now : null,
        };
        private static PlanningCaller C(PlanningMemberRow? p, bool link = false) => new(p, link);

        // ---------------------------------------------------------------- behörigheten

        [Fact]
        public void Nobody_reads_nothing()
        {
            PlanningAccess.CanReadContent(PlanningCaller.Nobody).Should().BeFalse();
            PlanningAccess.CanReadMessages(PlanningCaller.Nobody).Should().BeFalse();
        }

        [Fact]
        public void Only_the_invited_read_there_is_no_other_way_in()
        {
            // Stefan 2026-10-23: planeringsgruppen är sin egen enhet. Ingen förening, styrelse eller
            // roll ger läsrätt — anroparens typ har inte ens ett fält för det.
            typeof(PlanningCaller).GetConstructors().Single().GetParameters().Select(x => x.Name)
                .Should().BeEquivalentTo(new[] { "Participant", "ViaLink" });
        }

        [Fact]
        public void Follower_reads_everything_but_does_not_write_or_vote()
        {
            var c = C(P(1, PlanningAccessLevel.Follower));
            PlanningAccess.CanReadContent(c).Should().BeTrue();
            PlanningAccess.CanReadMessages(c).Should().BeTrue();
            PlanningAccess.CanWriteMessages(c).Should().BeFalse();
            PlanningAccess.CanUseMeetings(c).Should().BeFalse();
            PlanningAccess.CanEditPlanning(c).Should().BeFalse();
            PlanningAccess.CanInvite(c).Should().BeFalse();
            PlanningAccess.CanEditMeeting(c, new PlanningMeetingRow { CreatedBy = 1 }).Should().BeFalse();
        }

        [Fact]
        public void Followers_are_not_called_to_meetings()
        {
            PlanningAccess.IsCalledToMeetings(P(1, PlanningAccessLevel.Follower)).Should().BeFalse();
            PlanningAccess.IsCalledToMeetings(P(2, PlanningAccessLevel.Participant)).Should().BeTrue();
            PlanningAccess.IsCalledToMeetings(P(3, PlanningAccessLevel.Owner)).Should().BeTrue();
            PlanningAccess.IsCalledToMeetings(P(4, PlanningAccessLevel.Participant, removed: true)).Should().BeFalse();
        }

        [Fact]
        public void Levels_are_ordered_and_explained()
        {
            PlanningAccessLevel.All.Should().Equal(PlanningAccessLevel.Follower, PlanningAccessLevel.Participant,
                PlanningAccessLevel.Editor, PlanningAccessLevel.Owner);
            PlanningAccessLevel.All.Select(PlanningAccessLevel.Rank).Should().BeInAscendingOrder();
            PlanningAccessLevel.Label(PlanningAccessLevel.Owner).Should().Be("Sammankallande");
            PlanningAccessLevel.Label(PlanningAccessLevel.Follower).Should().Be("Följer");
            PlanningAccessLevel.All.Should().OnlyContain(a => PlanningAccessLevel.Help(a).Length > 20);
            // Den lagrade strängen byts aldrig: "Owner" visas som Sammankallande.
            PlanningAccessLevel.Normalize("owner").Should().Be("Owner");
        }

        [Fact]
        public void Editor_can_add_followers_but_only_a_convener_makes_conveners()
        {
            PlanningAccess.CanGrant(C(P(1, PlanningAccessLevel.Editor)), PlanningAccessLevel.Follower).Should().BeTrue();
            PlanningAccess.CanGrant(C(P(1, PlanningAccessLevel.Editor)), PlanningAccessLevel.Owner).Should().BeFalse();
            PlanningAccess.CanGrant(C(P(1, PlanningAccessLevel.Owner)), PlanningAccessLevel.Owner).Should().BeTrue();
            PlanningAccess.CanGrant(C(P(1, PlanningAccessLevel.Participant)), PlanningAccessLevel.Follower).Should().BeFalse();
        }

        [Fact]
        public void Removed_participant_has_no_access()
        {
            var c = C(P(1, PlanningAccessLevel.Owner, removed: true));
            PlanningAccess.CanReadContent(c).Should().BeFalse();
            PlanningAccess.CanReadMessages(c).Should().BeFalse();
        }

        [Fact]
        public void Participant_writes_and_books_meetings_but_does_not_edit_planning()
        {
            var c = C(P(1, PlanningAccessLevel.Participant));
            PlanningAccess.CanWriteMessages(c).Should().BeTrue();
            PlanningAccess.CanUseMeetings(c).Should().BeTrue(); // Stefan: alla i planeringen bokar möten
            PlanningAccess.CanEditPlanning(c).Should().BeFalse();
            PlanningAccess.CanInvite(c).Should().BeFalse();
        }

        [Fact]
        public void Editor_invites_but_cannot_grant_owner_or_manage()
        {
            var c = C(P(1, PlanningAccessLevel.Editor));
            PlanningAccess.CanInvite(c).Should().BeTrue();
            PlanningAccess.CanGrant(c, PlanningAccessLevel.Editor).Should().BeTrue();
            PlanningAccess.CanGrant(c, PlanningAccessLevel.Owner).Should().BeFalse();
            PlanningAccess.CanManageParticipants(c).Should().BeFalse();
        }

        [Fact]
        public void A_mail_link_never_edits_or_manages_the_planning()
        {
            var c = C(P(1, PlanningAccessLevel.Owner), link: true);
            PlanningAccess.CanWriteMessages(c).Should().BeTrue();
            PlanningAccess.CanUseMeetings(c).Should().BeTrue();
            PlanningAccess.CanEditPlanning(c).Should().BeFalse();
            PlanningAccess.CanManagePlanning(c).Should().BeFalse();
            PlanningAccess.CanInvite(c).Should().BeFalse();
        }

        [Fact]
        public void The_last_owner_cannot_be_removed_or_demoted()
        {
            var people = new List<PlanningMemberRow> { P(1, PlanningAccessLevel.Owner), P(2, PlanningAccessLevel.Participant) };
            PlanningAccess.WouldLeaveNoOwner(people, 1, null).Should().BeTrue();
            PlanningAccess.WouldLeaveNoOwner(people, 1, PlanningAccessLevel.Editor).Should().BeTrue();
            PlanningAccess.WouldLeaveNoOwner(people, 2, null).Should().BeFalse();
            people.Add(P(3, PlanningAccessLevel.Owner));
            PlanningAccess.WouldLeaveNoOwner(people, 1, null).Should().BeFalse();
        }

        [Fact]
        public void Only_the_author_edits_a_message()
        {
            var me = P(1, PlanningAccessLevel.Participant);
            var mine = new PlanningMessageRow { Id = 5, Kind = "Message", ParticipantId = 1 };
            var theirs = new PlanningMessageRow { Id = 6, Kind = "Message", ParticipantId = 2 };
            var system = new PlanningMessageRow { Id = 7, Kind = "System", SystemKey = "x" };
            PlanningAccess.CanEditMessage(C(me), mine).Should().BeTrue();
            PlanningAccess.CanEditMessage(C(me), theirs).Should().BeFalse();
            PlanningAccess.CanEditMessage(C(me), system).Should().BeFalse();
            PlanningAccess.CanEditMessage(C(P(1, PlanningAccessLevel.Owner)), theirs).Should().BeFalse();
        }

        // ---------------------------------------------------------------- notiserna

        [Fact]
        public void Default_notify_mode_is_one_mail_a_day()
        {
            PlanningNotifyMode.Default.Should().Be(PlanningNotifyMode.Daily);
            PlanningNotifyMode.Normalize(null).Should().Be(PlanningNotifyMode.Daily);
            PlanningNotifyMode.Normalize("skräp").Should().Be(PlanningNotifyMode.Daily);
        }

        [Theory]
        [InlineData(PlanningNotifyMode.Daily, false, true, false, true)]
        [InlineData(PlanningNotifyMode.All, true, true, true, true)]
        [InlineData(PlanningNotifyMode.Mentions, true, false, false, true)]
        [InlineData(PlanningNotifyMode.Off, false, false, false, false)]
        public void Notify_matrix(string mode, bool mention, bool digest, bool hourly, bool meeting)
        {
            PlanningNotifyRules.MailOnMention(mode).Should().Be(mention);
            PlanningNotifyRules.GetsDigest(mode).Should().Be(digest);
            PlanningNotifyRules.GetsHourlyPush(mode).Should().Be(hourly);
            PlanningNotifyRules.GetsMeetingMail(mode).Should().Be(meeting);
        }

        [Fact]
        public void Digest_goes_from_six_in_the_evening()
        {
            PlanningNotifyRules.DigestDue(new DateTime(2026, 10, 21, 17, 59, 0)).Should().BeFalse();
            PlanningNotifyRules.DigestDue(new DateTime(2026, 10, 21, 18, 0, 0)).Should().BeTrue();
            PlanningNotifyRules.DayKey(new DateTime(2026, 10, 21)).Should().Be(20261021);
        }

        [Fact]
        public void Hourly_push_waits_an_hour()
        {
            var now = new DateTime(2026, 10, 21, 12, 0, 0);
            PlanningNotifyRules.HourlyPushDue(null, now).Should().BeTrue();
            PlanningNotifyRules.HourlyPushDue(now.AddMinutes(-59), now).Should().BeFalse();
            PlanningNotifyRules.HourlyPushDue(now.AddMinutes(-60), now).Should().BeTrue();
        }

        [Fact]
        public void Meeting_reminder_steps()
        {
            var start = new DateTime(2026, 11, 4, 19, 0, 0);
            PlanningNotifyRules.MeetingReminderStep(start, start.AddHours(-25)).Should().BeNull();
            PlanningNotifyRules.MeetingReminderStep(start, start.AddHours(-24)).Should().Be("d1");
            PlanningNotifyRules.MeetingReminderStep(start, start.AddHours(-2)).Should().Be("d1");
            PlanningNotifyRules.MeetingReminderStep(start, start.AddMinutes(-60)).Should().Be("h1");
            PlanningNotifyRules.MeetingReminderStep(start, start).Should().BeNull();
            PlanningNotifyRules.MeetingReminderStep(start, start.AddMinutes(5)).Should().BeNull();
        }

        // ---------------------------------------------------------------- mötena

        private static PlanningMeetingOptionRow O(int id, int hour) => new() { Id = id, PlanningMeetingId = 1, StartsAt = new DateTime(2026, 11, 4, hour, 0, 0), EndsAt = new DateTime(2026, 11, 4, hour + 1, 0, 0) };
        private static PlanningMeetingVoteRow V(int opt, int pid, string a) => new() { PlanningMeetingId = 1, PlanningMeetingOptionId = opt, ParticipantId = pid, Answer = a };

        [Fact]
        public void No_votes_no_best_option()
        {
            PlanningMeetingRules.BestOption(new[] { O(1, 18), O(2, 19) }, Array.Empty<PlanningMeetingVoteRow>()).Should().BeNull();
        }

        [Fact]
        public void Best_option_most_yes_then_fewest_no_then_earliest()
        {
            var opts = new[] { O(1, 18), O(2, 19), O(3, 20) };
            PlanningMeetingRules.BestOption(opts, new[] { V(2, 1, "Yes"), V(2, 2, "Yes"), V(1, 1, "Yes") }).Should().Be(2);
            // lika många ja: färst nej vinner
            PlanningMeetingRules.BestOption(opts, new[] { V(1, 1, "Yes"), V(1, 2, "No"), V(3, 1, "Yes") }).Should().Be(3);
            // helt lika: tidigast
            PlanningMeetingRules.BestOption(opts, new[] { V(2, 1, "Yes"), V(3, 1, "Yes") }).Should().Be(2);
        }

        [Fact]
        public void Meeting_validation_rejects_unsafe_links_and_bad_times()
        {
            var t = new DateTime(2026, 11, 4, 19, 0, 0);
            PlanningService.ValidateMeeting(new("Möte", new() { (t, t.AddHours(1)) }, null, "javascript:alert(1)", null, null), true)
                .Should().NotBeNull();
            PlanningService.ValidateMeeting(new("Möte", new() { (t, t.AddHours(1)) }, null, "http://zoom.us/x", null, null), true)
                .Should().NotBeNull();
            PlanningService.ValidateMeeting(new("Möte", new() { (t, t.AddHours(1)) }, null, "https://teams.microsoft.com/l/x;y,z", null, null), true)
                .Should().BeNull();
            PlanningService.ValidateMeeting(new("Möte", new() { (t, t) }, null, null, null, null), true).Should().NotBeNull();
            PlanningService.ValidateMeeting(new("Möte", new() { (t, t.AddHours(1)), (t, t.AddHours(2)) }, null, null, null, null), true)
                .Should().NotBeNull();
            PlanningService.ValidateMeeting(new("", new() { (t, t.AddHours(1)) }, null, null, null, null), true).Should().NotBeNull();
        }

        [Fact]
        public void Planning_validation()
        {
            PlanningService.Validate("", null, null).Should().NotBeNull();
            PlanningService.Validate("SM 2028", new DateTime(2028, 8, 20), new DateTime(2028, 8, 14)).Should().NotBeNull();
            PlanningService.Validate("SM 2028", new DateTime(2028, 8, 14), new DateTime(2028, 8, 20)).Should().BeNull();
        }

        // ---------------------------------------------------------------- kalenderfilen

        [Fact]
        public void Ics_has_stable_uid_sequence_and_cancellation()
        {
            var m1 = new PlanningMeetingRow { Id = 42, Title = "Första träffen, SM 2028", Status = PlanningMeetingStatus.Scheduled, Sequence = 2,
                StartsAt = new DateTime(2026, 11, 4, 19, 0, 0), EndsAt = new DateTime(2026, 11, 4, 20, 0, 0), OnlineUrl = "https://teams.microsoft.com/l/a;b,c" };
            var m2 = new PlanningMeetingRow { Id = 43, Title = "Inställt", Status = PlanningMeetingStatus.Cancelled, Sequence = 3,
                StartsAt = new DateTime(2026, 11, 5, 19, 0, 0) };
            var ics = PlanningIcsBuilder.Build("SM 2028", new[] { m1, m2 }, "https://pistol.nu/tavlingsplanering/1", new DateTime(2026, 10, 21));
            var unfolded = ics.Replace("\r\n ", "");
            unfolded.Should().Contain("UID:planning-meeting-42@pistol.nu");
            unfolded.Should().Contain("SEQUENCE:2");
            unfolded.Should().Contain("DTSTART;TZID=Europe/Stockholm:20261104T190000");
            unfolded.Should().Contain("SUMMARY:Första träffen\\, SM 2028");
            unfolded.Should().Contain("URL:https://teams.microsoft.com/l/a;b,c"); // URL escapas inte
            unfolded.Should().Contain("METHOD:PUBLISH");
            unfolded.Should().Contain("STATUS:CANCELLED");
            ics.Split("\r\n").Should().OnlyContain(l => System.Text.Encoding.UTF8.GetByteCount(l) <= 75);
        }
    }
}
