namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Behovet per roll och dag (Stefan 2026-10-23: markörer behövs bara på tävlingsdagarna).
    /// En rad utan <see cref="StaffCrewNeed.DayDate"/> är rollens antal för alla dagar; en rad med dag
    /// ersätter det för just den dagen. 0 på en dag = rollen behövs inte den dagen.
    /// <para>Rena funktioner — samma regel för rutnätet, täckningen och utskriften.</para>
    /// </summary>
    public static class CrewNeedRules
    {
        /// <summary>Antalet som behövs av rollen den dagen. Utan dag (eller utan rad för dagen) = alla dagars antal.</summary>
        public static int ForDay(IEnumerable<StaffCrewNeed> rows, string scopeKind, string roleKey, DateTime? day)
        {
            var mine = rows.Where(r => string.Equals(r.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(r.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (day is { } d)
            {
                var ov = mine.FirstOrDefault(r => r.DayDate?.Date == d.Date);
                if (ov != null) return Math.Max(0, ov.Count);
            }
            return Math.Max(0, mine.FirstOrDefault(r => r.DayDate == null)?.Count ?? 0);
        }

        /// <summary>Rollens antal för alla dagar (raden utan dag), 0 om den saknas.</summary>
        public static int Default(IEnumerable<StaffCrewNeed> rows, string scopeKind, string roleKey) =>
            ForDay(rows, scopeKind, roleKey, null);

        /// <summary>
        /// Raderna som ska lagras för en roll: alla dagars antal (bara när det är större än 0) och en rad per
        /// dag som avviker från det. En dag med samma antal som alla dagar lagras inte — då följer den med när
        /// alla dagars antal ändras.
        /// </summary>
        public static List<(DateTime? Day, int Count)> Plan(int all, IEnumerable<(DateTime Day, int Count)> days)
        {
            all = Math.Max(0, all);
            var result = new List<(DateTime?, int)>();
            if (all > 0) result.Add((null, all));
            foreach (var (day, count) in days.GroupBy(x => x.Day.Date).Select(g => (g.Key, g.Last().Count)))
            {
                var c = Math.Max(0, count);
                if (c != all) result.Add((day, c));
            }
            return result;
        }

        /// <summary>Roller med ett behov på någon dag, med antalet den dagen (0 räknas inte).</summary>
        public static List<(string RoleKey, int Count)> EffectiveFor(IEnumerable<StaffCrewNeed> rows, string scopeKind, DateTime? day)
        {
            var list = rows.ToList();
            return list.Where(r => string.Equals(r.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RoleKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(k => (k, ForDay(list, scopeKind, k, day)))
                .Where(x => x.Item2 > 0)
                .ToList();
        }
    }
}
