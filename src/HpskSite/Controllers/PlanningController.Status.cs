using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Lägesrapporten (Anders, persontest 11): en skrivskyddad sammanställning över huvudplaneringens familj — siffror i meningar, milstolpar per månad. Den gäller den som
    /// får LÄSA huvudplaneringen (också nivån Följer), så den är ingen väg in i delarnas innehåll: en del läsaren inte själv får läsa visar bara siffror, aldrig uppgiftstitlar.
    /// Allt räknas vid anropet; ingenting lagras. Bemanningen räknas av samma rutnät som bandet (<see cref="StaffingPlaces"/>).
    /// </summary>
    public partial class PlanningController
    {
        [HttpGet]
        public async Task<IActionResult> FamilyStatus(int id)
        {
            var x = await ResolveAsync(id, null);   // utan mejllänk: rapporten är för inloggade deltagare
            if (x == null) return Missing();
            if (x.Planning.ParentPlanningId > 0) return No("Lägesrapporten finns i huvudplaneringen.");
            var today = DateTime.Now.Date;
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var owners = HttpContext.RequestServices.GetRequiredService<StaffOwnerResolver>();
            var grid = HttpContext.RequestServices.GetRequiredService<StaffingGridService>();
            var parts = new List<StatusPart>();
            var milestones = new List<StatusMilestone>();
            var family = _planning.Family(id);
            if (family.Count <= 1) return No("Lägesrapporten finns när planeringen har delplaneringar.");
            foreach (var part in family)
            {
                var readable = part.Id == id
                    || (PlanningFamily.ReadsSubViaParent(x.Caller.Participant) && !x.Caller.ViaLink)
                    || (_planning.ParticipantForMember(part.Id, x.MemberId) is { IsActive: true });
                var tasks = _planning.Tasks(part.Id);
                var open = tasks.Where(t => PlanningTaskStatus.IsOpen(t.Status)).ToList();
                int? needed = null, missing = null;
                var unlinked = 0;
                try
                {
                    var owner = owners.ForPlanning(part.Id);
                    if (owner != null)
                    {
                        var g = grid.BuildGrid(owner, owners.Discipline(owner), 0);
                        var (n, m, _) = StaffingPlaces.Count(g);
                        if (n > 0) { needed = n; missing = m; }
                        unlinked = StaffingPlaces.Unlinked(g);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: bemanningen i delen {Part} kunde inte räknas till lägesrapporten.", id, part.Id); }
                parts.Add(new StatusPart(part.Id == id ? part.Name + " (huvudplaneringen)" : part.Name, readable, open.Count, tasks.Count,
                    open.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < today), open.Count(t => t.Status == PlanningTaskStatus.NeedsHelp), needed, missing, unlinked));
                if (readable)
                    milestones.AddRange(open.Where(t => t.IsMilestone && t.DueDate.HasValue)
                        .Select(t => new StatusMilestone(part.Name, t.Title, t.DueDate!.Value.Date, t.DueDate.Value.Date < today)));
            }
            return Json(new
            {
                success = true,
                title = x.Planning.Name,
                headline = PlanningStatusRules.Headline(today, parts),
                parts = parts.Select(p => new { name = p.Name, line = PlanningStatusRules.PartLine(p), readable = p.Readable, overdue = p.Overdue, open = p.Open, total = p.Total, placesNeeded = p.PlacesNeeded, placesMissing = p.PlacesMissing }),
                months = PlanningStatusRules.ByMonth(milestones).Select(g => new
                {
                    label = PlanningStatusRules.MonthLabel(g.Month),
                    items = g.Items.Select(m => new { part = m.Part, title = m.Title, due = m.Due.ToString("d MMM", sv).TrimEnd('.'), overdue = m.Overdue }),
                }),
            });
        }
    }
}
