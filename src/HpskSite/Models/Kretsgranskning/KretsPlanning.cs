using System.Globalization;
using NPoco;

namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// En rad i Förbundets stomprogram (fas 4): årets fasta datum — SM, landsdelsmästerskap,
    /// rikstävlingar. Sajtadmin för in dem en gång per år; kretskalendern visar dem som bakgrund och
    /// varnar när en tävling i samma gren ligger samma dag. Aldrig ett stopp.
    /// </summary>
    [TableName("StomprogramItem")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StomprogramItem
    {
        public int Id { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Gren (CompetitionTypes-id). Null = alla grener — krockar med allt.</summary>
        public string? Discipline { get; set; }
        public string? Note { get; set; }
        /// <summary>En period (t.ex. rikstävling på hemortens banor i sex veckor): visas, men ger inga krockvarningar.</summary>
        public bool IsPeriod { get; set; }
        public DateTime CreatedAt { get; set; }
        public int CreatedByMemberId { get; set; }
    }

    /// <summary>
    /// Inklistrade rader till stomprogrammet, som ren funktion så att formatet går att testa.
    /// En rad: <c>datum[–slutdatum]; namn; gren</c>, där grenen kan utelämnas (= alla grener).
    /// Semikolon eller tabb skiljer fälten, så att en rad kopierad ur ett kalkylblad fungerar.
    /// </summary>
    public static class StomprogramPaste
    {
        public record Row(DateTime Start, DateTime? End, string Name, string? Discipline);
        public record Result(List<Row> Rows, List<string> Errors);

        public static Result Parse(string? text, Func<string, string?> resolveDiscipline)
        {
            var rows = new List<Row>();
            var errors = new List<string>();
            var lineNo = 0;
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ';', '\t' }).Select(p => p.Trim()).ToArray();
                if (parts.Length < 2) { errors.Add($"Rad {lineNo}: ange datum och namn, skilda med semikolon."); continue; }
                if (!TryRange(parts[0], out var start, out var end)) { errors.Add($"Rad {lineNo}: datumet \"{parts[0]}\" går inte att läsa (skriv 2027-06-12 eller 2027-06-12–2027-06-13)."); continue; }
                var name = parts[1];
                if (name.Length == 0) { errors.Add($"Rad {lineNo}: namnet saknas."); continue; }
                string? disc = null;
                if (parts.Length > 2 && parts[2].Length > 0 && !IsAll(parts[2]))
                {
                    disc = resolveDiscipline(parts[2]);
                    if (disc == null) { errors.Add($"Rad {lineNo}: grenen \"{parts[2]}\" känns inte igen."); continue; }
                }
                rows.Add(new Row(start, end, name.Length > 200 ? name[..200] : name, disc));
            }
            return new Result(rows, errors);
        }

        private static bool IsAll(string s) => s.Equals("alla", StringComparison.OrdinalIgnoreCase)
            || s.Equals("alla grener", StringComparison.OrdinalIgnoreCase) || s == "*";

        /// <summary>"2027-06-12", "2027-06-12–2027-06-13", "2027-06-12 - 2027-06-13" eller "2027-06-12--13".</summary>
        public static bool TryRange(string s, out DateTime start, out DateTime? end)
        {
            end = null;
            s = s.Replace('–', '|').Replace('—', '|').Replace(" - ", "|").Replace("--", "|");
            var bits = s.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length == 0 || !TryDate(bits[0], out start)) { start = default; return false; }
            if (bits.Length == 1) return true;
            var second = bits[1];
            // "2027-06-12--13": bara dagen
            if (second.Length <= 2 && int.TryParse(second, out var day) && day >= 1 && day <= 31)
            {
                try { end = new DateTime(start.Year, start.Month, day); } catch { return false; }
            }
            else if (TryDate(second, out var e)) end = e;
            else return false;
            if (end < start) return false;
            if (end == start) end = null;
            return true;
        }

        private static bool TryDate(string s, out DateTime d) =>
            DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-M-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    /// <summary>
    /// En punkt i kretsens arrangörschecklista (fas 4). Kretsens regler — reportage, KM-medaljer,
    /// lagavgift till kretsens bankgiro — blir uppgifter i förberedelserna för varje tävling i
    /// kretsen, i stället för en sida ingen läser.
    ///
    /// <para>⚠️ Punkten KOPIERAS in i tävlingens förberedelser när arrangören väljer det (WorkItem med
    /// <c>ScopeType = "KretsChecklist"</c>, <c>ScopeKey = Id</c>). Ändrar kretsen punkten senare rörs
    /// inte redan inlagda uppgifter — de är arrangörens nu.</para>
    /// </summary>
    [TableName("RegionOrganiserChecklistItem")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RegionOrganiserChecklistItem
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        /// <summary>Dagar före tävlingen punkten ska vara klar (negativt = efter). Null = inget datum.</summary>
        public int? DaysBeforeComp { get; set; }
        /// <summary><see cref="OrganiserChecklistScope"/>.</summary>
        public string AppliesTo { get; set; } = OrganiserChecklistScope.All;
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public static class OrganiserChecklistScope
    {
        /// <summary>Alla tävlingar i kretsen som inte är klubbinterna.</summary>
        public const string All = "All";
        /// <summary>Tävlingar med tävlingsnivå kretstävling eller högre.</summary>
        public const string KretsOrAbove = "KretsOrAbove";
        /// <summary>Bara kretsmästerskap.</summary>
        public const string Kretsmasterskap = "Kretsmasterskap";

        public static readonly string[] Values = { All, KretsOrAbove, Kretsmasterskap };

        public static string Label(string? v) => v switch
        {
            KretsOrAbove => "Kretstävling eller högre",
            Kretsmasterskap => "Kretsmästerskap",
            _ => "Alla tävlingar i kretsen"
        };

        public static string Normalize(string? v) => Values.Contains(v) ? v! : All;

        /// <summary>Gäller punkten för tävlingen? Klubbinterna tävlingar får aldrig kretsens punkter.</summary>
        public static bool Applies(string? scope, bool isClubOnly, bool kretsOrAbove, bool isKretsmasterskap)
        {
            if (isClubOnly) return false;
            return Normalize(scope) switch
            {
                KretsOrAbove => kretsOrAbove,
                Kretsmasterskap => isKretsmasterskap,
                _ => true
            };
        }
    }

    /// <summary>
    /// Kretsens grannkretsar: standarden är <see cref="RegionAdjacency"/>, men kretsen kan välja
    /// själv. Lagras i <c>RegionCalendarSettings.NeighbourOverrides</c> som kommaseparerade
    /// regionkoder. <b>Null = standarden</b>, tom sträng = inga grannar.
    /// </summary>
    public static class RegionNeighbourSetting
    {
        public static List<string> Resolve(string regionCode, string? overrides)
        {
            if (overrides == null) return RegionAdjacency.NeighboursOf(regionCode).ToList();
            return overrides.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => !c.Equals(regionCode, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Det som ska lagras: null om valet är exakt standarden (så att en ändring av
        /// standarden senare slår igenom), annars koderna.</summary>
        public static string? ToStored(string regionCode, IEnumerable<string> chosen)
        {
            var set = chosen.Where(c => !string.IsNullOrWhiteSpace(c) && !c.Equals(regionCode, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            var def = RegionAdjacency.NeighboursOf(regionCode).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            if (set.SequenceEqual(def, StringComparer.OrdinalIgnoreCase)) return null;
            return string.Join(",", set);
        }
    }
}
