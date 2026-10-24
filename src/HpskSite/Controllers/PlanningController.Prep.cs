using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HpskSite.Models.Competitions;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    /// <summary>
    /// P2 (Stefan 2026-10-23): förberedelserna i planeringen — kopplingen till tävlingen, tävlingens egen
    /// planering, områden, dokument, kommentarer, mallar, kretsens checklista, stationer, inklistring,
    /// utskrift/CSV och påminnelse om försenade. Se notes/tavlingsplanering-p2-plan-2026-10-23.md.
    /// </summary>
    public partial class PlanningController
    {
        // =====================================================================================
        //  Tävlingen planeringen hör till
        // =====================================================================================

        private CompetitionInfo? LinkedCompetition(int planningId)
        {
            var link = _planning.LinkFor(planningId);
            return link == null ? null : _competitions.GetSaved(link.TargetId);
        }

        /// <summary>Ankaret för "dagar före": den kopplade tävlingens dag, annars planeringens startdag.</summary>
        private DateTime? AnchorFor(PlanningRow p, CompetitionInfo? comp) =>
            PlanningTaskRules.Anchor(comp?.CompetitionDate, p.TargetStart);

        private object? CompetitionJson(int planningId)
        {
            var link = _planning.LinkFor(planningId);
            if (link == null) return null;
            var comp = _competitions.GetSaved(link.TargetId);
            if (comp == null) return new { id = link.TargetId, name = "Tävlingen finns inte längre", url = "", date = "", typeId = "", planningUrl = "", isFalt = false };
            var type = comp.TypeId ?? "";
            return new
            {
                id = comp.Id, name = comp.DisplayName, url = _competitionLinks.Url(comp.Id),
                date = D(comp.CompetitionDate), endDate = D(comp.CompetitionEndDate), typeId = type,
                planningUrl = $"/tavlingsplanering?c={comp.Id}",
                manageUrl = $"/competitionmanagement?competitionId={comp.Id}",
                isFalt = type is "Faltskytte" or "MagnumFalt",
                stationCount = type is "Faltskytte" or "MagnumFalt" ? comp.NumberOfSeriesOrStations : 0,
            };
        }

        /// <summary>
        /// Kortet på tävlingens sida (<c>/tavlingsplanering?c=</c>): finns tävlingens planering, och vilka egna
        /// planeringar kan kopplas? Läser bara — ingenting skapas när sidan visas.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CompetitionState(int competitionId)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return No("Du har inte behörighet att planera den här tävlingen.");
            var comp = _competitions.GetSaved(competitionId);
            if (comp == null) return No("Tävlingen finns inte.");
            var p = _planning.ForCompetition(competitionId);
            object? planning = null;
            if (p != null)
            {
                var tasks = _planning.Tasks(p.Id);
                var today = DateTime.Today;
                planning = new
                {
                    id = p.Id, name = p.Name, url = $"/tavlingsplanering/{p.Id}#vy=uppgifter",
                    open = tasks.Count(t => PlanningTaskStatus.IsOpen(t.Status)),
                    overdue = tasks.Count(t => PlanningTaskRules.IsOverdue(t, today)),
                    done = tasks.Count(t => t.Status == PlanningTaskStatus.Done),
                    total = tasks.Count,
                };
            }
            // Egna planeringar som kan kopplas: där jag är Sammankallande och som inte redan är kopplade.
            var mine = _planning.ForMember(me).Where(m => m.Access == PlanningAccessLevel.Owner && m.Status == "Active").ToList();
            var linked = _planning.LinkedCompetitions(mine.Select(m => m.PlanningId));
            var canEdit = await _auth.CanEditCompetitionAsync(competitionId);
            return Json(new
            {
                success = true,
                competition = new { id = comp.Id, name = comp.DisplayName, date = D(comp.CompetitionDate) },
                planning,
                oldPrepCount = p == null ? _planning.OldPrepCount(competitionId) : 0,
                canLink = canEdit && p == null,
                linkable = p == null && canEdit
                    ? mine.Where(m => !linked.ContainsKey(m.PlanningId)).Select(m => new { id = m.PlanningId, name = m.Name })
                    : Enumerable.Empty<object>(),
            });
        }

        /// <summary>Startar tävlingens egen planering och flyttar in de gamla förberedelserna.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartForCompetition(int competitionId)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return No("Du har inte behörighet att planera den här tävlingen.");
            var comp = _competitions.GetSaved(competitionId);
            if (comp == null) return No("Tävlingen finns inte.");
            var member = _memberService.GetById(me);
            var primary = _memberClubs.GetPrimaryClubId(member);
            var r = _planning.CreateForCompetition(new PlanningService.CompetitionPlanningInput(competitionId, comp.DisplayName,
                    comp.CompetitionDate, comp.CompetitionEndDate ?? comp.CompetitionDate, me, MemberDisplayName(member),
                    comp.ClubId > 0 ? comp.ClubId : (primary > 0 ? primary : null), null),
                new PlanningService.WorkBreakdownImport(mid => MemberDisplayName(_memberService.GetById(mid))));
            if (r.Error != null) return No(r.Error);
            AfterStaffingLinkChanged(r.PlanningId, competitionId, me);
            return Ok(new { success = true, id = r.PlanningId, created = r.Created, imported = r.ImportedTasks, url = $"/tavlingsplanering/{r.PlanningId}#vy=uppgifter" });
        }

        /// <summary>Kopplar en egen planering till tävlingen (Sammankallande OCH tävlingens redigerare, beslut 9.3).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LinkCompetition(int id, int competitionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanLinkCompetition(x.Caller)) return No("Planeringen kopplas av den sammankallande.");
            if (_competitions.GetSaved(competitionId) == null) return No("Tävlingen finns inte.");
            if (!await _auth.CanEditCompetitionAsync(competitionId)) return No("Du behöver kunna redigera tävlingen för att koppla planeringen till den.");
            var err = _planning.LinkToCompetition(id, competitionId, x.MemberId);
            if (err != null) return No(err);
            AfterStaffingLinkChanged(id, competitionId, x.MemberId);
            return Ok(new { success = true, url = $"/tavlingsplanering/{id}#vy=uppgifter" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnlinkCompetition(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanLinkCompetition(x.Caller)) return No("Planeringen kopplas loss av den sammankallande.");
            var link = _planning.LinkFor(id);
            if (link == null) return No("Planeringen är inte kopplad till någon tävling.");
            if (!await _auth.CanEditCompetitionAsync(link.TargetId)) return No("Du behöver kunna redigera tävlingen för att koppla loss planeringen.");
            var err = _planning.Unlink(id, x.MemberId);
            if (err != null) return No(err);
            AfterStaffingLinkChanged(id, link.TargetId, x.MemberId, unlinked: true);
            return Ok();
        }

        /// <summary>
        /// Omgång 2, rälsposten Tävlingen: tävlingar planeringen kan kopplas till — kommande tävlingar i klubbarna som
        /// någon i planeringen företräder, som den som frågar får redigera och som inte redan har en planering.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> LinkableCompetitions(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanLinkCompetition(x.Caller) || x.Caller.ViaLink) return No("Planeringen kopplas av den sammankallande.");
            if (_planning.LinkFor(id) != null) return No("Planeringen är redan kopplad till en tävling.");
            var clubs = BoardTargets(id).Where(t => t.OwnerType == Models.DocumentOwnerType.Club).ToDictionary(t => t.OwnerId, t => t.Name);
            var from = DateTime.Today.AddDays(-7);
            // Närmast planeringens datum först (en planering för 2028 ska inte få höstens tävlingar överst).
            var anchor = x.Planning.TargetStart ?? DateTime.Today;
            var list = new List<object>();
            foreach (var c in _competitions.List()
                         .Where(c => c.ClubId > 0 && clubs.ContainsKey(c.ClubId) && c.CompetitionDate >= from)
                         .OrderBy(c => Math.Abs((c.CompetitionDate!.Value - anchor).TotalDays)).ThenBy(c => c.CompetitionDate).Take(80))
            {
                if (_planning.ForCompetition(c.Id) != null || !await _auth.CanEditCompetitionAsync(c.Id)) continue;
                list.Add(new { id = c.Id, name = c.DisplayName, date = D(c.CompetitionDate), club = clubs[c.ClubId] });
                if (list.Count >= 30) break;
            }
            return Ok(new { success = true, competitions = list });
        }

        /// <summary>
        /// P2b: bemanningen har bytt tävling (StaffingPlanningLink körde i kopplingens transaktion). Speglingarna
        /// till tävlingen räknas om — tävlingsansvariga och stationschefer — och en kopplad planering får
        /// tävlingens dagar i bemanningen. Körs efter transaktionen: speglingarna skrivs genom tävlingslagringen.
        /// Ett fel här får aldrig ångra kopplingen; nästa ändring i bemanningen räknar om speglingarna igen.
        /// </summary>
        private void AfterStaffingLinkChanged(int planningId, int competitionId, int byMemberId, bool unlinked = false)
        {
            try
            {
                _staffing.SyncCompetitionMirrors(competitionId);
                if (!unlinked)
                    _staffDays.AddCompetitionDays(new HpskSite.Models.Staffing.StaffOwner(planningId, competitionId), byMemberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planering {Id}: bemanningens speglingar efter kopplingen till tävling {Comp} misslyckades.", planningId, competitionId);
            }
        }

        /// <summary>P2b: planeringens gren — styr bemanningens roller tills planeringen kopplas till en tävling.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetDiscipline(int id, string? value)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Grenen väljs av den som kan ändra planeringen.");
            var key = string.IsNullOrWhiteSpace(value) ? null : HpskSite.Models.CompetitionTypes.GetById(value.Trim())?.Id;
            if (!string.IsNullOrWhiteSpace(value) && key == null) return No("Grenen finns inte.");
            _planning.SetDisciplineKey(id, key, x.MemberId);
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetDoneAtMeetingsOnly(int id, string? value)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Det väljer den sammankallande.");
            _planning.SetDoneAtMeetingsOnly(id, value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase), x.MemberId);
            return Ok();
        }

        // =====================================================================================
        //  Områden
        // =====================================================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveArea(int id, int areaId, string name, int? responsibleId, string? responsibleName,
            string? description = null, int? hostType = null, int? hostId = null, string? hostText = null, string? details = null,
            string? stations = null)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Områdena ändras av den som kan ändra planeringen.");
            // Omgång 2 steg 4: beskrivning och värd skickas bara av områdesdialogen (details = "1"). Utelämnat = rör inte dem,
            // så mallarna och snabbdialogerna inte tömmer en värd någon satt.
            PlanningService.AreaDetails? extra = null;
            if (details == "1")
            {
                int? club = null, region = null;
                if (hostId > 0)
                {
                    // Bara en klubb eller krets som någon i planeringen företräder — samma urval som förslagens styrelser.
                    if (!BoardTargets(id).Any(t => t.OwnerType == hostType && t.OwnerId == hostId))
                        return No("Välj en klubb eller krets som någon i planeringen företräder, eller skriv värden som text.");
                    if (hostType == Models.DocumentOwnerType.Club) club = hostId; else region = hostId;
                }
                // P4: stationerna skickas av områdesdialogen; tomt fält = inga stationer.
                extra = new PlanningService.AreaDetails(description, club, region, hostText, stations ?? "");
            }
            var r = _planning.SaveArea(id, areaId, name, responsibleId, responsibleName, x.Caller.ParticipantId, extra);
            return r.Error == null ? Ok(new { success = true, areaId = r.AreaId }) : No(r.Error);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteArea(int id, int areaId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Områdena ändras av den som kan ändra planeringen.");
            var err = _planning.DeleteArea(id, areaId);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveArea(int id, int areaId, int direction)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Områdena ändras av den som kan ändra planeringen.");
            var err = _planning.MoveArea(id, areaId, direction);
            return err == null ? Ok() : No(err);
        }

        // =====================================================================================
        //  Kommentarer
        // =====================================================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTaskComment(int id, int taskId, string text, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            if (!PlanningAccess.CanCommentOnTask(x.Caller, task)) return No("Bara den som deltar kommenterar.");
            var err = _planning.AddTaskComment(id, taskId, x.Caller.ParticipantId, text, x.Caller.ViaLink);
            return err == null ? Ok() : No(err);
        }

        // =====================================================================================
        //  Mallar, kretsens checklista, stationer, inklistring
        // =====================================================================================

        /// <summary>Fyller i från en standardmall (klubb, krets eller sm) för tävlingens gren.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedTemplate(int id, string size, string? discipline)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Mallen fylls i av den som kan ändra planeringen.");
            var comp = LinkedCompetition(id);
            var disc = comp?.TypeId ?? discipline;
            var (areas, tasks) = _planning.SeedTemplate(id, PrepTemplates.For(disc, size), AnchorFor(x.Planning, comp), x.Caller.ParticipantId);
            return Ok(new { success = true, areas, tasks });
        }

        [HttpGet]
        public async Task<IActionResult> KretsChecklist(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var link = _planning.LinkFor(id);
            if (link == null) return Ok(new { success = true, available = false });
            var cl = _kretsPlanning.ForCompetition(link.TargetId);
            if (cl == null) return Ok(new { success = true, available = false });
            return Ok(new
            {
                success = true, available = true, regionName = cl.RegionName, added = cl.Added, dismissed = cl.Dismissed,
                canApply = PlanningAccess.CanManageAreas(x.Caller),
                pending = cl.Pending.Select(p => new { id = p.Id, title = p.Title, description = p.Description, daysBefore = p.DaysBeforeComp }),
            });
        }

        /// <summary>Lägger in valda punkter som uppgifter och registrerar de bortvalda.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyKretsChecklist(int id, string? add, string? dismiss)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Kretsens punkter läggs in av den som kan ändra planeringen.");
            var link = _planning.LinkFor(id);
            if (link == null) return No("Planeringen är inte kopplad till någon tävling.");
            var cl = _kretsPlanning.ForCompetition(link.TargetId);
            if (cl == null) return No("Kretsen har ingen checklista för den här tävlingen.");
            var addIds = Ids(add).ToHashSet();
            var dismissIds = Ids(dismiss).Where(i => !addIds.Contains(i)).ToList();
            var chosen = cl.Pending.Where(p => addIds.Contains(p.Id))
                .Select(p => new PlanningService.ChecklistTask(p.Id, p.Title, p.Description, p.DaysBeforeComp)).ToList();
            var comp = _competitions.GetSaved(link.TargetId);
            // Uppgifterna först (idempotent på punktens id), sedan markeringen — fallerar markeringen erbjuds
            // punkten igen men läggs inte in två gånger.
            var added = _planning.AddChecklistTasks(id, "Kretsens checklista — " + cl.RegionName, chosen, AnchorFor(x.Planning, comp), x.Caller.ParticipantId);
            _kretsPlanning.MarkHandled(link.TargetId, chosen.Select(c => c.ChecklistItemId), dismissIds, x.MemberId);
            return Ok(new { success = true, added, dismissed = dismissIds.Count });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedStations(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Stationerna läggs in av den som kan ändra planeringen.");
            var comp = LinkedCompetition(id);
            if (comp == null || comp.TypeId is not ("Faltskytte" or "MagnumFalt")) return No("Stationerna finns bara för en kopplad fälttävling.");
            if (comp.NumberOfSeriesOrStations <= 0) return No("Tävlingen har inga stationer angivna.");
            var added = _planning.SeedStationTasks(id, comp.NumberOfSeriesOrStations, x.Caller.ParticipantId);
            return Ok(new { success = true, added });
        }

        /// <summary>
        /// Klistra in från ett kalkylark. <paramref name="apply"/> = "1" lägger in; annars förhandsvisas raderna.
        /// En rad som inte går att läsa stoppar allt (en halvt inläst lista är svår att rätta).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PasteTasks(int id, string? text, int? areaId, string? apply)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanCreateTasks(x.Caller) || x.Caller.ViaLink) return No("Bara den som deltar lägger till uppgifter.");
            var rows = PlanningTaskRules.ParsePaste(text);
            if (rows.Count == 0) return No("Klistra in minst en rad.");
            if (rows.Count > PlanningService.MaxPasteRows) return No($"Högst {PlanningService.MaxPasteRows} rader åt gången.");
            var people = _planning.Participants(id);
            int? Match(string? who)
            {
                if (string.IsNullOrWhiteSpace(who)) return null;
                var hit = people.Where(p => string.Equals(p.DisplayName.Trim(), who.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                return hit.Count == 1 ? hit[0].Id : null;
            }
            var preview = rows.Select(r => new
            {
                title = r.Title, who = r.Who, due = D(r.Due), error = r.Error,
                assigneeId = Match(r.Who), matched = Match(r.Who) != null,
            }).ToList();
            var errors = preview.Where(p => p.error != null).Select(p => p.error!).ToList();
            if (apply != "1" || errors.Count > 0)
                return Ok(new { success = errors.Count == 0, rows = preview, errors, message = errors.Count > 0 ? "Rätta raderna först: " + string.Join(" ", errors.Take(3)) : null });
            var n = _planning.AddPastedTasks(id, areaId, rows.Select(r => new PlanningService.PastedTask(r.Title, Match(r.Who), Match(r.Who) == null ? r.Who : null, r.Due)), x.Caller.ParticipantId);
            return Ok(new { success = true, added = n });
        }

        /// <summary>Uppgifterna som CSV (semikolon, UTF-8 med BOM — öppnas rätt i Excel).</summary>
        [HttpGet]
        public async Task<IActionResult> ExportTasks(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return NotFound();
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var areas = _planning.Areas(id).ToDictionary(a => a.Id, a => a.Name);
            static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine("Område;Uppgift;Ansvarig;Sista dag;Läge;Senaste raden;Uppskattning (kr)");
            foreach (var t in _planning.Tasks(id).OrderBy(t => t.AreaId.HasValue ? 0 : 1).ThenBy(t => t.AreaId).ThenBy(t => t.SortOrder))
                sb.AppendLine(string.Join(";",
                    Q(t.AreaId.HasValue ? areas.GetValueOrDefault(t.AreaId.Value, "") : ""), Q(t.Title),
                    Q(t.AssigneeParticipantId.HasValue ? names.GetValueOrDefault(t.AssigneeParticipantId.Value, "") : t.AssigneeName),
                    Q(D(t.DueDate)), Q(PlanningTaskStatus.Label(t.Status)), Q(t.LatestReport),
                    Q(t.EstimatedCost?.ToString("0", CultureInfo.InvariantCulture))));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv; charset=utf-8", "uppgifter.csv");
        }

        /// <summary>Ett mejl till var och en som har försenade uppgifter (EN per person, inte per uppgift).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindOverdue(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Påminnelsen skickas av den som kan ändra planeringen.");
            var today = DateTime.Today;
            var byPerson = _planning.Tasks(id).Where(t => PlanningTaskRules.IsOverdue(t, today) && t.AssigneeParticipantId > 0)
                .GroupBy(t => t.AssigneeParticipantId!.Value).ToList();
            if (byPerson.Count == 0) return Ok(new { success = true, people = 0, sent = 0, message = "Ingen har försenade uppgifter." });
            var sent = 0;
            var failed = new List<string>();
            foreach (var g in byPerson)
            {
                try { if (await _notify.OverdueReminderAsync(id, g.Key, g.Select(t => t.Id).ToList(), x.Caller.Participant)) sent++; else failed.Add(_planning.Participant(id, g.Key)?.DisplayName ?? "Någon"); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: påminnelse till deltagare {P} misslyckades.", id, g.Key); failed.Add(_planning.Participant(id, g.Key)?.DisplayName ?? "Någon"); }
            }
            return Ok(new { success = true, people = byPerson.Count, sent, failed });
        }
    }
}
