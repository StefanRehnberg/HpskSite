using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Uppgifterna, mötets punkter och rollerna (Stefan 2026-10-23). Samma regler som resten av tjänsten:
    /// behörigheten prövas av anroparen med <see cref="PlanningAccess"/>, här prövas att varje id hör till
    /// RÄTT planering, och inga notiser skickas härifrån.
    /// </summary>
    public partial class PlanningService
    {
        // =====================================================================================
        //  Uppgifter
        // =====================================================================================

        public List<PlanningTaskRow> Tasks(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY Id", planningId);
        }

        public PlanningTaskRow? GetTask(int planningId, int taskId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
        }

        public List<PlanningTaskEventRow> TaskEvents(int planningId, int taskId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningTaskEventRow>(
                "SELECT * FROM PlanningTaskEvent WHERE PlanningId = @0 AND TaskId = @1 ORDER BY Id", planningId, taskId);
        }

        /// <summary>Hur många öppna uppgifter deltagaren har — siffran i rälsen.</summary>
        public int OpenTaskCount(int planningId, int participantId)
        {
            if (participantId <= 0) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM PlanningTask WHERE PlanningId = @0 AND AssigneeParticipantId = @1 AND DeletedAt IS NULL AND Status <> N'Done'",
                planningId, participantId);
        }

        private static string? AssigneeError(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int? assignee)
        {
            if (!(assignee > 0)) return null;
            var ok = db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL",
                assignee, planningId);
            return ok == 1 ? null : "Den du valde är inte med i planeringen.";
        }

        private static void TaskEvent(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, PlanningTaskRow t, string kind,
            int? byPid, string? oldStatus, string? newStatus, string? note, int? meetingId, bool viaEmail, DateTime now) =>
            db.Insert(new PlanningTaskEventRow
            {
                PlanningId = t.PlanningId, TaskId = t.Id, Kind = kind, OldStatus = oldStatus, NewStatus = newStatus,
                Note = Clean(note, PlanningTaskRules.MaxReport), MeetingId = meetingId, ParticipantId = byPid,
                ViaEmail = viaEmail, CreatedAt = now,
            });

        public sealed record TaskResult(int TaskId, string? Error, int? NewAssignee = null);

        /// <summary>En ny uppgift, på ett möte eller fristående.</summary>
        public TaskResult CreateTask(int planningId, int byParticipantId, string title, string? description,
            int? assignee, DateTime? due, int? fromMeetingId)
        {
            var err = PlanningTaskRules.ValidateTitle(title);
            if (err != null) return new TaskResult(0, err);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            err = AssigneeError(db, planningId, assignee);
            if (err != null) return new TaskResult(0, err);
            if (fromMeetingId > 0 && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMeeting WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL",
                    fromMeetingId, planningId) != 1)
                return new TaskResult(0, "Mötet finns inte.");
            var t = new PlanningTaskRow
            {
                PlanningId = planningId, Title = title.Trim(), Description = Clean(description, 4000),
                AssigneeParticipantId = assignee > 0 ? assignee : null, DueDate = due?.Date,
                Status = PlanningTaskStatus.NotStarted, FromMeetingId = fromMeetingId > 0 ? fromMeetingId : null,
                CreatedAt = now, CreatedBy = byParticipantId, UpdatedAt = now,
            };
            db.Insert(t);
            TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, null, t.FromMeetingId, false, now);
            Bump(db, planningId);
            return new TaskResult(t.Id, null, t.AssigneeParticipantId);
        }

        /// <summary>Ändrar uppgiftens innehåll. <see cref="TaskResult.NewAssignee"/> sätts när ansvarig BYTTS (då aviseras hen).</summary>
        public TaskResult UpdateTask(int planningId, int taskId, int byParticipantId, string title, string? description,
            int? assignee, DateTime? due)
        {
            var err = PlanningTaskRules.ValidateTitle(title);
            if (err != null) return new TaskResult(taskId, err);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return new TaskResult(taskId, "Uppgiften finns inte.");
            err = AssigneeError(db, planningId, assignee);
            if (err != null) return new TaskResult(taskId, err);
            var newAssignee = assignee > 0 ? assignee : null;
            var changed = new List<string>();
            if (t.Title != title.Trim()) changed.Add("rubriken");
            if ((t.Description ?? "") != (Clean(description, 4000) ?? "")) changed.Add("beskrivningen");
            if (t.AssigneeParticipantId != newAssignee) changed.Add("vem som har den");
            if (t.DueDate != due?.Date) changed.Add("sista dagen");
            if (changed.Count == 0) return new TaskResult(taskId, null);
            var reassigned = t.AssigneeParticipantId != newAssignee && newAssignee.HasValue;
            t.Title = title.Trim();
            t.Description = Clean(description, 4000);
            t.AssigneeParticipantId = newAssignee;
            t.DueDate = due?.Date;
            t.UpdatedAt = now;
            db.Update(t);
            TaskEvent(db, t, PlanningTaskEventKind.Edited, byParticipantId, null, null, "Ändrade " + string.Join(", ", changed) + ".", null, false, now);
            Bump(db, planningId);
            return new TaskResult(taskId, null, reassigned ? newAssignee : null);
        }

        /// <summary>
        /// Den ansvarige ändrar läget och/eller skriver hur det går. Läget får inte bli Utförd härifrån —
        /// det beslutar mötet (<see cref="DecideTask"/>).
        /// </summary>
        public string? ReportOnTask(int planningId, int taskId, int byParticipantId, string? status, string? report, bool viaEmail)
        {
            var st = PlanningTaskStatus.Normalize(status);
            if (status != null && (st == null || !PlanningTaskStatus.AssigneeSettable.Contains(st)))
                return "Det läget kan du inte välja här — om uppgiften är utförd bestäms på ett möte.";
            var text = Clean(report, PlanningTaskRules.MaxReport);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return "Uppgiften finns inte.";
            if (t.Status == PlanningTaskStatus.Done) return "Uppgiften är redan utförd. Ska den öppnas igen gör ni det på ett möte.";
            var statusChanged = st != null && st != t.Status;
            if (!statusChanged && text == null) return null;
            var old = t.Status;
            if (statusChanged) t.Status = st!;
            if (text != null) { t.LatestReport = text; t.LatestReportAt = now; t.LatestReportBy = byParticipantId; }
            t.UpdatedAt = now;
            db.Update(t);
            TaskEvent(db, t, statusChanged ? PlanningTaskEventKind.Status : PlanningTaskEventKind.Report, byParticipantId,
                statusChanged ? old : null, statusChanged ? t.Status : null, text, null, viaEmail, now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>
        /// Mötets beslut — eller den sammankallandes mellan mötena (<paramref name="meetingId"/> null).
        /// <paramref name="done"/>: Utförd. Annars "Fortsätter", med en ny sista dag om den anges.
        /// </summary>
        public string? DecideTask(int planningId, int taskId, int byParticipantId, bool done, int? meetingId, DateTime? newDue)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return "Uppgiften finns inte.";
            if (meetingId > 0 && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMeeting WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL",
                    meetingId, planningId) != 1)
                return "Mötet finns inte.";
            var old = t.Status;
            if (done)
            {
                if (t.Status == PlanningTaskStatus.Done) return null;
                t.StatusBeforeDone = t.Status;
                t.Status = PlanningTaskStatus.Done;
                t.DoneAt = now; t.DoneBy = byParticipantId; t.DoneAtMeetingId = meetingId > 0 ? meetingId : null;
                t.UpdatedAt = now;
                db.Update(t);
                TaskEvent(db, t, PlanningTaskEventKind.Done, byParticipantId, old, t.Status, null, t.DoneAtMeetingId, false, now);
            }
            else
            {
                if (t.Status == PlanningTaskStatus.Done) return "Uppgiften är utförd. Ångra det först, om den ska fortsätta.";
                t.Status = PlanningTaskRules.AfterContinue(t.Status);
                string? note = null;
                if (newDue.HasValue && newDue.Value.Date != t.DueDate)
                {
                    t.DueDate = newDue.Value.Date;
                    note = "Ny sista dag " + newDue.Value.ToString("yyyy-MM-dd") + ".";
                }
                t.UpdatedAt = now;
                db.Update(t);
                TaskEvent(db, t, PlanningTaskEventKind.Continued, byParticipantId, old, t.Status, note, meetingId > 0 ? meetingId : null, false, now);
            }
            Bump(db, planningId);
            return null;
        }

        /// <summary>Ångra Utförd: läget före beslutet kommer tillbaka.</summary>
        public string? UndoTaskDone(int planningId, int taskId, int byParticipantId, int? meetingId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return "Uppgiften finns inte.";
            if (t.Status != PlanningTaskStatus.Done) return null;
            var back = PlanningTaskRules.AfterUndo(t.StatusBeforeDone);
            t.Status = back;
            t.DoneAt = null; t.DoneBy = null; t.DoneAtMeetingId = null; t.StatusBeforeDone = null;
            t.UpdatedAt = now;
            db.Update(t);
            TaskEvent(db, t, PlanningTaskEventKind.Undone, byParticipantId, PlanningTaskStatus.Done, back, null, meetingId > 0 ? meetingId : null, false, now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Tar bort uppgiften (mjukt — historiken står kvar).</summary>
        public string? RemoveTask(int planningId, int taskId, int byParticipantId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>(
                "SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return "Uppgiften finns inte.";
            db.Execute("UPDATE PlanningTask SET DeletedAt = @2, UpdatedAt = @2 WHERE Id = @0 AND PlanningId = @1", taskId, planningId, now);
            TaskEvent(db, t, PlanningTaskEventKind.Removed, byParticipantId, null, null, null, null, false, now);
            Bump(db, planningId);
            return null;
        }

        // =====================================================================================
        //  Mötets punkter — "Att prata om"
        // =====================================================================================

        public List<PlanningMeetingTopicRow> Topics(int planningId, IEnumerable<int> meetingIds)
        {
            var ids = meetingIds.Distinct().ToList();
            if (ids.Count == 0) return new List<PlanningMeetingTopicRow>();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMeetingTopicRow>(
                "SELECT * FROM PlanningMeetingTopic WHERE PlanningId = @0 AND PlanningMeetingId IN (@1) AND DeletedAt IS NULL ORDER BY SortOrder, Id",
                planningId, ids);
        }

        public PlanningMeetingTopicRow? Topic(int planningId, int topicId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningMeetingTopicRow>(
                "SELECT * FROM PlanningMeetingTopic WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", topicId, planningId);
        }

        public string? AddTopic(int planningId, int meetingId, int byParticipantId, string text)
        {
            var t = Clean(text, PlanningTaskRules.MaxTopic);
            if (t == null) return "Skriv vad du vill ta upp.";
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            if (m.Status == PlanningMeetingStatus.Cancelled) return "Mötet är inställt.";
            var next = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM PlanningMeetingTopic WHERE PlanningMeetingId = @0", meetingId) ?? 0;
            db.Insert(new PlanningMeetingTopicRow
            {
                PlanningId = planningId, PlanningMeetingId = meetingId, Text = t, AddedBy = byParticipantId,
                AddedAt = now, SortOrder = next + 1,
            });
            Bump(db, planningId);
            return null;
        }

        public string? RemoveTopic(int planningId, int topicId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute("UPDATE PlanningMeetingTopic SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL",
                topicId, planningId, Now);
            if (n == 0) return "Punkten finns inte.";
            Bump(s.Database, planningId);
            return null;
        }

        // =====================================================================================
        //  Roller
        // =====================================================================================

        public List<PlanningRoleRow> Roles(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningRoleRow>(
                "SELECT * FROM PlanningRole WHERE PlanningId = @0 AND RemovedAt IS NULL ORDER BY Id", planningId);
        }

        /// <summary>
        /// Ersätter deltagarens roller med <paramref name="names"/>: nya läggs till, saknade tas bort (står kvar i
        /// historiken), oförändrade rörs inte — så att en roll behåller sitt datum.
        /// </summary>
        public string? SetRoles(int planningId, int participantId, IEnumerable<string>? names, int byParticipantId)
        {
            var wanted = PlanningTaskRules.CleanRoleNames(names);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL",
                    participantId, planningId) != 1)
                return "Personen är inte med i planeringen.";
            var current = db.Fetch<PlanningRoleRow>(
                "SELECT * FROM PlanningRole WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND ParticipantId = @1 AND RemovedAt IS NULL",
                planningId, participantId);
            foreach (var r in current.Where(r => !wanted.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
                db.Execute("UPDATE PlanningRole SET RemovedAt = @1, RemovedBy = @2 WHERE Id = @0", r.Id, now, byParticipantId);
            foreach (var n in wanted.Where(n => !current.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))))
                db.Insert(new PlanningRoleRow
                {
                    PlanningId = planningId, ParticipantId = participantId, Name = n, RoleKey = PlanningTaskRules.RoleKeyFor(n),
                    AddedAt = now, AddedBy = byParticipantId,
                });
            Bump(db, planningId);
            return null;
        }
    }
}
