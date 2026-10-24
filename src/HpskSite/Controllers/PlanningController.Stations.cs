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
            // Banan i planeringen (2026-10-24): antalet stationer ur tävlingen, banan och områdena — det största.
            var courseSvc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            var course = x.Caller.ViaLink ? null : await courseSvc.CourseConfigAsync(id);
            var rows = _planning.Stations(id, courseSvc.StationCount(id, course?.JsonBlob));
            var me = x.MemberId;
            object? courseJson = null;
            if (course != null)
            {
                var csvc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
                var canView = await csvc.CanViewAsync(course, me);
                var parts = await courseSvc.PartsAsync(id, course.JsonBlob);
                var total = courseSvc.StationCount(id, course.JsonBlob);
                var built = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.StationCount(course.JsonBlob);
                var attachedId = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.AttachedId(comp?.StationConfig);
                courseJson = new
                {
                    id = course.Id, name = course.Name, url = $"/faltkonfig/{course.Id}/redigera", status = course.ApprovalStatus ?? "Draft",
                    canView, canEdit = canView && await csvc.CanEditAsync(course, me),
                    assembledAt = CourseAssembly.AssembledAt(course.JsonBlob),
                    stationCount = total, builtStations = built,
                    parts = parts.Select(p => new { id = p.ConfigId, name = p.Name, status = p.Status, stations = p.Stations, state = p.State }),
                    // Tävlingens kopia: ansluten och aktuell, ansluten men äldre, en annan bana, eller ingen.
                    competition = falt ? new
                    {
                        attached = attachedId == course.Id,
                        stale = attachedId == course.Id
                            && HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.Differs(comp!.StationConfig, course.JsonBlob),
                        hasOther = attachedId != course.Id
                            && HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.StationCount(comp!.StationConfig) > 0,
                        canAttach = await _auth.CanEditCompetitionAsync(comp!.Id),
                    } : null,
                };
            }
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
                course = courseJson,
                canCreateCourse = course == null && PlanningAccess.CanEditPlanning(x.Caller) && !x.Caller.ViaLink,
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
            // Banan i planeringen (2026-10-24): planeringens bana först — den är det föreningarna bygger vidare på.
            var courseCfg = await HttpContext.RequestServices.GetRequiredService<PlanningCourseService>().CourseConfigAsync(id);
            if (courseCfg != null && HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.StationCount(courseCfg.JsonBlob) > 0)
                source = courseCfg.JsonBlob;
            var configId = source == null && falt ? StationConfigId(comp) : null;
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
            // Banan i planeringen (2026-10-24): områdets ansvarige bygger delen — även en som bara deltar i planeringen.
            var resp = area.Area.ResponsibleParticipantId > 0 ? _planning.Participant(id, area.Area.ResponsibleParticipantId!.Value) : null;
            if (resp is { IsActive: true, MemberId: > 0 } && resp.MemberId != x.MemberId)
            {
                var (respOk, _) = await svc.AddCollaboratorAsync(created.Id, resp.MemberId!.Value);
                if (respOk) added.Add(resp.DisplayName);
            }
            foreach (var c in _planning.StationChiefMembers(id).Where(c => ChiefStations(c.Station).Intersect(stations).Any()))
            {
                if (c.MemberId == x.MemberId || c.MemberId == resp?.MemberId) continue;
                var (addedOk, _) = await svc.AddCollaboratorAsync(created.Id, c.MemberId);
                if (addedOk) added.Add(c.Name);
            }
            return Ok(new { success = true, id = created.Id, url = $"/faltkonfig/{created.Id}/redigera", copied = source != null, added });
        }

        /// <summary>
        /// Banan i planeringen (Stefan 2026-10-24): planeringen äger banans fältkonfiguration innan tävlingen finns. Den hör
        /// till familjens huvudplanering. <paramref name="stationCount"/>: hur många stationer banan har (annars ur tävlingen
        /// eller områdena).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateCourseConfig(int id, int? stationCount)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som skapar banans fältkonfiguration.");
            if (stationCount is < 1 or > PlanningStations.Max) return No($"Banan kan ha 1–{PlanningStations.Max} stationer.");
            var svc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            var (cfg, err) = await svc.CreateCourseAsync(x.Planning, x.MemberId, stationCount ?? 0);
            return cfg == null ? No(err ?? "Banans fältkonfiguration kunde inte skapas.") : Ok(new { success = true, id = cfg.Id, url = $"/faltkonfig/{cfg.Id}/redigera" });
        }

        /// <summary>"Sätt ihop banan": områdenas delkonfigurationer läggs ihop till banans fältkonfiguration.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssembleCourse(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som sätter ihop banan.");
            var r = await HttpContext.RequestServices.GetRequiredService<PlanningCourseService>().AssembleAsync(x.Planning, x.MemberId);
            if (r.Error != null) return No(r.Error);
            return Ok(new
            {
                success = true, changed = r.Changed, url = $"/faltkonfig/{r.ConfigId}/redigera",
                warnings = r.Result?.Warnings ?? new List<string>(), missing = r.Result?.Missing ?? new List<int>(),
                fromCourse = r.Result?.FromCourse ?? new List<int>(), stationCount = r.Result?.StationCount ?? 0,
            });
        }

        /// <summary>Ansluter banan till den kopplade fälttävlingen (en kopia, som i Redigera tävling).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AttachCourseToCompetition(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ansluter banan.");
            var svc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            var comp = svc.FaltCompetition(id);
            if (comp == null) return No("Planeringen är inte kopplad till någon fälttävling.");
            if (!await _auth.CanEditCompetitionAsync(comp.Id)) return No("Du behöver kunna redigera tävlingen för att ansluta banan till den.");
            var err = await svc.AttachToCompetitionAsync(id, comp.Id, onlyIfEmpty: false);
            return err == null ? Ok(new { success = true, competition = comp.Name }) : No(err);
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
