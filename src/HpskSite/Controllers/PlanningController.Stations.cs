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
            // Omgång 3 (3.3): ett områdes delkonfiguration — en egen fältkonfiguration för områdets stationer.
            var areaConfigs = new List<object>();
            if (!x.Caller.ViaLink)
            {
                var svc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
                foreach (var l in _planning.AreaConfigLinks(id))
                {
                    var cfg = await svc.GetByIdAsync(l.ConfigId);
                    if (cfg == null) continue;
                    var canView = await svc.CanViewAsync(cfg, me);
                    areaConfigs.Add(new
                    {
                        areaId = l.AreaId, planningId = l.PlanningId, id = cfg.Id, name = cfg.Name, url = $"/faltkonfig/{cfg.Id}/redigera",
                        status = cfg.ApprovalStatus ?? "Draft", canView, canEdit = canView && await svc.CanEditAsync(cfg, me),
                        stations = HpskSite.Models.Planning.AreaConfigBlob.CourseStations(cfg.JsonBlob),
                    });
                }
            }
            return Json(new
            {
                success = true,
                competition = comp == null ? null : new { id = comp.Id, name = comp.Name, falt, stationCount = falt ? comp.NumberOfSeriesOrStations : 0 },
                config,
                areaConfigs,
                canCreateAreaConfig = PlanningAccess.CanEditPlanning(x.Caller) && !x.Caller.ViaLink,
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
        /// Omgång 3 (Expertgruppen 3.3, "arkitektens väg"): en fältkonfiguration för ETT områdes stationer, så att en krets
        /// eller klubb som har stationerna 3–6 kan bygga, få godkända och skriva ut sina egna stationer. Har tävlingen en
        /// ansluten konfiguration kopieras områdets stationer därifrån; annars börjar den tom. Den som skapar äger den och
        /// områdets stationschefer blir medarbetare. Konfigurationen numrerar 1..N och bär banans nummer i
        /// <c>_courseStations</c>.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateAreaConfig(int id, int areaId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som skapar områdets fältkonfiguration.");
            var area = _planning.AreaWithStations(id, areaId);
            if (area == null) return No("Området finns inte i planeringen.");
            if (area.Stations.Count == 0) return No("Ange områdets stationer först.");
            if (_planning.AreaConfigLinks(id).Any(l => l.AreaId == areaId)) return No("Området har redan en delkonfiguration.");
            var comp = StationCompetition(x.Planning);
            var falt = comp != null && IsFalt(comp.CompetitionType);
            var svc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
            string? source = null;
            var configId = falt ? StationConfigId(comp) : null;
            if (configId > 0)
            {
                var src = await svc.GetByIdAsync(configId.Value);
                if (src != null && await svc.CanViewAsync(src, x.MemberId)) source = src.JsonBlob;
            }
            // Utan ansluten konfiguration: tävlingens egen stationskonfiguration (en äldre tävling har den inline).
            if (source == null && falt && configId == null) source = comp!.StationConfig;
            var stations = area.Stations.OrderBy(n => n).ToList();
            var name = $"{x.Planning.Name} – {area.Area.Name} (station {string.Join(", ", stations)})";
            var (ok, msg, created) = await svc.CreateAsync(new HpskSite.CompetitionTypes.Faltskytte.Models.CreateFaltskytteConfigurationRequest
            {
                Name = name.Length > 200 ? name[..200] : name,
                Description = $"Delkonfiguration för området {area.Area.Name} i planeringen {x.Planning.Name}.",
                Visibility = "Private",
                OwnerClubId = area.Area.HostClubId,
                JsonBlob = HpskSite.Models.Planning.AreaConfigBlob.Build(source, stations),
            }, x.MemberId);
            if (!ok || created == null) return No(msg ?? "Fältkonfigurationen kunde inte skapas.");
            await svc.SetFirstStationNumberAsync(created.Id, stations[0]);
            var linkError = _planning.LinkAreaConfig(id, areaId, created.Id, x.MemberId);
            if (linkError != null) { await svc.DeleteAsync(created.Id); return No(linkError); }
            var added = new List<string>();
            foreach (var c in _planning.StationChiefMembers(id).Where(c => ChiefStations(c.Station).Intersect(stations).Any()))
            {
                if (c.MemberId == x.MemberId) continue;
                var (addedOk, _) = await svc.AddCollaboratorAsync(created.Id, c.MemberId);
                if (addedOk) added.Add(c.Name);
            }
            return Ok(new { success = true, id = created.Id, url = $"/faltkonfig/{created.Id}/redigera", copied = source != null, added });
        }

        private static IEnumerable<int> ChiefStations(string stations) =>
            (stations ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, out var n) ? n : 0).Where(n => n > 0);

        /// <summary>
        /// Lägger till planeringens stationschefer (medlemmar) som medarbetare i tävlingens fältkonfiguration. Den som frågar
        /// ska kunna ändra planeringen OCH konfigurationen — det är konfigurationens ägare som delar den.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GiveChiefsConfigAccess(int id, int? areaId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ger stationscheferna tillgång.");
            int? configId;
            IEnumerable<int>? onlyStations = null;
            if (areaId > 0)
            {
                // Omgång 3 (3.3): områdets delkonfiguration delas med områdets stationschefer — inte med hela banans.
                var link = _planning.AreaConfigLinks(id).FirstOrDefault(l => l.AreaId == areaId);
                if (link == null) return No("Området har ingen delkonfiguration.");
                configId = link.ConfigId;
                onlyStations = _planning.AreaWithStations(link.PlanningId, link.AreaId)?.Stations ?? new List<int>();
            }
            else
            {
                var comp = StationCompetition(x.Planning);
                configId = comp != null && IsFalt(comp.CompetitionType) ? StationConfigId(comp) : null;
                if (!(configId > 0)) return No("Tävlingen har ingen ansluten fältkonfiguration.");
            }
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
                if (onlyStations != null && !ChiefStations(c.Station).Intersect(onlyStations).Any()) continue;
                var (ok, _) = await svc.AddCollaboratorAsync(cfg.Id, c.MemberId);
                if (ok) added.Add(c.Name);
            }
            return Ok(new { success = true, added });
        }
    }
}
