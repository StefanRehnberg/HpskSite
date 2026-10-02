using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// Grenen på en stomprogramrad. Utöver tävlingstypernas id finns två lägen till:
    /// null = alla grener (krockar med allt, t.ex. Förbundsmötet) och <see cref="Other"/> = en gren
    /// vi inte har (PPC). En sådan rad visas i kalendern men krockar aldrig — ingen tävling här bär den.
    /// </summary>
    public static class StomprogramDisciplines
    {
        public const string Other = "Annan";

        public static string Label(string? discipline) =>
            string.IsNullOrEmpty(discipline) ? "Alla grener"
            : discipline == Other ? "Annan gren"
            : ActivityDiscipline.Label(discipline);

        /// <summary>Är värdet något en rad får bära? Tomt, <see cref="Other"/> eller en tävlingstyps id.</summary>
        public static bool IsValid(string? discipline) =>
            string.IsNullOrEmpty(discipline) || discipline == Other || CompetitionTypes.GetById(discipline) != null;
    }

    /// <summary>
    /// Läser Förbundets stomprogramsida (pistolskytteforbundet.se/stomprogram/…) som UNDERLAG.
    /// Ren funktion över HTML:en, så att tolkningen går att testa utan nätet. Ingenting sparas här —
    /// sajtadmin ser raderna, väljer och rättar.
    ///
    /// Sidans tabell har kolumnerna <c># · Dag(ar) · Månad · Tävling · Plats</c>, och datumen är
    /// skrivna för människor: "1-2 maj", "29-1 juli" (29 juni–1 juli), "24-6 april-juni",
    /// "28-19 augusti-september", och ibland bara en månad. Grenen står i namnet, ibland flera:
    /// "SM Fältskjutning, Precision och Militär snabbmatch" blir en rad per gren.
    /// </summary>
    public static class StomprogramHtmlImport
    {
        public record Row(
            int Number, DateTime? Start, DateTime? End, string Name, string? Discipline,
            string? Note, bool IsPeriod, string? Problem, string Source);

        public record Result(int? Year, List<Row> Rows, List<string> Warnings);

        private static readonly string[] Months =
            { "januari", "februari", "mars", "april", "maj", "juni", "juli", "augusti", "september", "oktober", "november", "december" };

        public static Result Parse(string? html, int? fallbackYear = null)
        {
            var warnings = new List<string>();
            var rows = new List<Row>();
            html ??= "";

            // Året ur rubriken. ⚠️ Adressen kan säga ett annat år än sidan (".../stomprogram-2025/" visar
            // "Stomprogram 2027") — rubriken är det sidan påstår om sig själv.
            int? year = null;
            var h1 = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (h1.Success)
            {
                var y = Regex.Match(Text(h1.Groups[1].Value), @"\b(20\d\d)\b");
                if (y.Success) year = int.Parse(y.Groups[1].Value);
            }
            if (year == null)
            {
                year = fallbackYear;
                warnings.Add(year == null
                    ? "Sidan säger inte vilket år programmet gäller. Ange året och läs in igen."
                    : $"Sidan säger inte vilket år programmet gäller — {year} är antaget.");
            }

            var table = FindTable(html);
            if (table == null)
            {
                warnings.Add("Sidan har ingen tabell som ser ut som ett stomprogram.");
                return new Result(year, rows, warnings);
            }

            var trs = Regex.Matches(table, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(m => Regex.Matches(m.Groups[1].Value, @"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                    .Select(c => Text(c.Groups[1].Value)).ToList())
                .Where(c => c.Count > 0)
                .ToList();
            if (trs.Count == 0) return new Result(year, rows, warnings);

            // Kolumnerna ur rubrikraden; om den saknas antas sidans nuvarande ordning.
            int cDays = 1, cMonth = 2, cName = 3, cPlace = 4, first = 0;
            var head = trs[0];
            if (head.Any(h => h.Contains("Tävling", StringComparison.OrdinalIgnoreCase)))
            {
                first = 1;
                cDays = head.FindIndex(h => h.StartsWith("Dag", StringComparison.OrdinalIgnoreCase));
                cMonth = head.FindIndex(h => h.StartsWith("Månad", StringComparison.OrdinalIgnoreCase));
                cName = head.FindIndex(h => h.Contains("Tävling", StringComparison.OrdinalIgnoreCase));
                cPlace = head.FindIndex(h => h.StartsWith("Plats", StringComparison.OrdinalIgnoreCase));
                if (cDays < 0 || cMonth < 0 || cName < 0)
                {
                    warnings.Add("Tabellens kolumner känns inte igen (Dag, Månad, Tävling).");
                    return new Result(year, rows, warnings);
                }
            }

            string Cell(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";

            // Fotnoter ("*Som orientering.") — en rad med bara ett namn som börjar med asterisk.
            var data = trs.Skip(first).ToList();
            string? footnote = null;
            foreach (var r in data)
            {
                var n = Cell(r, cName);
                if (n.StartsWith("*") && Cell(r, cDays).Length == 0 && Cell(r, cMonth).Length == 0)
                    footnote = n.TrimStart('*').Trim();
            }

            var number = 0;
            foreach (var r in data)
            {
                var name = Cell(r, cName);
                var days = Cell(r, cDays);
                var month = Cell(r, cMonth);
                if (name.Length == 0) continue;
                if (name.StartsWith("*") && days.Length == 0 && month.Length == 0) continue;
                number++;

                var source = string.Join(" · ", new[] { days, month, name, Cell(r, cPlace) }.Where(s => s.Length > 0));
                var place = Cell(r, cPlace);
                if (place == "?" ) place = "";
                string? note = place.Length > 0 ? place : null;
                if (name.EndsWith("*"))
                {
                    name = name.TrimEnd('*').Trim();
                    if (!string.IsNullOrEmpty(footnote)) note = string.IsNullOrEmpty(note) ? footnote : $"{note} — {footnote}";
                }

                var isPeriod = name.Contains("hemort", StringComparison.OrdinalIgnoreCase)
                    || place.Contains("hemmaban", StringComparison.OrdinalIgnoreCase);

                DateTime? start = null, end = null;
                string? problem = null;
                if (year == null) problem = "Året är okänt.";
                else if (!TryDates(days, month, year.Value, out start, out end, out problem)) { start = null; end = null; }

                var disciplines = Disciplines(name);
                foreach (var d in disciplines)
                    rows.Add(new Row(number, start, end, name.Length > 200 ? name[..200] : name, d, note, isPeriod, problem, source));
            }

            return new Result(year, rows, warnings);
        }

        /// <summary>
        /// Datumen ur sidans två kolumner. "29-1" + "juli" betyder 29 juni–1 juli: när slutdagen är
        /// mindre än startdagen och bara en månad står, är det SLUTETS månad.
        /// </summary>
        public static bool TryDates(string days, string month, int year, out DateTime? start, out DateTime? end, out string? problem)
        {
            start = null; end = null; problem = null;
            var monthParts = Split(month);
            if (monthParts.Count == 0) { problem = "Datum saknas på Förbundets sida."; return false; }
            var m = monthParts.Select(MonthNumber).ToList();
            if (m.Any(x => x == 0)) { problem = $"Månaden \"{month}\" går inte att läsa."; return false; }

            var dayParts = Split(days);
            if (dayParts.Count == 0) { problem = $"Förbundets sida anger bara månaden ({month}) — inget datum."; return false; }
            var d = new List<int>();
            foreach (var p in dayParts)
            {
                if (!int.TryParse(p, out var n) || n < 1 || n > 31) { problem = $"Dagen \"{days}\" går inte att läsa."; return false; }
                d.Add(n);
            }

            int sMonth, eMonth, sYear = year, eYear = year, sDay = d[0], eDay = d.Count > 1 ? d[1] : d[0];
            if (m.Count > 1)
            {
                sMonth = m[0]; eMonth = m[^1];
                if (eMonth < sMonth) eYear = year + 1;          // "december-januari": slutet i nästa år
            }
            else if (d.Count > 1 && eDay < sDay)
            {
                eMonth = m[0]; sMonth = m[0] == 1 ? 12 : m[0] - 1;
                if (m[0] == 1) sYear = year - 1;                 // "29-1 januari" börjar i december året före
            }
            else { sMonth = eMonth = m[0]; }

            try
            {
                start = new DateTime(sYear, sMonth, sDay);
                end = new DateTime(eYear, eMonth, eDay);
            }
            catch (ArgumentOutOfRangeException)
            {
                problem = $"Datumet \"{days} {month}\" finns inte.";
                start = end = null;
                return false;
            }
            if (end < start) { problem = $"Datumet \"{days} {month}\" slutar före det börjar."; start = end = null; return false; }
            if (end == start) end = null;
            return true;
        }

        /// <summary>
        /// Grenarna i ett tävlingsnamn — en rad per gren. ⚠️ Ordningen är bärande: "magnumprecision"
        /// innehåller "precision" och "magnumfältskjutning" innehåller "fältskjutning", så de långa
        /// orden plockas ut först. Ingen gren i namnet = alla grener (null); en gren vi inte har (PPC)
        /// = <see cref="StomprogramDisciplines.Other"/>.
        /// </summary>
        public static List<string?> Disciplines(string name)
        {
            var s = " " + name.ToLowerInvariant() + " ";
            var found = new List<string?>();
            void Take(string pattern, string id)
            {
                if (!Regex.IsMatch(s, pattern)) return;
                s = Regex.Replace(s, pattern, " ");
                if (!found.Contains(id)) found.Add(id);
            }
            Take(@"magnum\s*precision", "MagnumPrecision");
            Take(@"magnum\s*f(ä|a)l?t\w*", "MagnumFalt");
            Take(@"f(ä|a)l?t(skjutning|skytte)\w*", "Faltskytte");
            Take(@"milit(ä|a)r\s+snabbmatch|milsnabb", "Milsnabb");
            Take(@"spring(skytte|skjutning)\w*", "Springskytte");
            Take(@"helmatch", "NationellHelmatch");
            Take(@"\bduell\w*", "Duell");
            Take(@"standardpistol", "Standardpistol");
            Take(@"sportpistol", "Sportpistol");
            Take(@"precision", "Precision");
            if (Regex.IsMatch(s, @"\bppc\b|luftpistol|silhuett")) found.Add(StomprogramDisciplines.Other);
            if (found.Count == 0) found.Add(null);
            // Bara grener som finns som tävlingstyp får lämna funktionen med ett id.
            return found.Select(f => f == null || f == StomprogramDisciplines.Other || CompetitionTypes.GetById(f) != null ? f : StomprogramDisciplines.Other)
                .Distinct().ToList();
        }

        private static List<string> Split(string s) =>
            s.Replace('–', '-').Replace('—', '-').Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        private static int MonthNumber(string s)
        {
            var w = s.Trim().TrimEnd('.').ToLowerInvariant();
            if (w.Length < 3) return 0;
            for (var i = 0; i < Months.Length; i++)
                if (Months[i].StartsWith(w, StringComparison.Ordinal)) return i + 1;
            return 0;
        }

        private static string? FindTable(string html)
        {
            foreach (Match t in Regex.Matches(html, @"<table[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var inner = t.Groups[1].Value;
                if (Text(inner).Contains("Tävling", StringComparison.OrdinalIgnoreCase)) return inner;
            }
            return null;
        }

        private static string Text(string fragment)
        {
            var noTags = Regex.Replace(fragment, @"<[^>]+>", " ");
            var decoded = WebUtility.HtmlDecode(noTags).Replace(' ', ' ');
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }
    }
}
