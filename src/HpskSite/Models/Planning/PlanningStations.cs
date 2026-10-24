using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// P4 (Stefan 2026-10-24, fråga 13a): i fältskytte delas stationerna mellan kretsar eller klubbar — en förening ansvarar
    /// för en eller flera stationer. Det skrivs på OMRÅDET ("Stationer 3–6", värd Varbergs PK). Rena regler: läsa och skriva
    /// listan, och hitta en station som två områden gör anspråk på.
    /// </summary>
    public static class PlanningStations
    {
        public const int Max = 99;

        /// <summary>
        /// "3-6, 9" → [3,4,5,6,9]. Tom text = inga stationer (null-fel). Godtar bindestreck och tankstreck, komma, semikolon
        /// och mellanslag. Fel: ett tal utanför 1–99, ett bakvänt intervall, eller något som inte är en station.
        /// </summary>
        public static (List<int> Stations, string? Error) Parse(string? text)
        {
            var list = new SortedSet<int>();
            var t = (text ?? "").Trim();
            if (t.Length == 0) return (new List<int>(), null);
            foreach (var raw in t.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Replace('–', '-').Replace('—', '-').Trim();
                var dash = part.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(part[..dash], out var a) || !int.TryParse(part[(dash + 1)..], out var b))
                        return (new List<int>(), $"\"{raw}\" är inte stationer. Skriv till exempel 3-6, 9.");
                    if (b < a) return (new List<int>(), $"\"{raw}\": den första stationen ska vara den lägsta.");
                    if (a < 1 || b > Max) return (new List<int>(), $"Stationerna är 1–{Max}.");
                    for (var i = a; i <= b; i++) list.Add(i);
                }
                else
                {
                    if (!int.TryParse(part, out var n)) return (new List<int>(), $"\"{raw}\" är inte en station. Skriv till exempel 3-6, 9.");
                    if (n < 1 || n > Max) return (new List<int>(), $"Stationerna är 1–{Max}.");
                    list.Add(n);
                }
            }
            return (list.ToList(), null);
        }

        /// <summary>[3,4,5,6,9] → "3–6, 9". Två i följd skrivs "3, 4".</summary>
        public static string Format(IEnumerable<int> stations)
        {
            var s = stations.Distinct().OrderBy(x => x).ToList();
            var parts = new List<string>();
            for (var i = 0; i < s.Count;)
            {
                var j = i;
                while (j + 1 < s.Count && s[j + 1] == s[j] + 1) j++;
                if (j - i >= 2) parts.Add($"{s[i]}–{s[j]}");
                else for (var k = i; k <= j; k++) parts.Add(s[k].ToString());
                i = j + 1;
            }
            return string.Join(", ", parts);
        }

        /// <summary>Den första station som ett ANNAT område redan har, eller null.</summary>
        public static (int Station, string OtherArea)? Conflict(IEnumerable<int> mine, IEnumerable<(string Name, string? Stations)> others)
        {
            var set = mine.ToHashSet();
            foreach (var (name, text) in others)
                foreach (var n in Parse(text).Stations)
                    if (set.Contains(n)) return (n, name);
            return null;
        }
    }
}
