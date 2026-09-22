using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ Ett SIE-fel syns inte hos oss — det syns när klubbens revisor eller bokföringsprogram
    /// vägrar läsa filen, långt härifrån och utan att någon kan säga varför.
    /// </summary>
    public class SieFormatTests
    {
        // ── Fält ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Enkelt_falt_citeras_inte()
            => SieFormat.Field("Medlemsavgifter").Should().Be("Medlemsavgifter");

        /// <summary>
        /// ⚠️ "Övriga intäkter" utan citat blir TVÅ fält, och posten betyder något annat.
        /// </summary>
        [Fact]
        public void Falt_med_mellanslag_citeras()
            => SieFormat.Field("Övriga intäkter").Should().Be("\"Övriga intäkter\"");

        /// <summary>
        /// ⚠️⚠️ Ett tomt fält MÅSTE bli "" — utelämnas det förskjuts varje fält efter det.
        /// </summary>
        [Fact]
        public void Tomt_falt_blir_tomma_citattecken()
        {
            SieFormat.Field("").Should().Be("\"\"");
            SieFormat.Field(null).Should().Be("\"\"");
        }

        [Fact]
        public void Inre_citattecken_escapas()
            => SieFormat.Field("Kalles \"lån\"").Should().Be("\"Kalles \\\"lån\\\"\"");

        /// <summary>
        /// ⚠️ En radbrytning i en verifikationstext bryter formatet helt. Den kan komma dit genom
        /// att någon klistrat in text i beskrivningen.
        /// </summary>
        [Theory]
        [InlineData("Rad ett\nRad två")]
        [InlineData("Rad ett\r\nRad två")]
        [InlineData("Med\ttabb")]
        public void Radbrytningar_och_tabbar_tas_bort(string input)
        {
            var f = SieFormat.Field(input);
            f.Should().NotContain("\n");
            f.Should().NotContain("\r");
            f.Should().NotContain("\t");
        }

        // ── Belopp ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️⚠️ Svensk kultur ger decimalKOMMA, och komma i ett SIE-belopp gör posten oläsbar.
        /// Testet skulle falla om InvariantCulture togs bort — och det är hela skälet det finns.
        /// </summary>
        [Theory]
        [InlineData(1234.5, "1234.50")]
        [InlineData(-450, "-450.00")]
        [InlineData(0, "0.00")]
        [InlineData(0.05, "0.05")]
        public void Belopp_har_punkt_och_tva_decimaler(decimal value, string expected)
            => SieFormat.Amount(value).Should().Be(expected);

        [Fact]
        public void Belopp_har_inga_tusentalsavgransare()
            => SieFormat.Amount(1234567.89m).Should().Be("1234567.89");

        // ── Datum ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Datum_ar_aaaammdd()
            => SieFormat.Date(new DateTime(2026, 9, 22)).Should().Be("20260922");

        // ── Poster ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Post_byggs_med_mellanslag()
            => SieFormat.Post("KONTO", "3010", "Medlemsavgifter")
                .Should().Be("#KONTO 3010 Medlemsavgifter");

        [Fact]
        public void Post_citerar_dar_det_behovs()
            => SieFormat.Post("KONTO", "3890", "Övriga intäkter")
                .Should().Be("#KONTO 3890 \"Övriga intäkter\"");

        // ── Kontotyp ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Samma klassindelning som resten av liggaren: klass 2 är S och klass 8 är I. Vore de
        /// olika skulle SIE-filen och bokslutet beskriva samma konto på två sätt.
        /// </summary>
        [Theory]
        [InlineData(1930, "T")]
        [InlineData(2060, "S")]
        [InlineData(3010, "I")]
        [InlineData(8310, "I")]
        [InlineData(4010, "K")]
        [InlineData(7510, "K")]
        public void Kontotypen_foljer_klassen(int account, string expected)
            => SieFormat.AccountType(account).Should().Be(expected);

        /// <summary>
        /// Kontrollprov: SIE-typen och bokslutets riktning får inte kunna säga emot varandra.
        /// </summary>
        [Theory]
        [InlineData(3010)]
        [InlineData(8310)]
        public void Intaktstyp_i_sie_ar_intaktsriktad_i_bokslutet(int account)
        {
            SieFormat.AccountType(account).Should().Be("I");
            LedgerAccountClass.IsRevenueDirected(account).Should().BeTrue();
        }
    }
}
