using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    public partial class StaffingController
    {
        /// <summary>
        /// Steg 2 (Expertgruppen): familjens rutnät för huvudplaneringen — det gemensamma och varje del, var och en som sitt
        /// eget, oförändrade rutnät (<see cref="StaffingGridService.BuildGrid"/>), på en gemensam dagaxel. Varje rad och post
        /// bär sin planerings id (K6), och klienten skickar den med varje ändring; servern prövar då delen på nytt
        /// (<c>OwnerAsync</c> → <c>CanEditStaffingIn</c>). En del läsaren inte får ändra visas inte — den räknas bara.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetFamilyGrid(int competitionId)
        {
            var rootOwner = await OwnerAsync(competitionId);
            if (rootOwner is not { HasPlanning: true })
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var viewer = await ResolveViewerAsync();
                var root = _owners.Planning(rootOwner.PlanningId);
                if (root == null || root.ParentPlanningId > 0)
                    return Json(new { success = false, message = "Familjens rutnät visas i huvudplaneringen." });

                var resp = new FamilyGridResponse();
                foreach (var row in _planning.Family(root.Id))
                {
                    var isRoot = row.Id == root.Id;
                    if (viewer == null || !_planning.CanEditStaffingIn(row.Id, viewer.Id))
                    {
                        if (!isRoot) resp.HiddenParts++;
                        continue;
                    }
                    var owner = _owners.ForPlanning(row.Id);
                    if (owner == null) continue;
                    var discipline = _owners.Discipline(owner);
                    var grid = _grid.BuildGrid(owner, discipline, viewer.Id);
                    try { ApplyRoleChoice(grid, owner, discipline, row.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: role choice failed for planning {Planning}", row.Id); }
                    resp.Parts.Add(new FamilyGridPart
                    {
                        PlanningId = row.Id,
                        Name = row.Name,
                        IsShared = isRoot,
                        HostLabel = string.IsNullOrWhiteSpace(row.HostText) ? null : row.HostText.Trim(),
                        DayKeys = FamilyGridRules.DayKeys(grid.Columns),
                        Grid = grid,
                    });
                }

                resp.Columns = FamilyGridRules.UnionColumns(resp.Parts.Select(p => p.Grid.Columns));
                try
                {
                    var intents = HttpContext.RequestServices.GetRequiredService<ShootIntentService>();
                    resp.FamilyClashes = intents.Clashes(root.Id).Select(c => new GridClash
                    {
                        PersonKey = c.PersonKey, Name = c.Name, DateKey = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        A = c.A, B = c.B, Kind = c.Kind,
                    }).ToList();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Staffing: family clashes failed for planning {Planning}", root.Id); }
                return Json(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetFamilyGrid failed for planning {Planning}", rootOwner.PlanningId);
                return Json(new { success = false, message = "Kunde inte läsa familjens rutnät." });
            }
        }

        /// <summary>
        /// Steg 2 (Stefan B13): "Flytta raden till en annan del…". Radens id består. Rätten prövas i BÅDA delarna
        /// (<c>CanEditStaffingIn</c>), delarna ska höra till samma familj (B14: aldrig över familjegränsen) och rollen ska
        /// finnas i målet — en egen roll följer inte med av sig själv.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAssignmentToPlanning([FromBody] MoveAssignmentToPlanningRequest request)
        {
            if (request == null || request.Id <= 0 || request.TargetPlanningId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var from = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            var to = await RowOwnerAsync(_owners.ForPlanning(request.TargetPlanningId));
            if (from == null || to == null || !from.HasPlanning || !to.HasPlanning)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                if (!_planning.Family(from.PlanningId).Any(p => p.Id == to.PlanningId))
                    return Json(new { success = false, message = "Uppdraget kan bara flyttas inom samma huvudplanering." });
                var row = _staffing.GetById(request.Id);
                if (row == null) return Json(new { success = false, message = "Uppdraget hittades inte." });
                if (!_roleCatalog.For(to, _owners.Discipline(to)).Any(r => string.Equals(r.Key, row.RoleKey, StringComparison.OrdinalIgnoreCase)))
                    return Json(new { success = false, message = "Rollen finns inte i den delen. Skriv rollen där först." });
                var (ok, msg, revoked) = _staffing.MoveToPlanning(request.Id, from, to);
                if (ok && revoked)
                    _logger.LogInformation("Staffing: assignment {Id} moved from planning {From} to {To}; app access in the competition ended", request.Id, from.PlanningId, to.PlanningId);
                return Json(new { success = ok, message = msg, accessEnded = ok && revoked });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: MoveAssignmentToPlanning failed for {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte flytta uppdraget." });
            }
        }
    }
}
