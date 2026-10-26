using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Planera dagen, "Fördela dem åt mig": <b>delar en dag i block</b> som en person kan stå på. Ett behov utan pass är en lucka över hela dagsprogrammet (08–17); lösaren
    /// prövar hela luckan mot personens upptagna tid, så den som skjuter 11:30 (upptagen ~10:45–15:00) kan inte placeras alls. Delas dagen i block kan samma person ta
    /// ett block före eller efter sin start. Har dagen pass används passens tider; annars delas dagsprogrammet jämnt i block om ungefär <c>blockMinutes</c>.
    /// Ren funktion — ingen databas, ingen klocka.
    /// </summary>
    public static class SlotSlicer
    {
        public const int MinBlockMinutes = 60;
        public const int MaxBlockMinutes = 480;
        public const int DefaultBlockMinutes = 180;

        /// <summary>
        /// Blocken för en dag, i tidsordning, sammanhängande och utan överlapp. <paramref name="passes"/> (start, slut) har företräde; annars delas
        /// <paramref name="program"/> i så många lika långa block som går på ~<paramref name="blockMinutes"/> (minst ett), avrundat till hela 5 minuter.
        /// </summary>
        public static List<StaffInterval> Blocks(DateTime day, DayProgram program, int blockMinutes, IReadOnlyList<(TimeSpan Start, TimeSpan End)>? passes = null)
        {
            if (passes is { Count: > 0 })
            {
                var fromPasses = passes.Select(p => StaffInterval.From(day, p.Start, p.End)).Where(i => i != null).Select(i => i!.Value)
                    .OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
                if (fromPasses.Count > 0) return fromPasses;
            }
            var pr = program.OrDefault();
            var block = Math.Clamp(blockMinutes, MinBlockMinutes, MaxBlockMinutes);
            var total = (pr.End - pr.Start).TotalMinutes;
            var n = Math.Max(1, (int)Math.Round(total / block, MidpointRounding.AwayFromZero));   // hellre ett block mer än ett för långt (fler går att placera)
            var result = new List<StaffInterval>();
            var cursor = pr.Start;
            for (var i = 1; i <= n; i++)
            {
                var end = i == n ? pr.End : pr.Start + TimeSpan.FromMinutes(Math.Round(total * i / n / 5.0) * 5);
                if (end > cursor) { result.Add(new StaffInterval(day.Date + cursor, day.Date + end, AllDay: false)); cursor = end; }
            }
            return result;
        }
    }
}
