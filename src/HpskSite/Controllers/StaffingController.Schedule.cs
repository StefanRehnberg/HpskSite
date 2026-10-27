using System.Globalization;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Skiftschemat per ruta (Stefan 2026-10-27): rollen och dagen har ett schema med vem som står när, i stället för att man delar ett pass på
    /// personens sida. Här: att spara schemat (alla ändringar eller inga), att välja att en roll ska vara bemannad hela skjuttiden, och att
    /// markera hålen mot skjuttiden som rutnätet visar.
    /// </summary>
    public partial class StaffingController
    {
        /// <summary>Sparar en rutas skift i en transaktion. De som redan fått förfrågan och får ändrad tid får besked.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCellShifts([FromBody] SaveCellShiftsRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            if (_roleCatalog.Resolve(owner, _owners.Discipline(owner), request.RoleKey) == null)
                return Json(new { success = false, message = "Rollen finns inte." });
            try
            {
                var r = _staffing.SaveCellShifts(owner, request, viewer.Id);
                if (!r.Ok) return Json(new { success = false, message = r.Message });
                var told = 0;
                var notifier = HttpContext.RequestServices.GetRequiredService<StaffReplacementService>();
                foreach (var c in r.Changed.Where(c => c.Row.MemberId is > 0
                    && (string.Equals(c.Row.Status, StaffAssignmentStatus.Invited, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Row.Status, StaffAssignmentStatus.Accepted, StringComparison.OrdinalIgnoreCase))))
                    if (await notifier.NotifyTimeChangedAsync(c.Row, c.Before, viewer.Id)) told++;
                foreach (var p in r.Replaced)
                    if (await notifier.NotifyAsync(p.Row, p.OldName, p.OldMemberId, viewer.Id)) told++;
                return Json(new { success = true, notified = told });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SaveCellShifts failed for {Role} {Date}", request.RoleKey, request.Date);
                return Json(new { success = false, message = "Kunde inte spara skiften." });
            }
        }

        public class SetRoleWholeDayRequest
        {
            public int CompetitionId { get; set; }
            public string RoleKey { get; set; } = "";
            public bool On { get; set; }
        }

        /// <summary>Slår "ska vara bemannad hela skjuttiden" på eller av för en roll. Av som standard — en klubbtävling märker ingenting.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRoleWholeDay([FromBody] SetRoleWholeDayRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            if (_roleCatalog.Resolve(owner, _owners.Discipline(owner), request.RoleKey) == null)
                return Json(new { success = false, message = "Rollen finns inte." });
            try
            {
                _planning.SetRoleWholeDay(owner.PlanningId, request.RoleKey.Trim(), request.On, viewer.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetRoleWholeDay failed for planning {Planning}", owner.PlanningId);
                return Json(new { success = false, message = "Kunde inte spara valet." });
            }
        }

        /// <summary>
        /// Lägger skjuttiden (ur startlistan) på rutnätets dagar och, för roller som ska vara bemannade hela skjuttiden, ersätter dagens hål med hålen mot
        /// skjuttiden: "Stationschef station 3: ingen efter 11:00". Roller som inte valts mäts som förut — bara mellan sina egna skift. Ett fel
        /// här får aldrig fälla rutnätet.
        /// </summary>
        private void ApplyShiftSchedule(GridResponse grid, StaffOwner owner, PlanningRow? planning)
        {
            try
            {
                var windows = HttpContext.RequestServices.GetRequiredService<StaffBusyReader>().DayWindows(owner, _owners.ShootTime(owner.PlanningId));
                foreach (var c in grid.Columns)
                    if (windows.TryGetValue(c.Key, out var w)) { c.WindowFrom = ShiftScheduleRules.Hm(w.From); c.WindowTo = ShiftScheduleRules.Hm(w.To); }
                var roles = ShiftScheduleRules.RoleSet(planning?.WholeDayRoles);
                grid.WholeDayRoles = roles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (roles.Count == 0) return;
                foreach (var row in grid.Rows.Where(r => roles.Contains(r.RoleKey)))
                    foreach (var c in grid.Columns)
                    {
                        if (string.IsNullOrEmpty(c.Key) || !windows.TryGetValue(c.Key, out var w)) continue;
                        row.Cells.TryGetValue(c.Key, out var entries);
                        entries ??= new();
                        var need = row.NeedFor(c.Key);
                        if (need == 0 && row.HasNeed) continue;
                        if (!row.HasNeed && entries.Count == 0) continue;
                        var spans = entries.Where(e => !string.Equals(e.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))
                            .Select(Span).ToList();
                        var holes = ShiftScheduleRules.Holes(spans, new ShiftScheduleRules.Span(w.From, w.To), Math.Max(1, need));
                        row.Gaps.RemoveAll(g => g.DateKey == c.Key);
                        foreach (var h in holes)
                            row.Gaps.Add(new GridGap { DateKey = c.Key, From = ShiftScheduleRules.Hm(h.Start), To = ShiftScheduleRules.Hm(h.End) });
                    }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: shift schedule failed for planning {Planning}", owner.PlanningIdOrNull); }
        }

        /// <summary>Spannet en post står på: egna klockslag, passets tid, annars hela dagen (null).</summary>
        private static ShiftScheduleRules.Span? Span(GridEntry e)
        {
            if (e.From != null && e.To != null && HpskSite.Models.ClockTime.ToTimeSpan(e.From) is { } a && HpskSite.Models.ClockTime.ToTimeSpan(e.To) is { } b) return new ShiftScheduleRules.Span(a, b);
            return StaffTimeCoverage.ParseRange(e.TimeLabel) is { } r ? new ShiftScheduleRules.Span(r.Start, r.End) : null;
        }
    }
}
