using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Omgång 3 steg 9 (Stefan 2026-10-24, Expertgruppen 3.4): krockar över hela familjen — veckan. Rena funktioner.
    /// <list type="bullet">
    /// <item><b>Uppdrag</b> mot uppdrag i en ANNAN planering i familjen samma dag och tid (inom en planering räknar rutnätet själv).</item>
    /// <item><b>Start</b>: ett uppdrag mot personens egen start i en av veckans tävlingar (ur startlistan).</item>
    /// <item><b>Avsikt</b>: ett uppdrag en dag då personen sagt att hen tänker tävla — en varning, inte ett fel; den
    /// gäller bara så länge personen inte har en start i tävlingen (då räknas starten).</item>
    /// </list>
    /// Ett uppdrag eller en start utan tid gäller hela dagen. Kant i kant är ingen krock.
    /// </summary>
    public static class FamilyClashRules
    {
        public const string KindAssignment = "uppdrag";
        public const string KindStart = "start";
        public const string KindIntent = "avsikt";

        public sealed record Slot(string PersonKey, string Name, int PlanningId, DateTime Date, (TimeSpan Start, TimeSpan End)? Span, string Label);
        public sealed record Start(string PersonKey, int CompetitionId, DateTime Date, (TimeSpan Start, TimeSpan End)? Span, string Label);
        public sealed record Intent(string PersonKey, string Name, int CompetitionId, DateTime From, DateTime To, string Label);
        public sealed record Clash(string PersonKey, string Name, DateTime Date, string Kind, string A, string B);

        public static bool Overlaps((TimeSpan Start, TimeSpan End)? a, (TimeSpan Start, TimeSpan End)? b) =>
            a == null || b == null || (a.Value.Start < b.Value.End && b.Value.Start < a.Value.End);

        public static List<Clash> Find(IEnumerable<Slot> slots, IEnumerable<Start> starts, IEnumerable<Intent> intents)
        {
            var result = new List<Clash>();
            var bySlot = slots.GroupBy(s => s.PersonKey).ToDictionary(g => g.Key, g => g.ToList());
            var startList = starts.ToList();

            foreach (var (person, list) in bySlot)
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        var a = list[i]; var b = list[j];
                        if (a.PlanningId == b.PlanningId || a.Date.Date != b.Date.Date || !Overlaps(a.Span, b.Span)) continue;
                        result.Add(new Clash(person, a.Name, a.Date.Date, KindAssignment, a.Label, b.Label));
                    }

            foreach (var st in startList)
                if (bySlot.TryGetValue(st.PersonKey, out var list))
                    foreach (var a in list.Where(a => a.Date.Date == st.Date.Date && Overlaps(a.Span, st.Span)))
                        result.Add(new Clash(st.PersonKey, a.Name, a.Date.Date, KindStart, a.Label, st.Label));

            foreach (var it in intents)
            {
                if (!bySlot.TryGetValue(it.PersonKey, out var list)) continue;
                // Har personen en start i tävlingen gäller den — avsikten är då inte längre en gissning.
                if (it.CompetitionId > 0 && startList.Any(s => s.PersonKey == it.PersonKey && s.CompetitionId == it.CompetitionId)) continue;
                foreach (var a in list.Where(a => a.Date.Date >= it.From.Date && a.Date.Date <= it.To.Date))
                    result.Add(new Clash(it.PersonKey, it.Name, a.Date.Date, KindIntent, a.Label, it.Label));
            }
            return result.OrderBy(c => c.Date).ThenBy(c => c.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("sv-SE"), true)).ToList();
        }
    }
}
