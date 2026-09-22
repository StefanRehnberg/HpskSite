using System.Text;
using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ Det farliga felet i en kontoutdragsläsare kraschar inte — det ger ett belopp som ser
    /// rimligt ut. Varje tvetydig form har därför ett eget test, och de ambiguösa ska VÄGRAS.
    /// </summary>
    public class BankStatementFormatTests
    {
        // ── Belopp ──────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1234,50", 1234.50)]
        [InlineData("1 234,50", 1234.50)]          // vanligt blanksteg
        [InlineData("1 234,50", 1234.50)]     // ⚠️ hårt blanksteg — syns inte i filen
        [InlineData("1.234,50", 1234.50)]          // svensk med båda
        [InlineData("1,234.50", 1234.50)]          // engelsk med båda
        [InlineData("-450,00", -450.00)]
        [InlineData("450,00-", -450.00)]           // efterställt minus
        [InlineData("250", 250)]
        [InlineData("1.234.567", 1234567)]         // flera tusentalsavgränsare
        [InlineData("270,00 kr", 270.00)]
        [InlineData("1 234,50 SEK", 1234.50)]
        public void Belopp_lases(string cell, decimal expected)
        {
            BankStatementFormat.TryAmount(cell, out var amount).Should().BeTrue();
            amount.Should().Be(expected);
        }

        /// <summary>
        /// ⚠️⚠️ KÄRNAN. Tre siffror efter separatorn är TUSENTAL, en eller två är DECIMALER.
        /// Läses 1.234 som 1,23 blir avstämningen fel med en faktor tusen, och differensen går
        /// inte att förklara för någon.
        /// </summary>
        [Theory]
        [InlineData("1.234", 1234)]
        [InlineData("1,234", 1234)]
        [InlineData("1.23", 1.23)]
        [InlineData("1,2", 1.2)]
        public void Tre_siffror_ar_tusental_tva_ar_decimaler(string cell, decimal expected)
        {
            BankStatementFormat.TryAmount(cell, out var amount).Should().BeTrue();
            amount.Should().Be(expected);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Ingående saldo")]
        [InlineData("1,23456")]   // fyra+ siffror efter separatorn är varken tusental eller ören
        [InlineData("12-34")]
        [InlineData("abc")]
        public void Icke_belopp_vagras(string cell)
            => BankStatementFormat.TryAmount(cell, out _).Should().BeFalse();

        // ── Datum ───────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("2026-09-22")]
        [InlineData("2026/09/22")]
        [InlineData("2026.09.22")]
        [InlineData("20260922")]
        public void Datum_lases(string cell)
        {
            BankStatementFormat.TryDate(cell, out var d).Should().BeTrue();
            d.Should().Be(new DateTime(2026, 9, 22));
        }

        /// <summary>
        /// ⚠️⚠️ 03/04/2026 är 3 april eller 4 mars beroende på bank. Ett datum i fel månad
        /// flyttar posten till fel period — i avstämningen ser det ut som en differens, i
        /// bokslutet som en felperiodisering. Hellre en rad operatören får mappa själv.
        /// </summary>
        [Theory]
        [InlineData("03/04/2026")]
        [InlineData("04-03-2026")]
        [InlineData("22 september")]
        [InlineData("")]
        public void Tvetydiga_datum_vagras(string cell)
            => BankStatementFormat.TryDate(cell, out _).Should().BeFalse();

        // ── Avgränsare ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ En beskrivningstext bär ofta komma ("Swish, Kalle"), så ren teckenräkning väljer
        /// komma i en semikolonfil. Stabiliteten i kolumnantalet är det som avgör.
        /// </summary>
        [Fact]
        public void Semikolon_vinner_over_komma_i_texten()
        {
            var csv = "Datum;Text;Belopp\n"
                    + "2026-09-01;Swish, Kalle Karlsson;270,00\n"
                    + "2026-09-02;Kortköp, ICA;-120,50\n";

            BankStatementFormat.SniffDelimiter(csv).Should().Be(';');
        }

        [Fact]
        public void Tabb_hittas()
            => BankStatementFormat.SniffDelimiter("Datum\tText\tBelopp\n2026-09-01\tA\t1,00\n")
                .Should().Be('\t');

        [Fact]
        public void Citerat_falt_med_avgransare_halls_ihop()
        {
            var f = BankStatementFormat.SplitLine("2026-09-01;\"Swish; Kalle\";270,00", ';');
            f.Should().HaveCount(3);
            f[1].Should().Be("Swish; Kalle");
        }

        [Fact]
        public void Dubbelt_citattecken_ar_ett_escapat_citattecken()
        {
            var f = BankStatementFormat.SplitLine("a;\"han sa \"\"hej\"\"\";1,00", ';');
            f[1].Should().Be("han sa \"hej\"");
        }

        // ── Kodsida ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️⚠️ Ett felavkodat utdrag syns som mojibake i varje beskrivning och läses som ett
        /// produktfel. Strikt UTF-8 är det som gör fallbacken verklig: utan den tolkas ogiltiga
        /// byte som U+FFFD och avkodningen "lyckas" med en text full av frågetecken.
        /// </summary>
        [Fact]
        public void Windows1252_avkodas_nar_det_inte_ar_utf8()
        {
            var bytes = Encoding.Latin1.GetBytes("Överföring till Åsa");
            BankStatementFormat.Decode(bytes).Should().Be("Överföring till Åsa");
        }

        [Fact]
        public void Utf8_avkodas()
        {
            var bytes = Encoding.UTF8.GetBytes("Överföring till Åsa");
            BankStatementFormat.Decode(bytes).Should().Be("Överföring till Åsa");
        }

        [Fact]
        public void Utf8_med_bom_avkodas_utan_bom_i_texten()
        {
            var bytes = new UTF8Encoding(true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("Datum;Text")).ToArray();

            BankStatementFormat.Decode(bytes).Should().Be("Datum;Text");
        }

        // ── Kolumnförslaget ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Kolumnerna_gissas_ur_rubrikerna()
        {
            var g = BankStatementFormat.GuessColumns(
                new[] { "Bokföringsdag", "Text", "Belopp", "Saldo" });

            g.Date.Should().Be(0);
            g.Text.Should().Be(1);
            g.Amount.Should().Be(2);
            g.Balance.Should().Be(3);
            g.IsUsable.Should().BeTrue();
        }

        [Fact]
        public void Delade_in_och_ut_kolumner_kanns_igen()
        {
            var g = BankStatementFormat.GuessColumns(
                new[] { "Transaktionsdatum", "Beskrivning", "Insättning", "Uttag" });

            g.Date.Should().Be(0);
            g.AmountOut.Should().Be(3);
            g.IsUsable.Should().BeTrue();
        }

        [Fact]
        public void Utan_datumkolumn_gar_filen_inte_att_lasa()
            => BankStatementFormat.GuessColumns(new[] { "Text", "Saldo" })
                .IsUsable.Should().BeFalse();

        // ── Rubrikraden ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Svenska exporter inleds ofta med informationsrader före rubrikerna. Att blint ta
        /// rad 1 ger en mappning mot fel kolumner, och det syns först som konstiga belopp.
        /// </summary>
        [Fact]
        public void Rubrikraden_hittas_under_informationsrader()
        {
            var rows = new List<string[]>
            {
                new[] { "Kontonummer 1234-5678" },
                new[] { "" },
                new[] { "Bokföringsdag", "Text", "Belopp", "Saldo" },
                new[] { "2026-09-01", "Swish", "270,00", "5000,00" }
            };

            BankStatementFormat.FindHeaderRow(rows).Should().Be(2);
        }

        /// <summary>Kontrollprov: en fil utan rubriker ska svara -1, inte peka på en datarad.</summary>
        [Fact]
        public void Utan_rubrikrad_svaras_minus_ett()
        {
            var rows = new List<string[]>
            {
                new[] { "2026-09-01", "Swish", "270,00" },
                new[] { "2026-09-02", "Kort", "-120,00" }
            };

            BankStatementFormat.FindHeaderRow(rows).Should().Be(-1);
        }
    }
}
