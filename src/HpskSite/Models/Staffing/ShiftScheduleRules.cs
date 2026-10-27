using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Skiftschemat för en ruta (roll × station × dag), den rena delen: hål mot en tid som ska vara bemannad, och uppdelning av en tid i lika
    /// långa skift. Stefan 2026-10-27: "Dela passet" på personens sida var oåtkomligt och felade ("Tiden måste ligga inuti passet") — schemat
    /// byggs i stället för rutan, och appen ser själv vad som saknas. Inget här läser databasen.
    /// </summary>
    public static class ShiftScheduleRules
    {
        /// <summary>Ett klockslagsspann samma dag.</summary>
        public readonly record struct Span(TimeSpan Start, TimeSpan End);

        /// <summary>
        /// Stretchar inom <paramref name="window"/> där färre än <paramref name="need"/> är på plats. Ett spann utan tider (<c>null</c>) är på plats
        /// hela dagen. Hålen kommer i tidsordning och slås ihop när två gränsar till varandra.
        /// </summary>
        public static List<Span> Holes(IEnumerable<Span?> spans, Span window, int need = 1)
        {
            var holes = new List<Span>();
            if (need < 1 || window.End <= window.Start) return holes;
            var list = spans.ToList();
            var allDay = list.Count(s => s == null);
            var timed = list.Where(s => s != null).Select(s => s!.Value).Where(s => s.End > s.Start).ToList();

            // Gränserna där antalet kan ändras: fönstrets ändar och varje spanns ändar som ligger inuti det.
            var cuts = new SortedSet<TimeSpan> { window.Start, window.End };
            foreach (var s in timed)
            {
                if (s.Start > window.Start && s.Start < window.End) cuts.Add(s.Start);
                if (s.End > window.Start && s.End < window.End) cuts.Add(s.End);
            }
            var pts = cuts.ToList();
            for (var i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                var here = allDay + timed.Count(s => s.Start <= a && s.End >= b);
                if (here >= need) continue;
                if (holes.Count > 0 && holes[^1].End == a) holes[^1] = new Span(holes[^1].Start, b);
                else holes.Add(new Span(a, b));
            }
            return holes;
        }

        /// <summary>
        /// Delar <paramref name="window"/> i <paramref name="count"/> lika långa skift. Gränserna avrundas till hela kvartar så att skiften får
        /// tider man säger högt (08:00–11:00, inte 08:00–10:57); det sista skiftet tar resten. Går det inte att få plats med kvartar (för kort
        /// fönster eller för många skift) delas det i hela minuter.
        /// </summary>
        public static List<Span> Split(Span window, int count)
        {
            var result = new List<Span>();
            if (count < 1 || window.End <= window.Start) return result;
            var total = (window.End - window.Start).TotalMinutes;
            var step = total / count;
            var quarter = step >= 15;
            var cursor = window.Start;
            for (var i = 1; i <= count; i++)
            {
                TimeSpan end;
                if (i == count) end = window.End;
                else
                {
                    var raw = window.Start.TotalMinutes + step * i;
                    end = TimeSpan.FromMinutes(quarter ? Math.Round(raw / 15) * 15 : Math.Round(raw));
                    if (end <= cursor) end = cursor + TimeSpan.FromMinutes(1);
                    if (end >= window.End) end = window.End - TimeSpan.FromMinutes(1);
                }
                result.Add(new Span(cursor, end));
                cursor = end;
            }
            return result;
        }

        /// <summary>"08:00" för ett klockslag.</summary>
        public static string Hm(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";

        /// <summary>Roll-nycklarna i planeringens kommaseparerade lista (skiftläge ignoreras, tomma poster bort).</summary>
        public static HashSet<string> RoleSet(string? csv) =>
            new((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

        /// <summary>Listan efter att en roll slagits på eller av. Sorterad så att samma val alltid ger samma text; null när den blir tom.</summary>
        public static string? WithRole(string? csv, string roleKey, bool on)
        {
            var set = RoleSet(csv);
            if (string.IsNullOrWhiteSpace(roleKey) || roleKey.Contains(',')) return string.IsNullOrEmpty(csv) ? null : csv;
            if (on) set.Add(roleKey.Trim()); else set.Remove(roleKey.Trim());
            return set.Count == 0 ? null : string.Join(",", set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
    }
}
