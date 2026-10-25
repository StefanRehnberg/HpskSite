using System.Globalization;
using HpskSite.Models.Staffing;

namespace HpskSite.Services.Staffing
{
    /// <summary>En möjlig ersättare. <see cref="Free"/>: null = uppdragets tid är okänd, så det går inte att säga.</summary>
    public sealed record ReplacementCandidate(int MemberId, string Name, bool RoleMatch, bool? Free, string? BusyWith, string? Comment, string? Phone = null);

    /// <summary>
    /// Steg 6b (B11): <b>vem kan ta ett uppdrag som blivit ledigt</b>. Reservpoolen är de som anmält sig till dagen i någon del av
    /// familjen (steg 6a) — "disponibel" är ett tillstånd i anmälan, inte en roll. Rena funktioner; databasen sköts av
    /// <see cref="StaffReplacementService"/>.
    /// </summary>
    public static class ReplacementRules
    {
        private static readonly StringComparer Sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true);

        /// <summary>
        /// Har personen anmält sig till dagen — och till just den här rollen? Ett dagsval med tom roll ("var det behövs") eller samma roll
        /// räknas, liksom ett eget tillfälle (<paramref name="slotIdsOnDate"/>) den dagen. Ett val av en <i>annan</i> roll den dagen räknas inte:
        /// den som bara erbjudit sig som markör ska inte föreslås som skjutledare.
        /// </summary>
        public static (bool Volunteers, bool RoleMatch) Matches(IEnumerable<HelpSlotChoice> choices, DateTime date, string roleKey, ISet<int> slotIdsOnDate)
        {
            var any = false; var role = false;
            foreach (var c in choices)
            {
                if (c.SlotId > 0)
                {
                    if (slotIdsOnDate.Contains(c.SlotId)) any = true;
                    continue;
                }
                if (!c.IsDayChoice || !DateTime.TryParseExact(c.Date!.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Date != date.Date) continue;
                var r = (c.RoleKey ?? "").Trim();
                if (r.Length == 0) any = true;
                else if (string.Equals(r, roleKey, StringComparison.OrdinalIgnoreCase)) { any = true; role = true; }
            }
            return (any, role);
        }

        /// <summary>Ledig = inget av personens andra intervall den dagen överlappar uppdragets. Okänd tid på uppdraget = okänt (null).</summary>
        public static bool? FreeFor(StaffInterval? row, IEnumerable<StaffInterval> theirs) =>
            row is not { } r ? null : !theirs.Any(t => t.Overlaps(r));

        /// <summary>Lediga med rätt roll först, sedan lediga, sedan okänt, sist upptagna; inom varje grupp efter namn.</summary>
        public static List<ReplacementCandidate> Rank(IEnumerable<ReplacementCandidate> list) =>
            list.OrderBy(c => c.Free == true ? (c.RoleMatch ? 0 : 1) : c.Free == null ? 2 : 3)
                .ThenBy(c => c.Name, Sv)
                .ThenBy(c => c.MemberId)
                .ToList();
    }
}
