using System.Text;
using ClosedXML.Excel;

namespace HpskSite.Services
{
    /// <summary>
    /// Parses an uploaded Svenska Lag member export (.xlsx or .csv) into a header row
    /// plus a list of per-row dictionaries, and suggests a source-header → member-alias
    /// mapping from the known Svenska Lag column names (see Documentation/MEMBER_DATABASE.md §1).
    ///
    /// Plain helper — no DI. Instantiate in the controller or use the static entry points.
    /// </summary>
    public static class MemberImportParser
    {
        /// <summary>Result of parsing an uploaded file.</summary>
        public class ParseResult
        {
            public List<string> Headers { get; set; } = new();
            public List<Dictionary<string, string>> Rows { get; set; } = new();
        }

        /// <summary>
        /// Reads the uploaded stream into headers + rows. Picks the parser by file extension
        /// (.xlsx → ClosedXML, .csv → hand-rolled). Throws for unsupported extensions.
        /// </summary>
        public static ParseResult Parse(Stream stream, string fileName)
        {
            var ext = System.IO.Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".xlsx" => ParseXlsx(stream),
                ".csv" => ParseCsv(stream),
                _ => throw new NotSupportedException($"Filtypen '{ext}' stöds inte. Ladda upp .xlsx eller .csv.")
            };
        }

        // ---------------------------------------------------------------
        // XLSX (ClosedXML)
        // ---------------------------------------------------------------
        private static ParseResult ParseXlsx(Stream stream)
        {
            var result = new ParseResult();

            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheets.FirstOrDefault();
            if (worksheet == null)
            {
                return result;
            }

            // Work with absolute worksheet coordinates (IXLRow.Cell(int) is absolute), so a
            // used range that doesn't start at column A can't shift the data.
            var headerRow = worksheet.FirstRowUsed();
            var lastRow = worksheet.LastRowUsed();
            if (headerRow == null || lastRow == null)
            {
                return result;
            }

            int firstRowNumber = headerRow.RowNumber();
            int lastRowNumber = lastRow.RowNumber();

            // Row 1 (first used) = headers. Track the actual worksheet column numbers so blank
            // header cells don't shift subsequent data.
            var columns = new List<(int ColNumber, string Header)>();
            foreach (var cell in headerRow.CellsUsed())
            {
                var header = (cell.GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(header))
                {
                    continue;
                }
                columns.Add((cell.Address.ColumnNumber, header));
            }

            // De-duplicate header names (Svenska Lag can repeat e.g. "E-post 1")
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var resolvedColumns = new List<(int ColNumber, string Header)>();
            foreach (var (colNumber, header) in columns)
            {
                var name = header;
                if (seen.TryGetValue(header, out var count))
                {
                    seen[header] = count + 1;
                    name = $"{header} ({count + 1})";
                }
                else
                {
                    seen[header] = 1;
                }
                resolvedColumns.Add((colNumber, name));
                result.Headers.Add(name);
            }

            // Data rows (absolute worksheet row/column access)
            for (int rowNumber = firstRowNumber + 1; rowNumber <= lastRowNumber; rowNumber++)
            {
                var row = worksheet.Row(rowNumber);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool anyValue = false;
                foreach (var (colNumber, header) in resolvedColumns)
                {
                    var value = (row.Cell(colNumber).GetString() ?? "").Trim();
                    dict[header] = value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        anyValue = true;
                    }
                }
                if (anyValue)
                {
                    result.Rows.Add(dict);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------
        // CSV (hand-rolled — delimiter autodetect, quoted fields, doubled quotes)
        // ---------------------------------------------------------------
        private static ParseResult ParseCsv(Stream stream)
        {
            var result = new ParseResult();

            var text = ReadCsvText(stream);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            // Detect delimiter from the header line: ';' (common in Swedish exports) vs ','.
            char delimiter = DetectDelimiter(text);

            var records = ParseCsvRecords(text, delimiter);
            if (records.Count == 0)
            {
                return result;
            }

            // Header row
            var rawHeaders = records[0];
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headers = new List<string>();
            foreach (var raw in rawHeaders)
            {
                var header = (raw ?? "").Trim();
                if (string.IsNullOrEmpty(header))
                {
                    header = $"Kolumn {headers.Count + 1}";
                }
                if (seen.TryGetValue(header, out var count))
                {
                    seen[header] = count + 1;
                    header = $"{header} ({count + 1})";
                }
                else
                {
                    seen[header] = 1;
                }
                headers.Add(header);
            }
            result.Headers.AddRange(headers);

            // Data rows
            foreach (var record in records.Skip(1))
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool anyValue = false;
                for (int i = 0; i < headers.Count; i++)
                {
                    var value = i < record.Count ? (record[i] ?? "").Trim() : "";
                    dict[headers[i]] = value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        anyValue = true;
                    }
                }
                if (anyValue)
                {
                    result.Rows.Add(dict);
                }
            }

            return result;
        }

