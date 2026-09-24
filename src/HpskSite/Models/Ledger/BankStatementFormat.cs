using System.Globalization;
using System.Text;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Reglerna för att läsa ett kontoutdrag ur en internetbank, samlade där de går att pröva
    /// utan databas — samma form som <see cref="EventPaymentFormat"/> och
    /// <c>SkjutprovCandidate</c>.
    ///
    /// <para><b>⚠️⚠️ DET FARLIGA FELET ÄR INTE ETT SOM KRASCHAR — DET ÄR ETT BELOPP SOM TOLKAS
    /// FEL OCH ÄNDÅ SER RIMLIGT UT.</b> <c>1.234,50</c> läst som <c>1.234</c> blir 1,23 kr i
    /// stället för 1 234,50, och då visar avstämningen en differens ingen kan förklara. Därför
    /// <b>vägrar</b> <see cref="TryAmount"/> hellre än gissar, och varje tvetydig form har ett
    /// eget test.</para>
    ///
    /// <para><b>⚠️ Varför inget bank-API:</b> PSD2 är licens- och kostnadsdrivet och valdes bort.
    /// Föreningen laddar upp filen själv, och ingen tredje part får läsrätt till kontot.</para>
    ///
    /// <para><b>Filformaten i tur och ordning</b> (planen, rättad 2026-09-18): CSV/Excel ur
    /// internetbanken <i>först</i> — det enda en klubbkassör faktiskt kan få, men utan standard,
    /// så det kräver ett kolumnmappningssteg per bank. Sedan BGMAX, som är strukturerad och bär
    /// OCR-referensen. camt.053 sist, eftersom den kräver ett filkommunikationsavtal målgruppen
    /// inte har.</para>
    /// </summary>
    public static class BankStatementFormat
    {
        /// <summary>Avgränsare vi provar, i fallande sannolikhet för en svensk bank.</summary>
        /// <remarks>
        /// ⚠️ Semikolon FÖRST. Svenska exporter använder decimalkomma, och då kan fältavgränsaren
        /// inte vara komma — semikolon är därför normen här, tvärtemot engelskspråkig CSV.
        /// </remarks>
        private static readonly char[] Delimiters = { ';', '\t', ',', '|' };

        /// <summary>
        /// Avkodar filens byte till text.
        ///
        /// <para><b>⚠️⚠️ ETT FELAVKODAT KONTOUTDRAG SYNS SOM MOJIBAKE I VARJE BESKRIVNING</b>
        /// ("Ãverfring"), och det är den sortens fel som ser ut som ett produktfel men är en
        /// kodsidefråga. Bankerna exporterar oftast Windows-1252; UTF-8 blir vanligare.</para>
        ///
        /// <para>Ordningen är: BOM om den finns → <b>strikt</b> UTF-8 → annars 1252. Strikt är
        /// hela poängen: utan <c>throwOnInvalidBytes</c> tolkar .NET ogiltiga byte som U+FFFD och
        /// "lyckas" med en text full av frågetecken, alltså ingen fallback alls.</para>
        /// </summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            // ⚠️⚠️ UTF-16 — "exportera till Excel" i en del internetbanker. Utan den här grenen
            //    blev filen text med ett nolltecken mellan varje bokstav: ingen rubrik kändes igen,
            //    och mappningen visade bara "— saknas —" (felrapport 2026-09-24, Dalslands
            //    Sparbank). Med BOM avgör den; utan BOM avslöjar nollbytena på varannan plats det.
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (LooksLikeUtf16(bytes, out var bigEndian))
                return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // 1252, inte ISO-8859-1: de skiljer sig på 0x80-0x9F, där bankerna lägger
                // typografiska citattecken och tankstreck. Latin-1 hade gett kontrolltecken.
                return Encoding.Latin1.GetString(bytes);
            }
        }

        /// <summary>
        /// UTF-16 utan BOM: i en text som mest är ASCII är varannan byte noll. Mäts på de första
        /// byten — en vanlig CSV-fil har inga nollbyte alls.
        /// </summary>
        private static bool LooksLikeUtf16(byte[] bytes, out bool bigEndian)
        {
            bigEndian = false;
            var n = Math.Min(bytes.Length, 400) & ~1;
            if (n < 4) return false;

            int evenZeros = 0, oddZeros = 0;
            for (int i = 0; i < n; i += 2)
            {
                if (bytes[i] == 0) evenZeros++;
                if (bytes[i + 1] == 0) oddZeros++;
            }

            var pairs = n / 2;
            if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.1) return true;               // LE
            if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.1) { bigEndian = true; return true; }
            return false;
        }

        /// <summary>
        /// Gissar fältavgränsaren ur de första raderna.
        ///
        /// <para>⚠️ Mäter <b>stabiliteten</b> i kolumnantalet, inte hur många tecken som finns.
        /// En beskrivningstext innehåller ofta komma ("Swish, Kalle"), så ren räkning väljer
        /// komma i en semikolonfil. Rätt avgränsare ger samma antal fält på rad efter rad.</para>
        ///
        /// <para><b>⚠️⚠️ Mät mot det VANLIGASTE kolumnantalet, aldrig mot första raden.</b>
        /// Swedbanks export inleds med en informationsrad utan avgränsare
        /// (<c>* Transaktioner Period …</c>). Mätt mot den fick varje avgränsare en kolumn och
        /// vägrades, filen lästes som en enda kolumn och mappningen visade bara "— saknas —"
        /// (felrapport 2026-09-24).</para>
        /// </summary>
        public static char SniffDelimiter(string text)
        {
            var lines = SampleLines(text, 12);
            if (lines.Count == 0) return ';';

            char best = ';';
            int bestScore = -1;

            foreach (var d in Delimiters)
            {
                var counts = lines.Select(l => SplitLine(l, d).Length).ToList();

                // En avgränsare som inte delar något alls är inte en avgränsare. Vid lika många
                // rader vinner det större antalet — informationsraderna är de korta.
                var modal = counts.Where(c => c >= 2)
                    .GroupBy(c => c)
                    .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
                    .FirstOrDefault();
                if (modal == null) continue;

                var columns = modal.Key;
                var stable = modal.Count();

                // Fler kolumner bryter lika — en semikolonfil läst som komma ger färre.
                var score = stable * 100 + columns;
                if (score > bestScore) { bestScore = score; best = d; }
            }

            return best;
        }

        /// <summary>
        /// Delar en rad på avgränsaren och respekterar citattecken.
        /// <para>⚠️ Dubbla citattecken inuti ett citerat fält (<c>""</c>) är ett escapat
        /// citattecken, inte fältets slut — annars kapas beskrivningar mitt i.</para>
        /// </summary>
        public static string[] SplitLine(string line, char delimiter)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = !quoted;
                    continue;
                }

                if (c == delimiter && !quoted) { fields.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }

            fields.Add(sb.ToString().Trim());
            return fields.ToArray();
        }

        /// <summary>
        /// Läser ett belopp ur en cell. <c>false</c> när cellen inte är ett belopp <b>eller</b> är
        /// tvetydig.
        ///
        /// <para><b>⚠️⚠️ REGELN FÖR TUSENTALS KONTRA DECIMAL.</b> Finns både punkt och komma är
        /// den <b>sista</b> decimaltecknet. Finns bara en av dem avgör siffrorna efter den:
        /// exakt tre ⇒ tusentalsavgränsare (<c>1.234</c> = 1234), en eller två ⇒ decimal
        /// (<c>1,5</c> = 1,50). Det är säkert just för pengar, som alltid har högst två
        /// decimaler — och det är skillnaden mellan 1 234,50 kr och 1,23 kr.</para>
        ///
        /// <para>⚠️ Hårt blanksteg (U+00A0) är den vanligaste tusentalsavgränsaren i svenska
        /// exporter och syns inte vid en okulär kontroll av filen.</para>
        /// </summary>
        public static bool TryAmount(string? cell, out decimal amount)
        {
            amount = 0m;
            if (string.IsNullOrWhiteSpace(cell)) return false;

            var s = cell.Trim();

            // Ett inledande eller avslutande minus, plus valutabeteckningar och blanksteg bort.
            bool negative = s.StartsWith('-') || s.EndsWith('-');
            s = s.Trim('-', '+').Replace("kr", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("SEK", "", StringComparison.OrdinalIgnoreCase);

            s = new string(s.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != ' '
                                        && c != ' ').ToArray());

            if (s.Length == 0) return false;

            // Allt utom siffror och de två separatorerna gör cellen till något annat än ett belopp.
            if (s.Any(c => !char.IsDigit(c) && c != '.' && c != ',')) return false;

            int dots = s.Count(c => c == '.'), commas = s.Count(c => c == ',');
            string normalised;

            if (dots > 0 && commas > 0)
            {
                var decimalSep = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
                var thousandSep = decimalSep == '.' ? ',' : '.';
                normalised = s.Replace(thousandSep.ToString(), "").Replace(decimalSep, '.');
            }
            else if (dots + commas == 0)
            {
                normalised = s;
            }
            else
            {
                var sep = dots > 0 ? '.' : ',';

                // Flera av samma tecken kan bara vara tusentals: 1.234.567
                if (s.Count(c => c == sep) > 1) normalised = s.Replace(sep.ToString(), "");
                else
                {
                    var after = s.Length - s.IndexOf(sep) - 1;

                    // ⚠️ Exakt tre siffror efter = tusentals. Två eller färre = decimaler.
                    //    Fyra eller fler är varken och det är INTE ett belopp.
                    if (after == 3) normalised = s.Replace(sep.ToString(), "");
                    else if (after is 1 or 2) normalised = s.Replace(sep, '.');
                    else return false;
                }
            }

            if (!decimal.TryParse(normalised, NumberStyles.AllowDecimalPoint,
                                  CultureInfo.InvariantCulture, out amount)) return false;

            if (negative) amount = -amount;
            return true;
        }

        /// <summary>
        /// Läser ett datum ur en cell. Bara otvetydiga former.
        ///
        /// <para><b>⚠️⚠️ <c>03/04/2026</c> TOLKAS ALDRIG.</b> Den är 3 april eller 4 mars beroende
        /// på bank, och ett datum som hamnar i fel månad flyttar posten till fel period — vilket
        /// i en avstämning ser ut som en differens och i ett bokslut som en felperiodisering.
        /// Hellre en rad operatören får mappa själv än en tyst gissning.</para>
        /// </summary>
        public static bool TryDate(string? cell, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(cell)) return false;

            var s = cell.Trim().Replace('/', '-').Replace('.', '-');

            string[] formats = { "yyyy-MM-dd", "yyyy-M-d", "yyMMdd", "yyyyMMdd" };

            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out date)) return true;

            // Rena siffror utan avgränsare: 20260922 eller 260922.
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (digits.Length == 8 &&
                DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out date)) return true;

            return false;
        }

        /// <summary>
        /// Gissar vilken kolumn som är vad, utifrån rubrikraden.
        ///
        /// <para><b>⚠️ Ett FÖRSLAG, aldrig ett beslut.</b> Operatören ser och kan ändra varje
        /// val innan något importeras — bankerna döper kolumnerna olika och en tyst felmappning
        /// ger ett kontoutdrag som stämmer av mot fel siffror.</para>
        ///
        /// <para>Returnerar index eller -1. <c>AmountOut</c> är satt bara när banken delar
        /// beloppet i två kolumner (in och ut) i stället för ett tecken.</para>
        /// </summary>
        public static ColumnGuess GuessColumns(string[] header)
        {
            var g = new ColumnGuess();
            if (header == null) return g;

            for (int i = 0; i < header.Length; i++)
            {
                var h = (header[i] ?? "").Trim().ToLowerInvariant();
                if (h.Length == 0) continue;

                if (g.Date < 0 && (h.Contains("bokför") || h.Contains("bokfor")
                    || h.Contains("transaktionsdat") || h == "datum" || h.Contains("date")))
                    g.Date = i;

                else if (g.Text < 0 && (h.Contains("text") || h.Contains("beskriv")
                    || h.Contains("meddeland") || h.Contains("referens")
                    || h.Contains("motpart") || h.Contains("description")))
                    g.Text = i;

                else if (g.AmountOut < 0 && (h.Contains("uttag") || h.Contains("utbetal")))
                    g.AmountOut = i;

                else if (g.Amount < 0 && (h.Contains("belopp") || h.Contains("insätt")
                    || h.Contains("insatt") || h.Contains("amount")))
                    g.Amount = i;

                else if (g.Balance < 0 && (h.Contains("saldo") || h.Contains("balance")))
                    g.Balance = i;
            }

            return g;
        }

        /// <summary>Kolumnförslaget. -1 = hittades inte.</summary>
        public class ColumnGuess
        {
            public int Date { get; set; } = -1;
            public int Text { get; set; } = -1;
            public int Amount { get; set; } = -1;

            /// <summary>Satt bara när banken har skilda kolumner för in och ut.</summary>
            public int AmountOut { get; set; } = -1;

            public int Balance { get; set; } = -1;

            /// <summary>Går filen att läsa alls med det här förslaget?</summary>
            public bool IsUsable => Date >= 0 && (Amount >= 0 || AmountOut >= 0);
        }

        /// <summary>
        /// Rubrikraden är den första raden som INTE ser ut som en transaktion.
        ///
        /// <para>⚠️ Svenska bankers exporter inleds ofta med en eller flera informationsrader
        /// ("Kontonummer 1234-5678", en tom rad) före rubrikerna. Att blint ta rad 1 ger en
        /// mappning mot fel kolumner, och det syns först som konstiga belopp.</para>
        ///
        /// <para>Returnerar radindex, eller -1 när ingen rad duger.</para>
        /// </summary>
        public static int FindHeaderRow(IReadOnlyList<string[]> rows)
        {
            if (rows == null) return -1;

            for (int i = 0; i < Math.Min(rows.Count, 15); i++)
            {
                var r = rows[i];
                if (r.Length < 2) continue;

                // En rubrikrad bär inga belopp och inga datum — den bär ord.
                if (r.Any(c => TryDate(c, out _))) continue;

                var g = GuessColumns(r);
                if (g.IsUsable) return i;
            }

            return -1;
        }

        private static List<string> SampleLines(string text, int max)
            => (text ?? "").Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Trim().Length > 0)
                .Take(max)
                .ToList();
    }
}
