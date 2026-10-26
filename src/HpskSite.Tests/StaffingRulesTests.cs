using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Planera dagen steg 2: kan den här personen stå här? En ren regel för kandidatlistan, lägg in flera och förslaget.</summary>
    public class StaffingRulesTests
    {
        private static readonly DateTime D = new(2027, 6, 5);
        private static StaffInterval Iv(int fh, int fm, int th, int tm) => new(D.AddHours(fh).AddMinutes(fm), D.AddHours(th).AddMinutes(tm), false);
        private static BusyItem Start(StaffInterval i, string label = "egen start") => new(BusyKind.Start, i, label, "bana");
        private static BusyItem Job(StaffInterval i, string label = "annat uppdrag", string? place = "bana") => new(BusyKind.Assignment, i, label, place);

        [Fact]
        public void Fri_person_har_inget_hinder() =>
            StaffingRules.FirstConflict(Iv(13, 0, 15, 0), "bana", new[] { Start(Iv(8, 0, 11, 0)) }).Should().BeNull();

        [Fact]
        public void Samtidigt_ar_ett_hardt_hinder()
        {
            var c = StaffingRules.FirstConflict(Iv(10, 0, 12, 0), "bana", new[] { Start(Iv(11, 0, 14, 0)) });
            c.Should().NotBeNull();
            c!.IsHard.Should().BeTrue();
            c.Kind.Should().Be(BusyKind.Start);
        }

        [Fact]
        public void Kant_i_kant_ar_inget_hinder() =>
            StaffingRules.FirstConflict(Iv(11, 0, 13, 0), "bana", new[] { Start(Iv(8, 0, 11, 0)) }).Should().BeNull();

        [Fact]
        public void En_egen_start_gar_fore_ett_annat_uppdrag_nar_bada_krockar()
        {
            var c = StaffingRules.FirstConflict(Iv(10, 0, 12, 0), "bana", new[] { Job(Iv(9, 0, 11, 0)), Start(Iv(11, 0, 14, 0)) });
            c!.Kind.Should().Be(BusyKind.Start);
        }

        [Fact]
        public void Ett_hardt_hinder_gar_fore_en_trang_forflyttning()
        {
            // Pass på station 3 slutar 12:00; nästa plats station 5 börjar 12:10 (10 min emellan < 30) men ett annat pass krockar hårt.
            var busy = new[] { Job(Iv(8, 0, 12, 0), "station 3", "3"), Job(Iv(13, 0, 15, 0), "samtidigt", "5") };
            var c = StaffingRules.FirstConflict(Iv(12, 10, 14, 0), "5", busy);
            c!.IsHard.Should().BeTrue();
            c.Label.Should().Be("samtidigt");
        }

        [Fact]
        public void Trang_forflyttning_mellan_olika_platser_ar_en_varning_med_antal_minuter()
        {
            var c = StaffingRules.FirstConflict(Iv(12, 10, 14, 0), "5", new[] { Job(Iv(8, 0, 12, 0), "station 3", "3") });
            c!.Clash.Should().Be(ClashKind.TightTravel);
            c.IsHard.Should().BeFalse();
            c.GapMinutes.Should().Be(10);
        }

        [Fact]
        public void Samma_plats_ar_ingen_forflyttning() =>
            StaffingRules.FirstConflict(Iv(12, 10, 14, 0), "3", new[] { Job(Iv(8, 0, 12, 0), "station 3", "3") }).Should().BeNull();

        [Fact]
        public void Utanfor_det_personen_anmalt_sig_till_ar_ett_hinder()
        {
            var busy = StaffingRules.OutsideAvailability(D, new[] { (TimeSpan.FromHours(12), TimeSpan.FromHours(15)) }).ToList();
            busy.Should().HaveCount(2);
            StaffingRules.FirstConflict(Iv(8, 0, 10, 0), null, busy)!.Kind.Should().Be(BusyKind.Availability);
            StaffingRules.FirstConflict(Iv(16, 0, 17, 0), null, busy)!.Kind.Should().Be(BusyKind.Availability);
            StaffingRules.FirstConflict(Iv(12, 30, 14, 30), null, busy).Should().BeNull();
            StaffingRules.FirstConflict(Iv(12, 0, 15, 0), null, busy).Should().BeNull();   // exakt hela fönstret
            StaffingRules.FirstConflict(Iv(11, 0, 13, 0), null, busy).Should().NotBeNull(); // sticker ut före fönstret
        }

        [Fact]
        public void En_anmalan_utan_tider_ger_inga_hinder() =>
            StaffingRules.OutsideAvailability(D, Array.Empty<(TimeSpan, TimeSpan)>()).Should().BeEmpty();

        [Fact]
        public void Ett_fonster_som_slutar_fore_det_borjar_ignoreras() =>
            StaffingRules.OutsideAvailability(D, new[] { (TimeSpan.FromHours(15), TimeSpan.FromHours(12)) }).Should().BeEmpty();

        [Fact]
        public void Flera_fonster_lamnar_luckan_mellan_dem_som_hinder()
        {
            var busy = StaffingRules.OutsideAvailability(D, new[] { (TimeSpan.FromHours(8), TimeSpan.FromHours(10)), (TimeSpan.FromHours(14), TimeSpan.FromHours(16)) }).ToList();
            StaffingRules.FirstConflict(Iv(11, 0, 12, 0), null, busy).Should().NotBeNull();
            StaffingRules.FirstConflict(Iv(14, 30, 15, 30), null, busy).Should().BeNull();
        }

        [Theory]
        [InlineData(true, true, true, CandidateTier.VolunteerRoleFree)]
        [InlineData(true, false, true, CandidateTier.VolunteerFree)]
        [InlineData(true, true, null, CandidateTier.VolunteerRoleFree)]
        [InlineData(false, false, true, CandidateTier.CanAsk)]
        [InlineData(false, false, null, CandidateTier.Unknown)]
        [InlineData(true, true, false, CandidateTier.Busy)]
        [InlineData(false, false, false, CandidateTier.Busy)]
        public void Kandidatens_niva(bool volunteer, bool roleMatch, bool? free, CandidateTier expected) =>
            CandidateRules.Tier(volunteer, roleMatch, free).Should().Be(expected);

        [Fact]
        public void Rangordningen_ar_niva_sedan_namn_sedan_id_och_helt_deterministisk()
        {
            var list = new[] { ("Åke", 3, CandidateTier.Busy), ("Åsa", 2, CandidateTier.VolunteerFree), ("Anna", 1, CandidateTier.VolunteerFree), ("Anna", 0, CandidateTier.VolunteerFree), ("Bo", 4, CandidateTier.VolunteerRoleFree) };
            var a = CandidateRules.Rank(list, x => x.Item3, x => x.Item1, x => x.Item2).Select(x => x.Item2).ToArray();
            var b = CandidateRules.Rank(list.Reverse(), x => x.Item3, x => x.Item1, x => x.Item2).Select(x => x.Item2).ToArray();
            a.Should().Equal(4, 0, 1, 2, 3);
            b.Should().Equal(a);
        }

        [Theory]
        [InlineData("8:30", 8, 30)]
        [InlineData("08:30", 8, 30)]
        public void Klockslag_tolkas(string s, int h, int m) => StaffingRules.ParseTime(s).Should().Be(new TimeSpan(h, m, 0));

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("8")]
        [InlineData("24:00")]
        public void Allt_annat_ar_okant(string? s) => StaffingRules.ParseTime(s).Should().BeNull();
    }
}
