using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Controllers
{
    /// <summary>
    /// P4 (Stefan 2026-10-24, frågorna 13a och 13b): stationerna i fältskytte — vilken klubb eller krets som ansvarar för
    /// vilka stationer, stationscheferna ur bemanningen, och att ge stationscheferna tillgång till tävlingens
    /// fältkonfiguration (Stefan: "man kan bjuda in medlemmar till en konfiguration — det räcker"). Ingen ny hemlighetsregel.
    /// </summary>
    public partial class PlanningController
    {
        /// <summary>Tävlingen planeringen bemannar: egen koppling, annars huvudplaneringens.</summary>
        private HpskSite.Models.Competitions.CompetitionInfo? StationCompetition(PlanningRow p)
        {
            var link = _planning.LinkFor(p.Id) ?? (p.ParentPlanningId > 0 ? _planning.LinkFor(p.ParentPlanningId.Value) : null);
            return link == null ? null : _competitions.GetSaved(link.TargetId);
        }

        private static bool IsFalt(string? type) =>
            HpskSite.Models.Staffing.FunctionaryRoles.FaltFamily.Contains(type ?? "", StringComparer.OrdinalIgnoreCase);

        [HttpGet]
        public async Task<IActionResult> Stations(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var comp = StationCompetition(x.Planning);
            var falt = comp != null && IsFalt(comp.CompetitionType);
            var rows = _planning.Stations(id, falt ? comp!.NumberOfSeriesOrStations : 0);
            var me = x.MemberId;
            object? config = null;
            var configId = falt ? StationConfigId(comp) : null;
            if (configId > 0 && !x.Caller.ViaLink)
            {
                var svc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
                var cfg = await svc.GetByIdAsync(configId.Value);
                if (cfg != null)
                {
                    var collab = (await svc.GetCollaboratorsAsync(cfg.Id)).Select(c => c.MemberId).ToHashSet();
                    var chiefs = _planning.StationChiefMembers(id);
                    config = new
                    {
                        id = cfg.Id, name = cfg.Name, url = $"/faltkonfig/{cfg.Id}/redigera",
                        canEdit = await svc.CanEditAsync(cfg, me),
                        chiefs = chiefs.Select(c => new { memberId = c.MemberId, name = c.Name, stations = c.Station, hasAccess = c.MemberId == cfg.OwnerMemberId || collab.Contains(c.MemberId) }),
                    };
                }
            }
            return Json(new
            {
                success = true,
                competition = comp == null ? null : new { id = comp.Id, name = comp.Name, falt, stationCount = falt ? comp.NumberOfSeriesOrStations : 0 },
                config,
                stations = rows.Select(r => new
                {
                    number = r.Number, areaId = r.AreaId, area = r.AreaName,
                    host = r.HostClubId > 0 ? OrgName(DocumentOwnerType.Club, r.HostClubId) : r.HostRegionId > 0 ? OrgName(DocumentOwnerType.Region, r.HostRegionId) : r.HostText,
                    planningId = r.PlanningId, planning = r.PlanningId == id ? null : r.PlanningName,
                    chiefs = r.Chiefs, openTasks = r.OpenTasks,
                }),
            });
        }

        /// <summary>
        /// Lägger till planeringens stationschefer (medlemmar) som medarbetare i tävlingens fältkonfiguration. Den som frågar
        /// ska kunna ändra planeringen OCH konfigurationen — det är konfigurationens ägare som delar den.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GiveChiefsConfigAccess(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ger stationscheferna tillgång.");
            var comp = StationCompetition(x.Planning);
            var configId = comp != null && IsFalt(comp.CompetitionType) ? StationConfigId(comp) : null;
            if (!(configId > 0)) return No("Tävlingen har ingen ansluten fältkonfiguration.");
            var svc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
            var cfg = await svc.GetByIdAsync(configId.Value);
            if (cfg == null) return No("Fältkonfigurationen finns inte.");
            if (!await svc.CanEditAsync(cfg, x.MemberId))
                return No("Du kan inte dela fältkonfigurationen. Be den som äger den — eller någon som arbetar med den — att göra det.");
            var collab = (await svc.GetCollaboratorsAsync(cfg.Id)).Select(c => c.MemberId).ToHashSet();
            var added = new List<string>();
            foreach (var c in _planning.StationChiefMembers(id))
            {
                if (c.MemberId == cfg.OwnerMemberId || collab.Contains(c.MemberId)) continue;
                var (ok, _) = await svc.AddCollaboratorAsync(cfg.Id, c.MemberId);
                if (ok) added.Add(c.Name);
            }
            return Ok(new { success = true, added });
        }
    }
}
