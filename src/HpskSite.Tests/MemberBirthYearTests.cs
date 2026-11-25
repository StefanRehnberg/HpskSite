using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Födelseår ur personnummer/födelsedatum (steg 3b). Ålder räknas på kalenderår, så bara året spelar roll.</summary>
    public class MemberBirthYearTests
    {
        private const int Now = 2026;

        [Theory]
        [InlineData("19900512-1234", 1990)]
        [InlineData("199005121234", 1990)]
        [InlineData("1990 0512 1234", 1990)]
        [InlineData("19900561-1234", 1990)]   // samordningsnummer (dag + 60): året påverkas inte
        public void Komplett_personnummer_ger_saker_ar(string pn, int year)
        {
            var r = MemberBirthYear.From(null, pn, Now);
            Assert.Equal(year, r.Year);
            Assert.Equal(BirthYearStatus.Ok, r.Status);
        }

        [Theory]
        [InlineData("900512-1234", 1990)]
        [InlineData("050512-1234", 2005)]
        [InlineData("250512-1234", 2025)]
        [InlineData("260512-1234", 2026)]
        [InlineData("270512-1234", 1927)]    // efter i år: 1900-talet
        [InlineData("120512+1234", 1912)]    // plus = 100 år äldre
        public void Tiosiffrigt_personnummer_ar_osakert(string pn, int year)
        {
            var r = MemberBirthYear.From(null, pn, Now);
            Assert.Equal(year, r.Year);
            Assert.Equal(BirthYearStatus.Uncertain, r.Status);
            Assert.True(r.IsUsable);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Inget_alls_ar_Missing(string? pn)
        {
            var r = MemberBirthYear.From(pn, pn, Now);
            Assert.Null(r.Year);
            Assert.Equal(BirthYearStatus.Missing, r.Status);
            Assert.False(r.IsUsable);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("19900512-1")]       // ofullständigt (9 siffror)
        [InlineData("19900512-123456")]  // 14 siffror
        [InlineData("1990051")]
        [InlineData("18990512-1234")]    // före 1900
        public void Oläsligt_personnummer_ar_Invalid(string pn)
        {
            var r = MemberBirthYear.From(null, pn, Now);
            Assert.Null(r.Year);
            Assert.Equal(BirthYearStatus.Invalid, r.Status);
        }

        [Fact]
        public void Komplett_personnummer_efter_i_ar_ar_Future()
        {
            var r = MemberBirthYear.From(null, "20300512-1234", Now);
            Assert.Null(r.Year);
            Assert.Equal(BirthYearStatus.Future, r.Status);
        }

        [Theory]
        [InlineData("1990-05-12", 1990)]
        [InlineData("1990-05-12 00:00:00", 1990)]
        [InlineData("1990-12-31", 1990)]     // kalenderår: 31 dec och 1 jan i samma år ger samma år
        [InlineData("1990-01-01", 1990)]
        [InlineData("1992-02-29", 1992)]
        public void Fodelsedatum_ger_ar(string bd, int year)
        {
            var r = MemberBirthYear.From(bd, null, Now);
            Assert.Equal(year, r.Year);
            Assert.Equal(BirthYearStatus.Ok, r.Status);
        }

        [Theory]
        [InlineData("1899-12-31", BirthYearStatus.Invalid)]
        [InlineData("2031-01-01", BirthYearStatus.Future)]
        [InlineData("inte ett datum", BirthYearStatus.Invalid)]
        public void Dalig_fodelsedatum(string bd, BirthYearStatus status)
        {
            var r = MemberBirthYear.From(bd, null, Now);
            Assert.Null(r.Year);
            Assert.Equal(status, r.Status);
        }

        [Fact]
        public void Orimlig_alder_ar_inte_placerbar()
        {
            var r = MemberBirthYear.From("1900-01-01", null, 2026);   // 126 år
            Assert.Null(r.Year);
            Assert.Equal(BirthYearStatus.Implausible, r.Status);
            Assert.Equal(1906, MemberBirthYear.From("1906-01-01", null, 2026).Year);   // exakt 120 är kvar
        }

        [Fact]
        public void Samma_ar_i_bada_kallorna_ar_Ok_ocksa_for_tiosiffrigt()
        {
            var r = MemberBirthYear.From("1990-05-12", "900512-1234", Now);
            Assert.Equal(1990, r.Year);
            Assert.Equal(BirthYearStatus.Ok, r.Status);
        }

        [Fact]
        public void Komplett_personnummer_som_motsager_fodelsedatum_ar_Conflict()
        {
            var r = MemberBirthYear.From("1991-05-12", "19900512-1234", Now);
            Assert.Null(r.Year);
            Assert.Equal(BirthYearStatus.Conflict, r.Status);
            Assert.False(r.IsUsable);
        }

        [Fact]
        public void Tiosiffrigt_personnummer_ger_vika_for_ett_uttryckligt_fodelsedatum()
        {
            // "12" kan vara 1912 eller 2012; födelsedatumet avgör.
            var r = MemberBirthYear.From("1912-05-12", "120512-1234", Now);
            Assert.Equal(1912, r.Year);
            Assert.Equal(BirthYearStatus.Ok, r.Status);
            var r2 = MemberBirthYear.From("2012-05-12", "120512-1234", Now);
            Assert.Equal(2012, r2.Year);
        }

        [Fact]
        public void Oläsligt_personnummer_men_gott_fodelsedatum_anvander_fodelsedatum()
        {
            var r = MemberBirthYear.From("1985-03-04", "198503-", Now);   // pnrIncomplete från importen
            Assert.Equal(1985, r.Year);
            Assert.Equal(BirthYearStatus.Ok, r.Status);
        }
    }
}
