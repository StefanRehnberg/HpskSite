using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Omgång 2 steg 5 (Stefan 2026-10-24): ett område delas ut till en EGEN planering — en delplanering, en nivå —
    /// med egna deltagare, möten, meddelanden, uppgifter, dokument, bemanning och tävlingskoppling.
    ///
    /// <para><b>Vad som flyttar:</b> områdets uppgifter (med sin historik) och dess trådar (med svaren), och de dokument
    /// den sammankallande väljer. Deltagarna KOPIERAS — de som väljs är med i båda. Den som syns i det som flyttar men
    /// inte valdes (en författare, en som hade en uppgift) följer med som <i>inte längre med</i>, så att namnen står kvar.
    /// Mötena, förslagen och bemanningen stannar i huvudplaneringen.</para>
    ///
    /// <para><b>Vad som släpps:</b> en bifogning mellan något som flyttar och något som stannar (ett dokument som stannar
    /// men var bifogat till en uppgift som flyttar, eller tvärtom) — de två ligger i olika planeringar. Antalet sägs.</para>
    ///
    /// <para>Behörigheten prövas av anroparen (<see cref="PlanningAccess.CanSplitArea"/>). Allt i EN transaktion — en
    /// halvt flyttad del är värre än ingen.</para>
    /// </summary>
    public partial class PlanningService
    {
        /// <summary>Sammankallande (aktiv) i planeringens huvudplanering — den som får LÄSA delplaneringen.</summary>
        public bool IsParentConvener(PlanningRow p, int memberId)
        {
            if (!(p.ParentPlanningId > 0) || memberId <= 0) return false;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>(@"SELECT COUNT(*) FROM PlanningMember m JOIN Planning p ON p.Id = m.PlanningId
                WHERE m.PlanningId = @0 AND m.MemberId = @1 AND m.RemovedAt IS NULL AND m.Access = N'Owner' AND p.DeletedAt IS NULL",
                p.ParentPlanningId!.Value, memberId) > 0;
        }

        public sealed record SubPlanningSummary(int Id, string Name, string Status, string Conveners, int OpenTasks, int Tasks,
            int OverdueTasks, string? NextMilestoneTitle, DateTime? NextMilestoneDue, string? NextMeetingTitle, DateTime? NextMeetingAt,
            DateTime? ChangedAt, bool MemberIsParticipant);

        /// <summary>Delplaneringarnas läge, för huvudplaneringens Översikt. Bara siffror och namn — innehållet läses i delplaneringen.</summary>
        public List<SubPlanningSummary> SubPlannings(int planningId, int memberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var kids = db.Fetch<PlanningRow>("SELECT * FROM Planning WHERE ParentPlanningId = @0 AND DeletedAt IS NULL ORDER BY Name", planningId);
            var list = new List<SubPlanningSummary>();
            var today = Now.Date;
            foreach (var k in kids)
            {
                var conv = db.Fetch<string>("SELECT DisplayName FROM PlanningMember WHERE PlanningId = @0 AND RemovedAt IS NULL AND Access = N'Owner' ORDER BY DisplayName", k.Id);
                var tasks = db.Fetch<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE PlanningId = @0 AND DeletedAt IS NULL", k.Id);
                var open = tasks.Where(t => PlanningTaskStatus.IsOpen(t.Status)).ToList();
                var ms = open.Where(t => t.IsMilestone && t.DueDate.HasValue).OrderBy(t => t.DueDate).FirstOrDefault();
                var meet = db.FirstOrDefault<PlanningMeetingRow>(@"SELECT TOP 1 * FROM PlanningMeeting WHERE PlanningId = @0 AND DeletedAt IS NULL
                    AND Status = N'Scheduled' AND StartsAt >= @1 ORDER BY StartsAt", k.Id, Now);
                var changed = db.ExecuteScalar<DateTime?>("SELECT ChangedAt FROM PlanningActivity WHERE PlanningId = @0", k.Id);
                var mine = memberId > 0 && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1 AND RemovedAt IS NULL", k.Id, memberId) > 0;
                list.Add(new SubPlanningSummary(k.Id, k.Name, k.Status, string.Join(", ", conv), open.Count, tasks.Count,
                    open.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < today), ms?.Title, ms?.DueDate,
                    meet?.Title, meet?.StartsAt, changed, mine));
            }
            return list;
        }

        public sealed record SplitPreviewResult(int Tasks, int OpenTasks, int Threads, int Messages,
            List<int> SuggestedParticipantIds, List<int> SuggestedDocumentIds, int? SuggestedConvenerId, string? Error);

        /// <summary>Det som flyttar om området delas ut, och förslagen till deltagare och dokument. Skriver ingenting.</summary>
        public SplitPreviewResult SplitPreview(int planningId, int areaId, int byParticipantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var area = db.SingleOrDefault<PlanningAreaRow>("SELECT * FROM PlanningArea WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", areaId, planningId);
            if (area == null) return new SplitPreviewResult(0, 0, 0, 0, new(), new(), null, "Området finns inte.");
            var tasks = db.Fetch<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE PlanningId = @0 AND AreaId = @1 AND DeletedAt IS NULL", planningId, areaId);
            var msgs = MovedMessages(db, planningId, areaId);
            var active = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0 AND RemovedAt IS NULL", planningId)
                .ToDictionary(p => p.Id);
            // Förslaget: den som ansvarar för området, de som har områdets öppna uppgifter, och den som delar ut.
            var people = new List<int>();
            void Add(int? pid) { if (pid > 0 && active.ContainsKey(pid.Value) && !people.Contains(pid.Value)) people.Add(pid.Value); }
            Add(area.ResponsibleParticipantId);
            foreach (var t in tasks.Where(t => PlanningTaskStatus.IsOpen(t.Status))) Add(t.AssigneeParticipantId);
            Add(byParticipantId);
            // Sammankallande: områdets ansvarige om hen har konto, annars den som delar ut.
            int? convener = area.ResponsibleParticipantId > 0 && active.TryGetValue(area.ResponsibleParticipantId!.Value, out var resp) && !resp.IsGuest
                ? resp.Id : (active.ContainsKey(byParticipantId) ? byParticipantId : null);
            // Dokumenten: områdets kategoriers, och de som är bifogade till det som flyttar (eller till området).
            var taskIds = tasks.Select(t => t.Id).ToList();
            var msgIds = msgs.Select(m => m.Id).ToList();
            var docs = db.Fetch<int>(@"SELECT d.Id FROM PlanningDocument d JOIN PlanningDocumentCategory c ON c.Id = d.CategoryId
                WHERE d.PlanningId = @0 AND d.DeletedAt IS NULL AND c.AreaId = @1 AND c.DeletedAt IS NULL", planningId, areaId);
            foreach (var r in ActiveRefs(db, planningId))
            {
                var hit = (r.TargetKind == PlanningDocTarget.Task && taskIds.Contains(r.TargetId))
                    || (r.TargetKind == PlanningDocTarget.Message && msgIds.Contains(r.TargetId))
                    || (r.TargetKind == PlanningDocTarget.Area && r.TargetId == areaId);
                if (hit && !docs.Contains(r.DocumentId)) docs.Add(r.DocumentId);
            }
            var live = db.Fetch<int>("SELECT Id FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId).ToHashSet();
            return new SplitPreviewResult(tasks.Count, tasks.Count(t => PlanningTaskStatus.IsOpen(t.Status)),
                msgs.Count(m => m.ReplyToMessageId == null), msgs.Count, people, docs.Where(live.Contains).ToList(), convener, null);
        }

        public sealed record SplitInput(int AreaId, string Name, string? DisciplineKey, int ConvenerParticipantId,
            IReadOnlyCollection<int> ParticipantIds, IReadOnlyCollection<int> DocumentIds, int ByMemberId, int ByParticipantId);

        public sealed record SplitReport(int PlanningId, string Name, int Tasks, int Messages, int Participants, int Documents, int Detached);

        public (SplitReport? Report, string? Error) SplitArea(int planningId, SplitInput i)
        {
            var name = (i.Name ?? "").Trim();
            var err = Validate(name, null, null);
            if (err != null) return (null, err);
            var now = Now;
            using var outer = _scopes.CreateScope(autoComplete: true);
            var db = outer.Database;

            var parent = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WITH (UPDLOCK) WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (parent == null) return (null, "Planeringen finns inte.");
            if (parent.ParentPlanningId > 0) return (null, "En delplanering kan inte delas upp i sin tur — dela ut området från huvudplaneringen.");
            var area = db.SingleOrDefault<PlanningAreaRow>("SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", i.AreaId, planningId);
            if (area == null) return (null, "Området finns inte.");

            var all = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0", planningId).ToDictionary(p => p.Id);
            if (!all.TryGetValue(i.ConvenerParticipantId, out var convener) || !convener.IsActive)
                return (null, "Den du valde som sammankallande är inte med i planeringen.");
            if (convener.IsGuest)
                return (null, $"{convener.DisplayName} har inget konto på pistol.nu och kan inte leda planeringen. Välj någon med konto.");
            var chosen = i.ParticipantIds.Distinct().ToList();
            if (chosen.Any(pid => !all.TryGetValue(pid, out var row) || !row.IsActive))
                return (null, "Någon av dem du valde är inte med i planeringen längre. Läs om sidan och försök igen.");

            var liveDocs = db.Fetch<PlanningDocumentRow>("SELECT * FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId).ToDictionary(d => d.Id);
            var docIds = i.DocumentIds.Distinct().ToList();
            if (docIds.Any(d => !liveDocs.ContainsKey(d))) return (null, "Något av dokumenten finns inte längre. Läs om sidan och försök igen.");

            // ---- den nya planeringen, med den valda som sammankallande ----
            var (childId, ownerPid) = Create(new CreateInput(name, PlanningKind.Group, parent.TargetText, parent.TargetStart, parent.TargetEnd,
                convener.MemberId!.Value, convener.DisplayName, convener.RepresentsClubId, convener.RepresentsRegionId));
            var child = db.Single<PlanningRow>("SELECT * FROM Planning WHERE Id = @0", childId);
            child.ParentPlanningId = parent.Id;
            child.DisciplineKey = string.IsNullOrWhiteSpace(i.DisciplineKey) ? parent.DisciplineKey : i.DisciplineKey;
            child.DoneAtMeetingsOnly = parent.DoneAtMeetingsOnly;
            child.SignupRoleChoice = parent.SignupRoleChoice;
            child.CreatedBy = i.ByMemberId; child.UpdatedBy = i.ByMemberId;
            db.Update(child);
            if (!string.IsNullOrEmpty(convener.RepresentsText))
                db.Execute("UPDATE PlanningMember SET RepresentsText = @1 WHERE Id = @0", ownerPid, convener.RepresentsText);

            // ---- deltagarna: de valda är med (nivån följer med, men bara den valda leder) ----
            var map = new Dictionary<int, int> { [convener.Id] = ownerPid };
            int CopyRow(PlanningMemberRow src, bool active)
            {
                var row = new PlanningMemberRow
                {
                    PlanningId = childId, MemberId = src.MemberId, DisplayName = src.DisplayName, GuestEmail = src.GuestEmail,
                    Access = src.Access == PlanningAccessLevel.Owner ? PlanningAccessLevel.Editor : src.Access,
                    RepresentsClubId = src.RepresentsClubId, RepresentsRegionId = src.RepresentsRegionId, RepresentsText = src.RepresentsText,
                    NotifyMode = src.NotifyMode, AddedAt = now, AddedBy = i.ByMemberId,
                    RemovedAt = active ? null : now, RemovedBy = active ? null : i.ByMemberId,
                };
                db.Insert(row);
                return row.Id;
            }
            foreach (var pid in chosen.Where(pid => pid != convener.Id)) map[pid] = CopyRow(all[pid], true);
            var activeCount = map.Count;

            // ---- det som flyttar ----
            var tasks = db.Fetch<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE PlanningId = @0 AND AreaId = @1", planningId, area.Id);
            var taskIds = tasks.Select(t => t.Id).ToHashSet();
            var events = taskIds.Count == 0 ? new List<PlanningTaskEventRow>()
                : db.Fetch<PlanningTaskEventRow>($"SELECT * FROM PlanningTaskEvent WHERE PlanningId = @0 AND TaskId IN ({string.Join(",", taskIds)})", planningId);
            var msgs = MovedMessages(db, planningId, area.Id);
            var msgIds = msgs.Select(m => m.Id).ToHashSet();
            var mentions = msgIds.Count == 0 ? new List<PlanningMentionRow>()
                : db.Fetch<PlanningMentionRow>($"SELECT * FROM PlanningMessageMention WHERE MessageId IN ({string.Join(",", msgIds)})");
            var docSet = docIds.ToHashSet();
            var refs = ActiveRefs(db, planningId);
            bool TargetMoves(PlanningDocumentRefRow r) =>
                (r.TargetKind == PlanningDocTarget.Task && taskIds.Contains(r.TargetId)) || (r.TargetKind == PlanningDocTarget.Message && msgIds.Contains(r.TargetId));
            var refsMoving = refs.Where(r => docSet.Contains(r.DocumentId) && TargetMoves(r)).ToList();
            var refsDetached = refs.Where(r => docSet.Contains(r.DocumentId) != TargetMoves(r)
                && (docSet.Contains(r.DocumentId) || TargetMoves(r))).ToList();

            // Alla som nämns i det som flyttar och inte valdes: med som "inte längre med", så att namnen står kvar.
            int? R(int? pid)
            {
                if (!(pid > 0)) return null;
                if (map.TryGetValue(pid!.Value, out var m)) return m;
                if (!all.TryGetValue(pid.Value, out var src)) return null;
                // Samma medlem får bara EN rad i planeringen (UX_PlanningMember_Member).
                var twin = src.MemberId > 0 ? all.Values.FirstOrDefault(o => o.MemberId == src.MemberId && map.ContainsKey(o.Id)) : null;
                return map[pid.Value] = twin != null ? map[twin.Id] : CopyRow(src, false);
            }

            // P4: ett område med stationer följer med som område i delplaneringen — stationerna, värden och beskrivningen
            // behövs i stationsöversikten. Utan stationer är delplaneringen själv området.
            int? childArea = null;
            if (!string.IsNullOrWhiteSpace(area.Stations))
            {
                var ca = new PlanningAreaRow
                {
                    PlanningId = childId, Name = area.Name, Description = area.Description, HostClubId = area.HostClubId,
                    HostRegionId = area.HostRegionId, HostText = area.HostText, Stations = area.Stations, ResponsibleName = area.ResponsibleName, ResponsibleParticipantId = R(area.ResponsibleParticipantId),
                    SortOrder = 1, CreatedAt = now, CreatedBy = ownerPid,
                };
                db.Insert(ca);
                childArea = ca.Id;
            }

            foreach (var t in tasks)
            {
                db.Execute(@"UPDATE PlanningTask SET PlanningId = @1, AreaId = @7, AssigneeParticipantId = @2, CreatedBy = @3, DoneBy = @4,
                        LatestReportBy = @5, SentToForbundetBy = @6, FromMeetingId = NULL, DoneAtMeetingId = NULL WHERE Id = @0",
                    t.Id, childId, R(t.AssigneeParticipantId), R(t.CreatedBy) ?? ownerPid, R(t.DoneBy), R(t.LatestReportBy), R(t.SentToForbundetBy), childArea);
            }
            foreach (var e in events)
                db.Execute("UPDATE PlanningTaskEvent SET PlanningId = @1, ParticipantId = @2, MeetingId = NULL WHERE Id = @0", e.Id, childId, R(e.ParticipantId));
            foreach (var m in msgs)
            {
                // En hänvisning till något som stannar (ett möte, ett förslag) släpps.
                var keepRef = m.RefKind == "task" && m.RefId.HasValue && taskIds.Contains(m.RefId.Value);
                db.Execute(@"UPDATE PlanningMessage SET PlanningId = @1, AreaId = @6, ParticipantId = @2, SubjectParticipantId = @3,
                        RefKind = @4, RefId = @5 WHERE Id = @0",
                    m.Id, childId, R(m.ParticipantId), R(m.SubjectParticipantId), keepRef ? m.RefKind : null, keepRef ? m.RefId : null, childArea);
            }
            foreach (var mn in mentions)
            {
                var to = R(mn.ParticipantId);
                if (to.HasValue) db.Execute("UPDATE PlanningMessageMention SET PlanningId = @1, ParticipantId = @2 WHERE Id = @0", mn.Id, childId, to.Value);
                else db.Execute("DELETE FROM PlanningMessageMention WHERE Id = @0", mn.Id);
            }

            // Dokumenten: samma kategori (namnet) i den nya planeringen.
            var catNames = db.Fetch<PlanningDocumentCategoryRow>("SELECT * FROM PlanningDocumentCategory WHERE PlanningId = @0", planningId).ToDictionary(c => c.Id, c => c.Name);
            var childCats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var did in docIds)
            {
                var d = liveDocs[did];
                int? cat = null;
                if (d.CategoryId.HasValue && catNames.TryGetValue(d.CategoryId.Value, out var cn))
                {
                    if (!childCats.TryGetValue(cn, out var nc))
                    {
                        var row = new PlanningDocumentCategoryRow { PlanningId = childId, Name = cn, SortOrder = childCats.Count, CreatedAt = now, CreatedBy = ownerPid };
                        db.Insert(row);
                        childCats[cn] = nc = row.Id;
                    }
                    cat = nc;
                }
                db.Execute("UPDATE PlanningDocument SET PlanningId = @1, CategoryId = @2, AddedBy = @3 WHERE Id = @0", did, childId, cat, R(d.AddedBy) ?? ownerPid);
            }
            foreach (var r in refsMoving)
                db.Execute("UPDATE PlanningDocumentRef SET PlanningId = @1, AddedBy = @2 WHERE Id = @0", r.Id, childId, R(r.AddedBy) ?? ownerPid);
            foreach (var r in refsDetached)
                db.Execute("UPDATE PlanningDocumentRef SET RemovedAt = @1 WHERE Id = @0", r.Id, now);

            // Den flyttade historiken är inte "ny" för någon i den nya planeringen.
            var maxMoved = msgIds.Count == 0 ? 0 : msgIds.Max();
            var childMax = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", childId) ?? 0;
            foreach (var pid in map.Values.Distinct())
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE Id = @0 AND RemovedAt IS NULL", pid) > 0)
                    EnsureRead(db, childId, pid, Math.Max(maxMoved, childMax));

            // Området i huvudplaneringen: borta — det är nu en egen planering. Kategorierna står kvar, utan område.
            db.Execute("UPDATE PlanningArea SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1", area.Id, planningId, now);
            db.Execute("UPDATE PlanningDocumentCategory SET AreaId = NULL WHERE PlanningId = @0 AND AreaId = @1", planningId, area.Id);

            var what = $"{tasks.Count(t => t.DeletedAt == null)} uppgifter och {msgs.Count(m => m.ReplyToMessageId == null)} trådar";
            InsertSystem(db, childId, "planning-split-child",
                $"Planeringen är en del av {parent.Name}. Den skapades ur området {area.Name}: {what} flyttades hit."
                + (string.IsNullOrWhiteSpace(area.Description) ? "" : $" Områdets beskrivning: {area.Description.Trim()}"), ownerPid, null, null, now);
            InsertSystem(db, planningId, "planning-split",
                $"Området {area.Name} delades ut till en egen planering, {name}: {what} flyttades dit.", null, null, null, now);
            Bump(db, planningId);
            Bump(db, childId);
            return (new SplitReport(childId, name, tasks.Count(t => t.DeletedAt == null), msgs.Count, activeCount, docIds.Count, refsDetached.Count), null);
        }

        /// <summary>Områdets trådar: meddelandena märkta med området, och svaren på dem (svaren ärver tråden).</summary>
        private static List<PlanningMessageRow> MovedMessages(IUmbracoDatabase db, int planningId, int areaId)
        {
            var list = db.Fetch<PlanningMessageRow>("SELECT * FROM PlanningMessage WHERE PlanningId = @0 AND AreaId = @1", planningId, areaId);
            var ids = list.Select(m => m.Id).ToHashSet();
            if (ids.Count > 0)
                foreach (var r in db.Fetch<PlanningMessageRow>($"SELECT * FROM PlanningMessage WHERE PlanningId = @0 AND ReplyToMessageId IN ({string.Join(",", ids)})", planningId))
                    if (ids.Add(r.Id)) list.Add(r);
            return list;
        }

        private static List<PlanningDocumentRefRow> ActiveRefs(IUmbracoDatabase db, int planningId) =>
            db.Fetch<PlanningDocumentRefRow>("SELECT * FROM PlanningDocumentRef WHERE PlanningId = @0 AND RemovedAt IS NULL", planningId);
    }
}
