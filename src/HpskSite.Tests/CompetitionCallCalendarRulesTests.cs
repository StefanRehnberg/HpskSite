using FluentAssertions;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.CompetitionCalls;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Kretsutlysningen F5: vem som ser vilken utlysning i kretskalendern (planens matris 4.5, N10).</summary>
    public class CompetitionCallCalendarRulesTests
    {
        private static CompetitionCall Call(string status, DateTime? date = null, int? competitionId = null) => new()
        {
            Id = 1, RegionId = 3294, Year = 2027, Name = "KM Precision", Status = status,
            CompetitionDate = date ?? new DateTime(2027, 5, 1), CompetitionId = competitionId,
            AwardedClubId = status is CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad ? 9003 : null
        };

        private static string? Status(CompetitionCall c, bool planning = false, bool own = true, bool admin = false, bool manages = false, bool published = false) =>
            CompetitionCallRules.CalendarStatus(c, planning, own, admin, manages, published);

        [Fact]
        public void Skytten_ser_bara_utsedda()
        {
            Status(Call(CompetitionCallStatus.Planering)).Should().BeNull();
            Status(Call(CompetitionCallStatus.Utlyst)).Should().BeNull();
            Status(Call(CompetitionCallStatus.Tilldelad)).Should().Be(KretsCalendarStatus.Utsedd);
        }

        [Fact]
        public void Installd_och_datumlos_visas_aldrig()
        {
            Status(Call(CompetitionCallStatus.Installd), planning: true, manages: true, admin: true).Should().BeNull();
            var noDate = Call(CompetitionCallStatus.Tilldelad); noDate.CompetitionDate = null;
            Status(noDate, planning: true, manages: true, admin: true).Should().BeNull();
        }

        [Fact]
        public void Planering_bara_for_kretsens_egen_styrelse_i_planeringskalendern()
        {
            var c = Call(CompetitionCallStatus.Planering);
            Status(c, planning: true, manages: true).Should().Be(KretsCalendarStatus.Planering);
            Status(c, planning: true, admin: true).Should().BeNull("en klubbadmin ser inte kretsens planering");
            Status(c, planning: false, manages: true).Should().BeNull("den publika kalendern visar aldrig planering");
            Status(c, planning: true, own: false, manages: true).Should().BeNull("en grannes planering syns aldrig");
        }

        [Fact]
        public void Utlyst_i_planeringen_grannens_bara_for_administratorer()
        {
            var c = Call(CompetitionCallStatus.Utlyst);
            Status(c, planning: true, admin: true).Should().Be(KretsCalendarStatus.Utlyst);
            Status(c, planning: true, own: false, admin: true).Should().Be(KretsCalendarStatus.Utlyst);
            Status(c, planning: true, own: false).Should().BeNull();
        }

        [Fact]
        public void Publicerad_tavling_ersatter_utlysningen()
        {
            var c = Call(CompetitionCallStatus.Skapad, competitionId: 555);
            Status(c, planning: true, manages: true, published: true).Should().BeNull("tävlingen visas i stället (N10)");
        }

        [Fact]
        public void Skapad_men_dold_tavling_visas_som_utsedd()
        {
            var c = Call(CompetitionCallStatus.Skapad, competitionId: 555);
            Status(c, published: false).Should().Be(KretsCalendarStatus.Utsedd, "en dold tävling finns inte i kalendern");
        }

        [Fact]
        public void Grannens_utsedda_syns_for_alla()
        {
            Status(Call(CompetitionCallStatus.Tilldelad), own: false).Should().Be(KretsCalendarStatus.Utsedd);
        }
    }
}
