using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HpskSite.Models.Planning
{
    /// <summary>En del av banan: ett områdes delkonfiguration (banans nummer i <c>_courseStations</c>).</summary>
    public sealed record CoursePart(int ConfigId, string Name, string? Blob);

    /// <summary>Vad <see cref="CourseAssembly.Assemble"/> gav: banans nya innehåll och det som behöver sägas.</summary>
    public sealed record CourseAssemblyResult(string Blob, List<string> Warnings, List<int> Missing, List<int> FromCourse, int StationCount);

    /// <summary>En del som banan sattes ihop av (ur banans <c>_assembledFrom</c>).</summary>
    public sealed record AssembledPart(int ConfigId, string Name, string Checksum, List<int> Stations);

    /// <summary>
    /// Banan i planeringen (Stefan 2026-10-24): varje förening kan bygga sina stationer i en egen delkonfiguration, och
    /// "Sätt ihop banan" lägger ihop delarna till banans konfiguration. Rena funktioner — ingen databas.
    ///
    /// <para><b>Regeln:</b> per vapengrupp tas varje stations innehåll ur den del som har stationen, omnumrerat till banans
    /// nummer. En station som ingen del har behålls ur banans egen konfiguration — en planering utan områden bygger allt där,
    /// och en station som ännu inte fördelats försvinner inte när banan sätts ihop.</para>
    ///
    /// <para><b>⚠️ Kontrollsumman är INNEHÅLLETS</b> (<see cref="Checksum"/>), inte ändringstiden: ett godkännande ändrar
    /// konfigurationens ändringstid men inte stationerna, och då får delen inte se ändrad ut.</para>
    /// </summary>
    public static class CourseAssembly
    {
        public const string AssembledFromKey = "_assembledFrom";
        public const string AssembledAtKey = "_assembledAt";
        public const string StationCountKey = "_stationCount";

        /// <summary>Banans meta-nycklar som en ihopläggning behåller från banan (aldrig från en del).</summary>
        private static readonly string[] CourseMeta = { "_mode", "_morker", "_scoringMode", "_linkedGroups", "_attachedConfigId" };

        public static CourseAssemblyResult Assemble(string? courseBlob, IReadOnlyList<CoursePart> parts, int stationCount, DateTime now)
        {
            var course = Parse(courseBlob) ?? new JObject();
            var warnings = new List<string>();
            var result = new JObject();
            foreach (var k in CourseMeta)
                if (course[k] != null) result[k] = course[k]!.DeepClone();

            // Delarnas stationer: banans nummer → (del, delens station-index).
            var covered = new Dictionary<int, CoursePart>();
            var parsed = new List<(CoursePart Part, JObject Blob, List<int> Stations)>();
            foreach (var p in parts.OrderBy(p => p.ConfigId))
            {
                var blob = Parse(p.Blob);
                var stations = AreaConfigBlob.CourseStations(p.Blob);
                if (blob == null || stations.Count == 0) { warnings.Add($"{p.Name} har inga stationer kopplade till banan och togs inte med."); continue; }
                var mine = new List<int>();
                foreach (var n in stations)
                {
                    if (covered.TryGetValue(n, out var other)) { warnings.Add($"Station {n} finns både i {other.Name} och {p.Name} — {other.Name} gäller."); continue; }
                    covered[n] = p; mine.Add(n);
                }
                parsed.Add((p, blob, mine));
                foreach (var k in new[] { "_scoringMode", "_morker" })
                {
                    var a = (course[k] ?? result[k])?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                    var b = blob[k]?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                    if (b.Length > 0 && a.Length == 0) { result[k] = blob[k]!.DeepClone(); continue; }
                    if (b.Length > 0 && a.Length > 0 && a != b)
                        warnings.Add(k == "_scoringMode"
                            ? $"{p.Name} har en annan tävlingstyp än banan — banans gäller."
                            : $"{p.Name} har en annan inställning för mörkerskytte än banan — banans gäller.");
                }
            }

            // Vapengrupperna: alla som finns i banan eller någon del.
            var classes = WeaponClasses(course).Concat(parsed.SelectMany(x => WeaponClasses(x.Blob))).Distinct().ToList();
            var fromCourse = new SortedSet<int>();
            var all = new SortedSet<int>();
            foreach (var wc in classes)
            {
                var byNumber = new SortedDictionary<int, JObject>();
                foreach (var st in Stations(course, wc))
                {
                    var n = StationNumber(st);
                    if (n <= 0 || covered.ContainsKey(n)) continue;
                    byNumber[n] = (JObject)st.DeepClone();
                    fromCourse.Add(n);
                }
                foreach (var (part, blob, mine) in parsed)
                {
                    var map = AreaConfigBlob.CourseStations(part.Blob);
                    var list = Stations(blob, wc).ToList();
                    if (list.Count == 0 && mine.Count > 0)
                    {
                        warnings.Add($"{part.Name} saknar vapengrupp {wc} (station {string.Join(", ", mine)}).");
                        continue;
                    }
                    foreach (var st in list)
                    {
                        var i = StationNumber(st);
                        if (i < 1 || i > map.Count) continue;
                        var n = map[i - 1];
                        if (!mine.Contains(n)) continue;
                        var copy = (JObject)st.DeepClone();
                        copy.Remove("Station");
                        copy["station"] = n;
                        foreach (var key in copy.Properties().Where(x => x.Name.StartsWith("_linkedFrom", StringComparison.Ordinal)).Select(x => x.Name).ToList())
                            copy.Remove(key);
                        byNumber[n] = copy;
                    }
                }
                if (byNumber.Count == 0) continue;
                foreach (var n in byNumber.Keys) all.Add(n);
                result[wc] = new JObject { ["stations"] = new JArray(byNumber.Values) };
            }

            var count = Math.Max(stationCount, Math.Max(all.Count == 0 ? 0 : all.Max, covered.Count == 0 ? 0 : covered.Keys.Max()));
            var missing = Enumerable.Range(1, Math.Max(count, 0)).Where(n => !all.Contains(n)).ToList();
            if (count > 0) result[StationCountKey] = count;
            result[AssembledFromKey] = new JArray(parsed.Select(x => new JObject
            {
                ["configId"] = x.Part.ConfigId,
                ["name"] = x.Part.Name,
                ["checksum"] = Checksum(x.Part.Blob),
                ["stations"] = new JArray(x.Stations.Cast<object>().ToArray()),
            }));
            result[AssembledAtKey] = now.ToString("yyyy-MM-dd HH:mm");
            return new CourseAssemblyResult(result.ToString(Newtonsoft.Json.Formatting.None), warnings, missing, fromCourse.ToList(), count);
        }

        /// <summary>Delarna banan sattes ihop av, eller tom lista.</summary>
        public static List<AssembledPart> AssembledFrom(string? courseBlob)
        {
            var o = Parse(courseBlob);
            if (o?[AssembledFromKey] is not JArray arr) return new();
            return arr.OfType<JObject>().Select(p => new AssembledPart(
                p.Value<int?>("configId") ?? 0, p.Value<string>("name") ?? "", p.Value<string>("checksum") ?? "",
                (p["stations"] as JArray)?.Select(t => t.Value<int>()).ToList() ?? new List<int>())).Where(p => p.ConfigId > 0).ToList();
        }

        /// <summary>När banan senast sattes ihop ("yyyy-MM-dd HH:mm"), eller null.</summary>
        public static string? AssembledAt(string? courseBlob) => Parse(courseBlob)?.Value<string>(AssembledAtKey);

        /// <summary>
        /// Innehållets kontrollsumma: stationerna och meta-nycklarna, oberoende av egenskapernas ordning. Det som bara säger
        /// var konfigurationen är ansluten (<c>_attachedConfigId</c>) räknas inte.
        /// </summary>
        public static string Checksum(string? blob)
        {
            var o = Parse(blob);
            if (o == null) return "";
            o.Remove("_attachedConfigId");
            o.Remove(AssembledAtKey);
            var canonical = Canonical(o).ToString(Newtonsoft.Json.Formatting.None);
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
        }

        /// <summary>
        /// Varför den sammansatta banan inte kan begäras godkänd, eller null. Varje del ska vara godkänd och oförändrad sedan
        /// banan sattes ihop. <paramref name="current"/>: delens nuvarande (namn, innehåll, godkänd) per konfiguration, null =
        /// finns inte längre.
        /// </summary>
        public static string? ApprovalBlocker(string? courseBlob, Func<int, (string Name, string? Blob, bool Approved)?> current)
        {
            var parts = AssembledFrom(courseBlob);
            if (parts.Count == 0) return null;
            var notApproved = new List<string>();
            var changed = new List<string>();
            foreach (var p in parts)
            {
                var now = current(p.ConfigId);
                if (now == null) { changed.Add(p.Name); continue; }
                if (Checksum(now.Value.Blob) != p.Checksum) changed.Add(now.Value.Name);
                else if (!now.Value.Approved) notApproved.Add(now.Value.Name);
            }
            if (changed.Count > 0)
                return $"Delarna har ändrats sedan banan sattes ihop ({string.Join(", ", changed)}). Sätt ihop banan igen i planeringen först.";
            if (notApproved.Count > 0)
                return $"Varje del ska vara godkänd innan hela banan kan godkännas. Inte godkända än: {string.Join(", ", notApproved)}.";
            return null;
        }

        // ---------------------------------------------------------------- hjälpare

        private static JObject? Parse(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) return null;
            try { return JObject.Parse(blob); } catch { return null; }
        }

        private static IEnumerable<string> WeaponClasses(JObject o) =>
            o.Properties().Where(p => !p.Name.StartsWith("_", StringComparison.Ordinal) && p.Value is JObject).Select(p => p.Name);

        private static IEnumerable<JObject> Stations(JObject o, string wc) =>
            ((o[wc] as JObject)?["stations"] as JArray ?? (o[wc] as JObject)?["Stations"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

        private static int StationNumber(JObject st) => st.Value<int?>("station") ?? st.Value<int?>("Station") ?? 0;

        private static JToken Canonical(JToken t) => t switch
        {
            JObject o => new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))),
            JArray a => new JArray(a.Select(Canonical)),
            _ => t.DeepClone(),
        };
    }
}
