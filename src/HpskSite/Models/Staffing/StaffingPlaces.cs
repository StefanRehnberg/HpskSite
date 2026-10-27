using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Platser = behov minus bemannat per roll och dag (per pass när dagen har flera pass). EN räkning som både bandet "Bemanningen steg för steg" och lägesrapporten läser,
    /// så att de två aldrig kan säga olika. Ren funktion över rutnätssvaret.
    /// </summary>
    public static class StaffingPlaces
    {
        public static (int Needed, int Missing, Dictionary<string, (int Needed, int Missing)> PerRole) Count(GridResponse resp)
        {
            var cols = resp.Columns.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();
            int needed = 0, missing = 0;
            var perRole = new Dictionary<string, (int Needed, int Missing)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in resp.Rows)
                foreach (var c in cols)
                {
                    int n, m;
                    var pc = row.PassCover.TryGetValue(c.Key, out var l) ? l : new List<GridPassCover>();
                    if (pc.Any(p => p.Needed > 0)) { n = pc.Sum(p => p.Needed); m = pc.Sum(p => Math.Max(0, p.Needed - p.Filled)); }
                    else
                    {
                        n = row.NeedFor(c.Key);
                        var filled = row.FilledByDay.TryGetValue(c.Key, out var f) ? f : 0;
                        m = Math.Max(0, n - filled);
                    }
                    needed += n; missing += m;
                    var key = row.RoleName ?? row.RoleKey;
                    var cur = perRole.TryGetValue(key, out var x) ? x : (0, 0);
                    perRole[key] = (cur.Item1 + n, cur.Item2 + m);
                }
            return (needed, missing, perRole);
        }
    }
}
