using Newtonsoft.Json.Linq;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// EN kontroll av att en stationskonfiguration passar tävlingens typ (Magnum-omtaget 2026-11-22). Magnum ligger under EN nyckel,
    /// "M", för alla klasser M1–M9; vanlig fält under A, A_Opt, B, C, R. En Magnumfält-tävling vars konfiguration saknar M får
    /// annars antingen fel klass siffror eller ingen skjuttid alls på banan. Rena funktioner, ingen databas.
    /// Används av konfigurationsväljaren vid koppling till en tävling (<c>FaltskytteConfiguration/CompatibilityCheck</c>) och
    /// kan anropas av granskningen.
    /// </summary>
    public static class FaltskytteConfigCompatibility
    {
        public const string MagnumKey = "M";

        /// <summary>Vapengrupperna (nycklarna) i en konfigurationsblob, i ordning. Hanterar både platt form och <c>{ "WeaponConfigs": {…} }</c>.</summary>
        public static IReadOnlyList<string> WeaponGroupsOf(string? jsonBlob)
        {
            if (string.IsNullOrWhiteSpace(jsonBlob)) return Array.Empty<string>();
            try
            {
                if (JToken.Parse(jsonBlob) is not JObject o) return Array.Empty<string>();
                if (o["WeaponConfigs"] is JObject wrapped) o = wrapped;
                return o.Properties().Select(p => p.Name).Where(n => !n.StartsWith('_')).ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        public static bool IsMagnumCompetition(string? competitionType) =>
            string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase);

        public static bool IsFieldCompetition(string? competitionType) =>
            string.Equals(competitionType, "Faltskytte", StringComparison.OrdinalIgnoreCase) || IsMagnumCompetition(competitionType);

        /// <summary>Det som inte stämmer mellan konfigurationen och tävlingstypen, som meningar att visa för arrangören. Tom lista = det stämmer.</summary>
        public static List<string> Problems(string? jsonBlob, string? competitionType)
        {
            var problems = new List<string>();
            if (!IsFieldCompetition(competitionType)) return problems;
            var groups = WeaponGroupsOf(jsonBlob);
            var hasMagnum = groups.Contains(MagnumKey);
            var others = groups.Where(g => g != MagnumKey).ToList();
            if (IsMagnumCompetition(competitionType))
            {
                if (!hasMagnum)
                    problems.Add("Tävlingen är en Magnumfält-tävling, men konfigurationen saknar vapengrupp M (Magnum). Skjuttid och figurer för Magnumskyttarna kan då inte visas på banan. Öppna konfigurationen och lägg till M under \"Välj vapenklasser\".");
                if (others.Count > 0)
                    problems.Add("Konfigurationen innehåller också " + string.Join(", ", others) + ", som inte används i en Magnumfält-tävling.");
            }
            else if (hasMagnum)
            {
                problems.Add("Konfigurationen innehåller vapengrupp M (Magnum), som inte används i en vanlig fälttävling.");
            }
            if (groups.Count == 0)
                problems.Add("Konfigurationen har inga vapengrupper ännu.");
            return problems;
        }
    }
}
