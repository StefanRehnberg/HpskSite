using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Omgång 3 (Expertgruppen 3.3): en fältkonfiguration för ETT områdes stationer. Konfiguratorn numrerar sina stationer
    /// 1..N och det numret är stationens identitet där (resultat, QR, särskjutning) — därför numreras inget om. I stället bär
    /// konfigurationen <c>_courseStations</c>: konfigurationens station 1 är banans station <c>_courseStations[0]</c>.
    /// </summary>
    public static class AreaConfigBlob
    {
        public const string CourseStationsKey = "_courseStations";

        /// <summary>
        /// Bygger delkonfigurationen. Med en källa (tävlingens anslutna konfiguration) kopieras områdets stationer per
        /// vapenklass och numreras 1..N i banans ordning; meta-nycklarna för läge, mörker och tävlingstyp följer med, men
        /// aldrig <c>_attachedConfigId</c> (delkonfigurationen är inte ansluten till tävlingen). Utan källa: bara mappningen.
        /// </summary>
        public static string Build(string? sourceBlob, IReadOnlyList<int> courseStations)
        {
            var result = new JObject();
            JObject? src = null;
            if (!string.IsNullOrWhiteSpace(sourceBlob))
            {
                try { src = JObject.Parse(sourceBlob); } catch { src = null; }
            }
            if (src != null)
            {
                foreach (var key in new[] { "_mode", "_morker", "_scoringMode" })
                    if (src[key] != null) result[key] = src[key]!.DeepClone();
                foreach (var prop in src.Properties())
                {
                    if (prop.Name.StartsWith("_") || prop.Value is not JObject wc) continue;
                    var stations = (wc["stations"] ?? wc["Stations"]) as JArray;
                    if (stations == null) continue;
                    var picked = new JArray();
                    for (var i = 0; i < courseStations.Count; i++)
                    {
                        var hit = stations.OfType<JObject>().FirstOrDefault(s => (s.Value<int?>("station") ?? s.Value<int?>("Station")) == courseStations[i]);
                        if (hit == null) continue;
                        var copy = (JObject)hit.DeepClone();
                        copy.Remove("Station");
                        copy["station"] = i + 1;
                        foreach (var k in copy.Properties().Where(p => p.Name.StartsWith("_linkedFrom")).Select(p => p.Name).ToList()) copy.Remove(k);
                        picked.Add(copy);
                    }
                    if (picked.Count > 0) result[prop.Name] = new JObject { ["stations"] = picked };
                }
            }
            result[CourseStationsKey] = new JArray(courseStations.Cast<object>().ToArray());
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Banans stationsnummer ur en konfiguration, eller tom lista.</summary>
        public static List<int> CourseStations(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) return new();
            try { return (JObject.Parse(blob)[CourseStationsKey] as JArray)?.Select(t => t.Value<int>()).Where(n => n > 0).ToList() ?? new(); }
            catch { return new(); }
        }
    }
}
