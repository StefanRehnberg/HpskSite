using System.Globalization;
using System.Text;

namespace HpskSite.Models.Ledger
{
    /// <summary>En inläst SIE-fil — bara det importen behöver.</summary>
    public sealed class SieFile
    {
        public string? Program { get; set; }
        public string? CompanyName { get; set; }
        public string? OrgNumber { get; set; }
        public string? SieType { get; set; }
        public string EncodingName { get; set; } = "";

        /// <summary><c>#RAR 0</c> — det räkenskapsår filen gäller.</summary>
        public DateTime? YearStart { get; set; }
        public DateTime? YearEnd { get; set; }

        /// <summary><c>#KONTO</c>: nummer → namn.</summary>
        public Dictionary<int, string> Accounts { get; } = new();

        /// <summary><c>#IB 0</c>: ingående balans per konto för filens år.</summary>
        public Dictionary<int, decimal> OpeningBalances { get; } = new();

        public List<SieVoucher> Vouchers { get; } = new();

        /// <summary>Fel som gör filen oläslig på en viss rad. Tom = läsbar.</summary>
        public List<string> Errors { get; } = new();

        /// <summary>Sådant vi läste men inte tar in (#OBJEKT, #RES, #BTRANS …).</summary>
        public List<string> Notes { get; } = new();
    }

    public sealed class SieVoucher
    {
        public string Series { get; set; } = "";
        public int Number { get; set; }
        public DateTime Date { get; set; }
        public string Text { get; set; } = "";
        public int Line { get; set; }
        public List<SieTransaction> Transactions { get; } = new();

        /// <summary>Debet minus kredit — noll för en balanserad verifikation.</summary>
        public decimal Imbalance => Transactions.Sum(t => t.Amount);

        /// <summary>Summan av debetsidan — det verifikationen "är på".</summary>
        public decimal Total => Transactions.Where(t => t.Amount > 0).Sum(t => t.Amount);
    }

    public sealed class SieTransaction
    {
        public int Account { get; set; }

        /// <summary>Positivt = debet, negativt = kredit (SIE:s tecken).</summary>
        public decimal Amount { get; set; }

        public string? Text { get; set; }
    }

    /// <summary>
    /// Läser SIE typ 4 (och det ur typ 1–3 som finns i den). <b>Ren funktion</b> — ingen databas.
    ///
    /// <para><b>⚠️ Kodsidan.</b> Standarden föreskriver CP437 (<c>#FORMAT PC8</c>), men flera program
    /// skriver Windows-1252 och sätter ändå PC8. Vi väljer den kodning under vilken filen innehåller
    /// flest svenska bokstäver — en felgissning blir annars "F”reningskonto" i kontoplanen, och det
    /// syns först när kassören läser namnen.</para>
    ///
    /// <para><b>⚠️ #RTRANS och #BTRANS läses inte som rader.</b> SIE 4B skriver en tillagd rad som
    /// #RTRANS FÖLJD av en identisk #TRANS (för äldre läsare), och en borttagen som #BTRANS. Tar vi in
    /// #RTRANS dubbleras raden; tar vi in #BTRANS bokförs något som togs bort.</para>
    /// </summary>
    public static class SieParser
    {
        public static SieFile Parse(byte[] bytes)
        {
            var (text, encodingName) = Decode(bytes);
            var file = new SieFile { EncodingName = encodingName };

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            SieVoucher? current = null;
            var inBlock = false;
            var skipped = new Dictionary<string, int>();

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                var raw = lines[i].Trim();
                if (raw.Length == 0) continue;

                if (raw == "{")
                {
                    if (current == null) { file.Errors.Add($"Rad {lineNo}: '{{' utan #VER före."); continue; }
                    inBlock = true;
                    continue;
                }
                if (raw == "}")
                {
                    if (current != null) file.Vouchers.Add(current);
                    current = null;
                    inBlock = false;
                    continue;
                }
                if (!raw.StartsWith('#')) continue;

                List<string> f;
                try { f = Tokenize(raw); }
                catch (FormatException ex) { file.Errors.Add($"Rad {lineNo}: {ex.Message}"); continue; }
                if (f.Count == 0) continue;
                var label = f[0].ToUpperInvariant();

                if (inBlock)
                {
                    if (label == "#TRANS")
                    {
                        // #TRANS kontonr {objektlista} belopp [transdat] [transtext] [kvantitet] [sign]
                        if (f.Count < 4 || !int.TryParse(f[1], out var acct) || !TryAmount(f[3], out var amt))
                        {
                            file.Errors.Add($"Rad {lineNo}: #TRANS kunde inte läsas.");
                            continue;
                        }
                        current!.Transactions.Add(new SieTransaction
                        {
                            Account = acct,
                            Amount = amt,
                            Text = f.Count > 5 && !string.IsNullOrWhiteSpace(f[5]) ? f[5] : null
                        });
                    }
                    else Count(skipped, label);
                    continue;
                }

                switch (label)
                {
                    case "#PROGRAM": file.Program = f.Count > 1 ? f[1] : null; break;
                    case "#FNAMN": file.CompanyName = f.Count > 1 ? f[1] : null; break;
                    case "#ORGNR": file.OrgNumber = f.Count > 1 ? f[1] : null; break;
                    case "#SIETYP": file.SieType = f.Count > 1 ? f[1] : null; break;
                    case "#RAR":
                        if (f.Count >= 4 && f[1] == "0" && TryDate(f[2], out var s) && TryDate(f[3], out var e))
                        {
                            file.YearStart = s;
                            file.YearEnd = e;
                        }
                        break;
                    case "#KONTO":
                        if (f.Count >= 2 && int.TryParse(f[1], out var num))
                            file.Accounts[num] = f.Count > 2 ? f[2] : "";
                        break;
                    case "#IB":
                        // #IB årsnr konto saldo [kvantitet] — bara innevarande år (0).
                        if (f.Count >= 4 && f[1] == "0" && int.TryParse(f[2], out var ibAcct) && TryAmount(f[3], out var ib))
                            file.OpeningBalances[ibAcct] = file.OpeningBalances.GetValueOrDefault(ibAcct) + ib;
                        break;
                    case "#VER":
                        // #VER serie vernr verdatum [vertext] [regdatum] [sign]
                        if (f.Count < 4 || !int.TryParse(f[2], out var vnr) || !TryDate(f[3], out var vdate))
                        {
                            file.Errors.Add($"Rad {lineNo}: #VER kunde inte läsas (serie, nummer och datum krävs).");
                            current = null;
                            continue;
                        }
                        current = new SieVoucher
                        {
                            Series = f[1],
                            Number = vnr,
                            Date = vdate,
                            Text = f.Count > 4 ? f[4] : "",
                            Line = lineNo
                        };
                        break;
                    default:
                        Count(skipped, label);
                        break;
                }
            }

