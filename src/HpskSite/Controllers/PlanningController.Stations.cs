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

        /// <summary>
        /// Var en stations förening hör hemma, sett från planeringen <paramref name="viewer"/> (Expertgruppen 2026-10-24 F4):
        /// null = här, annars "Delplanering: X" eller "Huvudplaneringen: X".
        /// </summary>
        private string? FamilyLabel(PlanningRow viewer, int ownerPlanningId)
        {
            if (ownerPlanningId == viewer.Id) return null;
            var owner = _planning.Get(ownerPlanningId);
            if (owner == null) return null;
            return owner.Id == viewer.ParentPlanningId ? "Huvudplaneringen: " + owner.Name : "Delplanering: " + owner.Name;
        }

        /// <summary>
        /// Stefan 2026-10-25: stationerna har EN plats (<see cref="PlanningService.StationOwnerId"/>) — det som gäller hela
        /// stationsuppsättningen görs där. Null = här går det.
        /// </summary>
        private string? WholeStationsElsewhere(PlanningRow p)
        {
            var ownerId = _planning.StationOwnerId(p.Id);
            if (ownerId == 0 || ownerId == p.Id) return null;
            var owner = _planning.Get(ownerId);
            return $"Stationerna sköts i {(owner?.ParentPlanningId > 0 ? "delplaneringen" : "huvudplaneringen")}{(owner == null ? "" : " " + owner.Name)}.";
        }

        /// <summary>Stationernas planering, för sidan: id, namn och om det är den här.</summary>
        private object StationsOwnerJson(PlanningRow p)
        {
            var ownerId = _planning.StationOwnerId(p.Id);
            var owner = ownerId > 0 && ownerId != p.Id ? _planning.Get(ownerId) : null;
            return new
            {
                here = ownerId == p.Id,
                id = ownerId,
                name = owner?.Name,
                isSub = owner?.ParentPlanningId > 0,
                url = owner == null ? null : $"/tavlingsplanering/{owner.Id}#vy=stationer",
            };
        }

        private static string NonEmpty(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

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
            // Stefan 2026-10-25: planeringens stationer ligger i planeringens projekt i Fältkonfiguratorn — också de som
            // skapades innan projektet fanns.
            if (course != null && !(course.ProjectId > 0) && PlanningAccess.CanEditPlanning(x.Caller))
                await courseSvc.EnsureProjectAsync(id, course, x.MemberId);
            var rows = _planning.Stations(id, courseSvc.StationCount(id, course?.JsonBlob));
            var me = x.MemberId;
            object? courseJson = null;
            if (course != null)
            {
                var csvc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
                var canView = await csvc.CanViewAsync(course, me);
                var total = courseSvc.StationCount(id, course.JsonBlob);
                var built = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.StationCount(course.JsonBlob);
                var attachedId = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.AttachedId(comp?.StationConfig);
                courseJson = new
                {
                    id = course.Id, name = course.Name, url = $"/faltkonfig/{course.Id}/redigera", status = course.ApprovalStatus ?? "Draft",
                    canView, canEdit = canView && await csvc.CanEditAsync(course, me),
                    assembledAt = CourseAssembly.AssembledAt(course.JsonBlob),
                    stationCount = total, builtStations = built,
                    approvedStations = Enumerable.Range(1, Math.Max(total, 0)).Count(n => StationBlob.Current(course.JsonBlob, n)?.Status == StationBlob.Approved),
                    projectUrl = course.ProjectId > 0 ? "/faltkonfig" : null, // konfiguratorns lista grupperar per projekt
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
            var builtSet = new SortedSet<int>(CourseAssembly.BuiltStations(course?.JsonBlob));
            var canEditPlanning = PlanningAccess.CanEditPlanning(x.Caller) && !x.Caller.ViaLink;
            // Stefan 2026-10-25: de ansvariga — en rad per ansvarig med sina stationer (PlanningStationResponsible).
            var people = _planning.Participants(_planning.StationOwnerId(id)).ToDictionary(p => p.Id);
            string WhoName(PlanningStationResponsibleRow r) =>
                r.Kind == PlanningStationResponsibleRow.KindPerson && r.ParticipantId is int pid && people.TryGetValue(pid, out var p) ? p.DisplayName
                : r.Kind == PlanningStationResponsibleRow.KindClub && r.HostId > 0 ? NonEmpty(OrgName(DocumentOwnerType.Club, r.HostId), r.Name)
                : r.Kind == PlanningStationResponsibleRow.KindRegion && r.HostId > 0 ? NonEmpty(OrgName(DocumentOwnerType.Region, r.HostId), r.Name)
                : r.Name;
            var responsibles = rows.Where(r => r.Responsible != null).Select(r => r.Responsible!).GroupBy(r => r.Key).Select(g =>
            {
                var f = g.First();
                var contact = f.Kind != PlanningStationResponsibleRow.KindPerson && f.ParticipantId is int cid && people.TryGetValue(cid, out var c) ? c : null;
                return new
                {
                    key = g.Key, kind = f.Kind, name = WhoName(f), participantId = f.ParticipantId, hostId = f.HostId,
                    contactId = contact?.Id, contact = contact?.DisplayName,
                    // En person som lämnat planeringen kan inte ändra stationerna — det ska synas.
                    left = f.ParticipantId is int lp && !people.ContainsKey(lp),
                    stations = g.Select(r => r.Station).OrderBy(n => n).ToList(),
                    editable = canEditPlanning,
                };
            }).OrderBy(r => r.stations.First()).ToList();
            var urls = x.Caller.ViaLink ? new Dictionary<int, string>() : await StationUrlsAsync(id, me, falt ? StationConfigId(comp) : null);
            return Json(new
            {
                success = true,
                competition = comp == null ? null : new { id = comp.Id, name = comp.Name, falt, stationCount = falt ? comp.NumberOfSeriesOrStations : 0 },
                config,
                responsibles,
                course = courseJson,
                stationCount = courseSvc.StationCount(id, course?.JsonBlob),
                canCreateCourse = course == null && canEditPlanning && WholeStationsElsewhere(x.Planning) == null,
                wholeElsewhere = WholeStationsElsewhere(x.Planning),
                stations = rows.Select(r => new
                {
                    number = r.Number,
                    responsible = r.Responsible == null ? null : WhoName(r.Responsible), responsibleKey = r.Responsible?.Key,
                    planningId = r.PlanningId,
                    chiefs = r.Chiefs, openTasks = r.OpenTasks,
                    // Godkännandet per station (Stefan 2026-10-25): Pending / Approved / Returned, null = utkast.
                    approval = course == null ? null : StationBlob.Current(course.JsonBlob, r.Number)?.Status,
                    approvalComment = course == null ? null : StationBlob.Current(course.JsonBlob, r.Number)?.Comment,
                    built = builtSet.Contains(r.Number), url = urls.GetValueOrDefault(r.Number),
                }),
            });
        }

        /// <summary>
        /// Stefan 2026-10-25: "Ge stationer en ansvarig". <paramref name="who"/> = <c>p:{deltagare}</c> (en person i
        /// planeringen), <c>h:{0=klubb|1=krets}:{id}</c> eller <c>text</c> (<paramref name="name"/>). <paramref name="contactId"/> =
        /// personen i planeringen som utformar stationerna åt en klubb, krets eller ett namn. <paramref name="release"/> =
        /// stationer samma ansvarige hade och inte längre ska ha. Inget område skapas.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStationResponsible(int id, string? stations, string? who, int? contactId, string? name, string? release)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som väljer stationernas ansvariga.");
            if (WholeStationsElsewhere(x.Planning) is { } elsewhere) return No(elsewhere);
            var (list, perr) = PlanningStations.Parse(stations ?? "");
            if (perr != null) return No(perr);
            var (rel, rerr) = PlanningStations.Parse(release ?? "");
            if (rerr != null) return No(rerr);
            PlanningService.StationWho w;
            var v = (who ?? "").Trim();
            if (v.StartsWith("p:") && int.TryParse(v[2..], out var pid) && pid > 0)
            {
                var p = _planning.Participant(id, pid);
                if (p == null || !p.IsActive) return No("Den ansvarige finns inte i planeringen.");
                w = new(PlanningStationResponsibleRow.KindPerson, pid, null, p.DisplayName);
            }
            else if (v.StartsWith("h:"))
            {
                var parts = v.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[1], out var ht) || !int.TryParse(parts[2], out var hid) || hid <= 0) return No("Välj klubben eller kretsen.");
                var hostType = ht;
                if (!BoardTargets(id).Any(t => t.OwnerType == hostType && t.OwnerId == hid))
                    return No("Välj en klubb eller krets som någon i planeringen företräder, eller skriv namnet.");
                w = new(hostType == DocumentOwnerType.Club ? PlanningStationResponsibleRow.KindClub : PlanningStationResponsibleRow.KindRegion,
                    contactId > 0 ? contactId : null, hid, OrgName(hostType, hid));
            }
            else if (v == "text") w = new(PlanningStationResponsibleRow.KindName, contactId > 0 ? contactId : null, null, name ?? "");
            else return No("Välj vem som är ansvarig.");
            var courseSvc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            // Taket gäller bara när antalet är KÄNT (stationernas konfiguration eller en kopplad fälttävling) — annars räknas
            // StationCount ur stationerna som redan har en ansvarig, och nästa station vägrades (p4 hittade det).
            var course = await courseSvc.CourseConfigAsync(id);
            var max = course != null || courseSvc.FaltCompetition(id) != null ? courseSvc.StationCount(id, course?.JsonBlob) : 0;
            var err = _planning.SetStationResponsible(id, list, rel, w, max, x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>Stationerna blir utan ansvarig ("Ta bort som ansvarig", "Ingen ansvarig").</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearStationResponsible(int id, string? stations)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som väljer stationernas ansvariga.");
            if (WholeStationsElsewhere(x.Planning) is { } elsewhere) return No(elsewhere);
            var (list, perr) = PlanningStations.Parse(stations ?? "");
            if (perr != null) return No(perr);
            var err = _planning.ClearStationResponsible(id, list);
            return err == null ? Ok() : No(err);
        }

        /// <summary>"Ändra antalet stationer" (Stationerna-vyn).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStationCount(int id, int stationCount)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ändrar antalet stationer.");
            if (WholeStationsElsewhere(x.Planning) is { } elsewhere) return No(elsewhere);
            var err = await HttpContext.RequestServices.GetRequiredService<PlanningCourseService>().SetStationCountAsync(x.Planning, x.MemberId, stationCount);
            return err == null ? Ok(new { success = true }) : No(err);
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
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som skapar stationerna.");
            if (WholeStationsElsewhere(x.Planning) is { } elsewhere) return No(elsewhere);
            if (stationCount is < 1 or > PlanningStations.Max) return No($"Tävlingen kan ha 1–{PlanningStations.Max} stationer.");
            var svc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            var (cfg, err) = await svc.CreateCourseAsync(x.Planning, x.MemberId, stationCount ?? 0);
            return cfg == null ? No(err ?? "Stationerna kunde inte skapas.") : Ok(new { success = true, id = cfg.Id, url = $"/faltkonfig/{cfg.Id}/redigera" });
        }

        /// <summary>Ansluter banan till den kopplade fälttävlingen (en kopia, som i Redigera tävling).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AttachCourseToCompetition(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ansluter stationerna.");
            var svc = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
            var comp = svc.FaltCompetition(id);
            if (comp == null) return No("Planeringen är inte kopplad till någon fälttävling.");
            if (!await _auth.CanEditCompetitionAsync(comp.Id)) return No("Du behöver kunna redigera tävlingen för att ansluta stationerna till den.");
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
        public async Task<IActionResult> GiveChiefsConfigAccess(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som ger stationscheferna tillgång.");
            var comp = StationCompetition(x.Planning);
            int? configId = comp != null && IsFalt(comp.CompetitionType) ? StationConfigId(comp) : null;
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
