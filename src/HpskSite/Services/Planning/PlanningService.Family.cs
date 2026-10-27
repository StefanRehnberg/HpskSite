using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// FAMILJEN: en huvudplanering och dess delplaneringar (en nivå). Stefan 2026-10-25 (Expertgruppen, "Ta ett steg
    /// tillbaka"): område och delplanering är TVÅ skilda saker med var sin plats.
    ///
    /// <list type="bullet">
    /// <item><b>En delplanering skapas direkt</b> (<see cref="CreateSub"/>) — för en gren, en tävling eller en förening som
    ///   sköter en del, med egna deltagare, möten, uppgifter, bemanning och tävlingskoppling. Aldrig ur ett område.</item>
    /// <item><b>Ett område flyttar</b> inom familjen, åt båda hållen (<see cref="MoveAreas"/>) — med sina uppgifter, trådar och
    ///   dokumentkategorier. Det är så en del av arbetet "går till" en delplanering, och så den tas tillbaka. Det ersätter
    ///   den gamla utdelningen (<c>SplitArea</c>), där området försvann och inte gick att få tillbaka.</item>
    /// <item><b>Huvudplaneringens sammankallande är sammankallande i varje delplanering</b> (<see cref="EnsureParentOwner"/>)
    ///   — förr blev hen <i>Kan ändra</i> och kunde varken koppla en tävling eller byta ut någon där (Göran, S3).</item>
    /// <item>Kan ändra i huvudplaneringen LÄSER delplaneringarna (<see cref="PlanningFamily.ReadsSubViaParentSql"/>).</item>
    /// </list>
    ///
    /// <para>Behörigheten prövas av anroparen. Allt som flyttar gör det i EN transaktion — en halvt flyttad del är värre än
    /// ingen.</para>
    /// </summary>
    public partial class PlanningService
    {
        /// <summary>En planering i listsidans familjer: id, namn, läge, mål och huvudplaneringen (null = en huvudplanering).</summary>
        public sealed class HubFamilyRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";
            public string? TargetText { get; set; }
            public int? ParentPlanningId { get; set; }
        }

        /// <summary>
        /// Listsidan (Stefan 2026-10-25: delplaneringarna under sin huvudplanering). För medlemmens planeringar
        /// <paramref name="mineIds"/>: varje planerings huvudplanering (<c>Parents</c>, för dem medlemmen inte själv är med i —
        /// de visas som en rubrik) och huvudplaneringens delplaneringar som medlemmen får LÄSA men inte är med i
        /// (<c>ReadableSubs</c>, <see cref="PlanningFamily.ReadsSubViaParentSql"/>). <c>ParentOf</c> = huvudplaneringen för var
        /// och en av medlemmens planeringar.
        /// </summary>
        public (Dictionary<int, int> ParentOf, List<HubFamilyRow> Parents, List<HubFamilyRow> ReadableSubs) HubFamilies(int memberId, IReadOnlyCollection<int> mineIds)
        {
            var empty = (new Dictionary<int, int>(), new List<HubFamilyRow>(), new List<HubFamilyRow>());
            if (memberId <= 0 || mineIds.Count == 0) return empty;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var ids = string.Join(",", mineIds);
            var mine = db.Fetch<HubFamilyRow>($"SELECT Id, Name, Status, TargetText, ParentPlanningId FROM Planning WHERE Id IN ({ids}) AND DeletedAt IS NULL");
            var parentOf = mine.Where(p => p.ParentPlanningId > 0).ToDictionary(p => p.Id, p => p.ParentPlanningId!.Value);
            var missing = parentOf.Values.Distinct().Where(id => !mineIds.Contains(id)).ToList();
            var parents = missing.Count == 0 ? new List<HubFamilyRow>()
                : db.Fetch<HubFamilyRow>($"SELECT Id, Name, Status, TargetText, ParentPlanningId FROM Planning WHERE Id IN ({string.Join(",", missing)}) AND DeletedAt IS NULL");
            var subs = db.Fetch<HubFamilyRow>($@"SELECT s.Id, s.Name, s.Status, s.TargetText, s.ParentPlanningId FROM Planning s
                 WHERE s.DeletedAt IS NULL AND s.ParentPlanningId IN ({ids}) AND s.Id NOT IN ({ids})
                   AND EXISTS (SELECT 1 FROM PlanningMember m WHERE m.PlanningId = s.ParentPlanningId AND m.MemberId = @0
                               AND {PlanningFamily.ReadsSubViaParentSql})
                 ORDER BY s.Name", memberId);
            return (parentOf, parents, subs);
        }

        /// <summary>Får medlemmen LÄSA delplaneringen <paramref name="p"/> genom huvudplaneringen (<see cref="PlanningFamily"/>)?</summary>
        public bool ReadsSubViaParent(PlanningRow p, int memberId)
        {
            if (!(p.ParentPlanningId > 0) || memberId <= 0) return false;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>($@"SELECT COUNT(*) FROM PlanningMember m JOIN Planning p ON p.Id = m.PlanningId
                WHERE m.PlanningId = @0 AND m.MemberId = @1 AND p.DeletedAt IS NULL AND {PlanningFamily.ReadsSubViaParentSql}",
                p.ParentPlanningId!.Value, memberId) > 0;
        }

        /// <summary>
        /// Stefan 2026-10-25 (B4): huvudplaneringens sammankallande är SAMMANKALLANDE i varje delplanering. Saknar hen en rad
        /// (en sammankallande som tillkommit efter att delplaneringen skapades), eller har hen en lägre nivå, får hen det
        /// här — första gången hen öppnar delplaneringen. Notisläget för en ny rad är <i>Bara när någon nämner mig</i>:
        /// huvudplaneringens ledning ska inte få kvällsmejl från varje del (Stefans beslut omgång 2). Null = inte
        /// sammankallande i huvudplaneringen.
        /// </summary>
        public PlanningMemberRow? EnsureParentOwner(PlanningRow sub, int memberId, string displayName)
        {
            if (!(sub.ParentPlanningId > 0) || memberId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var parentRow = db.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1 AND RemovedAt IS NULL AND ViaCompetition = 0", sub.ParentPlanningId!.Value, memberId);
            if (parentRow == null || parentRow.Access != PlanningAccessLevel.Owner) return null;
            return EnsureOwnerRow(db, sub.Id, memberId, parentRow, displayName, Now);
        }

        private PlanningMemberRow EnsureOwnerRow(IUmbracoDatabase db, int planningId, int memberId, PlanningMemberRow source, string? displayName, DateTime now)
        {
            var row = db.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND MemberId = @1", planningId, memberId);
            var maxId = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;
            if (row != null)
            {
                if (row.IsActive && row.Access == PlanningAccessLevel.Owner && !row.ViaCompetition) return row;
                var wasActive = row.IsActive;
                row.Access = PlanningAccessLevel.Owner; row.ViaCompetition = false; row.RemovedAt = null; row.RemovedBy = null;
                db.Update(row);
                if (!wasActive) EnsureRead(db, planningId, row.Id, maxId);
                Bump(db, planningId);
                return row;
            }
            row = new PlanningMemberRow
            {
                PlanningId = planningId, MemberId = memberId, DisplayName = Clean(displayName, 200) ?? source.DisplayName,
                Access = PlanningAccessLevel.Owner, NotifyMode = PlanningNotifyMode.Mentions,
                RepresentsClubId = source.RepresentsClubId, RepresentsRegionId = source.RepresentsRegionId, RepresentsText = source.RepresentsText,
                AddedAt = now, AddedBy = memberId,
            };
            db.Insert(row);
            EnsureRead(db, planningId, row.Id, maxId);
            Bump(db, planningId);
            return row;
        }

        /// <summary>Familjen: huvudplaneringen och dess delplaneringar (en nivå), i den ordningen (<see cref="PlanningFamily"/>).</summary>
        public List<PlanningRow> Family(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return PlanningFamily.Rows(s.Database, planningId);
        }

        /// <summary>
        /// N5: planeringen i familjen där uppgiften nu ligger, när den inte ligger i <paramref name="planningId"/> (den har
        /// flyttat med sitt område). Null när uppgiften inte finns i familjen, eller är borttagen.
        /// </summary>
        public PlanningRow? TaskMovedTo(int planningId, int taskId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<PlanningRow>(@"SELECT f.* FROM PlanningTask t
                  JOIN Planning f ON f.Id = t.PlanningId AND f.DeletedAt IS NULL
                  JOIN Planning p ON p.Id = @0 AND p.FamilyRootId = f.FamilyRootId
                 WHERE t.Id = @1 AND t.PlanningId <> @0 AND t.DeletedAt IS NULL", planningId, taskId);
        }

        private sealed class FamilyLink { public int PlanningId { get; set; } public int TargetId { get; set; } }

        /// <summary>Familjens kopplingar, planering → tävling.</summary>
        public Dictionary<int, int> FamilyLinks(IReadOnlyCollection<PlanningRow> family)
        {
            if (family.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<FamilyLink>($"SELECT PlanningId, TargetId FROM PlanningLink WHERE TargetKind = N'competition' AND PlanningId IN ({string.Join(",", family.Select(f => f.Id))})")
                .GroupBy(l => l.PlanningId).ToDictionary(g => g.Key, g => g.First().TargetId);
        }

        /// <summary>Den tävling planeringens bemanning gäller (<see cref="PlanningFamilyRules.StaffingCompetition"/>).</summary>
        public int? StaffingCompetitionFor(int planningId)
        {
            var family = Family(planningId);
            var p = family.FirstOrDefault(f => f.Id == planningId);
            return p == null ? null : PlanningFamilyRules.StaffingCompetition(p, FamilyLinks(family));
        }

        /// <summary>
        /// "Bemannar för": vilken av familjens kopplade tävlingar planeringens bemanning gäller (null = huvudplaneringens).
        /// Tävlingen måste vara kopplad till en planering i familjen. Bemanningens rader räknas om i samma transaktion.
        /// </summary>
        public string? SetStaffsFor(int planningId, int? competitionId, int byMemberId)
        {
            var family = Family(planningId);
            var p = family.FirstOrDefault(f => f.Id == planningId);
            if (p == null) return "Planeringen finns inte.";
            var links = FamilyLinks(family);
            if (links.ContainsKey(planningId)) return "Planeringen är kopplad till en egen tävling — bemanningen gäller den.";
            if (competitionId > 0 && !links.Values.Contains(competitionId.Value)) return "Tävlingen hör inte till någon planering i familjen.";
            using var s = _scopes.CreateScope();   // INTE autoComplete: valet och bemanningens omräkning hör ihop
            var db = s.Database;
            db.Execute("UPDATE Planning SET StaffsForCompetitionId = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0",
                planningId, competitionId > 0 ? competitionId : null, Now, byMemberId);
            NoteRevokedAccess(db, HpskSite.Services.Staffing.StaffingPlanningLink.RecomputeFamily(db, planningId), Now);
            Bump(db, planningId);
            s.Complete();
            return null;
        }

        /// <summary>Samma person (medlem, annars gästens adress) som aktiv deltagare i familjens ANDRA planeringar.</summary>
        public List<(PlanningRow Planning, PlanningMemberRow Participant)> FamilyMemberships(int planningId, PlanningMemberRow person)
        {
            var list = new List<(PlanningRow, PlanningMemberRow)>();
            foreach (var f in Family(planningId).Where(f => f.Id != planningId))
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                var row = person.MemberId > 0
                    ? s.Database.SingleOrDefault<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1 AND RemovedAt IS NULL", f.Id, person.MemberId)
                    : string.IsNullOrEmpty(person.GuestEmail) ? null
                    : s.Database.SingleOrDefault<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0 AND MemberId IS NULL AND GuestEmail = @1 AND RemovedAt IS NULL", f.Id, person.GuestEmail);
                if (row != null) list.Add((f, row));
            }
            return list;
        }

        public sealed record SubPlanningSummary(int Id, string Name, string Status, string Conveners, int OpenTasks, int Tasks,
            int OverdueTasks, string? NextMilestoneTitle, DateTime? NextMilestoneDue, string? NextMeetingTitle, DateTime? NextMeetingAt,
            DateTime? ChangedAt, bool MemberIsParticipant, string? DisciplineKey, int? HostClubId, int? HostRegionId, string? HostText);

        /// <summary>
        /// Kolumnen "Sammankallande" i Delplaneringar. <paramref name="ownOnly"/> är delens ledare som INTE också leder huvudplaneringen; är den listan tom men delen har en ledare
        /// är det huvudplaneringens egen — då står det så, i stället för ett streck som läses som "ingen".
        /// </summary>
        public static string ConvenersText(IReadOnlyList<string> ownOnly, bool partHasOwners) =>
            ownOnly.Count > 0 ? string.Join(", ", ownOnly) : partHasOwners ? "Som huvudplaneringen" : "";

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
                // Sammankallande = delens egna ledare; huvudplaneringens sammankallande (som också är det där) räknas inte upp.
                var conv = db.Fetch<string>(@"SELECT m.DisplayName FROM PlanningMember m WHERE m.PlanningId = @0 AND m.RemovedAt IS NULL AND m.Access = N'Owner'
                    AND NOT EXISTS (SELECT 1 FROM PlanningMember pm WHERE pm.PlanningId = @1 AND pm.MemberId = m.MemberId AND pm.RemovedAt IS NULL AND pm.Access = N'Owner')
                    ORDER BY m.DisplayName", k.Id, planningId);
                // Anders (persontest 11): ägde samma person huvudplaneringen och delen blev listan tom och kolumnen "—". Då är delens ledare "som huvudplaneringen".
                var hasOwners = conv.Count > 0 || db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND RemovedAt IS NULL AND Access = N'Owner'", k.Id) > 0;
                var convText = ConvenersText(conv, hasOwners);
                var tasks = db.Fetch<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE PlanningId = @0 AND DeletedAt IS NULL", k.Id);
                var open = tasks.Where(t => PlanningTaskStatus.IsOpen(t.Status)).ToList();
                var ms = open.Where(t => t.IsMilestone && t.DueDate.HasValue).OrderBy(t => t.DueDate).FirstOrDefault();
                var meet = db.FirstOrDefault<PlanningMeetingRow>(@"SELECT TOP 1 * FROM PlanningMeeting WHERE PlanningId = @0 AND DeletedAt IS NULL
                    AND Status = N'Scheduled' AND StartsAt >= @1 ORDER BY StartsAt", k.Id, Now);
                var changed = db.ExecuteScalar<DateTime?>("SELECT ChangedAt FROM PlanningActivity WHERE PlanningId = @0", k.Id);
                var mine = memberId > 0 && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1 AND RemovedAt IS NULL", k.Id, memberId) > 0;
                list.Add(new SubPlanningSummary(k.Id, k.Name, k.Status, convText, open.Count, tasks.Count,
                    open.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < today), ms?.Title, ms?.DueDate,
                    meet?.Title, meet?.StartsAt, changed, mine, k.DisciplineKey, k.HostClubId, k.HostRegionId, k.HostText));
            }
            return list;
        }

        // =====================================================================================
        //  Ny delplanering
        // =====================================================================================

        /// <param name="AreaIds">Huvudplaneringens områden som flyttar med (med uppgifter, trådar och dokumentkategorier).</param>
        public sealed record SubInput(string Name, string? DisciplineKey, int ConvenerParticipantId, DateTime? Start, DateTime? End,
            int? HostClubId, int? HostRegionId, string? HostText, IReadOnlyCollection<int> AreaIds, int ByMemberId);

        public sealed record SubReport(int PlanningId, string Name, MoveReport? Moved);

        /// <summary>
        /// Skapar en delplanering under <paramref name="parentId"/>. Den valda deltagaren (med konto) blir sammankallande,
        /// och huvudplaneringens sammankallande är det också. Valda områden flyttar dit i samma transaktion.
        /// </summary>
        public (SubReport? Report, string? Error) CreateSub(int parentId, SubInput i)
        {
            var name = (i.Name ?? "").Trim();
            var err = Validate(name, i.Start, i.End);
            if (err != null) return (null, err);
            var now = Now;
            using var outer = _scopes.CreateScope();
            var db = outer.Database;

            var parent = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WITH (UPDLOCK) WHERE Id = @0 AND DeletedAt IS NULL", parentId);
            if (parent == null) return (null, "Planeringen finns inte.");
            if (parent.ParentPlanningId > 0) return (null, "En delplanering har inga egna delplaneringar — skapa den i huvudplaneringen.");
            var people = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0", parentId).ToDictionary(p => p.Id);
            if (!people.TryGetValue(i.ConvenerParticipantId, out var convener) || !convener.IsActive)
                return (null, "Den du valde som sammankallande är inte med i planeringen.");
            if (convener.IsGuest)
                return (null, $"{convener.DisplayName} har inget konto på pistol.nu och kan inte leda planeringen. Välj någon med konto.");

            var (childId, ownerPid) = Create(new CreateInput(name, PlanningKind.Group, null, i.Start ?? parent.TargetStart, i.End ?? parent.TargetEnd,
                convener.MemberId!.Value, convener.DisplayName, convener.RepresentsClubId, convener.RepresentsRegionId));
            var child = db.Single<PlanningRow>("SELECT * FROM Planning WHERE Id = @0", childId);
            child.ParentPlanningId = parent.Id;
            child.DisciplineKey = string.IsNullOrWhiteSpace(i.DisciplineKey) ? null : i.DisciplineKey;
            child.DoneAtMeetingsOnly = parent.DoneAtMeetingsOnly;
            child.SignupRoleChoice = parent.SignupRoleChoice;
            child.ShooterMode = parent.ShooterMode;
            child.WholeDayRoles = parent.WholeDayRoles;
            child.ShootBeforeMin = parent.ShootBeforeMin; child.ShootDurationMin = parent.ShootDurationMin; child.ShootAfterMin = parent.ShootAfterMin;
            child.HostClubId = i.HostClubId > 0 ? i.HostClubId : null;
            child.HostRegionId = child.HostClubId.HasValue ? null : (i.HostRegionId > 0 ? i.HostRegionId : null);
            child.HostText = child.HostClubId.HasValue || child.HostRegionId.HasValue ? null : Clean(i.HostText, 200);
            child.CreatedBy = i.ByMemberId; child.UpdatedBy = i.ByMemberId;
            db.Update(child);
            if (!string.IsNullOrEmpty(convener.RepresentsText))
                db.Execute("UPDATE PlanningMember SET RepresentsText = @1 WHERE Id = @0", ownerPid, convener.RepresentsText);

            // Huvudplaneringens sammankallande (B4) — sammankallande även här.
            foreach (var o in people.Values.Where(p => p.IsActive && p.Access == PlanningAccessLevel.Owner && p.MemberId > 0 && !p.ViaCompetition
                                                        && p.MemberId != convener.MemberId))
                EnsureOwnerRow(db, childId, o.MemberId!.Value, o, o.DisplayName, now);

            InsertSystem(db, childId, "planning-sub-created", $"Planeringen är en delplanering av {parent.Name}.", ownerPid, null, null, now);
            InsertSystem(db, parentId, "planning-sub-made", $"Delplaneringen {name} skapades, med {convener.DisplayName} som sammankallande.", null, null, null, now);

            MoveReport? moved = null;
            if (i.AreaIds.Count > 0)
            {
                // Ett fel här ångrar allt (scopet slutförs inte) — ingen halv delplanering.
                var (rep, moveErr) = MoveAreasCore(db, parent, child, i.AreaIds, i.ByMemberId, now);
                if (moveErr != null) return (null, moveErr);
                moved = rep;
            }
            Bump(db, parentId);
            Bump(db, childId);
            outer.Complete();
            return (new SubReport(childId, name, moved), null);
        }

        /// <summary>Stefan B11: vem som sköter en delplanering — en klubb, en krets eller fri text. Bara på en delplanering.</summary>
        public string? SetSubHost(int planningId, int? hostClubId, int? hostRegionId, string? hostText, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return "Planeringen finns inte.";
            if (!(p.ParentPlanningId > 0)) return "Bara en delplanering sköts av någon.";
            p.HostClubId = hostClubId > 0 ? hostClubId : null;
            p.HostRegionId = p.HostClubId.HasValue ? null : (hostRegionId > 0 ? hostRegionId : null);
            p.HostText = p.HostClubId.HasValue || p.HostRegionId.HasValue ? null : Clean(hostText, 200);
            p.UpdatedAt = Now; p.UpdatedBy = byMemberId;
            db.Update(p);
            Bump(db, planningId);
            return null;
        }

        // =====================================================================================
        //  Flytta områden inom familjen
        // =====================================================================================

        public sealed record MoveReport(int Areas, int Tasks, int Threads, int Documents, int Detached, List<string> Joined, string TargetName);

        public sealed record MovePreviewResult(int Tasks, int OpenTasks, int Threads, int Documents, int Detached, List<string> Joining, string? Error);

        /// <summary>Det som flyttar om områdena flyttas till <paramref name="targetId"/> — och vilka som då blir med där. Skriver inget.</summary>
        public MovePreviewResult MovePreview(int sourceId, int targetId, IReadOnlyCollection<int> areaIds)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var err = targetId > 0 ? MoveTargetError(db, sourceId, targetId) : null;
            if (err != null) return new MovePreviewResult(0, 0, 0, 0, 0, new(), err);
            var plan = PlanMove(db, sourceId, areaIds);
            if (plan.Error != null) return new MovePreviewResult(0, 0, 0, 0, 0, new(), plan.Error);
            var target = targetId > 0 ? db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0", targetId) : new List<PlanningMemberRow>();
            var joining = plan.Workers.Select(pid => plan.People.GetValueOrDefault(pid))
                .Where(p => p is { IsActive: true } && FindTwin(target, p) == null).Select(p => p!.DisplayName).Distinct().OrderBy(n => n).ToList();
            return new MovePreviewResult(plan.Tasks.Count(t => t.DeletedAt == null), plan.Tasks.Count(t => t.DeletedAt == null && PlanningTaskStatus.IsOpen(t.Status)),
                plan.Messages.Count(m => m.ReplyToMessageId == null), plan.DocIds.Count, plan.Detached.Count, joining, null);
        }

        /// <summary>Flyttar områdena från <paramref name="sourceId"/> till <paramref name="targetId"/> (samma familj, åt båda hållen).</summary>
        public (MoveReport? Report, string? Error) MoveAreas(int sourceId, int targetId, IReadOnlyCollection<int> areaIds, int byMemberId)
        {
            using var outer = _scopes.CreateScope();
            var db = outer.Database;
            var err = MoveTargetError(db, sourceId, targetId);
            if (err != null) return (null, err);
            // Lås planeringarna i id-ordning, så att två flyttar åt var sitt håll inte låser varandra.
            foreach (var id in new[] { sourceId, targetId }.OrderBy(x => x))
                db.ExecuteScalar<int>("SELECT COUNT(*) FROM Planning WITH (UPDLOCK) WHERE Id = @0", id);
            var source = db.Single<PlanningRow>("SELECT * FROM Planning WHERE Id = @0", sourceId);
            var target = db.Single<PlanningRow>("SELECT * FROM Planning WHERE Id = @0", targetId);
            var r = MoveAreasCore(db, source, target, areaIds, byMemberId, Now);
            if (r.Error != null) return r;
            Bump(db, sourceId);
            Bump(db, targetId);
            outer.Complete();
            return r;
        }

        private static string? MoveTargetError(IUmbracoDatabase db, int sourceId, int targetId)
        {
            if (sourceId == targetId) return "Välj en annan planering än den området redan ligger i.";
            return PlanningFamily.SameFamily(db, sourceId, targetId) ? null
                : "Områden flyttar bara mellan en huvudplanering och dess delplaneringar.";
        }

        private sealed record MovePlan(List<PlanningAreaRow> Areas, List<PlanningTaskRow> Tasks, List<PlanningMessageRow> Messages,
            List<PlanningDocumentCategoryRow> Categories, HashSet<int> DocIds, List<PlanningDocumentRefRow> Moving, List<PlanningDocumentRefRow> Detached,
            Dictionary<int, PlanningMemberRow> People, HashSet<int> Workers, string? Error);

        /// <summary>
        /// Vad som flyttar: områdena, deras uppgifter (även borttagna — historiken följer med), trådarna märkta med områdena
        /// och svaren, områdenas dokumentkategorier med dokumenten, och ett okategoriserat dokument vars alla bifogningar
        /// flyttar. En bifogning mellan något som flyttar och något som stannar släpps. "Arbetare" = områdets ansvarige och
        /// de som har en öppen uppgift där — de blir med (med sin nivå) där arbetet hamnar.
        /// </summary>
        private static MovePlan PlanMove(IUmbracoDatabase db, int sourceId, IReadOnlyCollection<int> areaIds)
        {
            var ids = areaIds.Distinct().ToList();
            var none = new MovePlan(new(), new(), new(), new(), new(), new(), new(), new(), new(), null);
            if (ids.Count == 0) return none with { Error = "Välj minst ett område." };
            var inList = string.Join(",", ids);
            var areas = db.Fetch<PlanningAreaRow>($"SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL AND Id IN ({inList})", sourceId);
            if (areas.Count != ids.Count) return none with { Error = "Något av områdena finns inte längre här. Läs om sidan och försök igen." };
            var tasks = db.Fetch<PlanningTaskRow>($"SELECT * FROM PlanningTask WITH (UPDLOCK) WHERE PlanningId = @0 AND AreaId IN ({inList})", sourceId);
            var taskIds = tasks.Select(t => t.Id).ToHashSet();
            var msgs = new List<PlanningMessageRow>();
            foreach (var a in areas) msgs.AddRange(MovedMessages(db, sourceId, a.Id));
            var msgIds = msgs.Select(m => m.Id).ToHashSet();
            var cats = db.Fetch<PlanningDocumentCategoryRow>($"SELECT * FROM PlanningDocumentCategory WHERE PlanningId = @0 AND DeletedAt IS NULL AND AreaId IN ({inList})", sourceId);
            var catIds = cats.Select(c => c.Id).ToHashSet();
            var docs = db.Fetch<PlanningDocumentRow>("SELECT * FROM PlanningDocument WHERE PlanningId = @0 AND DeletedAt IS NULL", sourceId);
            var refs = ActiveRefs(db, sourceId);
            bool TargetMoves(PlanningDocumentRefRow r) =>
                (r.TargetKind == PlanningDocTarget.Task && taskIds.Contains(r.TargetId))
                || (r.TargetKind == PlanningDocTarget.Message && msgIds.Contains(r.TargetId))
                || (r.TargetKind == PlanningDocTarget.Area && ids.Contains(r.TargetId));
            var docIds = docs.Where(d => d.CategoryId.HasValue && catIds.Contains(d.CategoryId.Value)).Select(d => d.Id).ToHashSet();
            foreach (var d in docs.Where(d => !d.CategoryId.HasValue))
            {
                var on = refs.Where(r => r.DocumentId == d.Id).ToList();
                if (on.Count > 0 && on.All(TargetMoves)) docIds.Add(d.Id);
            }
            var moving = refs.Where(r => docIds.Contains(r.DocumentId) && TargetMoves(r)).ToList();
            // Släpps: en bifogning där bara den ena sidan flyttar — och en bifogning till ett flyttat OMRÅDE vars dokument
            // stannar (förr pekade den kvar på ett borttaget område, Expertgruppen databas 4).
            var detached = refs.Where(r => docIds.Contains(r.DocumentId) != TargetMoves(r)).ToList();
            var people = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0", sourceId).ToDictionary(p => p.Id);
            var workers = new HashSet<int>();
            foreach (var a in areas) if (a.ResponsibleParticipantId > 0) workers.Add(a.ResponsibleParticipantId!.Value);
            foreach (var t in tasks.Where(t => t.DeletedAt == null && PlanningTaskStatus.IsOpen(t.Status) && t.AssigneeParticipantId > 0))
                workers.Add(t.AssigneeParticipantId!.Value);
            return new MovePlan(areas, tasks, msgs, cats, docIds, moving, detached, people, workers, null);
        }

        private static PlanningMemberRow? FindTwin(IEnumerable<PlanningMemberRow> rows, PlanningMemberRow p) =>
            p.MemberId > 0 ? rows.FirstOrDefault(o => o.MemberId == p.MemberId)
            : string.IsNullOrEmpty(p.GuestEmail) ? null
            : rows.FirstOrDefault(o => o.MemberId == null && string.Equals(o.GuestEmail, p.GuestEmail, StringComparison.OrdinalIgnoreCase));

        private (MoveReport? Report, string? Error) MoveAreasCore(IUmbracoDatabase db, PlanningRow source, PlanningRow target,
            IReadOnlyCollection<int> areaIds, int byMemberId, DateTime now)
        {
            var plan = PlanMove(db, source.Id, areaIds);
            if (plan.Error != null) return (null, plan.Error);

            // ---- deltagarna: samma person är samma rad i målet; annars en ny rad ----
            var targetRows = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK) WHERE PlanningId = @0", target.Id);
            var maxMsg = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", target.Id) ?? 0;
            var map = new Dictionary<int, int?>();
            var joined = new List<string>();
            var fallback = targetRows.FirstOrDefault(r => r.MemberId == byMemberId && r.IsActive)?.Id
                ?? targetRows.FirstOrDefault(r => r.IsActive && r.Access == PlanningAccessLevel.Owner)?.Id ?? 0;
            int? R(int? pid)
            {
                if (!(pid > 0)) return null;
                if (map.TryGetValue(pid!.Value, out var m)) return m;
                if (!plan.People.TryGetValue(pid.Value, out var src)) return map[pid.Value] = null;
                var twin = FindTwin(targetRows, src);
                if (twin != null) return map[pid.Value] = twin.Id;
                // Den som gör arbetet (områdets ansvarige, den som har en öppen uppgift) blir med där det hamnar, med sin
                // nivå (en sammankallande blir Kan ändra — målets ledning är redan bestämd). Den som bara skrivit eller
                // gjort något följer, så att namnen står kvar och hen kan läsa sitt. Den som lämnat är det inte heller där.
                var works = src.IsActive && plan.Workers.Contains(src.Id);
                // Den som kan ändra i huvudplaneringen LÄSER redan delplaneringen genom den (B3) — en vilande rad bär namnet,
                // en aktiv Följer-rad skulle göra hen till deltagare i stället för läsare.
                var active = src.IsActive && (works || !(target.ParentPlanningId == source.Id && PlanningFamily.ReadsSubViaParent(src)));
                var row = new PlanningMemberRow
                {
                    PlanningId = target.Id, MemberId = src.MemberId, DisplayName = src.DisplayName, GuestEmail = src.GuestEmail,
                    Access = works ? (src.Access == PlanningAccessLevel.Owner ? PlanningAccessLevel.Editor : src.Access) : PlanningAccessLevel.Follower,
                    RepresentsClubId = src.RepresentsClubId, RepresentsRegionId = src.RepresentsRegionId, RepresentsText = src.RepresentsText,
                    NotifyMode = src.NotifyMode, AddedAt = now, AddedBy = byMemberId,
                    RemovedAt = active ? null : now, RemovedBy = active ? null : byMemberId,
                };
                db.Insert(row);
                targetRows.Add(row);
                if (row.IsActive) { EnsureRead(db, target.Id, row.Id, maxMsg); joined.Add(src.DisplayName); }
                return map[pid.Value] = row.Id;
            }

            // ---- områdena: en ny rad i målet (eller målets område med samma namn); den gamla tas bort ----
            // (FK (PlanningId, AreaId): ett område kan inte byta planering medan något pekar på det — därför en ny rad.)
            var targetAreas = db.Fetch<PlanningAreaRow>("SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL", target.Id);
            var sort = targetAreas.Count == 0 ? 0 : targetAreas.Max(a => a.SortOrder);
            var areaMap = new Dictionary<int, int>();
            foreach (var a in plan.Areas.OrderBy(a => a.SortOrder).ThenBy(a => a.Id))
            {
                var resp = R(a.ResponsibleParticipantId);
                var same = targetAreas.FirstOrDefault(t => string.Equals(t.Name, a.Name, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    if (same.ResponsibleParticipantId == null && string.IsNullOrEmpty(same.ResponsibleName))
                    {
                        same.ResponsibleParticipantId = resp; same.ResponsibleName = resp > 0 ? null : a.ResponsibleName;
                        db.Update(same);
                    }
                    areaMap[a.Id] = same.Id;
                    continue;
                }
                var na = new PlanningAreaRow
                {
                    PlanningId = target.Id, Name = a.Name, ResponsibleParticipantId = resp, ResponsibleName = resp > 0 ? null : a.ResponsibleName,
                    SortOrder = ++sort, CreatedAt = now, CreatedBy = R(a.CreatedBy) ?? fallback,
                };
                db.Insert(na);
                targetAreas.Add(na);
                areaMap[a.Id] = na.Id;
            }

            // ---- uppgifterna och deras historik (mötena stannar: kopplingen till ett möte släpps) ----
            var taskIds = plan.Tasks.Select(t => t.Id).ToHashSet();
            foreach (var t in plan.Tasks)
                db.Execute(@"UPDATE PlanningTask SET PlanningId = @1, AreaId = @2, AssigneeParticipantId = @3, CreatedBy = @4, DoneBy = @5,
                        LatestReportBy = @6, SentToForbundetBy = @7, FromMeetingId = NULL, DoneAtMeetingId = NULL WHERE Id = @0",
                    t.Id, target.Id, areaMap[t.AreaId!.Value], R(t.AssigneeParticipantId), R(t.CreatedBy) ?? fallback, R(t.DoneBy),
                    R(t.LatestReportBy), R(t.SentToForbundetBy));
            if (taskIds.Count > 0)
                foreach (var e in db.Fetch<PlanningTaskEventRow>($"SELECT * FROM PlanningTaskEvent WHERE PlanningId = @0 AND TaskId IN ({string.Join(",", taskIds)})", source.Id))
                    db.Execute("UPDATE PlanningTaskEvent SET PlanningId = @1, ParticipantId = @2, MeetingId = NULL WHERE Id = @0", e.Id, target.Id, R(e.ParticipantId));

            // ---- trådarna, svaren och omnämnandena ----
            var msgIds = plan.Messages.Select(m => m.Id).ToHashSet();
            foreach (var m in plan.Messages)
            {
                // En hänvisning till något som stannar (ett möte, ett förslag) släpps.
                var keepRef = m.RefKind == "task" && m.RefId.HasValue && taskIds.Contains(m.RefId.Value);
                int? area = m.AreaId.HasValue && areaMap.TryGetValue(m.AreaId.Value, out var am) ? am : null;
                db.Execute(@"UPDATE PlanningMessage SET PlanningId = @1, AreaId = @2, ParticipantId = @3, SubjectParticipantId = @4,
                        RefKind = @5, RefId = @6 WHERE Id = @0",
                    m.Id, target.Id, area, R(m.ParticipantId), R(m.SubjectParticipantId), keepRef ? m.RefKind : null, keepRef ? m.RefId : null);
            }
            if (msgIds.Count > 0)
                foreach (var mn in db.Fetch<PlanningMentionRow>($"SELECT * FROM PlanningMessageMention WHERE MessageId IN ({string.Join(",", msgIds)})"))
                {
                    var to = R(mn.ParticipantId);
                    if (to.HasValue) db.Execute("UPDATE PlanningMessageMention SET PlanningId = @1, ParticipantId = @2 WHERE Id = @0", mn.Id, target.Id, to.Value);
                    else db.Execute("DELETE FROM PlanningMessageMention WHERE Id = @0", mn.Id);
                }

            // ---- dokumentkategorierna och dokumenten ----
            var targetCats = db.Fetch<PlanningDocumentCategoryRow>("SELECT * FROM PlanningDocumentCategory WHERE PlanningId = @0 AND DeletedAt IS NULL", target.Id);
            var catMap = new Dictionary<int, int>();
            foreach (var c in plan.Categories)
            {
                var same = targetCats.FirstOrDefault(t => string.Equals(t.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    catMap[c.Id] = same.Id;
                    db.Execute("UPDATE PlanningDocumentCategory SET DeletedAt = @1 WHERE Id = @0", c.Id, now);
                    continue;
                }
                db.Execute("UPDATE PlanningDocumentCategory SET PlanningId = @1, AreaId = @2, SortOrder = @3, CreatedBy = @4 WHERE Id = @0",
                    c.Id, target.Id, areaMap[c.AreaId!.Value], targetCats.Count + catMap.Count, R(c.CreatedBy) ?? fallback);
                catMap[c.Id] = c.Id;
            }
            var docs = plan.DocIds.Count == 0 ? new List<PlanningDocumentRow>()
                : db.Fetch<PlanningDocumentRow>($"SELECT * FROM PlanningDocument WHERE Id IN ({string.Join(",", plan.DocIds)})");
            foreach (var d in docs)
                db.Execute("UPDATE PlanningDocument SET PlanningId = @1, CategoryId = @2, AddedBy = @3 WHERE Id = @0",
                    d.Id, target.Id, d.CategoryId.HasValue && catMap.TryGetValue(d.CategoryId.Value, out var nc) ? nc : null, R(d.AddedBy) ?? fallback);
            foreach (var r in plan.Moving)
                db.Execute("UPDATE PlanningDocumentRef SET PlanningId = @1, TargetId = @2, AddedBy = @3 WHERE Id = @0", r.Id, target.Id,
                    r.TargetKind == PlanningDocTarget.Area ? areaMap[r.TargetId] : r.TargetId, R(r.AddedBy) ?? fallback);
            foreach (var r in plan.Detached)
                db.Execute("UPDATE PlanningDocumentRef SET RemovedAt = @1 WHERE Id = @0", r.Id, now);

            // ---- områdena i källan: borta (allt som pekade på dem har flyttat) ----
            foreach (var a in plan.Areas)
                db.Execute("UPDATE PlanningArea SET DeletedAt = @1 WHERE Id = @0", a.Id, now);

            // Den flyttade historiken är inte "ny" för någon i målet.
            var maxMoved = msgIds.Count == 0 ? 0 : msgIds.Max();
            foreach (var pid in map.Values.Where(v => v > 0).Select(v => v!.Value).Distinct())
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE Id = @0 AND RemovedAt IS NULL", pid) > 0)
                    EnsureRead(db, target.Id, pid, Math.Max(maxMoved, maxMsg));

            var names = string.Join(", ", plan.Areas.Select(a => a.Name));
            var taskCount = plan.Tasks.Count(t => t.DeletedAt == null);
            var threads = plan.Messages.Count(m => m.ReplyToMessageId == null);
            var what = $"{taskCount} {(taskCount == 1 ? "uppgift" : "uppgifter")} och {threads} {(threads == 1 ? "tråd" : "trådar")}";
            var areaWord = plan.Areas.Count == 1 ? "Området" : "Områdena";
            InsertSystem(db, source.Id, "areas-moved-out", $"{areaWord} {names} flyttades till {target.Name}: {what}.", null, null, null, now);
            InsertSystem(db, target.Id, "areas-moved-in",
                $"{areaWord} {names} flyttades hit från {source.Name}: {what}."
                + (joined.Count == 0 ? "" : $" {string.Join(", ", joined.Distinct())} är nu med här."), null, null, null, now);
            return (new MoveReport(plan.Areas.Count, taskCount, threads, docs.Count, plan.Detached.Count, joined.Distinct().ToList(), target.Name), null);
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
