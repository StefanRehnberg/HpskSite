using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Schedule;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 4: medlemmens vecka över en planeringsfamilj (Mitt schema, hubben, kalenderfilen) och veckomejlet (B9).</summary>
    public class MyWeekTests
    {
        private static readonly DateTime Lor = new(2028, 8, 12);
        private static readonly DateTime Nu = new(2028, 8, 12, 7, 0, 0);

        private static PersonWeekEntry Asg(string key, DateTime date, int? fromH, int? toH, string role, string part, string? place = null,
            int? planning = 10, int? comp = null, string status = "Accepted") =>
            new(key, PersonWeekKind.Assignment, date,
                fromH == null ? StaffInterval.From(date, null, null) : StaffInterval.From(date, TimeSpan.FromHours(fromH.Value), TimeSpan.FromHours(toH!.Value)),
                place, $"{role} ({part})", planning, comp, role, part, status);

        private static MySchedule Week(params PersonWeekEntry[] e) =>
            MyWeekService.ToSchedule(1, "SM-veckan 2028", PersonWeek.Build(e, Lor, Lor.AddDays(6)), null, null, Nu);

        [Fact]
        public void Heldag_far_inget_klockslag_for_kalendern_och_pushen()
        {
            var it = Week(Asg("a1", Lor, null, null, "Sekretariat", "Gemensamt")).Days.Single().Items.Single();
            it.TimeLabel.Should().Be("Heldag");
            it.StartsAt.Should().BeNull();
            it.EndsAt.Should().BeNull();
        }

        [Fact]
        public void Ett_pass_far_sina_klockslag()
        {
            var it = Week(Asg("a1", Lor, 8, 12, "Skjutledare", "Fält")).Days.Single().Items.Single();
            it.TimeLabel.Should().Be("08:00–12:00");
            it.StartsAt.Should().Be(Lor.AddHours(8));
            it.EndsAt.Should().Be(Lor.AddHours(12));
        }

        [Fact]
        public void Raden_visar_rollen_som_rubrik_och_delen_och_platsen_under()
        {
            var it = Week(Asg("a1", Lor, 8, 12, "Skjutledare", "Fält", "Skogsbanan")).Days.Single().Items.Single();
            it.Title.Should().Be("Skjutledare");
            it.Where.Should().Be("Fält · Skogsbanan");
            it.Kind.Should().Be(ScheduleItemKind.Funktionar);
        }

        [Fact]
        public void Uppdragets_nyckel_blir_samma_som_i_tavlingsschemat()
            => Week(Asg("a42", Lor, 8, 12, "Markör", "Fält")).Days.Single().Items.Single().SourceKey.Should().Be("uppdrag-42");

        [Fact]
        public void Overlappning_mellan_delar_blir_en_krock_pa_bada_raderna()
        {
            var w = Week(Asg("a1", Lor, 8, 12, "Skjutledare", "Fält"), Asg("a2", Lor, 10, 14, "Markör", "Precision", planning: 11));
            w.ConflictCount.Should().Be(2);
            w.Days.Single().Items.Should().OnlyContain(i => i.HasConflict);
            w.Days.Single().Items.First().ConflictText.Should().Contain("Markör");
        }

        [Fact]
        public void Heldag_i_det_gemensamma_krockar_med_ett_pass_i_en_del()
        {
            var w = Week(Asg("a1", Lor, null, null, "Sekretariat", "Gemensamt", planning: 1), Asg("a2", Lor, 13, 15, "Skjutledare", "Precision", planning: 11));
            w.ConflictCount.Should().Be(2);
        }

        [Fact]
        public void Heldag_i_gemensamt_mot_SAMMA_roll_med_tider_i_en_del_ar_ingen_krock()
        {
            // Sekretariatet finns både i Gemensamt och per del (B8) — samma jobb skrivet två gånger, inte en dubbelbokning.
            var w = Week(Asg("a1", Lor, null, null, "Sekretariat", "Gemensamt", planning: 1), Asg("a2", Lor, 8, 10, "Sekretariat", "Fält", planning: 11));
            w.ConflictCount.Should().Be(0);
            w.Days.Single().Items.Should().OnlyContain(i => string.IsNullOrEmpty(i.WarningText));
        }

        [Fact]
        public void Samma_roll_jamfors_utan_hansyn_till_versaler_och_blanksteg()
            => Week(Asg("a1", Lor, null, null, " sekretariat ", "Gemensamt", planning: 1), Asg("a2", Lor, 8, 10, "Sekretariat", "Fält", planning: 11)).ConflictCount.Should().Be(0);

        [Fact]
        public void Heldag_mot_en_ANNAN_roll_krockar_fortfarande()
            => Week(Asg("a1", Lor, null, null, "Kassa", "Gemensamt", planning: 1), Asg("a2", Lor, 8, 10, "Sekretariat", "Fält", planning: 11)).ConflictCount.Should().Be(2);

        [Fact]
        public void Tva_tidsatta_pass_med_samma_roll_som_overlappar_krockar_fortfarande()
            => Week(Asg("a1", Lor, 8, 12, "Markör", "Fält"), Asg("a2", Lor, 10, 14, "Markör", "Precision", planning: 11)).ConflictCount.Should().Be(2);

        [Fact]
        public void Tva_heldagar_med_samma_roll_i_olika_delar_krockar_fortfarande()
            => Week(Asg("a1", Lor, null, null, "Sekretariat", "Fält"), Asg("a2", Lor, null, null, "Sekretariat", "Precision", planning: 11)).ConflictCount.Should().Be(2);

        [Fact]
        public void Tight_forflyttning_ar_en_varning_och_ingen_krock()
        {
            var w = Week(Asg("a1", Lor, 8, 12, "Skjutledare", "Fält", "Skogsbanan"),
                         new PersonWeekEntry("a2", PersonWeekKind.Assignment, Lor, StaffInterval.From(Lor, new TimeSpan(12, 10, 0), new TimeSpan(15, 0, 0)),
                             "25-metersbanan", "Markör (Precision)", 11, null, "Markör", "Precision", "Accepted"));
            w.ConflictCount.Should().Be(0);
            w.Days.Single().Items.Should().OnlyContain(i => !string.IsNullOrEmpty(i.WarningText));
            w.Days.Single().Items.First().WarningText.Should().Contain("bara 10 min mellan platserna");
        }

        [Fact]
        public void En_obesvarad_inbjudan_ber_om_svar_med_lank_till_planeringen_nar_tavling_saknas()
        {
            var it = Week(Asg("a1", Lor, 8, 12, "Servering", "Gemensamt", planning: 1, comp: null, status: "Invited")).Days.Single().Items.Single();
            it.NeedsResponse.Should().BeTrue();
            it.LinkText.Should().Be("Svara på inbjudan");
            it.Link.Should().Be("/bemanna?p=1");
        }

        [Fact]
        public void Uppdrag_med_tavling_lankar_till_tavlingens_bemanning()
            => Week(Asg("a1", Lor, 8, 12, "Markör", "Fält", comp: 77)).Days.Single().Items.Single().Link.Should().Be("/bemanna?c=77");

        [Fact]
        public void Egen_start_blir_skytte_med_lank_till_startlistan()
        {
            var start = new PersonWeekEntry("s:77:x", PersonWeekKind.Start, Lor, StaffInterval.From(Lor, new TimeSpan(15, 12, 0), new TimeSpan(16, 0, 0)),
                null, "egen start i Fält: Klass A 15:12", null, 77);
            var it = Week(start).Days.Single().Items.Single();
            it.Kind.Should().Be(ScheduleItemKind.Skytte);
            it.Link.Should().Be("/startlista/77");
        }

        [Fact]
        public void Avsikt_utan_tid_heter_under_dagen()
        {
            var i = new PersonWeekEntry("i1:20280812", PersonWeekKind.Intent, Lor, null, null, "tänker tävla i Precision", null, 77);
            Week(i).Days.Single().Items.Single().TimeLabel.Should().Be("Under dagen");
        }

        [Fact]
        public void Natt_pass_over_midnatt_sags_i_tidsetiketten()
            => Week(Asg("a1", Lor, 22, 2, "Bevakning", "Fält")).Days.Single().Items.Single().TimeLabel.Should().Be("22:00–02:00 (till nästa dag)");

        [Fact]
        public void Fonstret_klipper_dagarna_och_ett_tomt_fonster_ger_ingen_vecka()
        {
            var e = new[] { Asg("a1", Lor, 8, 9, "A", "X"), Asg("a2", Lor.AddDays(3), 8, 9, "B", "X") };
            var days = PersonWeek.Build(e, Lor, Lor.AddDays(6));
            MyWeekService.ToSchedule(1, "n", days, Lor.AddDays(2), Lor.AddDays(4), Nu).Days.Should().HaveCount(1);
            MyWeekService.ToSchedule(1, "n", days, Lor.AddDays(10), Lor.AddDays(12), Nu).HasAny.Should().BeFalse();
        }

        [Fact]
        public void Dagen_far_versal_och_idag_markeras()
        {
            var d = Week(Asg("a1", Lor, 8, 12, "Markör", "Fält")).Days.Single();
            d.DayLabel.Should().Be("Lördag 12 augusti");
            d.IsToday.Should().BeTrue();
        }

        [Fact]
        public void Nasta_ar_forsta_raden_med_ett_klockslag_i_framtiden()
        {
            var w = Week(Asg("a1", Lor, null, null, "Sekretariat", "Gemensamt"), Asg("a2", Lor, 13, 15, "Markör", "Fält", planning: 11));
            w.NextItem!.Title.Should().Be("Markör");
        }

        [Fact]
        public void Veckan_ar_en_vecka_och_far_p_i_adressen()
        {
            var w = Week(Asg("a1", Lor, 8, 12, "Markör", "Fält"));
            w.IsWeek.Should().BeTrue();
            w.ScheduleQuery.Should().Be("p=1");
            new MySchedule { CompetitionId = 5 }.ScheduleQuery.Should().Be("c=5");
        }

        [Fact]
        public void Kalenderfilen_for_en_vecka_hoppar_over_heldag_och_far_p_i_uid_och_url()
        {
            var w = Week(Asg("a1", Lor, 8, 12, "Skjutledare", "Fält"), Asg("a2", Lor, null, null, "Sekretariat", "Gemensamt", planning: 1));
            var (ics, exported, skipped) = new ScheduleIcsBuilder().Build(w, "https://pistol.nu");
            exported.Should().Be(1);
            skipped.Should().Be(1);
            ics.Should().Contain("UID:uppdrag-1-p1@pistol.nu");
            ics.Should().Contain("/mitt-schema?p=1");
        }
    }

    /// <summary>B9: ett mejl per person och dag.</summary>
    public class StaffWeekDigestTests
    {
        [Theory]
        [InlineData(16, 59, false)]
        [InlineData(17, 0, true)]
        [InlineData(21, 59, true)]
        [InlineData(22, 0, false)]
        public void Mejlet_gar_bara_pa_kvallen(int h, int m, bool expected)
            => StaffWeekDigest.InSendWindow(new DateTime(2028, 8, 11, h, m, 0)).Should().Be(expected);

        [Fact]
        public void Mejlet_galler_i_morgon()
            => StaffWeekDigest.DayFor(new DateTime(2028, 8, 11, 18, 30, 0)).Should().Be(new DateTime(2028, 8, 12));

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("Planned", false)]      // pennad, ingen har bett personen — ett utkast ska inte mejlas
        [InlineData("Accepted", true)]
        [InlineData("Confirmed", true)]
        [InlineData("Invited", true)]       // den som tagit över ett pass och inte svarat ska ändå veta att hen väntas
        [InlineData("Declined", false)]
        [InlineData("declined", false)]
        public void Pennade_och_avbojda_far_ingen_paminnelse_men_inbjudna_far_(string? status, bool expected)
            => StaffWeekDigest.Remindable(status).Should().Be(expected);

        [Fact]
        public void Sparren_ar_en_per_person_och_dag_oavsett_familj()
        {
            StaffWeekDigest.Key(new DateTime(2028, 8, 12)).Should().Be("week-20280812");
            StaffWeekDigest.Key(new DateTime(2028, 8, 12)).Should().NotBe(StaffWeekDigest.Key(new DateTime(2028, 8, 13)));
            StaffWeekDigest.FailPrefix(new DateTime(2028, 8, 12)).Should().Be("weekfail-20280812-");
            StaffWeekDigest.Key(new DateTime(2028, 8, 12)).Should().NotStartWith(StaffWeekDigest.FailPrefix(new DateTime(2028, 8, 12)));
        }

        [Fact]
        public void Rubriken_namnar_planeringen_och_dagen_och_utelamnar_namnet_nar_det_ar_flera()
        {
            StaffWeekDigest.Subject(new[] { "SM-veckan 2028" }, new DateTime(2028, 8, 12)).Should().Be("I morgon (lördag 12 augusti): din dag i SM-veckan 2028");
            StaffWeekDigest.Subject(new[] { "A", "B" }, new DateTime(2028, 8, 12)).Should().Be("I morgon (lördag 12 augusti): din dag");
        }

        [Fact]
        public void Fotnoten_namnger_kontaktpersonen_med_telefon_och_sager_att_mejlet_kan_vara_gammalt()
        {
            var f = StaffWeekDigest.Footer("Anna Ek", "070-123 45 67");
            f.Should().Contain("Anna Ek").And.Contain("tel:070-123 45 67").And.Contain("kan svara").And.Contain("inaktuellt");
            StaffWeekDigest.Footer().Should().NotContain("Frågor om uppdraget").And.Contain("inaktuellt");
            StaffWeekDigest.Footer("<b>x</b>", null).Should().Contain("&lt;b&gt;x&lt;/b&gt;");
        }

        [Fact]
        public void En_obesvarad_inbjudan_sager_att_man_inte_svarat_och_lankar_till_svaret()
        {
            var lor = new DateTime(2028, 8, 12);
            var e = new PersonWeekEntry("a9", PersonWeekKind.Assignment, lor, StaffInterval.From(lor, TimeSpan.FromHours(8), TimeSpan.FromHours(12)), null,
                "Markör (Fält)", 10, null, "Markör", "Fält", "Invited");
            var week = MyWeekService.ToSchedule(1, "SM", PersonWeek.Build(new[] { e }, lor, lor), null, null, lor.AddDays(-1));
            var html = StaffWeekDigest.Inner(week, lor, "https://pistol.nu/mitt-schema?p=1", "https://pistol.nu");
            html.Should().Contain("Du har inte svarat").And.Contain("https://pistol.nu/bemanna?p=10");
        }

        [Fact]
        public void Flera_familjer_far_en_rubrik_var()
        {
            var lor = new DateTime(2028, 8, 12);
            var e = new PersonWeekEntry("a1", PersonWeekKind.Assignment, lor, StaffInterval.From(lor, TimeSpan.FromHours(8), TimeSpan.FromHours(9)), null, "X", 10, null, "X", "Fält", "Accepted");
            var week = MyWeekService.ToSchedule(1, "SM-veckan", PersonWeek.Build(new[] { e }, lor, lor), null, null, lor.AddDays(-1));
            StaffWeekDigest.Inner(week, lor, "u", heading: true).Should().Contain("<h3").And.Contain("SM-veckan");
            StaffWeekDigest.Inner(week, lor, "u", heading: false).Should().NotContain("<h3");
        }

        [Fact]
        public void Mejlet_har_bara_dagens_rader_med_tid_roll_plats_och_krock_och_ar_html_sakert()
        {
            var lor = new DateTime(2028, 8, 12);
            var e = new[]
            {
                new PersonWeekEntry("a1", PersonWeekKind.Assignment, lor, StaffInterval.From(lor, TimeSpan.FromHours(8), TimeSpan.FromHours(12)), "Skogsbanan",
                    "Skjutledare (Fält)", 10, null, "Skjutledare <b>", "Fält", "Accepted"),
                new PersonWeekEntry("a2", PersonWeekKind.Assignment, lor, StaffInterval.From(lor, TimeSpan.FromHours(10), TimeSpan.FromHours(14)), null,
                    "Markör (Precision)", 11, null, "Markör", "Precision", "Accepted"),
                new PersonWeekEntry("a3", PersonWeekKind.Assignment, lor.AddDays(1), StaffInterval.From(lor.AddDays(1), TimeSpan.FromHours(9), TimeSpan.FromHours(10)), null,
                    "Söndagsuppdrag (Fält)", 10, null, "Söndagsuppdrag", "Fält", "Accepted"),
            };
            var week = MyWeekService.ToSchedule(1, "SM-veckan", PersonWeek.Build(e, lor, lor.AddDays(2)), null, null, lor.AddDays(-1));
            var html = StaffWeekDigest.Inner(week, lor, "https://pistol.nu/mitt-schema?p=1");

            // HtmlEncode skriver ö som &#246; — giltig HTML, och samma kodning som planeringens övriga mejl.
            html.Should().Contain("08:00–12:00").And.Contain("Skogsbanan").And.Contain("Mark&#246;r");
            html.Should().Contain("Krockar med:");
            html.Should().Contain("https://pistol.nu/mitt-schema?p=1");
            html.Should().NotContain("Söndagsuppdrag");
            html.Should().NotContain("<b>>").And.Contain("Skjutledare &lt;b&gt;");
        }
    }
}
