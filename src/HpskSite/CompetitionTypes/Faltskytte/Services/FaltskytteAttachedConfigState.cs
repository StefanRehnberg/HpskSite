using HpskSite.CompetitionTypes.Faltskytte.Models;
using Newtonsoft.Json.Linq;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Har den anslutna konfigurationen ändrats sedan den anslöts till tävlingen?
    ///
    /// ⚠️ En ansluten konfiguration är en KOPIA i tävlingens stationConfig, och den förs
    /// medvetet ALDRIG över automatiskt (Stefans beslut): konfigurationen kan delas av flera
    /// tävlingar, även genomförda med resultat, och bangranskningen jämför mot just kopian.
    /// Felet som rapporterades (Michael, tävling 5450, 2026-10-20) var att INGEN yta sa att
    /// kopian blivit inaktuell — Redigera tävling visade till och med konfigurationens
    /// stationsantal som om det vore tävlingens. Den här klassen avgör bara skillnaden; den
    /// ändrar ingenting.
    /// </summary>
    public static class FaltskytteAttachedConfigState
    {
        public const string AttachedKey = "_attachedConfigId";

        /// <summary>Konfigurationens id ur tävlingens kopia, eller null.</summary>
        public static int? AttachedId(string? competitionJson)
        {
            if (string.IsNullOrWhiteSpace(competitionJson)) return null;
            try { return JObject.Parse(competitionJson).Value<int?>(AttachedKey); }
            catch { return null; }
        }

        /// <summary>
        /// Skiljer sig tävlingens kopia från konfigurationen? Jämför innehållet, inte texten,
        /// och utan <c>_attachedConfigId</c> (som bara kopian bär). Går något av dem inte att
        /// läsa jämförs texten rakt av.
        /// </summary>
        public static bool Differs(string? competitionJson, string? configBlob)
        {
            var a = Strip(competitionJson);
            var b = Strip(configBlob);
            if (a != null && b != null) return !JToken.DeepEquals(a, b);
            return !string.Equals((competitionJson ?? "").Trim(), (configBlob ?? "").Trim(), StringComparison.Ordinal);
        }

        /// <summary>Antal stationer (utan särskjutningsstationer), samma räkning som Stationer-fliken.</summary>
        public static int StationCount(string? json)
        {
            var cfg = FaltskytteConfigParser.Parse(json);
            var first = cfg.WeaponConfigs.Values.FirstOrDefault();
            return first?.Stations.Where(s => !s.IsShootOffOnly).Select(s => s.Station).Distinct().Count() ?? 0;
        }

        private static JObject? Strip(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var o = JObject.Parse(json);
                o.Remove(AttachedKey);
                return o;
            }
            catch { return null; }
        }
    }
}
