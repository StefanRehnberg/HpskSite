using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Lägger "Du avlöses kl 14:00 av Berit" och "Du tar över kl 11:00 efter Anna" på funktionärsraderna i Mitt schema och veckan. EN läsning: skiften i
    /// samma roller i samma planeringar som personens egna (en tävling utan planering har ingen bemanning) — reglerna ligger i <see cref="ShiftHandoverRules"/>.
    /// Ett fel här lämnar raderna utan text och loggas; en avlösningsrad är en hjälp, aldrig ett villkor för att schemat visas.
    /// </summary>
    public static class ShiftHandoverReader
    {
        public static void Fill(IScopeProvider scopes, IEnumerable<ScheduleItem> items, ILogger logger)
        {
            try
            {
                var mine = items.Where(i => i.Kind == ScheduleItemKind.Funktionar && i.AssignmentId > 0 && i.StartsAt != null).ToList();
                if (mine.Count == 0) return;
                var ids = mine.Select(i => i.AssignmentId).Distinct().ToList();
                List<StaffAssignment> mineRows, cellRows;
                using (var s = scopes.CreateScope(autoComplete: true))
                {
                    mineRows = s.Database.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE Id IN (@ids)", new { ids });
                    var roles = mineRows.Select(r => r.RoleKey).Distinct().DefaultIfEmpty("").ToList();
                    var pids = mineRows.Where(r => r.PlanningId is > 0).Select(r => r.PlanningId!.Value).Distinct().DefaultIfEmpty(0).ToList();
                    cellRows = s.Database.Fetch<StaffAssignment>(
                        "SELECT * FROM StaffAssignment WHERE StartsAt IS NOT NULL AND RoleKey IN (@roles) AND PlanningId IN (@pids)",
                        new { roles, pids });
                }
                var byId = mineRows.ToDictionary(r => r.Id);
                foreach (var it in mine)
                {
                    if (!byId.TryGetValue(it.AssignmentId, out var row)) continue;
                    var h = ShiftHandoverRules.For(row, cellRows);
                    it.HandoverBefore = h.Before; it.HandoverAfter = h.After;
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Mitt schema: avlösningsraderna gick inte att läsa."); }
        }
    }
}
