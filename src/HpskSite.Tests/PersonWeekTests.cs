using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Expertgruppen 1c: tidsintervallet (heldag, midnatt, platsbyte) och personens vecka.</summary>
    public class StaffIntervalTests
    {
        private static readonly DateTime Lor = new(2028, 8, 12);
        private static TimeSpan H(int h, int m = 0) => new(h, m, 0);

        [Fact]
        public void Heldag_ar_delens_dagsprogram()
        {
            var i = StaffInterval.From(Lor, null, null, new DayProgram(H(9), H(16)))!.Value;
            i.AllDay.Should().BeTrue();
            i.Start.Should().Be(Lor.AddHours(9));
            i.End.Should().Be(Lor.AddHours(16));
        }

        [Fact]
        public void Heldag_utan_program_ar_08_till_17()
        {
            var i = StaffInterval.From(Lor, null, null)!.Value;
            (i.Start.TimeOfDay, i.End.TimeOfDay).Should().Be((H(8), H(17)));
        }

        [Fact]
        public void Ett_trasigt_dagsprogram_ger_forvalet()
            => StaffInterval.From(Lor, null, null, new DayProgram(H(17), H(8)))!.Value.Start.TimeOfDay.Should().Be(H(8));

        [Fact]
        public void Heldag_krockar_med_ett_pass_samma_dag()
        {
            // Fält-sekretariat heldag + Precision 13–15 — förr syntes aldrig den krocken.
            var heldag = StaffInterval.From(Lor, null, null)!.Value;
            var pass = StaffInterval.From(Lor, H(13), H(15))!.Value;
            StaffInterval.Between(heldag, "Skogsbanan", pass, "25-metersbanan").Should().Be(ClashKind.Overlap);
        }

        [Fact]
        public void Bara_ena_anden_ar_okand_och_krockar_aldrig()
        {
            StaffInterval.From(Lor, H(13), null).Should().BeNull();
            StaffInterval.From(Lor, null, H(15)).Should().BeNull();
        }

        [Fact]
        public void Start_lika_med_slut_ar_ett_fel()
            => StaffInterval.From(Lor, H(13), H(13)).Should().BeNull();

        [Fact]
        public void Nattpass_slutar_nasta_dag()
        {
            var i = StaffInterval.From(Lor, H(22), H(2))!.Value;
            i.End.Should().Be(Lor.AddDays(1).AddHours(2));
            i.CrossesMidnight.Should().BeTrue();
            i.Length.Should().Be(TimeSpan.FromHours(4));
        }

        [Fact]
        public void Till_midnatt_ar_fortfarande_samma_dag()
        {
            var i = StaffInterval.From(Lor, H(22), H(0))!.Value;
            i.End.Should().Be(Lor.AddDays(1));
            i.CrossesMidnight.Should().BeFalse();
        }

        [Fact]
        public void Nattpass_krockar_med_ett_tidigt_pass_nasta_morgon()
        {
            var natt = StaffInterval.From(Lor, H(22), H(2))!.Value;
            var morgon = StaffInterval.From(Lor.AddDays(1), H(1), H(3))!.Value;
            natt.Overlaps(morgon).Should().BeTrue();
        }

        [Fact]
        public void Kant_i_kant_ar_ingen_overlappning()
        {
            var a = StaffInterval.From(Lor, H(9), H(13))!.Value;
            var b = StaffInterval.From(Lor, H(13), H(17))!.Value;
            a.Overlaps(b).Should().BeFalse();
            StaffInterval.Between(a, "Skogsbanan", b, "Skogsbanan").Should().Be(ClashKind.None);
        }

        [Fact]
        public void Platsbyte_med_for_kort_tid_ar_en_varning()
        {
            var a = StaffInterval.From(Lor, H(8), H(10))!.Value;
            var b = StaffInterval.From(Lor, H(10, 20), H(13))!.Value;
            StaffInterval.Between(a, "Skogsbanan", b, "25-metersbanan").Should().Be(ClashKind.TightTravel);
        }

        [Fact]
        public void Platsbyte_med_precis_marginalen_ar_ok()
        {
            var a = StaffInterval.From(Lor, H(8), H(10))!.Value;
            var b = StaffInterval.From(Lor, H(10, 30), H(13))!.Value;
            StaffInterval.Between(a, "Skogsbanan", b, "25-metersbanan").Should().Be(ClashKind.None);
        }

        [Fact]
        public void Marginalen_kan_stallas()
        {
            var a = StaffInterval.From(Lor, H(8), H(10))!.Value;
            var b = StaffInterval.From(Lor, H(10, 45), H(13))!.Value;
            StaffInterval.Between(a, "A", b, "B", TimeSpan.FromMinutes(60)).Should().Be(ClashKind.TightTravel);
        }

        [Theory]
        [InlineData("Skogsbanan", "skogsbanan ", false)]
        [InlineData("Skogsbanan", "Hallen", true)]
        [InlineData(null, "Hallen", false)]
        [InlineData("Skogsbanan", "", false)]
        public void Platser_jamfors_utan_skiftlage_och_okand_plats_ar_ingen_skillnad(string? a, string? b, bool different)
            => StaffInterval.DifferentPlaces(a, b).Should().Be(different);

        [Fact]
        public void FromMoments_kraver_bada_och_ett_slut_efter_starten()
        {
            StaffInterval.FromMoments(Lor.AddHours(9), Lor.AddHours(12)).Should().NotBeNull();
            StaffInterval.FromMoments(Lor.AddHours(9), null).Should().BeNull();
            StaffInterval.FromMoments(null, null).Should().BeNull();
            StaffInterval.FromMoments(Lor.AddHours(12), Lor.AddHours(9)).Should().BeNull();
        }
    }

    public class PersonWeekTests
    {
        private static readonly DateTime Lor = new(2028, 8, 12);
        private static readonly DateTime Son = Lor.AddDays(1);
        private static TimeSpan H(int h, int m = 0) => new(h, m, 0);

        private static PersonWeekEntry Uppdrag(string key, DateTime date, TimeSpan? s, TimeSpan? e, string label,
            string? place = null, int? planning = 1, int? comp = null)
            => new(key, PersonWeekKind.Assignment, date, StaffInterval.From(date, s, e), place, label, planning, comp);

        private static PersonWeekEntry Start(string key, DateTime date, TimeSpan s, TimeSpan e, string label, int comp, string? place = null)
            => new(key, PersonWeekKind.Start, date, StaffInterval.From(date, s, e), place, label, null, comp);

        private static PersonWeekEntry Avsikt(string key, DateTime date, int comp)
            => new(key, PersonWeekKind.Intent, date, null, null, "Jag tänker tävla", null, comp);

        private static PersonWeekDay Dag(System.Collections.Generic.List<PersonWeekDay> v, DateTime d) => v.Single(x => x.Date == d);

        [Fact]
        public void Lisas_helg_tre_planeringar_en_egen_start()
        {
            // Vapenkontroll Fält 07–09, skjutledare Precision 10–13 (20 km bort), egen start 15:12, sekretariat 17–19.
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor, H(7), H(9), "Vapenkontroll", "Skogsbanan", planning: 2),
                Uppdrag("a2", Lor, H(10), H(13), "Skjutledare", "25-metersbanan", planning: 3),
                Start("s1", Lor, H(15, 12), H(16, 0), "Egen start", 7, "25-metersbanan"),
                Uppdrag("a3", Lor, H(17), H(19), "Sekretariat", "25-metersbanan", planning: 1),
            }, Lor, Son);

            var dag = Dag(v, Lor);
            dag.Items.Select(i => i.Entry.SourceKey).Should().Equal("a1", "a2", "s1", "a3");
            // 09:00 → 10:00 är en timme mellan banorna: marginalen på 30 minuter räcker.
            dag.Items.Should().OnlyContain(i => i.Clashes.Count == 0);
        }

        [Fact]
        public void Trang_forflyttning_syns_pa_bada_raderna()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor, H(8), H(10), "Vapenkontroll", "Skogsbanan"),
                Uppdrag("a2", Lor, H(10, 10), H(12), "Skjutledare", "25-metersbanan", planning: 2),
            }, Lor, Lor);

            var items = Dag(v, Lor).Items;
            items.Should().OnlyContain(i => i.Clashes.Single().Kind == ClashKind.TightTravel);
            items.Should().OnlyContain(i => !i.HasClash && i.HasWarning);
        }

        [Fact]
        public void Heldag_i_en_del_krockar_med_ett_pass_i_en_annan()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor, null, null, "Sekretariat (heldag)", "Skogsbanan", planning: 2),
                Uppdrag("a2", Lor, H(13), H(15), "Skjutledare", "25-metersbanan", planning: 3),
            }, Lor, Lor);

            Dag(v, Lor).Items.Should().OnlyContain(i => i.HasClash);
        }

        [Fact]
        public void Okand_tid_krockar_aldrig()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor, H(13), null, "Markör"),
                Uppdrag("a2", Lor, H(13), H(15), "Skjutledare", planning: 2),
            }, Lor, Lor);

            Dag(v, Lor).Items.Should().OnlyContain(i => i.Clashes.Count == 0);
        }

        [Fact]
        public void Varje_rad_exakt_en_gang()
        {
            var rad = Uppdrag("a1", Lor, H(9), H(12), "Markör");
            var v = PersonWeek.Build(new[] { rad, rad, rad with { Label = "Samma rad igen" } }, Lor, Son);
            v.SelectMany(d => d.Items).Should().HaveCount(1);
            v.Single().Items.Single().Entry.Label.Should().Be("Markör");
        }

        [Fact]
        public void Rader_utan_nyckel_raknas_var_och_en()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("", Lor, H(9), H(12), "Markör"),
                Uppdrag("", Lor, H(9), H(12), "Markör"),
            }, Lor, Lor);
            // Två identiska rader utan identitet är två rader — och de krockar med varandra.
            Dag(v, Lor).Items.Should().HaveCount(2);
            Dag(v, Lor).Items.Should().OnlyContain(i => i.HasClash);
        }

        [Fact]
        public void Rader_utanfor_veckan_uteslutas_och_tomma_dagar_utelamnas()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor.AddDays(-1), H(9), H(12), "Före"),
                Uppdrag("a2", Lor, H(9), H(12), "Inom"),
                Uppdrag("a3", Lor.AddDays(5), H(9), H(12), "Efter"),
            }, Lor, Son);
            v.Select(d => d.Date).Should().Equal(Lor);
        }

        [Fact]
        public void Nattpass_star_pa_startdagen_och_visas_nasta_dag_som_fortsattning()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("n1", Lor, H(22), H(2), "Bevakning"),
                Uppdrag("a2", Son, H(9), H(12), "Markör"),
            }, Lor, Son);

            Dag(v, Lor).Items.Single().EndsNextDay.Should().BeTrue();
            Dag(v, Lor).CarriedIn.Should().BeEmpty();
            Dag(v, Son).CarriedIn.Select(e => e.SourceKey).Should().Equal("n1");
            // Raden finns som rad på ett enda ställe.
            v.SelectMany(d => d.Items).Count(i => i.Entry.SourceKey == "n1").Should().Be(1);
        }

        [Fact]
        public void Nattpass_krockar_med_ett_tidigt_pass_nasta_dag()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("n1", Lor, H(22), H(2), "Bevakning"),
                Uppdrag("a2", Son, H(1), H(3), "Tidigt pass", planning: 2),
            }, Lor, Son);

            Dag(v, Lor).Items.Single().HasClash.Should().BeTrue();
            Dag(v, Son).Items.Single().HasClash.Should().BeTrue();
        }

        [Fact]
        public void Pass_som_ligger_tva_dagar_isar_krockar_inte()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("a1", Lor, null, null, "Heldag"),
                Uppdrag("a2", Lor.AddDays(2), null, null, "Heldag", planning: 2),
            }, Lor, Lor.AddDays(2));
            v.SelectMany(d => d.Items).Should().OnlyContain(i => i.Clashes.Count == 0);
        }

        [Fact]
        public void Avsikt_varnar_mot_ett_uppdrag_samma_dag()
        {
            var v = PersonWeek.Build(new[]
            {
                Avsikt("i1", Lor, comp: 7),
                Uppdrag("a1", Lor, H(9), H(12), "Markör"),
            }, Lor, Lor);

            Dag(v, Lor).Items.Should().OnlyContain(i => i.Clashes.Single().Kind == ClashKind.Intent && !i.HasClash);
        }

        [Fact]
        public void Avsikt_galler_inte_i_en_tavling_dar_personen_har_en_start()
        {
            var v = PersonWeek.Build(new[]
            {
                Avsikt("i1", Lor, comp: 7),
                Start("s1", Lor, H(15), H(16), "Egen start", comp: 7),
                Uppdrag("a1", Lor, H(9), H(12), "Markör"),
            }, Lor, Lor);

            Dag(v, Lor).Items.Should().OnlyContain(i => i.Clashes.Count == 0);
        }

        [Fact]
        public void Avsikt_i_en_annan_tavling_an_startens_galler_fortfarande()
        {
            var v = PersonWeek.Build(new[]
            {
                Avsikt("i1", Lor, comp: 7),
                Start("s1", Lor, H(15), H(16), "Start i en annan tävling", comp: 8),
                Uppdrag("a1", Lor, H(9), H(12), "Markör"),
            }, Lor, Lor);

            Dag(v, Lor).Items.Where(i => i.Entry.Kind != PersonWeekKind.Start).Should().OnlyContain(i => i.Clashes.Count == 1);
        }

        [Fact]
        public void Avsikt_varnar_inte_en_dag_utan_uppdrag()
        {
            var v = PersonWeek.Build(new[]
            {
                Avsikt("i1", Lor, comp: 7),
                Uppdrag("a1", Son, H(9), H(12), "Markör"),
            }, Lor, Son);

            v.SelectMany(d => d.Items).Should().OnlyContain(i => i.Clashes.Count == 0);
        }

        [Fact]
        public void En_egen_start_krockar_med_ett_uppdrag()
        {
            var v = PersonWeek.Build(new[]
            {
                Start("s1", Lor, H(13), H(14), "Egen start", 7),
                Uppdrag("a1", Lor, H(12), H(15), "Markör"),
            }, Lor, Lor);
            Dag(v, Lor).Items.Should().OnlyContain(i => i.HasClash);
        }

        [Fact]
        public void Ordningen_ar_tid_forst_och_otidsatta_sist()
        {
            var v = PersonWeek.Build(new[]
            {
                Avsikt("i1", Lor, 7),
                Uppdrag("b", Lor, H(13), H(14), "B"),
                Uppdrag("a", Lor, H(9), H(10), "A"),
            }, Lor, Lor);
            Dag(v, Lor).Items.Select(i => i.Entry.SourceKey).Should().Equal("a", "b", "i1");
        }

        [Fact]
        public void Gemensamt_utan_tavling_hor_hemma_i_veckan()
        {
            var v = PersonWeek.Build(new[]
            {
                Uppdrag("g1", Lor, null, null, "Tävlingsledning (hela veckan)", planning: 1, comp: null),
            }, Lor, Lor);
            Dag(v, Lor).Items.Single().Entry.CompetitionId.Should().BeNull();
        }
    }
}
