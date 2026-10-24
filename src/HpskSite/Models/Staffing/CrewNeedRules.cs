namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Behovet per roll och dag (Stefan 2026-10-23: markörer behövs bara på tävlingsdagarna).
    /// En rad utan <see cref="StaffCrewNeed.DayDate"/> är rollens antal för alla dagar; en rad med dag
    /// ersätter det för just den dagen. 0 på en dag = rollen behövs inte den dagen.
    /// <para>Omgång 3 (Expertgruppen 3.2): en rad kan också gälla en bestämd station (<see cref="StaffCrewNeed.ScopeKey"/>)
    /// och ett bestämt pass (<see cref="StaffCrewNeed.PassId"/>) — <see cref="For"/> väljer den mest bestämda.</para>
    /// <para>Rena funktioner — samma regel för rutnätet, täckningen och utskriften.</para>
    /// </summary>
    public static class CrewNeedRules
    {
        private static bool Plain(StaffCrewNeed r) => string.IsNullOrEmpty(r.ScopeKey) && r.PassId == null;

        /// <summary>Antalet som behövs av rollen den dagen. Utan dag (eller utan rad för dagen) = alla dagars antal.
        /// Läser bara raderna utan station och pass.</summary>
        public static int ForDay(IEnumerable<StaffCrewNeed> rows, string scopeKind, string roleKey, DateTime? day)
        {
            var mine = rows.Where(r => Plain(r) && string.Equals(r.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(r.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)).ToList();
            return DayOrDefault(mine, day) ?? 0;
        }

        private static int? DayOrDefault(List<StaffCrewNeed> mine, DateTime? day)
        {
            if (day is { } d)
            {
                var ov = mine.FirstOrDefault(r => r.DayDate?.Date == d.Date);
                if (ov != null) return Math.Max(0, ov.Count);
            }
            var def = mine.FirstOrDefault(r => r.DayDate == null);
            return def == null ? null : Math.Max(0, def.Count);
        }

        /// <summary>
        /// Omgång 3 (3.2): det mest bestämda behovet. Ordning: stationen och passet → stationen hela dagen → passet på varje
        /// station → dagen. Ett pass gäller bara sin egen dag (raden bär passets dag).
        /// </summary>
        public static int For(IEnumerable<StaffCrewNeed> rows, string scopeKind, string roleKey, DateTime? day, string? scopeKey, int? passId)
        {
            var mine = rows.Where(r => string.Equals(r.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(r.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase)).ToList();
            bool KeyIs(StaffCrewNeed r, string? k) => string.Equals(r.ScopeKey ?? "", k ?? "", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(scopeKey))
            {
                if (passId != null && mine.FirstOrDefault(r => KeyIs(r, scopeKey) && r.PassId == passId) is { } sp) return Math.Max(0, sp.Count);
                if (DayOrDefault(mine.Where(r => KeyIs(r, scopeKey) && r.PassId == null).ToList(), day) is int sd) return sd;
            }
            if (passId != null && mine.FirstOrDefault(r => string.IsNullOrEmpty(r.ScopeKey) && r.PassId == passId) is { } p) return Math.Max(0, p.Count);
            return DayOrDefault(mine.Where(Plain).ToList(), day) ?? 0;
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

        /// <summary>
        /// Omgång 3: passen som ska lagras — bara de som avviker från sin dags antal (samma antal följer dagen). Ett pass vars
        /// dag inte finns bland dagarna jämförs med alla dagars antal.
        /// </summary>
        public static List<(int PassId, DateTime Day, int Count)> PlanPasses(int all, IEnumerable<(DateTime Day, int Count)> days,
            IEnumerable<(int PassId, DateTime Day, int Count)> passes)
        {
            var byDay = days.GroupBy(x => x.Day.Date).ToDictionary(g => g.Key, g => Math.Max(0, g.Last().Count));
            return passes.GroupBy(p => p.PassId).Select(g => g.Last())
                .Select(p => (p.PassId, Day: p.Day.Date, Count: Math.Max(0, p.Count)))
                .Where(p => p.Count != (byDay.TryGetValue(p.Day, out var d) ? d : Math.Max(0, all)))
                .ToList();
        }

        /// <summary>Roller med ett behov på någon dag, med antalet den dagen (0 räknas inte). Bara raderna utan station och pass.</summary>
        public static List<(string RoleKey, int Count)> EffectiveFor(IEnumerable<StaffCrewNeed> rows, string scopeKind, DateTime? day)
        {
            var list = rows.Where(Plain).ToList();
            return list.Where(r => string.Equals(r.ScopeKind, scopeKind, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RoleKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(k => (k, ForDay(list, scopeKind, k, day)))
                .Where(x => x.Item2 > 0)
                .ToList();
        }
    }
}