        /// <summary>
        /// Decodes CSV bytes to text, picking the encoding rather than assuming UTF-8.
        ///
        /// WHY THIS EXISTS: Swedish club exports (Svenska Lag, Excel "CSV (semikolon­avgränsad)")
        /// are routinely written as Windows-1252 with NO byte-order mark. Decoding those as UTF-8
        /// turns every å/ä/ö into U+FFFD — silently, because invalid bytes are replaced rather
        /// than thrown. The 2026-08-21 import destroyed 155 cells that way, including the header
        /// "Förnamn", and mojibake in a header also breaks mapping suggestion.
        ///
        /// Order: honour a BOM if present; otherwise try STRICT UTF-8 and fall back to Latin-1
        /// only when strict decoding fails. Latin-1 and Windows-1252 agree on every byte Swedish
        /// text uses (å=0xE5, ä=0xE4, ö=0xF6); they differ only in 0x80–0x9F (smart quotes, €),
        /// and Latin-1 is built into .NET so this needs no extra encoding provider.
        /// </summary>
        private static string ReadCsvText(Stream stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return "";
            }

            // A BOM is an explicit declaration — trust it and strip it.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // No BOM: strict UTF-8 throws on invalid bytes instead of inserting U+FFFD, which is
            // what lets us detect a single-byte file and retry rather than corrupt it.
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        private static char DetectDelimiter(string text)
        {
            // Look at the first line only.
            int newlineIdx = text.IndexOfAny(new[] { '\r', '\n' });
            var firstLine = newlineIdx >= 0 ? text.Substring(0, newlineIdx) : text;

            int semicolons = firstLine.Count(c => c == ';');
            int commas = firstLine.Count(c => c == ',');
            return semicolons >= commas ? ';' : ',';
        }

