using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Uppgifterna, mötets punkter och rollerna (Stefan 2026-10-23). Samma två sätt att vara inloggad som
    /// resten av API:t — gästen kommer in med länken ur sitt mejl och kan säga hur det går med sina uppgifter.
    /// </summary>
    public partial class PlanningController
    {
        private object TaskJson(PlanningTaskRow t, PlanningCaller c, IReadOnlyDictionary<int, string> names,
            IReadOnlyDictionary<int, PlanningMeetingRow> meetings, DateTime now, PlanningRow? planning = null,
            IReadOnlyDictionary<int, int>? comments = null, IReadOnlyDictionary<int, string>? stationUrls = null) => new
        {
            id = t.Id, title = t.Title, description = t.Description,
            assigneeId = t.AssigneeParticipantId,
            assignee = t.AssigneeParticipantId.HasValue ? names.GetValueOrDefault(t.AssigneeParticipantId.Value, "Har lämnat planeringen") : t.AssigneeName,
            assigneeName = t.AssigneeName,
            due = D(t.DueDate), overdue = PlanningTaskRules.IsOverdue(t, now), dueDaysBefore = t.DueDaysBefore,
            areaId = t.AreaId, scopeType = t.ScopeType, scopeKey = t.ScopeKey, estimatedCost = t.EstimatedCost,
            sortOrder = t.SortOrder, source = t.ImportedFromWorkItemId.HasValue ? "Imported" : t.SourceKind,
            stationUrl = t.ScopeType == "Station" && int.TryParse(t.ScopeKey, out var stNo) && stationUrls != null ? stationUrls.GetValueOrDefault(stNo) : null,
            comments = comments?.GetValueOrDefault(t.Id) ?? 0,
            canDecide = planning != null && !planning.DoneAtMeetingsOnly && PlanningAccess.CanDecideTask(c, false, planning, t),
            status = t.Status, statusLabel = PlanningTaskStatus.Label(t.Status),
            report = t.LatestReport, reportAt = DT(t.LatestReportAt),
            reportBy = t.LatestReportBy.HasValue ? names.GetValueOrDefault(t.LatestReportBy.Value, "Någon") : null,
            fromMeetingId = t.FromMeetingId,
            fromMeeting = t.FromMeetingId.HasValue && meetings.TryGetValue(t.FromMeetingId.Value, out var fm) ? fm.Title : null,
            fromMeetingAt = t.FromMeetingId.HasValue && meetings.TryGetValue(t.FromMeetingId.Value, out var fm2) ? D(fm2.StartsAt ?? fm2.CreatedAt) : "",
            createdBy = names.GetValueOrDefault(t.CreatedBy, "Någon"), createdAt = DT(t.CreatedAt),
            doneAt = DT(t.DoneAt), doneBy = t.DoneBy.HasValue ? names.GetValueOrDefault(t.DoneBy.Value, "Någon") : null,
            doneAtMeetingId = t.DoneAtMeetingId,
            doneAtMeeting = t.DoneAtMeetingId.HasValue && meetings.TryGetValue(t.DoneAtMeetingId.Value, out var dm) ? dm.Title : null,
            mine = t.AssigneeParticipantId == c.ParticipantId && c.IsParticipant,
            isMilestone = t.IsMilestone, sendToForbundet = t.SendToForbundet,
            sentToForbundetAt = DT(t.SentToForbundetAt),
            sentToForbundetBy = t.SentToForbundetBy.HasValue ? names.GetValueOrDefault(t.SentToForbundetBy.Value, "Någon") : null,
            canEdit = PlanningAccess.CanEditTask(c, t),
            canReport = PlanningAccess.CanReportOnTask(c, t),
        };

        /// <summary>
        /// Banan i planeringen (2026-10-24): var station N byggs — i områdets delkonfiguration om stationen har en (med
        /// delens eget nummer), annars i banans konfiguration, annars i tävlingens. <c>#station=N</c> rullar fram stationen.
        /// </summary>
        private async Task<Dictionary<int, string>> StationUrlsAsync(int planningId, int memberId, int? competitionConfigId)
        {
            var urls = new Dictionary<int, string>();
            try
            {
                var svc = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.FaltskytteConfigurationService>();
                var course = _planning.CourseConfigId(planningId) ?? competitionConfigId;
                if (course > 0)
                    for (var n = 1; n <= PlanningStations.Max; n++) urls[n] = $"/faltkonfig/{course}/redigera#station={n}";
                foreach (var l in _planning.AreaConfigLinks(planningId))
                {
                    var cfg = await svc.GetByIdAsync(l.ConfigId);
                    if (cfg == null) continue;
                    var map = AreaConfigBlob.CourseStations(cfg.JsonBlob);
                    for (var i = 0; i < map.Count; i++) urls[map[i]] = $"/faltkonfig/{cfg.Id}/redigera#station={i + 1}";
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: stationernas länkar kunde inte räknas.", planningId); }
            return urls;
        }

        private async Task<int> StationCountAsync(int planningId)
        {
            try
            {
                var course = HttpContext.RequestServices.GetRequiredService<PlanningCourseService>();
                return course.StationCount(planningId, (await course.CourseConfigAsync(planningId))?.JsonBlob);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: banans stationer kunde inte räknas.", planningId); return 0; }
        }

        [HttpGet]
        public async Task<IActionResult> Tasks(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var now = DateTime.Now;
            var comp = LinkedCompetition(id);
            // "N dagar före" följer tävlingens (eller planeringens) dag om den flyttats.
            _planning.RealignRelativeDueDates(id, AnchorFor(x.Planning, comp));
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var meetings = _planning.Meetings(id).ToDictionary(m => m.Id);
            var tasks = _planning.Tasks(id);
            var comments = _planning.CommentCounts(id);
            var cfg = await StationUrlsAsync(id, x.MemberId, StationConfigId(comp));
            var anchor = AnchorFor(x.Planning, comp);
            return Json(new
            {
                success = true,
                canCreate = PlanningAccess.CanCreateTasks(x.Caller),
                canDecide = PlanningAccess.CanDecideTask(x.Caller, false, x.Planning, null),
                canManageAreas = PlanningAccess.CanManageAreas(x.Caller),
                canAddDocument = PlanningAccess.CanAddDocument(x.Caller),
                // Förbundets adress är en sajtinställning utan förval (beslut 10.2).
                forbundetEmail = ForbundetEmail(),
                canSendToForbundet = PlanningAccess.CanEditPlanning(x.Caller) && !x.Caller.ViaLink,
                doneAtMeetingsOnly = x.Planning.DoneAtMeetingsOnly,
                anchor = D(anchor), anchorIsCompetition = comp?.CompetitionDate is { Year: > 1900 },
                scopeTypes = PlanningTaskRules.ScopeTypes,
                stationCount = await StationCountAsync(id),
                statuses = PlanningTaskStatus.All.Select(s => new { value = s, label = PlanningTaskStatus.Label(s), assignee = PlanningTaskStatus.AssigneeSettable.Contains(s) }),
                areas = AreasJson(id, names, tasks),
                tasks = tasks.OrderBy(tk => tk.SortOrder).ThenBy(tk => tk.Id).Select(tk => TaskJson(tk, x.Caller, names, meetings, now, x.Planning, comments, cfg)),
            });
        }

        [HttpGet]
        public async Task<IActionResult> TaskHistory(int id, int taskId, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            if (_planning.GetTask(id, taskId) == null) return No("Uppgiften finns inte.");
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var meetings = _planning.Meetings(id).ToDictionary(m => m.Id, m => m.Title);
            return Json(new
            {
                success = true,
                events = _planning.TaskEvents(id, taskId).Select(e => new
                {
                    kind = e.Kind, at = DT(e.CreatedAt),
                    who = e.ParticipantId.HasValue ? names.GetValueOrDefault(e.ParticipantId.Value, "Någon") : "Någon",
                    oldStatus = e.OldStatus == null ? null : PlanningTaskStatus.Label(e.OldStatus),
                    newStatus = e.NewStatus == null ? null : PlanningTaskStatus.Label(e.NewStatus),
                    note = e.Note, meeting = e.MeetingId.HasValue ? meetings.GetValueOrDefault(e.MeetingId.Value) : null,
                    viaEmail = e.ViaEmail,
                }),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTask(int id, string title, string? description, int? assignee, string? due, int? meetingId, string? t,
            int? assigneeMemberId, string? assigneeName, int? dueDaysBefore, int? areaId, string? scopeType, string? scopeKey, string? estimatedCost,
            string? isMilestone = null, string? sendToForbundet = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanCreateTasks(x.Caller)) return No("Bara den som deltar lägger till uppgifter.");
            var (input, err) = await TaskInputAsync(x, title, description, assignee, assigneeMemberId, assigneeName, due, dueDaysBefore, areaId, scopeType, scopeKey, estimatedCost);
            if (input != null) input = input with { IsMilestone = Flag(isMilestone), SendToForbundet = Flag(sendToForbundet) };
            if (err != null) return No(err);
            var r = _planning.CreateTask(id, x.Caller.ParticipantId, input!, meetingId, AnchorFor(x.Planning, LinkedCompetition(id)));
            if (r.Error != null) return No(r.Error);
            var mail = await NotifyAssignedAsync(id, r.TaskId, r.NewAssignee, x.Caller);
            return Ok(new { success = true, taskId = r.TaskId, mailed = mail == PlanningNotifier.TaskMailOutcome.Sent, mailQueued = mail == PlanningNotifier.TaskMailOutcome.Queued });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTask(int id, int taskId, string title, string? description, int? assignee, string? due, string? t,
            int? assigneeMemberId, string? assigneeName, int? dueDaysBefore, int? areaId, string? scopeType, string? scopeKey, string? estimatedCost,
            string? isMilestone = null, string? sendToForbundet = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            if (!PlanningAccess.CanEditTask(x.Caller, task)) return No("Uppgiften ändras av den som lade till den, eller av den som kan ändra planeringen.");
            var (input, err) = await TaskInputAsync(x, title, description, assignee, assigneeMemberId, assigneeName, due, dueDaysBefore, areaId, scopeType, scopeKey, estimatedCost,
                task.AssigneeName);
            if (err != null) return No(err);
            // Utelämnat = behåll (en äldre klient som inte känner till milstolparna får inte ta bort flaggan).
            input = input! with
            {
                IsMilestone = isMilestone == null ? task.IsMilestone : Flag(isMilestone),
                SendToForbundet = sendToForbundet == null ? task.SendToForbundet : Flag(sendToForbundet),
            };
            var r = _planning.UpdateTask(id, taskId, x.Caller.ParticipantId, input!, AnchorFor(x.Planning, LinkedCompetition(id)));
            if (r.Error != null) return No(r.Error);
            var mail = await NotifyAssignedAsync(id, taskId, r.NewAssignee, x.Caller);
            return Ok(new { success = true, mailed = mail == PlanningNotifier.TaskMailOutcome.Sent, mailQueued = mail == PlanningNotifier.TaskMailOutcome.Queued });
        }

        /// <summary>Den ansvarige: läget (fram till Klar att pröva) och en rad om hur det går.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReportTask(int id, int taskId, string? status, string? report, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            if (!PlanningAccess.CanReportOnTask(x.Caller, task)) return No("Det är den som har uppgiften som säger hur det går.");
            if (PlanningTaskStatus.Normalize(status) == PlanningTaskStatus.Done)
            {
                // Tävlingens egen planering ("Utförd beslutas på mötena" av): den ansvarige bockar av själv.
                if (!PlanningAccess.CanDecideTask(x.Caller, false, x.Planning, task))
                    return No("Om uppgiften är utförd bestämmer ni på ett möte, eller den sammankallande.");
                var e1 = string.IsNullOrWhiteSpace(report) ? null : _planning.ReportOnTask(id, taskId, x.Caller.ParticipantId, null, report, x.Caller.ViaLink);
                var e2 = e1 ?? _planning.DecideTask(id, taskId, x.Caller.ParticipantId, true, null, null);
                return e2 == null ? Ok() : No(e2);
            }
            var err = _planning.ReportOnTask(id, taskId, x.Caller.ParticipantId, string.IsNullOrWhiteSpace(status) ? null : status, report, x.Caller.ViaLink);
            return err == null ? Ok() : No(err);
        }

        /// <summary>
        /// Mötets beslut: <paramref name="decision"/> = done | continue | undo. Med ett pågående möte
        /// (<paramref name="meetingId"/>) beslutar alla som deltar; annars bara den sammankallande.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DecideTask(int id, int taskId, string decision, int? meetingId, string? newDue, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            var m = meetingId > 0 ? _planning.Meeting(id, meetingId!.Value) : null;
            if (meetingId > 0 && m == null) return No("Mötet finns inte.");
            var atMeeting = PlanningTaskRules.MeetingIsCurrent(m, DateTime.Now);
            if (!PlanningAccess.CanDecideTask(x.Caller, atMeeting, x.Planning, task))
                return No(meetingId > 0 && !atMeeting
                    ? "Mötet pågår inte nu. Om en uppgift är utförd bestämmer ni på mötet — eller den sammankallande."
                    : "Om en uppgift är utförd bestäms på ett möte, eller av den sammankallande.");
            var mid = atMeeting ? m!.Id : (int?)null;
            var err = (decision ?? "").Trim().ToLowerInvariant() switch
            {
                "done" => _planning.DecideTask(id, taskId, x.Caller.ParticipantId, true, mid, null),
                "continue" => _planning.DecideTask(id, taskId, x.Caller.ParticipantId, false, mid, ParseDate(newDue)),
                "undo" => _planning.UndoTaskDone(id, taskId, x.Caller.ParticipantId, mid),
                _ => "Okänt beslut.",
            };
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveTask(int id, int taskId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            if (!PlanningAccess.CanEditTask(x.Caller, task)) return No("Uppgiften tas bort av den som lade till den, eller av den som kan ändra planeringen.");
            var err = _planning.RemoveTask(id, taskId, x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>
        /// Formulärets fält som en <see cref="PlanningService.TaskInput"/>.
        /// <para>⚠️ Omgång 3 (Stefan 2026-10-24, upphäver 2026-10-23): en uppgift går bara till någon som ÄR med. Att ge en
        /// uppgift till en medlem utanför lade förr till hen som Deltar — en väg förbi inbjudningsnivån (Expertgruppen K2).
        /// Ett fritt namn godtas bara när det redan står på uppgiften (<paramref name="keepName"/>).</para>
        /// </summary>
        private Task<(PlanningService.TaskInput? Input, string? Error)> TaskInputAsync(Ctx x, string title, string? description, int? assignee,
            int? assigneeMemberId, string? assigneeName, string? due, int? dueDaysBefore, int? areaId, string? scopeType, string? scopeKey, string? estimatedCost,
            string? keepName = null)
        {
            int? pid = assignee > 0 ? assignee : null;
            if (pid == null && assigneeMemberId > 0)
                return Task.FromResult<(PlanningService.TaskInput?, string?)>((null, "Bara den som är med i planeringen kan få en uppgift. Bjud in hen först."));
            if (pid is int p && _planning.Participant(x.Planning.Id, p) is not { IsActive: true })
                return Task.FromResult<(PlanningService.TaskInput?, string?)>((null, "Personen är inte med i planeringen."));
            if (pid == null && !string.IsNullOrWhiteSpace(assigneeName)
                && !string.Equals(assigneeName.Trim(), (keepName ?? "").Trim(), StringComparison.Ordinal))
                return Task.FromResult<(PlanningService.TaskInput?, string?)>((null, "Bara den som är med i planeringen kan få en uppgift. Bjud in hen först."));
            decimal? cost = null;
            var costText = (estimatedCost ?? "").Trim().Replace(" ", "").Replace(",", ".");
            if (costText.Length > 0)
            {
                if (!decimal.TryParse(costText, NumberStyles.Number, Inv, out var c)) return Task.FromResult<(PlanningService.TaskInput?, string?)>((null, "Skriv uppskattningen som ett belopp i kronor."));
                cost = c;
            }
            var dueDate = ParseDate(due);
            if (!string.IsNullOrWhiteSpace(due) && dueDate == null && dueDaysBefore == null)
                return Task.FromResult<(PlanningService.TaskInput?, string?)>((null, "Skriv sista dagen som ett datum, till exempel 2027-05-14."));
            return Task.FromResult<(PlanningService.TaskInput?, string?)>((new PlanningService.TaskInput(title, description, pid,
                pid == null ? assigneeName : null, dueDaysBefore.HasValue ? null : dueDate, dueDaysBefore, areaId, scopeType, scopeKey, cost), null));
        }

        private static bool Flag(string? v) => v is "1" or "true" or "on";

        internal const string ForbundetEmailKey = "Planning:ForbundetEmail";
        private string ForbundetEmail() => (_siteSettings.Get(ForbundetEmailKey, "") ?? "").Trim();

        /// <summary>Förbundets adress (sajtadmin). Tom adress = knappen säger att den saknas.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetForbundetEmail(string? email)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return No("Förbundets adress fylls i av en sajtadministratör.");
            var e = (email ?? "").Trim();
            if (e.Length > 0 && (e.Length > 256 || !System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
                return No("Skriv en giltig e-postadress.");
            _siteSettings.Set(ForbundetEmailKey, e, await MemberIdAsync());
            return Ok(new { success = true, email = e });
        }

        /// <summary>
        /// Skicka en milstolpe till förbundet (P3, beslut 10.2): mejl med valda dokument ur planeringens arkiv,
        /// svarsadress = den som skickar. Milstolpen bockas av och gruppen får en rad i meddelandena — bara när
        /// mejlet faktiskt gick iväg.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendToForbundet(int id, int taskId, string? documentIds, string? message)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som skickar till förbundet.");
            var task = _planning.GetTask(id, taskId);
            if (task == null || !task.IsMilestone || !task.SendToForbundet) return No("Milstolpen skickas inte till förbundet.");
            var to = ForbundetEmail();
            if (to.Length == 0) return No("Förbundets adress saknas. En sajtadministratör fyller i den under planeringens Inställningar.");
            var ids = (documentIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
            var docs = ids.Select(i => _planning.Document(id, i)).ToList();
            if (docs.Any(d => d == null)) return No("Ett dokument finns inte längre.");
            if (ids.Count > 10) return No("Bifoga högst tio dokument.");
            var streams = new List<(string, System.IO.Stream, string)>();
            var links = new List<PlanningDocumentRow>();
            long total = 0;
            try
            {
                foreach (var d in docs)
                {
                    if (!d!.IsFile) { links.Add(d); continue; }
                    var st = await _docs.OpenReadAsync(d.StoredFileName);
                    if (st == null) return No($"Filen {d.Title} kunde inte läsas.");
                    var ms = new System.IO.MemoryStream();
                    await st.CopyToAsync(ms); await st.DisposeAsync(); ms.Position = 0;
                    total += ms.Length;
                    if (total > 15L * 1024 * 1024) { ms.Dispose(); return No("De bifogade filerna är större än 15 MB tillsammans. Välj färre."); }
                    streams.Add((string.IsNullOrWhiteSpace(d.OriginalFileName) ? d.StoredFileName! : d.OriginalFileName!, ms, HpskSite.Services.Staffing.PrepDocumentStorage.ContentTypeFor(d.StoredFileName!)));
                }
                var ok = await _notify.ForbundetAsync(id, task, x.Caller.Participant, to, message, streams, links);
                if (!ok) return No("Mejlet till förbundet gick inte iväg. Försök igen om en stund.");
                var summary = docs.Count == 0 ? "" : "Bifogat: " + string.Join(", ", docs.Select(d => d!.Title)) + ".";
                var err = _planning.MarkSentToForbundet(id, taskId, x.Caller.ParticipantId, summary);
                return err == null ? Ok(new { success = true, to }) : No(err);
            }
            finally { foreach (var (_, s, _) in streams) s.Dispose(); }
        }

        /// <summary>Fältkonfigurationen tävlingen är ansluten till — "Station N" länkar dit.</summary>
        private static int? StationConfigId(HpskSite.Models.Competitions.CompetitionInfo? comp)
        {
            if (comp == null || string.IsNullOrWhiteSpace(comp.StationConfig)) return null;
            try { return Newtonsoft.Json.Linq.JObject.Parse(comp.StationConfig).Value<int?>("_attachedConfigId"); }
            catch { return null; }
        }

        /// <summary>Mejl till den som fått uppgiften — inte till den som gav sig själv den.</summary>
        private async Task<PlanningNotifier.TaskMailOutcome> NotifyAssignedAsync(int planningId, int taskId, int? assignee, PlanningCaller by)
        {
            if (!(assignee > 0) || assignee == by.ParticipantId) return PlanningNotifier.TaskMailOutcome.None;
            try { return await _notify.TaskAssignedOutcomeAsync(planningId, taskId, assignee!.Value, by.Participant); }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: uppgift {Task} kunde inte aviseras.", planningId, taskId); return PlanningNotifier.TaskMailOutcome.None; }
        }

        // ---------------------------------------------------------------- "Att prata om"

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTopic(int id, int meetingId, string text, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanAddTopic(x.Caller)) return No("Bara den som deltar lägger till punkter.");
            var err = _planning.AddTopic(id, meetingId, x.Caller.ParticipantId, text);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveTopic(int id, int topicId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var topic = _planning.Topic(id, topicId);
            if (topic == null) return No("Punkten finns inte.");
            if (!PlanningAccess.CanRemoveTopic(x.Caller, topic)) return No("Punkten tas bort av den som lade till den, eller av den som kan ändra planeringen.");
            var err = _planning.RemoveTopic(id, topicId);
            return err == null ? Ok() : No(err);
        }

        // ---------------------------------------------------------------- Roller

        /// <summary>Ersätter deltagarens roller. <paramref name="roles"/> = en roll per rad.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRoles(int id, int participantId, string? roles)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageRoles(x.Caller)) return No("Roller ges av den sammankallande.");
            var err = _planning.SetRoles(id, participantId, (roles ?? "").Split('\n'), x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }
    }
}
