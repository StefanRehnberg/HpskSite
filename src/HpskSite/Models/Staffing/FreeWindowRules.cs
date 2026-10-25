using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    public readonly record struct FreeWindow(DateTime Start, DateTime End)
    {
        public TimeSpan Length => End - Start;
    }

    /// <summary>
    /// Steg 6c (B12): <b>lediga fönster</b> — när en funktionär faktiskt har tid att skjuta själv. Funktionärernas egen skjutning är
    /// ett av tävlingsplanerarens fyra hål: ett uppdrag heldag i Fält och ett pass 13–15 i Precision lämnar bara vissa timmar,
    /// och planeraren ser det först när startlistan redan är gjord. Här räknas fönstren ur personens vecka; ett eget
    /// funktionärsskjutlag kommer senare (B12). Rena funktioner, ingen databas.
    /// </summary>
    public static class FreeWindowRules
    {
        /// <summary>En tävlingsdag för den som skjuter: tidigare och senare än ett funktionärspass (som heldag räknar 08–17).</summary>
        public static readonly DayProgram ShootDay = new(TimeSpan.FromHours(7), TimeSpan.FromHours(20));

        /// <summary>Kortare fönster än så hinner man inte skjuta i.</summary>
        public static readonly TimeSpan MinLength = TimeSpan.FromHours(1);

        /// <summary>
        /// Luckorna mellan <paramref name="busy"/> inom dagens program, minst <paramref name="min"/> långa, i tidsordning. Uppdrag som
        /// överlappar slås ihop; ett pass från kvällen före som fortsätter över midnatt klipps mot dagen; kant i kant lämnar ingen lucka.
        /// </summary>
        public static List<FreeWindow> Windows(IEnumerable<StaffInterval> busy, DateTime date, DayProgram? program = null, TimeSpan? min = null)
        {
            var p = (program ?? ShootDay).OrDefault();
            var from = date.Date + p.Start;
            var to = date.Date + p.End;
            var minLen = min ?? MinLength;

            var clipped = busy
                .Select(b => (S: b.Start < from ? from : b.Start, E: b.End > to ? to : b.End))
                .Where(b => b.E > b.S)
                .OrderBy(b => b.S).ThenBy(b => b.E)
                .ToList();

            var result = new List<FreeWindow>();
            var cursor = from;
            foreach (var b in clipped)
            {
                if (b.S - cursor >= minLen) result.Add(new FreeWindow(cursor, b.S));
                if (b.E > cursor) cursor = b.E;
            }
            if (to - cursor >= minLen) result.Add(new FreeWindow(cursor, to));
            return result;
        }
    }
}
