using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Avlösningen sedd från den som står i skiftet (Stefan 2026-10-27: de som delar en placering ska se att det är X som avlöser dem kl Z). Ren funktion över
    /// skiftschemats rader: vem tar över efter mig, och vem står före mig, i SAMMA ruta (roll, station, dag). Grannen är den som börjar när jag slutar — eller
    /// inom en timme efter — och den som slutar när jag börjar, eller inom en timme före. Samtidiga skift (två på samma tid) är ingen avlösning.
    /// </summary>
    public static class ShiftHandoverRules
    {
        /// <summary>Längsta glapp mellan två skift som ändå räknas som avlösning, och som texten då nämner.</summary>
        public static readonly TimeSpan MaxGap = TimeSpan.FromHours(1);

        public sealed record Handover(string? Before, string? After);

        public static Handover For(StaffAssignment mine, IEnumerable<StaffAssignment> others)
        {
            if (mine.StartsAt is not DateTime s || mine.EndsAt is not DateTime e || e <= s) return new(null, null);
            var near = others.Where(o => o.Id != mine.Id && SameCell(mine, o) && o.StartsAt is not null && o.EndsAt is not null
                    && o.EndsAt > o.StartsAt && o.StartsAt.Value.Date == s.Date
                    && !SamePerson(mine, o)).ToList();

            var next = near.Where(o => o.StartsAt >= e && o.StartsAt - e <= MaxGap).OrderBy(o => o.StartsAt).ThenBy(o => o.Id).FirstOrDefault();
            var prev = near.Where(o => o.EndsAt <= s && s - o.EndsAt <= MaxGap).OrderByDescending(o => o.EndsAt).ThenBy(o => o.Id).FirstOrDefault();

            string? after = next == null ? null
                : next.StartsAt == e ? $"Du avlöses kl {Hm(e)} av {Name(next)}."
                : $"Nästa på platsen är {Name(next)} från {Hm(next.StartsAt!.Value)}.";
            string? before = prev == null ? null
                : prev.EndsAt == s ? $"Du tar över kl {Hm(s)} efter {Name(prev)}."
                : $"Före dig på platsen: {Name(prev)} till {Hm(prev.EndsAt!.Value)}.";
            return new(before, after);
        }

        private static string Hm(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        private static string Name(StaffAssignment a) => string.IsNullOrWhiteSpace(a.DisplayName) ? "någon" : a.DisplayName.Trim();

        private static string Scope(string? t) => string.IsNullOrEmpty(t) || string.Equals(t, StaffScopeType.All, StringComparison.OrdinalIgnoreCase) ? "" : t.ToLowerInvariant();

        private static bool SameCell(StaffAssignment a, StaffAssignment b) =>
            string.Equals(a.RoleKey, b.RoleKey, StringComparison.OrdinalIgnoreCase)
            && a.PlanningId == b.PlanningId && (a.PlanningId is > 0 || a.CompetitionId == b.CompetitionId)
            && Scope(a.ScopeType) == Scope(b.ScopeType) && (a.ScopeKey ?? "") == (b.ScopeKey ?? "");

        private static bool SamePerson(StaffAssignment a, StaffAssignment b) =>
            a.MemberId is > 0 ? a.MemberId == b.MemberId
            : string.Equals((a.DisplayName ?? "").Trim(), (b.DisplayName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
