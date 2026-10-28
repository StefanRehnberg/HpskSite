using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Meddela placeringarna (Stefan 2026-10-28): en placering tilldelas, den begärs inte. Reglerna som inte kräver databas eller mejl: vilka rader som ska meddelas,
    /// vem som är "samma människa", hur ett gammalt Invited/Declined läses, och hur personens tillstånd rullas upp.
    /// </summary>
    public class PlacementNoticeTests
    {
        private static StaffAssignment Row(string status = "Planned", DateTime? notified = null, int? member = 5, string? email = null)
            => new() { Id = 1, MemberId = member, Email = email, Status = status, NotifiedAt = notified, DisplayName = "Anna Ek", RoleKey = "markor" };

        [Theory]
        [InlineData("Planned", false, true)]
        [InlineData("Planned", true, false)]      // redan meddelad: aldrig ett andra mejl
        [InlineData("Accepted", false, false)]    // anmäld själv: ingen placering att meddela
        [InlineData("Confirmed", false, false)]
        [InlineData("Declined", false, true)]     // ett kvarliggande gammalt värde är en vanlig placering
        public void Bara_en_placering_som_ingen_fatt_besked_om_ska_meddelas(string status, bool notified, bool expected)
            => PlacementNoticeService.Due(Row(status, notified ? new DateTime(2028, 8, 1) : null)).Should().Be(expected);

        [Fact]
        public void Samma_manniska_ar_medlemmen_annars_e_postadressen()
        {
            PlacementNoticeService.PersonKey(Row(member: 12)).Should().Be("m:12");
            PlacementNoticeService.PersonKey(Row(member: null, email: " Kalle@Example.se ")).Should().Be("e:kalle@example.se");
            PlacementNoticeService.PersonKey(Row(member: null, email: "  ")).Should().BeNull();   // ingen väg att nå personen
            // Två rader med samma medlem blir EN grupp: ett mejl.
            new[] { Row(member: 12), Row(member: 12), Row(member: 13) }.GroupBy(PlacementNoticeService.PersonKey).Should().HaveCount(2);
        }

        [Fact]
        public void Amnet_namnar_placeringen_och_tavlingen_och_aldrig_en_forfragan()
        {
            PlacementNoticeService.Subject(1, "SM Fält 2028").Should().Be("Du har fått en placering: SM Fält 2028");
            PlacementNoticeService.Subject(3, "SM Fält 2028").Should().Be("Du har fått 3 placeringar: SM Fält 2028").And.NotContain("örfrågan");
        }

        [Fact]
        public void Tiden_i_mejlet_ar_dag_och_klockslag_eller_hela_dagen_och_aldrig_en_gissning()
        {
            var timed = new StaffAssignment { StartsAt = new DateTime(2028, 8, 12, 8, 0, 0), EndsAt = new DateTime(2028, 8, 12, 13, 0, 0) };
            PlacementNoticeService.WhenText(timed, null).Should().Be("lördag 12 augusti 08:00–13:00");
            PlacementNoticeService.WhenText(new StaffAssignment { DayDate = new DateTime(2028, 8, 12) }, null).Should().Be("lördag 12 augusti hela dagen");
            PlacementNoticeService.WhenText(new StaffAssignment(), null).Should().BeEmpty();
        }

        [Theory]
        [InlineData("Planned", "Planned")]
        [InlineData("Accepted", "Accepted")]
        [InlineData("Confirmed", "Confirmed")]
        [InlineData("Invited", "Planned")]       // gamla värden läses som en vanlig placering
        [InlineData("Declined", "Planned")]
        [InlineData(null, "Planned")]
        [InlineData("skräp", "Planned")]
        public void Gamla_och_okanda_statusar_laeses_som_Planned(string? status, string expected)
            => StaffAssignmentStatus.Normalize(status).Should().Be(expected);

        [Fact]
        public void Personens_tillstand_rullas_upp_sa_att_det_som_inte_ar_meddelat_syns_forst()
        {
            var t = new DateTime(2028, 8, 1);
            PersonState.OfRows(new (string?, DateTime?)[] { ("Planned", t), ("Planned", null) }).Should().Be(PersonState.Planned);          // en ej meddelad räcker
            PersonState.OfRows(new (string?, DateTime?)[] { ("Planned", t), ("Confirmed", null) }).Should().Be(PersonState.Notified);
            PersonState.OfRows(new (string?, DateTime?)[] { ("Accepted", null) }).Should().Be(PersonState.Accepted);
            PersonState.OfRows(new (string?, DateTime?)[] { ("Confirmed", null) }).Should().Be(PersonState.Confirmed);
            PersonState.OfRows(Array.Empty<(string?, DateTime?)>()).Should().Be(PersonState.Unplaced);
            PersonState.OfRow("Invited", null).Should().Be(PersonState.Notified);                                                              // ett kvarliggande Invited är redan meddelat
        }

        [Fact]
        public void Statusetiketterna_ar_placerad_meddelad_anmald_sjalv_och_bekraftad()
        {
            PersonState.Planned.Should().Be("Placerad");
            PersonState.Notified.Should().Be("Meddelad");
            PersonState.Accepted.Should().Be("Anmäld själv");
            PersonState.Confirmed.Should().Be("Bekräftad");
            // Den som inte är meddelad sorteras före den som är det: det är den som kräver något av arrangören.
            PersonState.Priority(PersonState.Planned).Should().BeLessThan(PersonState.Priority(PersonState.Notified));
        }

        [Fact]
        public void Angra_tar_aldrig_bort_en_placering_som_personen_fatt_besked_om()
        {
            var t0 = new DateTime(2028, 8, 1, 10, 0, 0);
            var fresh = new StaffAssignment { Status = "Planned", CreatedDate = t0, ModifiedDate = t0 };
            StaffProposalService.UntouchedSinceApply(fresh).Should().BeTrue();
            // Att meddela rör varken status eller ModifiedDate — märket NotifiedAt är det enda som skiljer en meddelad rad från ett orört förslag.
            StaffProposalService.UntouchedSinceApply(new StaffAssignment { Status = "Planned", CreatedDate = t0, ModifiedDate = t0, NotifiedAt = t0.AddMinutes(5) }).Should().BeFalse();
        }

        [Fact]
        public void Kvallspaminnelsen_gar_bara_till_den_som_ar_meddelad_eller_sjalvanmald()
        {
            HpskSite.Services.Schedule.StaffWeekDigest.Remindable("Planned", null).Should().BeFalse();
            HpskSite.Services.Schedule.StaffWeekDigest.Remindable("Planned", new DateTime(2028, 8, 1)).Should().BeTrue();
            HpskSite.Services.Schedule.StaffWeekDigest.Remindable("Accepted", null).Should().BeTrue();
        }
    }
}
