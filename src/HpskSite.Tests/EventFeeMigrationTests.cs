using FluentAssertions;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tolkningen av den gamla fritextavgiften till ett debiterbart tal.
    ///
    /// <para><b>⚠️ ETT FEL HÄR DEBITERAR FEL PERSON FEL SUMMA</b>, och felet upptäcks av medlemmen
    /// vid betalningen — inte av oss. Migreringen körs dessutom EN gång över data vi inte kan läsa
    /// i förväg (prod), så varje form måste prövas innan en enda rad rörs.</para>
    ///
    /// <para><b>Regeln som testerna vaktar: tolka bara det otvetydiga.</b> Allt som bär ett villkor
    /// ska rapporteras till en människa, aldrig gissas på.</para>
    /// </summary>
    public class EventFeeMigrationTests
    {
        private static EventFeeMigration.Result P(string? s) => EventFeeMigration.Parse(s);

        // ── Tomt ────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Tomt_falt_ar_ingenting_att_migrera(string? raw)
            => P(raw).Outcome.Should().Be(EventFeeMigration.FeeParse.Empty);

        // ── Entydiga belopp ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("300", 300)]
        [InlineData("300 kr", 300)]
        [InlineData("300kr", 300)]
        [InlineData("300:-", 300)]
        [InlineData("300 SEK", 300)]
        [InlineData("300 kronor", 300)]
        [InlineData("  325  ", 325)]
        [InlineData("1 200 kr", 1200)]
        [InlineData("250,50", 250.50)]
        [InlineData("0", 0)]
        public void Entydigt_belopp_tolkas(string raw, decimal expected)
        {
            var r = P(raw);
            r.Outcome.Should().Be(EventFeeMigration.FeeParse.Parsed, r.Reason);
            r.Amount.Should().Be(expected);
        }

        [Theory]
        [InlineData("Gratis")]
        [InlineData("gratis")]
        [InlineData("GRATIS")]
        [InlineData("Fritt")]
        [InlineData("Kostnadsfritt")]
        [InlineData("Ingen avgift")]
        [InlineData("-")]
        public void Ensamt_gratisord_blir_noll(string raw)
        {
            var r = P(raw);
            r.Outcome.Should().Be(EventFeeMigration.FeeParse.Parsed, r.Reason);
            r.Amount.Should().Be(0m);
        }

        /// <summary>
        /// ⚠️ Noll är ett SVAR, inte ett tomt fält — "gratis" är en uppgift arrangören lämnat.
        /// Samma regel som tävlingarnas juniorAvgift, där 0 betyder fritt och tomt betyder
        /// "använd grundavgiften".
        /// </summary>
        [Fact]
        public void Noll_skiljs_fran_tomt()
        {
            P("Gratis").Outcome.Should().Be(EventFeeMigration.FeeParse.Parsed);
            P("").Outcome.Should().Be(EventFeeMigration.FeeParse.Empty);
        }

        // ── ⚠️ VILLKOREN — det här är hela poängen med sviten ────────────────────────────────

        /// <summary>
        /// "Gratis för juniorer" betyder att NÅGON betalar, och beloppet står ingenstans. Slås det
        /// till 0 blir evenemanget gratis för alla; tolkas det som ett pris finns inget pris att
        /// tolka. Enda riktiga svaret är att lämna det till en människa.
        /// </summary>
        [Theory]
        [InlineData("Gratis for juniorer")]
        [InlineData("gratis for medlemmar")]
        [InlineData("100 kr, gratis for ungdomar")]
        [InlineData("100 kr for icke-medlemmar")]
        [InlineData("50 kr/person")]
        [InlineData("200 kr inkl. fika")]
        [InlineData("enligt overenskommelse")]
        [InlineData("se inbjudan")]
        [InlineData("ca 300 kr")]
        [InlineData("300-400 kr")]
        public void Villkorad_eller_vag_avgift_rapporteras_aldrig_gissas(string raw)
            => P(raw).Outcome.Should().Be(EventFeeMigration.FeeParse.Unparseable);

        /// <summary>
        /// ⚠️ Punkt lämnas ORÖRD och gör strängen otolkbar. "1.200" är tusental på svenska och
        /// "1.50" är decimal — samma tecken, två betydelser som skiljer sig med faktor 1000. En
        /// gissning där är skillnaden mellan 1 kr och 1 200 kr på en faktura.
        /// </summary>
        [Theory]
        [InlineData("1.200")]
        [InlineData("1.50")]
        public void Punkt_ar_tvetydig_och_gissas_inte(string raw)
            => P(raw).Outcome.Should().Be(EventFeeMigration.FeeParse.Unparseable);

        /// <summary>
        /// Dev bar 14300 i fältet. Att tyst migrera in det som ett debiterbart belopp vore att göra
        /// en gammal felskrivning till en faktura.
        /// </summary>
        [Fact]
        public void Orimligt_hogt_belopp_flaggas_for_handpalaggning()
        {
            var r = P("14300");
            r.Outcome.Should().Be(EventFeeMigration.FeeParse.Unparseable);
            r.Reason.Should().Contain("hogt");
        }

        [Fact]
        public void Negativt_belopp_avvisas() => P("-50").Outcome.Should().Be(EventFeeMigration.FeeParse.Unparseable);

        /// <summary>
        /// ⚠️ Hårt mellanslag (U+00A0) följer med vid inklistring från Word och Excel. Det SER ut
        /// som ett vanligt mellanslag, så en missad normalisering ger ett otolkbart värde som ingen
        /// förstår varför det inte gick igenom.
        /// </summary>
        [Fact]
        public void Hart_mellanslag_hanteras_som_vanligt()
        {
            var r = P("1 200 kr");
            r.Outcome.Should().Be(EventFeeMigration.FeeParse.Parsed, r.Reason);
            r.Amount.Should().Be(1200m);
        }

        /// <summary>
        /// Skälet följer med varje utfall — rapporten ska gå att läsa utan att öppna koden.
        /// </summary>
        [Fact]
        public void Varje_utfall_bar_ett_skal()
        {
            P("300 kr").Reason.Should().NotBeNullOrWhiteSpace();
            P("Gratis for juniorer").Reason.Should().Contain("Gratis for juniorer");
        }
    }
}
