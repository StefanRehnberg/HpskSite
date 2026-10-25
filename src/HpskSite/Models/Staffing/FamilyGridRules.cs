using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Steg 2: familjens rutnät. Dagaxeln är <b>unionen</b> av delarnas dagar — en dag som bara en del har står som kolumn
    /// i alla, grå där delen inte har den. Ren funktion (enhetstestad); delarnas egna rutnät rörs inte, så varje dels grupp
    /// är exakt dagens rutnät för delen.
    /// </summary>
    public static class FamilyGridRules
    {
        /// <summary>
        /// Unionen av delarnas kolumner i datumordning ("yyyy-MM-dd" sorterar som text); "Utan datum" (tom nyckel) sist. En
        /// dag som någon del har planerad är planerad; den bär arbete om någon del har arbete. Dagens id är en dels —
        /// dagar ändras i delen — så den nollas.
        /// </summary>
        public static List<GridColumn> UnionColumns(IEnumerable<IEnumerable<GridColumn>> perPart)
        {
            var byKey = new Dictionary<string, GridColumn>(StringComparer.Ordinal);
            foreach (var cols in perPart)
                foreach (var c in cols)
                {
                    var key = c.Key ?? "";
                    if (!byKey.TryGetValue(key, out var u))
                    {
                        byKey[key] = new GridColumn
                        {
                            Key = key, Label = c.Label, DayLabel = c.DayLabel, Kind = c.Kind, KindLabel = c.KindLabel,
                            IsPlanned = c.IsPlanned, HasAssignments = c.HasAssignments, TimeLabel = c.TimeLabel, DayId = 0,
                        };
                        continue;
                    }
                    u.IsPlanned |= c.IsPlanned;
                    u.HasAssignments |= c.HasAssignments;
                    if (string.IsNullOrWhiteSpace(u.DayLabel)) u.DayLabel = c.DayLabel;
                    if (string.IsNullOrWhiteSpace(u.TimeLabel)) u.TimeLabel = c.TimeLabel;
                }
            return byKey.Values
                .OrderBy(c => string.IsNullOrEmpty(c.Key) ? 1 : 0)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Delens egna dagar: nycklarna i dess rutnät (utan "Utan datum").</summary>
        public static List<string> DayKeys(IEnumerable<GridColumn> cols) =>
            cols.Where(c => !string.IsNullOrEmpty(c.Key)).Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
    }
}