        /// <summary>
        /// Splits CSV text into records of fields. Handles quoted fields (with doubled
        /// quotes for a literal quote) and newlines inside quoted fields.
        /// </summary>
        private static List<List<string>> ParseCsvRecords(string text, char delimiter)
        {
            var records = new List<List<string>>();
            var current = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            int i = 0;
            int len = text.Length;
            while (i < len)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < len && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        inQuotes = false;
                        i++;
                        continue;
                    }
                    field.Append(c);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                    continue;
                }

                if (c == delimiter)
                {
                    current.Add(field.ToString());
                    field.Clear();
                    i++;
                    continue;
                }

                if (c == '\r')
                {
                    // Swallow a following \n (CRLF)
                    if (i + 1 < len && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    current.Add(field.ToString());
                    field.Clear();
                    records.Add(current);
                    current = new List<string>();
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    current.Add(field.ToString());
                    field.Clear();
                    records.Add(current);
                    current = new List<string>();
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
            }

            // Trailing field / record
            if (field.Length > 0 || current.Count > 0)
            {
                current.Add(field.ToString());
                records.Add(current);
            }

            return records;
        }

        // ---------------------------------------------------------------
        // Mapping suggestions
        // ---------------------------------------------------------------

        /// <summary>
        /// Known Svenska Lag header → pistol.nu member alias (see MEMBER_DATABASE.md §1).
        /// Keys are matched case-insensitively after trimming.
        /// </summary>
        private static readonly Dictionary<string, string> KnownMappings =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Förnamn"] = "firstName",
                ["Efternamn"] = "lastName",
                ["Födelsedatum"] = "birthDate",
                ["Personnummer"] = "personNumber",
                ["E-post"] = "email",
                ["E-post 1"] = "email",
                ["Mobil"] = "phoneNumber",
                ["Telefon"] = "landlinePhone",
                ["C/O"] = "coAddress",
                ["Adress"] = "address",
                ["Postnr"] = "postalCode",
                ["Ort"] = "city",
                ["Kön"] = "gender",
                ["Målsman 1 - Namn"] = "guardian1Name",
                ["Målsman 1 - Mobil"] = "guardian1Mobile",
                ["Målsman 1 - E-post 1"] = "guardian1Email",
                ["Målsman 1 - E-post"] = "guardian1Email",
                ["Målsman 2 - Namn"] = "guardian2Name",
                ["Målsman 2 - Mobil"] = "guardian2Mobile",
                ["Målsman 2 - E-post 1"] = "guardian2Email",
                ["Målsman 2 - E-post"] = "guardian2Email",
                ["Godkänd kontroll i belastningsregistret"] = "backgroundCheckDate",
                ["Medlem sedan"] = "memberSince",
                ["Pistolkort#"] = "shooterIdNumber",
                ["Finns i MAP"] = "registeredInMap",
                ["Aktiv i förbund"] = "federations",
                ["Medlemsavgift betald"] = "memberNotes",
                ["Nyckel"] = "nyckel",
                ["Skjutledare"] = "skjutledare",
                ["Anteckningar"] = "memberNotes",
                ["Guldmärke"] = "guldmarkeNumber",
                // Informational / skipped by default
                ["Huvudmedlemsskap"] = "",
                ["WAID"] = "",
                ["IPSC"] = ""
            };

        /// <summary>
        /// The alias + human label pairs offered in the mapping dropdowns.
        /// The empty alias "" is the "do not import" option.
        /// </summary>
        public static List<(string Alias, string Label)> TargetFields => new()
        {
            ("", "(Importera inte)"),
            ("firstName", "Förnamn"),
            ("lastName", "Efternamn"),
            ("email", "E-post"),
            ("phoneNumber", "Mobil"),
            ("landlinePhone", "Telefon (fast)"),
            ("personNumber", "Personnummer"),
            ("birthDate", "Födelsedatum"),
            ("gender", "Kön"),
            ("address", "Adress"),
            ("coAddress", "C/O"),
            ("postalCode", "Postnummer"),
            ("city", "Ort"),
            ("shooterIdNumber", "Pistolkort#"),
            ("memberSince", "Medlem sedan"),
            ("membershipType", "Medlemstyp"),
            ("membershipStatus", "Medlemsstatus"),
            ("backgroundCheckApproved", "Belastningskontroll godkänd"),
            ("backgroundCheckDate", "Belastningskontroll datum"),
            ("registeredInMap", "Finns i MAP"),
            ("federations", "Aktiv i förbund"),
            ("guardian1Name", "Målsman 1 – Namn"),
            ("guardian1Mobile", "Målsman 1 – Mobil"),
            ("guardian1Email", "Målsman 1 – E-post"),
            ("guardian2Name", "Målsman 2 – Namn"),
            ("guardian2Mobile", "Målsman 2 – Mobil"),
            ("guardian2Email", "Målsman 2 – E-post"),
            ("emergencyContactName", "Närmast anhörig – Namn"),
            ("emergencyContactPhone", "Närmast anhörig – Telefon"),
            ("memberNotes", "Anteckningar"),
            // Club-specific actions that map to OTHER tables/systems, not member/ClubMembership fields.
            ("guldmarkeNumber", "Guldmärkesnr"),
            ("guldmarkeAwarded", "Guldmärke tilldelad (år/datum)"),
            ("nyckel", "Nyckel/bricka → skapar nyckelpost"),
            ("skjutledare", "Skjutledare → roll")
        };

        /// <summary>
        /// Suggests a header → alias mapping using the known Svenska Lag column names.
        /// Unknown headers map to "" (do not import).
        /// </summary>
        public static Dictionary<string, string> SuggestMapping(List<string> headers)
        {
            var mapping = new Dictionary<string, string>();
            if (headers == null)
            {
                return mapping;
            }

            foreach (var header in headers)
            {
                var trimmed = (header ?? "").Trim();
                // Strip any de-dup suffix like " (2)" before matching.
                var lookupKey = StripDedupSuffix(trimmed);
                mapping[header] = KnownMappings.TryGetValue(lookupKey, out var alias) ? alias : "";
            }

            return mapping;
        }

        private static string StripDedupSuffix(string header)
        {
            // Remove a trailing " (n)" that ParseXlsx/ParseCsv may have appended.
            var match = System.Text.RegularExpressions.Regex.Match(header, @"^(.*?)\s+\(\d+\)$");
            return match.Success ? match.Groups[1].Value : header;
        }
    }
}
