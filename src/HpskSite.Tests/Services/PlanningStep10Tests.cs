using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using HpskSite.Models.Planning;
using HpskSite.Services;
using HpskSite.Services.Planning;
using NPoco;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Tävlingsplaneringen, omgång 3 steg 10: gäster, volym och rensning.</summary>
    public class PlanningStep10Tests
    {
        private static PlanningMemberRow P(int id, string access, int? memberId) => new()
        {
            Id = id, PlanningId = 1, MemberId = memberId, DisplayName = "P" + id, Access = access,
        };
        private static PlanningDocumentRow Doc(int addedBy) => new() { Id = 1, PlanningId = 1, AddedBy = addedBy, Title = "d" };

        // ---------------------------------------------------------------- delarna (progressivt avslöjande)

        [Fact]
        public void Parts_has_reads_the_comma_list_without_regard_to_case_or_spaces()
        {
            PlanningParts.Has("beslut, overskott", PlanningParts.Surplus).Should().BeTrue();
            PlanningParts.Has("BESLUT", PlanningParts.Decisions).Should().BeTrue();
            PlanningParts.Has(null, PlanningParts.Decisions).Should().BeFalse();
            PlanningParts.Has("beslutx", PlanningParts.Decisions).Should().BeFalse();
        }

        [Fact]
        public void Parts_with_adds_once_removes_and_drops_unknown_parts()
        {
            PlanningParts.With(null, PlanningParts.Surplus, true).Should().Be("overskott");
            PlanningParts.With("overskott", PlanningParts.Decisions, true).Should().Be("beslut,overskott");
            PlanningParts.With("beslut,overskott", PlanningParts.Decisions, true).Should().Be("beslut,overskott");
            PlanningParts.With("beslut,overskott", PlanningParts.Decisions, false).Should().Be("overskott");
            PlanningParts.With("skräp,beslut", PlanningParts.Surplus, false).Should().Be("beslut");
        }

        // ---------------------------------------------------------------- gästens dokument (K5)

        [Fact]
        public void A_guest_via_the_link_adds_documents_and_removes_only_their_own()
        {
            var guest = new PlanningCaller(P(5, PlanningAccessLevel.Participant, memberId: null), ViaLink: true);
            PlanningAccess.CanAddDocument(guest).Should().BeTrue();
            PlanningAccess.CanRemoveDocument(guest, Doc(addedBy: 5)).Should().BeTrue();
            PlanningAccess.CanRemoveDocument(guest, Doc(addedBy: 9)).Should().BeFalse();
        }

        [Fact]
        public void A_guest_editor_via_the_link_still_cannot_remove_someone_elses_document()
        {
            var guestEditor = new PlanningCaller(P(5, PlanningAccessLevel.Editor, memberId: null), ViaLink: true);
            PlanningAccess.CanRemoveDocument(guestEditor, Doc(addedBy: 9)).Should().BeFalse();
        }

        [Fact]
        public void A_member_with_an_account_logs_in_to_work_with_documents()
        {
            var memberViaLink = new PlanningCaller(P(6, PlanningAccessLevel.Editor, memberId: 77), ViaLink: true);
            PlanningAccess.CanAddDocument(memberViaLink).Should().BeFalse();
            PlanningAccess.CanRemoveDocument(memberViaLink, Doc(addedBy: 6)).Should().BeFalse();
            var signedIn = new PlanningCaller(P(6, PlanningAccessLevel.Editor, memberId: 77), ViaLink: false);
            PlanningAccess.CanAddDocument(signedIn).Should().BeTrue();
            PlanningAccess.CanRemoveDocument(signedIn, Doc(addedBy: 9)).Should().BeTrue();
        }

        [Fact]
        public void A_follower_guest_adds_nothing()
        {
            var follower = new PlanningCaller(P(5, PlanningAccessLevel.Follower, memberId: null), ViaLink: true);
            PlanningAccess.CanAddDocument(follower).Should().BeFalse();
        }

        // ---------------------------------------------------------------- medlemmens klubbar i en läsning

        [Fact]
        public void All_club_ids_put_the_primary_first_and_skip_duplicates_and_junk()
        {
            var row = new MemberPropertyRow();
            Values(row)["primaryClubId"] = "2604";
            Values(row)["memberClubIds"] = "2610, 2604,x, 0,2614";
            row.AllClubIds.Should().Equal(2604, 2610, 2614);
        }

        [Fact]
        public void All_club_ids_without_a_primary_still_read_the_others()
        {
            var row = new MemberPropertyRow();
            Values(row)["memberClubIds"] = "2610";
            row.AllClubIds.Should().Equal(2610);
        }

        private static Dictionary<string, string> Values(MemberPropertyRow r) =>
            (Dictionary<string, string>)typeof(MemberPropertyRow).GetProperty("Values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;

        // ---------------------------------------------------------------- rensningens tabellista (K7)

        /// <summary>
        /// Varje tabell (POCO med [TableName]) som bär PlanningId ska stå i <see cref="PlanningPurgeService.PlanningIdTables"/>
        /// — annars blir dess rader kvar för alltid när en borttagen planering rensas.
        /// </summary>
        [Fact]
        public void Purge_lists_every_table_that_carries_a_PlanningId()
        {
            // Rensas med egna satser i PurgeOneAsync. CompetitionApplication är föreningens och kopplas bara loss.
            var handledElsewhere = new[] { "Planning", "PlanningMeeting", "PlanningMeetingOption", "PlanningMeetingVote", "PlanningShootIntent", "CompetitionApplication" };
            var withPlanningId = typeof(PlanningRow).Assembly.GetTypes()
                .Select(t => (Type: t, Table: t.GetCustomAttribute<TableNameAttribute>()?.Value))
                .Where(x => x.Table != null && x.Type.GetProperty("PlanningId") != null)
                .Select(x => x.Table!)
                .Where(t => !handledElsewhere.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            withPlanningId.Should().NotBeEmpty();
            withPlanningId.Should().OnlyContain(t => PlanningPurgeService.PlanningIdTables.Contains(t, StringComparer.OrdinalIgnoreCase),
                "en ny tabell med PlanningId måste läggas i rensningens lista");
        }
    }
}
