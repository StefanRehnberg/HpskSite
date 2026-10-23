using System;
using System.Collections.Generic;
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
            IReadOnlyDictionary<int, PlanningMeetingRow> meetings, DateTime now) => new
        {
            id = t.Id, title = t.Title, description = t.Description,
            assigneeId = t.AssigneeParticipantId,
            assignee = t.AssigneeParticipantId.HasValue ? names.GetValueOrDefault(t.AssigneeParticipantId.Value, "Har lämnat planeringen") : null,
            due = D(t.DueDate), overdue = t.DueDate.HasValue && t.DueDate.Value.Date < now.Date && PlanningTaskStatus.IsOpen(t.Status),
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
            canEdit = PlanningAccess.CanEditTask(c, t),
            canReport = PlanningAccess.CanReportOnTask(c, t),
        };

        [HttpGet]
        public async Task<IActionResult> Tasks(int id, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var now = DateTime.Now;
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var meetings = _planning.Meetings(id).ToDictionary(m => m.Id);
            var tasks = _planning.Tasks(id);
            return Json(new
            {
                success = true,
                canCreate = PlanningAccess.CanCreateTasks(x.Caller),
                canDecide = PlanningAccess.CanDecideTask(x.Caller, atCurrentMeeting: false),
                statuses = PlanningTaskStatus.All.Select(s => new { value = s, label = PlanningTaskStatus.Label(s), assignee = PlanningTaskStatus.AssigneeSettable.Contains(s) }),
                tasks = tasks.Select(tk => TaskJson(tk, x.Caller, names, meetings, now)),
            });
        }

        [HttpGet]
        public async Task<IActionResult> TaskHistory(int id, int taskId, string? t)
        {
            var x = await ResolveAsync(id, t);
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
        public async Task<IActionResult> CreateTask(int id, string title, string? description, int? assignee, string? due, int? meetingId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanCreateTasks(x.Caller)) return No("Bara den som deltar lägger till uppgifter.");
            var r = _planning.CreateTask(id, x.Caller.ParticipantId, title, description, assignee, ParseDate(due), meetingId);
            if (r.Error != null) return No(r.Error);
            var mailed = await NotifyAssignedAsync(id, r.TaskId, r.NewAssignee, x.Caller);
            return Ok(new { success = true, taskId = r.TaskId, mailed });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTask(int id, int taskId, string title, string? description, int? assignee, string? due, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var task = _planning.GetTask(id, taskId);
            if (task == null) return No("Uppgiften finns inte.");
            if (!PlanningAccess.CanEditTask(x.Caller, task)) return No("Uppgiften ändras av den som lade till den, eller av den som kan ändra planeringen.");
            var r = _planning.UpdateTask(id, taskId, x.Caller.ParticipantId, title, description, assignee, ParseDate(due));
            if (r.Error != null) return No(r.Error);
            var mailed = await NotifyAssignedAsync(id, taskId, r.NewAssignee, x.Caller);
            return Ok(new { success = true, mailed });
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
            if (_planning.GetTask(id, taskId) == null) return No("Uppgiften finns inte.");
            var m = meetingId > 0 ? _planning.Meeting(id, meetingId!.Value) : null;
            if (meetingId > 0 && m == null) return No("Mötet finns inte.");
            var atMeeting = PlanningTaskRules.MeetingIsCurrent(m, DateTime.Now);
            if (!PlanningAccess.CanDecideTask(x.Caller, atMeeting))
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

        /// <summary>Mejl till den som fått uppgiften — inte till den som gav sig själv den.</summary>
        private async Task<bool> NotifyAssignedAsync(int planningId, int taskId, int? assignee, PlanningCaller by)
        {
            if (!(assignee > 0) || assignee == by.ParticipantId) return false;
            try { return await _notify.TaskAssignedAsync(planningId, taskId, assignee!.Value, by.Participant); }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: uppgift {Task} kunde inte aviseras.", planningId, taskId); return false; }
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
