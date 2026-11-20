using FluentAssertions;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Kretsutlysningens regler (F2): vad som är giltigt, vad som får göras i vilket läge, och årsprogrammets rader.</summary>
    public class CompetitionCallRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 20);

        private static CompetitionCall Call(string status = CompetitionCallStatus.Planering, DateTime? date = null, int year = 2027) => new()
        {
            Id = 1, RegionId = 3294, Year = year, Name = "KM Precision", Status = status, CompetitionDate = date,
            AwardedClubId = status is CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad ? 9003 : null
        };

        [Fact]
        public void Giltig_utan_datum() => CompetitionCallRules.ValidateContent(Call(), Today).Should().BeNull();

        [Fact]
        public void Namn_kravs() =>
            CompetitionCallRules.ValidateContent(new CompetitionCall { Name = "  ", Year = 2027 }, Today).Should().Contain("namn");

        [Fact]
        public void Datum_maste_ligga_i_aret() =>
            CompetitionCallRules.ValidateContent(Call(date: new DateTime(2028, 1, 5)), Today).Should().Contain("2027");

        [Fact]
        public void Fastställt_datum_kraver_ett_datum()
        {
            var c = Call(); c.LockedByKrets = true;
            CompetitionCallRules.ValidateContent(c, Today).Should().Contain("datum");
        }

        [Fact]
        public void Fastställt_datum_bara_for_kretsniva()
        {
            var c = Call(date: new DateTime(2027, 5, 1)); c.LockedByKrets = true; c.Level = "Nationell";
            CompetitionCallRules.ValidateContent(c, Today).Should().Contain("kretstävling");
        }

        [Fact]
        public void Onskefristen_far_inte_ligga_efter_tavlingen()
        {
            var c = Call(date: new DateTime(2027, 5, 1)); c.WishDeadline = new DateTime(2027, 6, 1);
            CompetitionCallRules.ValidateContent(c, Today).Should().Contain("Sista dag");
        }

        [Theory]
        [InlineData("Planering", true, true, false, true, false, true)]
        [InlineData("Utlyst", true, false, true, true, false, true)]
        [InlineData("Tilldelad", true, false, false, false, true, true)]
        [InlineData("Skapad", false, false, false, false, true, true)]   // F4 (Stefan 2026-11-20): utnämningen kan tas tillbaka även efter skapad tävling
        [InlineData("Installd", false, false, false, false, false, false)]
        public void Vad_som_far_goras_i_varje_lage(string status, bool edit, bool publish, bool withdraw, bool award, bool unaward, bool cancel)
        {
            CompetitionCallRules.CanEdit(status).Should().Be(edit);
            CompetitionCallRules.CanPublish(status).Should().Be(publish);
            CompetitionCallRules.CanWithdraw(status, 0).Should().Be(withdraw);
            CompetitionCallRules.CanAward(status).Should().Be(award);
            CompetitionCallRules.CanUnaward(status, null).Should().Be(unaward);
            // Skapad utan kopplad tävling (halvfärdig skapning) kan ändå inte ställas in av misstag här: CompetitionId styr.
            CompetitionCallRules.CanCancel(status, null).Should().Be(status != CompetitionCallStatus.Installd);
            _ = cancel;
        }

        [Fact]
        public void Utlysning_med_onskemal_gar_inte_att_dra_tillbaka() =>
            CompetitionCallRules.CanWithdraw(CompetitionCallStatus.Utlyst, 2).Should().BeFalse();

        [Fact]
        public void Skapad_tavling_hindrar_att_stalla_in_men_inte_att_ta_tillbaka()
        {
            // Beslut 2 (Stefan 2026-11-20): kretsen får alltid byta klubb — tävlingen står kvar hos klubben, och kretsen varnas.
            CompetitionCallRules.CanUnaward(CompetitionCallStatus.Tilldelad, 55).Should().BeTrue();
            CompetitionCallRules.CanUnaward(CompetitionCallStatus.Skapad, 55).Should().BeTrue();
            CompetitionCallRules.CanCancel(CompetitionCallStatus.Tilldelad, 55).Should().BeFalse();
        }

        [Fact]
        public void Varningen_vid_byte_av_klubb_namnger_anmalningar_och_att_inget_aterbetalas()
        {
            CompetitionCallRules.UnawardWarning(null, false, 0).Should().BeNull();
            CompetitionCallRules.UnawardWarning(55, false, 0).Should().Contain("står kvar hos klubben").And.NotContain("anmälningar");
            var w = CompetitionCallRules.UnawardWarning(55, true, 12)!;
            w.Should().Contain("publicerad").And.Contain("12 anmälningar").And.Contain("återbetalas inget automatiskt");
        }

        [Theory]
        [InlineData("Tilldelad", null, "precision", null)]
        [InlineData("Tilldelad", null, null, "gren")]            // utan gren går det inte att skapa en tävling
        [InlineData("Tilldelad", 55, "precision", "redan skapad")]
        [InlineData("Skapad", 55, "precision", "redan skapad")]
        [InlineData("Utlyst", null, "precision", "inte utsett")]
        public void Skapa_tavlingen_prövas_mot_laget(string status, int? competitionId, string? discipline, string? expectedFragment)
        {
            var why = CompetitionCallRules.WhyNotCreate(status, competitionId, discipline);
            if (expectedFragment == null) why.Should().BeNull(); else why.Should().Contain(expectedFragment);
        }

        [Theory]
        [InlineData(true, false, true)]     // fastställt + klubben  → varning
        [InlineData(true, true, false)]     // fastställt + kretsen  → ingen varning
        [InlineData(false, false, false)]   // inte fastställt       → klubben ändrar utan varning
        public void Datumregeln_varnar_bara_klubben_pa_ett_fastställt_datum(bool locked, bool isKrets, bool warned) =>
            (CompetitionCallRules.DateChangeWarning(locked, isKrets, 0) != null).Should().Be(warned);

        [Fact]
        public void Datumvarningen_blir_hardare_med_anmalningar_och_sager_att_inget_aterbetalas()
        {
            var utan = CompetitionCallRules.DateChangeWarning(true, false, 0)!;
            utan.Should().NotContain("anmält");
            var med = CompetitionCallRules.DateChangeWarning(true, false, 7)!;
            med.Should().Contain("7 personer har redan anmält").And.Contain("återbetalas inget automatiskt");
            CompetitionCallRules.DateChangeWarning(true, false, 1)!.Should().Contain("1 person har redan anmält");
        }

        [Theory]
        [InlineData("edit", "Skapad")]
        [InlineData("award", "Tilldelad")]
        [InlineData("unaward", "Utlyst")]
        [InlineData("publish", "Utlyst")]
        public void En_avstangd_handling_har_alltid_en_forklaring(string action, string status) =>
            CompetitionCallRules.WhyNot(action, status, status == "Skapad" ? 55 : null).Should().NotBeNullOrWhiteSpace();

        [Fact]
        public void En_tillaten_handling_har_ingen_forklaring() =>
            CompetitionCallRules.WhyNot("award", CompetitionCallStatus.Utlyst, null).Should().BeNull();

        // ── Årsprogrammet ───────────────────────────────────────────────────────────────────

        private static List<CompetitionCallRules.ProgramRow> Rows(IEnumerable<CompetitionCall> calls, bool includePlanning) =>
            CompetitionCallRules.ProgramRows(calls, id => id == 9003 ? "Ankeborgs PK" : null, d => d ?? "", includePlanning);

        [Fact]
        public void Program_sorterar_datum_foerst_och_datumlosa_sist()
        {
            var calls = new[]
            {
                Call(CompetitionCallStatus.Utlyst, null), Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 6, 1)),
                Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 2, 1)),
            };
            var rows = Rows(calls, true);
            rows.Select(r => r.Date).Should().Equal(new DateTime?[] { new DateTime(2027, 2, 1), new DateTime(2027, 6, 1), null });
        }

        [Fact]
        public void Program_utelamnar_installda_och_valfritt_planering()
        {
            var calls = new[] { Call(CompetitionCallStatus.Planering), Call(CompetitionCallStatus.Installd), Call(CompetitionCallStatus.Utlyst) };
            Rows(calls, false).Should().HaveCount(1);
            Rows(calls, true).Should().HaveCount(2);
        }

        [Fact]
        public void Program_visar_utsedd_klubb_och_Ej_utsedd_som_null()
        {
            var rows = Rows(new[] { Call(CompetitionCallStatus.Tilldelad), Call(CompetitionCallStatus.Utlyst) }, true);
            rows.Select(r => r.Organiser).Should().BeEquivalentTo(new string?[] { "Ankeborgs PK", null });
        }

        [Fact]
        public void Program_veckodag_ar_pa_svenska()
        {
            var rows = Rows(new[] { Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 5, 1)) }, true);
            rows[0].Weekday.Should().Be("Lördag");
        }

        [Fact]
        public void Manadsgrupper_och_datumlosa_i_egen_grupp_sist()
        {
            var rows = Rows(new[]
            {
                Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 3, 1)), Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 3, 20)),
                Call(CompetitionCallStatus.Utlyst, new DateTime(2027, 9, 4)), Call(CompetitionCallStatus.Utlyst, null),
            }, true);
            var groups = CompetitionCallRules.ByMonth(rows);
            groups.Select(g => g.Heading).Should().Equal("Mars", "September", "Datum inte bestämt");
            groups[0].Rows.Should().HaveCount(2);
        }
    }
}
