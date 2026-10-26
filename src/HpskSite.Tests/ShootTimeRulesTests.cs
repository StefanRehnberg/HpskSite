using System;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Planera dagen steg 0: skjuttid per gren i stället för en konstant på 2 h (persontestet scenario 6).</summary>
    public class ShootTimeRulesTests
    {
        [Theory]
        [InlineData("Faltskytte", 45, 180, 30)]
        [InlineData("MagnumFalt", 45, 180, 30)]
        [InlineData("Precision", 30, 60, 15)]
        [InlineData("Milsnabb", 30, 60, 15)]
        [InlineData("Springskytte", 30, 120, 15)]
        [InlineData(null, 30, 120, 15)]
        [InlineData("", 30, 120, 15)]
        public void Forvalet_foljer_grenen(string? gren, int fore, int tid, int efter)
            => ShootTimeRules.Default(gren).Should().Be(new ShootTime(fore, tid, efter));

        [Fact]
        public void Fält_är_tre_timmar_inte_två()
            => ShootTimeRules.Default("Faltskytte").DurationMinutes.Should().Be(180);

        [Fact]
        public void Grenen_matchas_utan_hansyn_till_skiftlage_och_blanksteg()
            => ShootTimeRules.Default(" faltskytte ").Should().Be(ShootTimeRules.Default("Faltskytte"));

        [Fact]
        public void Planeringens_egna_varden_gar_fore_och_tomma_ger_forvalet()
        {
            var t = ShootTimeRules.Resolve("Faltskytte", 60, null, 0);
            t.Should().Be(new ShootTime(60, 180, 0));   // före skrivet över, skjuttid = förval, efter = 0 är ett eget värde
        }

        [Theory]
        [InlineData(-1, 100, 10)]
        [InlineData(10, 0, 10)]
        [InlineData(10, 100, 721)]
        [InlineData(10, 721, 10)]
        public void Ogiltiga_varden_ignoreras_vid_uppslag_och_vagras_vid_sparande(int fore, int tid, int efter)
        {
            ShootTimeRules.Validate(fore, tid, efter).Should().NotBeNull();
            var r = ShootTimeRules.Resolve("Faltskytte", fore, tid, efter);
            var d = ShootTimeRules.Default("Faltskytte");
            if (fore is < 0 or > 720) r.BeforeMinutes.Should().Be(d.BeforeMinutes);
            if (tid is <= 0 or > 720) r.DurationMinutes.Should().Be(d.DurationMinutes);
            if (efter is < 0 or > 720) r.AfterMinutes.Should().Be(d.AfterMinutes);
        }

        [Fact]
        public void Tomma_varden_ar_giltiga_och_betyder_forvalet()
            => ShootTimeRules.Validate(null, null, null).Should().BeNull();

        [Fact]
        public void Fonstret_for_en_fältstart_utan_slut_ar_fore_plus_skjuttid_plus_efter()
        {
            var start = new DateTime(2027, 6, 5, 11, 50, 0);
            var w = new ShootTime(45, 180, 30).Window(start, null);
            w.Start.Should().Be(new DateTime(2027, 6, 5, 11, 5, 0));
            w.End.Should().Be(new DateTime(2027, 6, 5, 15, 20, 0));
            w.AllDay.Should().BeFalse();
        }

        [Fact]
        public void Ett_kant_slut_gar_fore_skjuttiden()
        {
            var start = new DateTime(2027, 6, 5, 10, 0, 0);
            var w = new ShootTime(30, 60, 15).Window(start, new DateTime(2027, 6, 5, 10, 40, 0));
            w.End.Should().Be(new DateTime(2027, 6, 5, 10, 55, 0));
        }

        [Fact]
        public void Ett_slut_fore_starten_ar_ett_fel_i_uppgifterna_och_ignoreras()
        {
            var start = new DateTime(2027, 6, 5, 10, 0, 0);
            new ShootTime(0, 60, 0).Window(start, new DateTime(2027, 6, 5, 9, 0, 0)).End.Should().Be(start.AddMinutes(60));
        }

        [Fact]
        public void Ett_uppdrag_i_marginalen_krockar_med_starten_men_inte_utanfor()
        {
            var start = new DateTime(2027, 6, 5, 11, 50, 0);
            var win = new ShootTime(45, 180, 30).Window(start, null);
            // Pass 08–11 slutar 05 minuter före fönstret börjar (11:05): ingen krock. Pass 08–11:30 går in i förberedelsen: krock.
            StaffInterval.Between(win, "bana", new StaffInterval(start.Date.AddHours(8), start.Date.AddHours(11), false), "bana").Should().Be(ClashKind.None);
            StaffInterval.Between(win, "bana", new StaffInterval(start.Date.AddHours(8), start.Date.AddHours(11).AddMinutes(30), false), "bana").Should().Be(ClashKind.Overlap);
            // Efter: starten slutar 14:50 + 30 min vila = 15:20; pass 15:00 krockar, 15:20 gör det inte.
            StaffInterval.Between(win, "bana", new StaffInterval(start.Date.AddHours(15), start.Date.AddHours(17), false), "bana").Should().Be(ClashKind.Overlap);
            StaffInterval.Between(win, "bana", new StaffInterval(start.Date.AddHours(15).AddMinutes(20), start.Date.AddHours(17), false), "bana").Should().Be(ClashKind.None);
        }

        [Theory]
        [InlineData(180, "3 h")]
        [InlineData(150, "2 h 30 min")]
        [InlineData(45, "45 min")]
        [InlineData(60, "1 h")]
        public void Texten_sags_som_en_manniska_sager_det(int minuter, string text)
            => ShootTime.Text(minuter).Should().Be(text);
    }
}
