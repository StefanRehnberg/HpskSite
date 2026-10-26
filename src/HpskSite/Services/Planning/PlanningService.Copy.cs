using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Kopiera planeringen och lämna över ledningen (P3 steg C). Behörigheten prövas av anroparen (bara den
    /// sammankallande); inga notiser skickas härifrån.
    /// </summary>
    public partial class PlanningService
    {
        /// <param name="SourceAnchor">Källans dag: den kopplade tävlingens, annars planeringens första.</param>
        /// <param name="SourceCompetitionId">Den kopplade tävlingen — bemanningens äldre rader kan ligga på den.</param>
        /// <param name="Subs">Stefan 2026-10-25 (B7): delplaneringarna som följer med — var och ens dag och tävling (samma
        /// slags uppgifter som <paramref name="SourceAnchor"/>/<paramref name="SourceCompetitionId"/>). Null = inga.</param>
        public sealed record CopyInput(string Name, DateTime? TargetStart, DateTime? TargetEnd,
            int RecipientMemberId, string RecipientName, int? RecipientClubId,
            int CopierMemberId, string CopierName, int? CopierClubId, bool AddCopier,
            DateTime? SourceAnchor, int? SourceCompetitionId,
            IReadOnlyDictionary<int, (DateTime? Anchor, int? CompetitionId)>? Subs = null);

        /// <summary>
        /// En ny planering med källans form. Mottagaren blir sammankallande; den som kopierar är med som Kan ändra
        /// om hen vill (och inte själv är mottagaren). Allt i EN transaktion — en halv kopia är värre än ingen.
        /// </summary>
        public (PlanningCopyReport? Report, string? Error) Copy(int sourceId, CopyInput i)
        {
            var err = Validate(i.Name, i.TargetStart, i.TargetEnd);
            if (err != null) return (null, err);
            using var outer = _scopes.CreateScope();
            var db = outer.Database;
            var (report, e) = CopyOne(db, sourceId, i, null);
            if (e != null || report == null) return (null, e);
            // B7: delplaneringarna följer med — med sina områden och uppgifter, samma mottagare, flyttade lika mycket som
            // huvudplaneringen. Bara från en huvudplanering (en delplanering har inga egna).
            var subs = 0;
            if (i.Subs != null)
            {
                var offset = PlanningCopyRules.Offset(i.SourceAnchor, i.TargetStart);
                foreach (var sub in db.Fetch<PlanningRow>("SELECT * FROM Planning WHERE ParentPlanningId = @0 AND DeletedAt IS NULL ORDER BY Name, Id", sourceId))
                {
                    var (anchor, comp) = i.Subs.TryGetValue(sub.Id, out var a) ? a : (sub.TargetStart, (int?)null);
                    var start = PlanningCopyRules.Shift(sub.TargetStart ?? anchor, offset);
                    var end = PlanningCopyRules.Shift(sub.TargetEnd, offset);
                    var (sr, se) = CopyOne(db, sub.Id, i with { Name = sub.Name, TargetStart = start, TargetEnd = end ?? start,
                        SourceAnchor = anchor ?? sub.TargetStart, SourceCompetitionId = comp, Subs = null }, report.PlanningId);
                    if (se != null || sr == null) return (null, $"Delplaneringen {sub.Name}: {se}");
                    subs++;
                }
            }
            outer.Complete();
            return (report with { SubPlannings = subs }, null);
        }

        private (PlanningCopyReport? Report, string? Error) CopyOne(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int sourceId, CopyInput i, int? parentId)
        {
            var now = Now;
            var src = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", sourceId);
            if (src == null) return (null, "Planeringen finns inte.");

            var (newId, ownerPid) = Create(new CreateInput(i.Name, src.Kind, null, i.TargetStart, i.TargetEnd,
                i.RecipientMemberId, i.RecipientName, i.RecipientClubId, null));
            var copy = db.Single<PlanningRow>("SELECT * FROM Planning WHERE Id = @0", newId);
            copy.DisciplineKey = src.DisciplineKey;
            copy.SignupRoleChoice = src.SignupRoleChoice;
            copy.ShootBeforeMin = src.ShootBeforeMin; copy.ShootDurationMin = src.ShootDurationMin; copy.ShootAfterMin = src.ShootAfterMin;
            copy.DoneAtMeetingsOnly = src.DoneAtMeetingsOnly;
            copy.GroupName = src.GroupName;
            copy.CopiedFromPlanningId = src.Id;
            copy.Parts = src.Parts;               // Omgång 3 steg 10: samma delar som förlagan
            copy.RolesChosen = src.RolesChosen;
            copy.ParentPlanningId = parentId;
            copy.HostClubId = src.HostClubId; copy.HostRegionId = src.HostRegionId; copy.HostText = src.HostText;
            copy.CreatedBy = i.CopierMemberId; copy.UpdatedBy = i.CopierMemberId;
            db.Update(copy);

            var offset = PlanningCopyRules.Offset(i.SourceAnchor, i.TargetStart);

            // Områdena: namn och ordning, och en ansvarig som fritt namn — aldrig en deltagare (personerna följer inte med).
            var areaMap = new Dictionary<int, int>();
            foreach (var a in db.Fetch<PlanningAreaRow>("SELECT * FROM PlanningArea WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", sourceId))
            {
                var na = new PlanningAreaRow
                {
                    PlanningId = newId, Name = a.Name, ResponsibleName = a.ResponsibleName, SortOrder = a.SortOrder,
                    CreatedAt = now, CreatedBy = ownerPid,
                };
                db.Insert(na);
                areaMap[a.Id] = na.Id;
            }

            // Uppgifterna och milstolparna: från början igen, utan ansvarig deltagare.
            int taskCount = 0, datesDropped = 0;
            foreach (var t in db.Fetch<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", sourceId))
            {
                DateTime? due;
                if (t.DueDaysBefore.HasValue) due = PlanningTaskRules.DueFromDaysBefore(t.DueDaysBefore, i.TargetStart);
                else
                {
                    due = PlanningCopyRules.ShiftDeadline(t.DueDate, i.SourceAnchor, i.TargetStart);
                    if (t.DueDate.HasValue && due == null) datesDropped++;
                }
                db.Insert(new PlanningTaskRow
                {
                    PlanningId = newId, Title = t.Title, Description = t.Description, AssigneeName = t.AssigneeName,
                    DueDaysBefore = t.DueDaysBefore, DueDate = due,
                    AreaId = t.AreaId.HasValue && areaMap.TryGetValue(t.AreaId.Value, out var am) ? am : null,
                    ScopeType = t.ScopeType, ScopeKey = t.ScopeKey, EstimatedCost = t.EstimatedCost, SortOrder = t.SortOrder,
                    IsMilestone = t.IsMilestone, SendToForbundet = t.SendToForbundet, SourceKind = t.SourceKind, SourceKey = t.SourceKey,
                    Status = PlanningTaskStatus.NotStarted, CreatedAt = now, CreatedBy = ownerPid, UpdatedAt = now,
                });
                taskCount++;
            }

            // Bemanningens roller: planeringens egna, och tävlingens för de nycklar planeringen inte har egna.
            var comp = i.SourceCompetitionId is > 0 ? i.SourceCompetitionId.Value : -1;
            var roles = db.Execute(@"INSERT INTO StaffRole (OwnerType, OwnerId, RoleKey, DisplayName, PluralName, DefaultScopeType, SupportsTargetRange,
                    SupportsFunctionTitle, Description, NeedsJson, Disciplines, SortOrder, IsActive, CreatedByMemberId, CreatedDate, ModifiedDate)
                SELECT N'Planning', @0, r.RoleKey, r.DisplayName, r.PluralName, r.DefaultScopeType, r.SupportsTargetRange,
                    r.SupportsFunctionTitle, r.Description, r.NeedsJson, r.Disciplines, r.SortOrder, r.IsActive, @3, @4, @4
                FROM StaffRole r
                WHERE (r.OwnerType = N'Planning' AND r.OwnerId = @1)
                   OR (r.OwnerType = N'Competition' AND r.OwnerId = @2
                       AND NOT EXISTS (SELECT 1 FROM StaffRole p WHERE p.OwnerType = N'Planning' AND p.OwnerId = @1 AND p.RoleKey = r.RoleKey))",
                newId, sourceId, comp, i.CopierMemberId, now);

            // Dagarna och behoven per dag flyttas; utan en dag att räkna från följer de inte med.
            var owner = $"(PlanningId = @1 OR (PlanningId IS NULL AND CompetitionId = @2))";
            int days = 0, daysDropped = 0;
            if (offset.HasValue)
                days = db.Execute($@"INSERT INTO StaffDay (CompetitionId, PlanningId, DayDate, Label, Kind, SortOrder, CreatedByMemberId, CreatedDate)
                    SELECT NULL, @0, DATEADD(day, @3, DayDate), Label, Kind, SortOrder, @4, @5 FROM StaffDay WHERE {owner}",
                    newId, sourceId, comp, offset.Value, i.CopierMemberId, now);
            else daysDropped = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM StaffDay WHERE {owner}", newId, sourceId, comp);
            var needs = offset.HasValue
                // Passens behov följer inte med (passen kopieras inte); stationens behov (ScopeKey) gör det.
                ? db.Execute($@"INSERT INTO StaffCrewNeed (CompetitionId, PlanningId, ScopeKind, RoleKey, Count, DayDate, ScopeKey)
                    SELECT NULL, @0, ScopeKind, RoleKey, Count, CASE WHEN DayDate IS NULL THEN NULL ELSE DATEADD(day, @3, DayDate) END, ScopeKey
                    FROM StaffCrewNeed WHERE {owner} AND PassId IS NULL", newId, sourceId, comp, offset.Value)
                : db.Execute($@"INSERT INTO StaffCrewNeed (CompetitionId, PlanningId, ScopeKind, RoleKey, Count, DayDate, ScopeKey)
                    SELECT NULL, @0, ScopeKind, RoleKey, Count, NULL, ScopeKey FROM StaffCrewNeed WHERE {owner} AND DayDate IS NULL AND PassId IS NULL", newId, sourceId, comp);

            if (i.AddCopier && i.CopierMemberId != i.RecipientMemberId)
            {
                var add = AddParticipant(newId, i.CopierMemberId, i.CopierName, null, PlanningAccessLevel.Editor, i.CopierClubId, null, null, i.CopierMemberId);
                if (add.Error != null) return (null, add.Error);
            }
            InsertSystem(db, newId, "planning-copied", $"Planeringen är en kopia av {src.Name}.", ownerPid, null, null, now);
            if (parentId == null) InsertSystem(db, sourceId, "planning-copy-made", $"Planeringen kopierades till {i.Name.Trim()}.", null, null, null, now);
            Bump(db, sourceId);
            return (new PlanningCopyReport(newId, areaMap.Count, taskCount, roles, days, needs, datesDropped, daysDropped), null);
        }

        /// <summary>
        /// Lämnar över ledningen: en annan deltagare blir sammankallande och den som lämnar blir Kan ändra, i ett steg.
        /// En gäst utan konto kan inte leda (ledningen kräver inloggning).
        /// </summary>
        public string? HandOver(int planningId, int fromParticipantId, int toParticipantId)
        {
            if (fromParticipantId == toParticipantId) return "Välj någon annan än dig själv.";
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var from = db.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL", fromParticipantId, planningId);
            var to = db.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL", toParticipantId, planningId);
            if (from == null || from.Access != PlanningAccessLevel.Owner) return "Det är den sammankallande som lämnar över.";
            if (to == null) return "Den du valde är inte med i planeringen.";
            if (to.IsGuest) return $"{to.DisplayName} har inget konto på pistol.nu och kan inte leda planeringen. Välj någon med konto.";
            to.Access = PlanningAccessLevel.Owner; to.ViaCompetition = false;
            from.Access = PlanningAccessLevel.Editor;
            db.Update(to); db.Update(from);
            InsertSystem(db, planningId, "handover", $"{from.DisplayName} lämnade över ledningen till {to.DisplayName}, som nu är sammankallande.",
                from.Id, to.Id, null, now);
            Bump(db, planningId);
            return null;
        }
    }
}
