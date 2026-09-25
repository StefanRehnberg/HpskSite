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

        // ── Hela filen, som banken lämnar den ───────────────────────────────────────────────

        /// <summary>
        /// Swedbanks export (<c>Transaktioner_2026-09-24_16-41-33.csv</c>): en informationsrad
        /// UTAN avgränsare överst, sedan kommaseparerade rubriker och decimalpunkt.
        ///
        /// <para>⚠️⚠️ Felrapporten 2026-09-24: avgränsaren mättes mot FÖRSTA radens kolumnantal.
        /// Informationsraden har en kolumn med varje avgränsare, så alla vägrades, filen lästes
        /// som en enda kolumn och mappningen visade bara "— saknas —". Testet ovan med redan
        /// uppdelade rader kunde inte falla på det — därför läses hela filen här.</para>
        /// </summary>
        private const string Swedbank =
              "* Transaktioner Period 2026-01-01 – 2026-09-24 Skapad 2026-09-24 16:41 CEST\n"
            + "Radnummer,Clearingnummer,Kontonummer,Produkt,Valuta,Bokföringsdag,Transaktionsdag,Valutadag,Referens,Beskrivning,Belopp,Bokfört saldo\n"
            + "1,8327-9,123456789,Företagskonto,SEK,2026-09-23,2026-09-23,2026-09-23,\"Swish\",\"Kalle Karlsson\",270.00,5270.00\n"
            + "2,8327-9,123456789,Företagskonto,SEK,2026-09-20,2026-09-19,2026-09-20,\"Kortköp\",\"ICA Kvantum\",-120.50,5000.00\n"
            + "3,8327-9,123456789,Företagskonto,SEK,2026-09-18,2026-09-18,2026-09-18,\"Bg 1234-5678\",\"Anmälningsavgift\",1250.00,5120.50\n";

        [Fact]
        public void Informationsrad_utan_avgransare_overst_faller_inte_avgransaren()
            => BankStatementFormat.SniffDelimiter(Swedbank).Should().Be(',');

        /// <summary>
        /// ⚠️ Motprovet till regeln ovan: i en semikolonfil med decimalkomma delar KOMMA också
        /// varje datarad lika många gånger. Semikolon måste ändå vinna — rubrikraden och det
        /// större kolumnantalet avgör.
        /// </summary>
        [Fact]
        public void Semikolonfil_med_decimalkomma_och_informationsrad_forblir_semikolon()
        {
            var csv = "Kontonummer 1234-5678\n"
                    + "Bokföringsdag;Text;Belopp;Saldo\n"
                    + "2026-09-01;Swish;270,00;5270,00\n"
                    + "2026-09-02;Kort;-120,50;5149,50\n"
                    + "2026-09-03;Bg;1250,00;6399,50\n";

            BankStatementFormat.SniffDelimiter(csv).Should().Be(';');
        }

        [Fact]
        public void Swedbanks_fil_forhandslases_med_rubriker_och_forslag()
        {
            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var (map, sample, header) = svc.Preview(Encoding.UTF8.GetBytes(Swedbank));

            header.Should().HaveCount(12);
            map.HeaderRow.Should().Be(1);
            map.Delimiter.Should().Be(',');
            map.Date.Should().Be(5);      // Bokföringsdag
            map.Amount.Should().Be(10);   // Belopp
            map.Balance.Should().Be(11);  // Bokfört saldo
            sample.Should().HaveCount(3);
        }

        /// <summary>
        /// ⚠️⚠️ Andra felrapporten 2026-09-24 (Dalslands Sparbank, Swedbanks plattform): rättningen
        /// ovan var ute och filen lästes ändå inte. En "exportera till Excel"-fil är UTF-16 — utan
        /// avkodningen blir varannan byte ett nolltecken och ingen rubrik känns igen.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Swedbanks_fil_i_UTF16_forhandslases(bool withBom)
        {
            var body = Encoding.Unicode.GetBytes(Swedbank);
            var bytes = withBom ? Encoding.Unicode.GetPreamble().Concat(body).ToArray() : body;

            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var (map, sample, header) = svc.Preview(bytes);

            header.Should().HaveCount(12);
            header[5].Should().Be("Bokföringsdag");
            map.Date.Should().Be(5);
            map.Amount.Should().Be(10);
            svc.Parse(bytes, map).Rows.Select(r => r.Amount).Should().Equal(270.00m, -120.50m, 1250.00m);
        }

        /// <summary>
        /// ⚠️ Rubriker vi inte känner igen får INTE ge en tom mappning. Går filen att dela upp ska
        /// operatören få "Kolumn 1…N" och kunna peka ut kolumnerna själv — det är vad ytan lovar.
        /// </summary>
        [Fact]
        public void Okanda_rubriker_ger_valbara_kolumner_och_gar_att_mappa_for_hand()
        {
            var csv = "Dag;Vad;Summa\n2026-09-01;Swish;270,00\n2026-09-02;Kort;-120,50\n";
            var bytes = Encoding.UTF8.GetBytes(csv);

            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var (map, sample, header) = svc.Preview(bytes);

            // ⚠️ Sedan 2026-09-25: rubrikraden är raden närmast före första datumraden, så kassören
            //    får BANKENS namn att välja bland (inte "Kolumn 1…N"), och datumet läses ur datat.
            header.Should().Equal("Dag", "Vad", "Summa");
            map.HeaderRow.Should().Be(0);
            map.Date.Should().Be(0, "datumkolumnen syns i datat");
            map.Amount.Should().Be(-1, "\"Summa\" känns inte igen — ett belopp gissas aldrig ur datat");
            sample.Should().NotBeEmpty();

            // Operatören pekar ut beloppet. Rubrikraden hoppas över utan att räknas som överhoppad.
            map.Text = 1; map.Amount = 2;
            var parsed = svc.Parse(bytes, map);
            parsed.Rows.Select(r => r.Amount).Should().Equal(270.00m, -120.50m);
            parsed.Skipped.Should().BeEmpty();
        }

        /// <summary>
        /// ⚠️⚠️ MICHAELS RIKTIGA FORMAT (Dalslands Sparbank, bild 2026-09-25): FÖRKORTADE rubriker —
        /// "Bokfdag", "Transdag", "Valutadag" — och kolumnen "Text" efter "Referens". Utan dem
        /// hittades inget datum, knappen "Läs in" doldes och han såg en ifylld mappning utan någon
        /// väg att spara. Mitt Swedbank-exempel ovan hade de LÅNGA rubrikerna och kunde inte falla.
        /// </summary>
        private const string Dalsland =
              "* Transaktionsrapport Period 2025-01-01 – 2025-12-31 Skapad 2026-09-24 16:41 CEST\n"
            + "Radnr,Clnr,Kontonr,Produkt,Valuta,Bokfdag,Transdag,Valutadag,Referens,Text,Belopp,Saldo\n"
            + "1,XXXXXXX,XXXXXXXX,\"Föreningskonto\",SEK,2025-12-30,2025-12-30,2025-12-30,\"Polismyndigheten\",\"Bg-bet. via internet\",-360.00,122860.80\n"
            + "2,XXXXXXX,XXXXXXXX,\"Föreningskonto\",SEK,2025-12-22,2025-12-22,2025-12-22,\"Swish\",\"Kalle Karlsson\",270.00,123220.80\n";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Michaels_fil_med_forkortade_rubriker_lases(bool windows1252)
        {
            var bytes = windows1252 ? System.Text.Encoding.Latin1.GetBytes(Dalsland) : Encoding.UTF8.GetBytes(Dalsland);
            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var (map, _, header) = svc.Preview(bytes);

            map.HeaderRow.Should().Be(1, "informationsraden är ingen rubrik");
            header[5].Should().Be("Bokfdag");
            map.Date.Should().Be(5, "Bokfdag är bokföringsdagen");
            map.Text.Should().Be(9, "\"Text\" går före \"Referens\"");
            map.Amount.Should().Be(10);
            map.Balance.Should().Be(11);

            var parsed = svc.Parse(bytes, map);
            parsed.Skipped.Should().BeEmpty();
            parsed.Rows.Select(r => r.Amount).Should().Equal(-360.00m, 270.00m);
            parsed.Rows[0].BookedDate.Should().Be(new DateTime(2025, 12, 30));
            parsed.Rows[0].Text.Should().Be("Bg-bet. via internet");
        }

        /// <summary>"Bokfört saldo" börjar också på "bokf" — det får aldrig bli datumkolumnen.</summary>
        [Fact]
        public void Bokfort_saldo_blir_aldrig_datum()
        {
            var g = BankStatementFormat.GuessColumns(new[] { "Bokfört saldo", "Bokfdag", "Belopp" });
            g.Date.Should().Be(1);
            g.Balance.Should().Be(0);
        }

        /// <summary>
        /// Känns ingen rubrik igen som datum läses datumkolumnen ur DATAT — den kolumn där varje
        /// exempelrad är ett otvetydigt datum.
        /// </summary>
        [Fact]
        public void Datumkolumnen_hittas_i_datat_nar_rubriken_ar_okand()
        {
            var csv = "Nr;Dag;Belopp\n1;2026-09-01;270,00\n2;2026-09-02;-120,50\n";
            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var (map, _, _) = svc.Preview(Encoding.UTF8.GetBytes(csv));

            map.Date.Should().Be(1);
            map.Amount.Should().Be(2);
        }

        [Fact]
        public void Swedbanks_fil_lases_in_med_ratt_belopp()
        {
            var svc = new HpskSite.Services.Ledger.LedgerBankImportService(null!, null!);
            var bytes = Encoding.UTF8.GetBytes(Swedbank);
            var (map, _, _) = svc.Preview(bytes);

            var parsed = svc.Parse(bytes, map);

            parsed.Skipped.Should().BeEmpty();
            parsed.Rows.Select(r => r.Amount).Should().Equal(270.00m, -120.50m, 1250.00m);
            parsed.Rows[0].BookedDate.Should().Be(new DateTime(2026, 9, 23));
            parsed.Rows[0].Balance.Should().Be(5270.00m);
        }
    }
}