            if (current != null || inBlock) file.Errors.Add("Filen slutar mitt i en verifikation (ett '}' saknas).");

            foreach (var (label, n) in skipped.Where(kv => kv.Key is "#RTRANS" or "#BTRANS" or "#OBJEKT" or "#DIM"
                                                          or "#RES" or "#UB" or "#PSALDO" or "#PBUDGET"))
                file.Notes.Add($"{label} ({n} st) läses inte in.");

            return file;
        }

        private static void Count(Dictionary<string, int> d, string label) => d[label] = d.GetValueOrDefault(label) + 1;

        /// <summary>
        /// Delar en rad i fält: blanksteg skiljer, "citat" håller ihop (\" och \\ är escapade),
        /// och {objektlista} blir ETT fält.
        /// </summary>
        internal static List<string> Tokenize(string line)
        {
            var result = new List<string>();
            var i = 0;
            while (i < line.Length)
            {
                var c = line[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    var closed = false;
                    while (i < line.Length)
                    {
                        var ch = line[i];
                        if (ch == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                        {
                            sb.Append(line[i + 1]); i += 2; continue;
                        }
                        if (ch == '"') { closed = true; i++; break; }
                        sb.Append(ch); i++;
                    }
                    if (!closed) throw new FormatException("ett citattecken stängs aldrig.");
                    result.Add(sb.ToString());
                    continue;
                }
                if (c == '{')
                {
                    var start = i;
                    var depth = 0;
                    var inQuote = false;
                    while (i < line.Length)
                    {
                        var ch = line[i];
                        if (ch == '"') inQuote = !inQuote;
                        else if (!inQuote && ch == '{') depth++;
                        else if (!inQuote && ch == '}') { depth--; if (depth == 0) { i++; break; } }
                        i++;
                    }
                    result.Add(line[start..i]);
                    continue;
                }
                var st = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                result.Add(line[st..i]);
            }
            return result;
        }

        internal static bool TryAmount(string s, out decimal value)
            => decimal.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value);

        internal static bool TryDate(string s, out DateTime value)
            => DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>
        /// Väljer kodning. UTF-8 med BOM, eller giltig UTF-8 med flerbytestecken, vinner. Annars
        /// CP437 eller Windows-1252 — den som ger flest svenska bokstäver.
        /// </summary>
        internal static (string Text, string Name) Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");

            var hasHigh = bytes.Any(b => b >= 0x80);
            if (!hasHigh) return (Encoding.ASCII.GetString(bytes), "ASCII");

            try
            {
                var strict = new UTF8Encoding(false, true);
                return (strict.GetString(bytes), "UTF-8");
            }
            catch (DecoderFallbackException) { }

            var cp437 = Encoding.GetEncoding(437).GetString(bytes);
            var cp1252 = Encoding.GetEncoding(1252).GetString(bytes);
            return SwedishScore(cp1252) > SwedishScore(cp437) ? (cp1252, "Windows-1252") : (cp437, "CP437");
        }

        private static int SwedishScore(string s) => s.Count(c => "åäöÅÄÖé".IndexOf(c) >= 0);
    }
}
