using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HpskSite.Models.Planning
{
    /// <summary>Godkännandet av EN station (Stefan 2026-10-25: "station för station, sedan helheten").</summary>
    public sealed record StationApproval(string Status, int? ByMemberId, string? At, string? Comment, string? Checksum);

    /// <summary>
    /// Stefan 2026-10-25: planeringens stationer är EN fältkonfiguration, och varje station kan ha en ansvarig som bara
    /// ändrar sina stationer. Rena funktioner för stationen som enhet: kontrollsumma, sammanslagning (en sparning lägger in
    /// BARA sina stationer — två ansvariga skriver inte över varandra) och godkännandet per station i meta-nyckeln
    /// <c>_stationApproval</c>.
    /// </summary>
    public static class StationBlob
    {
        public const string ApprovalKey = "_stationApproval";
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Returned = "Returned";

        private static JObject Parse(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) return new JObject();
            try { return JObject.Parse(blob); } catch { return new JObject(); }
        }

        private static IEnumerable<string> WeaponClasses(JObject o) =>
            o.Properties().Where(p => !p.Name.StartsWith("_", StringComparison.Ordinal) && p.Value is JObject).Select(p => p.Name);

        private static JArray? StationsOf(JObject o, string wc) => (o[wc] as JObject)?["stations"] as JArray ?? (o[wc] as JObject)?["Stations"] as JArray;

        private static int No(JToken t) => t.Value<int?>("station") ?? t.Value<int?>("Station") ?? 0;

        private static JToken Canonical(JToken t) => t switch
        {
            JObject o => new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))),
            JArray a => new JArray(a.Select(Canonical)),
            _ => t.DeepClone(),
        };

        /// <summary>Stationens innehåll i alla vapengrupper som en kontrollsumma (tom sträng = stationen finns inte).</summary>
        public static string Checksum(string? blob, int station) => Checksum(Parse(blob), station);

        private static string Checksum(JObject o, int station)
        {
            var parts = new JObject();
            foreach (var wc in WeaponClasses(o).OrderBy(x => x, StringComparer.Ordinal))
                if (StationsOf(o, wc)?.FirstOrDefault(s => No(s) == station) is JObject st) parts[wc] = Canonical(st);
            if (!parts.HasValues) return "";
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(parts.ToString(Newtonsoft.Json.Formatting.None))))[..16];
        }

        /// <summary>Kontrollsumman för varje station som finns i någon vapengrupp.</summary>
        public static Dictionary<int, string> Checksums(string? blob)
        {
            var o = Parse(blob);
            var numbers = WeaponClasses(o).SelectMany(wc => StationsOf(o, wc)?.Select(No) ?? Enumerable.Empty<int>()).Where(n => n > 0).Distinct();
            return numbers.ToDictionary(n => n, n => Checksum(o, n));
        }

        /// <summary>Stationerna vars innehåll skiljer mellan två versioner.</summary>
        public static List<int> Changed(string? before, string? after)
        {
            var a = Checksums(before); var b = Checksums(after);
            return a.Keys.Union(b.Keys).Where(n => a.GetValueOrDefault(n, "") != b.GetValueOrDefault(n, "")).OrderBy(n => n).ToList();
        }

        /// <summary>
        /// Lägger in <paramref name="stations"/> ur <paramref name="incoming"/> i <paramref name="current"/>: varje vapengrupp
        /// får stationen som den ser ut i det inkommande (eller tappar den om den inte finns där). Övriga stationer och
        /// all meta tas ur <paramref name="current"/> — en sparning rör aldrig någon annans station.
        /// </summary>
        public static string Merge(string? current, string? incoming, IEnumerable<int> stations)
        {
            var cur = Parse(current); var inc = Parse(incoming);
            var set = stations.ToHashSet();
            foreach (var wc in WeaponClasses(cur).Union(WeaponClasses(inc)).ToList())
            {
                if (cur[wc] is not JObject cwc) { cwc = new JObject { ["stations"] = new JArray() }; cur[wc] = cwc; }
                var arr = StationsOf(cur, wc) ?? new JArray();
                var kept = arr.Where(s => !set.Contains(No(s))).Select(s => s.DeepClone()).ToList();
                var added = (StationsOf(inc, wc) ?? new JArray()).Where(s => set.Contains(No(s))).Select(s => s.DeepClone()).ToList();
                cwc.Remove("Stations");
                cwc["stations"] = new JArray(kept.Concat(added).OrderBy(No));
            }
            return cur.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---------------------------------------------------------------- godkännandet per station

        public static Dictionary<int, StationApproval> Approvals(string? blob)
        {
            var map = new Dictionary<int, StationApproval>();
            if (Parse(blob)[ApprovalKey] is not JObject o) return map;
            foreach (var p in o.Properties())
                if (int.TryParse(p.Name, out var n) && p.Value is JObject a)
                    map[n] = new StationApproval(a.Value<string>("s") ?? "", a.Value<int?>("by"), a.Value<string>("at"), a.Value<string>("c"), a.Value<string>("sum"));
            return map;
        }

        /// <summary>Skriver godkännandena (null = stationen är ett utkast igen) och returnerar blobben.</summary>
        public static string WithApprovals(string? blob, IDictionary<int, StationApproval?> changes)
        {
            var o = Parse(blob);
            var map = o[ApprovalKey] as JObject ?? new JObject();
            foreach (var (n, a) in changes)
            {
                if (a == null) { map.Remove(n.ToString()); continue; }
                map[n.ToString()] = new JObject { ["s"] = a.Status, ["by"] = a.ByMemberId, ["at"] = a.At, ["c"] = a.Comment, ["sum"] = a.Checksum };
            }
            if (map.HasValues) o[ApprovalKey] = map; else o.Remove(ApprovalKey);
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Godkännandet som gäller nu: en godkänd eller väntande station vars innehåll ändrats sedan dess är ett utkast igen.
        /// En återsänd station behåller sin kommentar tills den begärs godkänd igen.
        /// </summary>
        public static StationApproval? Current(string? blob, int station)
        {
            var a = Approvals(blob).GetValueOrDefault(station);
            if (a == null) return null;
            if ((a.Status == Approved || a.Status == Pending) && a.Checksum != Checksum(blob, station)) return null;
            return a;
        }

        /// <summary>Varför helheten inte kan godkännas: stationerna 1..<paramref name="count"/> som inte är godkända, eller null.</summary>
        public static string? WholeBlocker(string? blob, int count)
        {
            var missing = Enumerable.Range(1, Math.Max(0, count)).Where(n => Current(blob, n)?.Status != Approved).ToList();
            if (missing.Count == 0) return null;
            return $"Varje station ska vara godkänd innan helheten kan godkännas. Inte godkända än: station {string.Join(", ", missing)}.";
        }
    }
}
