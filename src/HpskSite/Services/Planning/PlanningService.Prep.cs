using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// P2 (Stefan 2026-10-23): förberedelserna flyttar in i planeringen. Kopplingen till tävlingen,
    /// tillgång via tävlingen, områden, dokument, kommentarer, mallar, kretsens checklista, stationer och
    /// flytten av de gamla förberedelserna (<c>WorkArea</c>/<c>WorkItem</c>). Se
    /// notes/tavlingsplanering-p2-plan-2026-10-23.md.
    ///
    /// <para>Samma regler som resten av tjänsten: behörigheten prövas av anroparen, varje id prövas mot
    /// planeringen, och inga notiser skickas härifrån. Det som behöver tävlingen (datum, gren, namn på
    /// medlemmar) får anroparen läsa och skicka in — tjänsten känner bara planeringens tabeller.</para>
    /// </summary>
    public partial class PlanningService
    {
        public const int MaxAreaName = 200;
        public const int MaxDocTitle = 200;
        public const int MaxComment = 4000;
        public const int MaxPasteRows = 300;

        // =====================================================================================
        //  Kopplingen till tävlingen
        // =====================================================================================

        public PlanningLinkRow? LinkFor(int planningId)
        {
            if (planningId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<PlanningLinkRow>(
                "SELECT TOP 1 * FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition' ORDER BY Id", planningId);
        }

        /// <summary>Planeringen tävlingen hör till, om någon (en raderad planering räknas inte).</summary>
        public PlanningRow? ForCompetition(int competitionId)
        {
            if (competitionId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<PlanningRow>(
                @"SELECT p.* FROM PlanningLink l JOIN Planning p ON p.Id = l.PlanningId
                  WHERE l.TargetKind = N'competition' AND l.TargetId = @0 AND p.DeletedAt IS NULL", competitionId);
        }

        /// <summary>Omgång 3 (K6): alla tävlingar som redan har en planering, i en fråga (urvalet av tävlingar att koppla).</summary>
        public HashSet<int> LinkedCompetitionIds()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<int>(
                @"SELECT l.TargetId FROM PlanningLink l JOIN Planning p ON p.Id = l.PlanningId
                  WHERE l.TargetKind = N'competition' AND p.DeletedAt IS NULL").ToHashSet();
        }

        /// <summary>Hur många uppgifter tävlingens gamla förberedelselista har (flyttas in när planeringen startas).</summary>
        public int OldPrepCount(int competitionId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            try { return s.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM WorkItem WHERE CompetitionId = @0", competitionId); }
            catch { return 0; }
        }

        /// <summary>Kopplade tävlingar per planering, för listan (en fråga).</summary>
        public Dictionary<int, int> LinkedCompetitions(IEnumerable<int> planningIds)
        {
            var ids = planningIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, int>();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningLinkRow>(
                    "SELECT * FROM PlanningLink WHERE TargetKind = N'competition' AND PlanningId IN (@0)", ids)
                .GroupBy(l => l.PlanningId).ToDictionary(g => g.Key, g => g.First().TargetId);
        }

        public sealed record CompetitionPlanningInput(int CompetitionId, string Name, DateTime? Start, DateTime? End,
            int MemberId, string DisplayName, int? RepresentsClubId, int? RepresentsRegionId);

        /// <summary>Det den gamla förberedelselistan hade, för flytten. Namn per medlem läses av anroparen.</summary>
        public sealed record WorkBreakdownImport(Func<int, string> MemberName);

        public sealed record CreateForCompetitionResult(int PlanningId, bool Created, int ImportedTasks, string? Error);

        /// <summary>
        /// Skapar tävlingens egen planering med den som startar den som Sammankallande, kopplar den, och flyttar
        /// in tävlingens gamla förberedelser i samma transaktion. Finns redan en planering för tävlingen svaras
        /// den (två som startar samtidigt får samma planering, inte två).
        /// </summary>
        public CreateForCompetitionResult CreateForCompetition(CompetitionPlanningInput i, WorkBreakdownImport? import)
        {
            var existing = ForCompetition(i.CompetitionId);
            if (existing != null) return new CreateForCompetitionResult(existing.Id, false, 0, null);
            var err = Validate(i.Name, i.Start, i.End);
            if (err != null) return new CreateForCompetitionResult(0, false, 0, err);
            var now = Now;
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                var db = s.Database;
                // Lås kopplingarna för tävlingen: två samtidiga startar ska inte båda skapa en planering.
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningLink WITH (UPDLOCK, HOLDLOCK) WHERE TargetKind = N'competition' AND TargetId = @0",
                        i.CompetitionId) > 0)
                    return new CreateForCompetitionResult(ForCompetition(i.CompetitionId)?.Id ?? 0, false, 0, null);
                var p = new PlanningRow
                {
                    Name = i.Name.Trim(), Kind = PlanningKind.ClubCompetition, TargetStart = i.Start?.Date, TargetEnd = i.End?.Date,
                    Status = "Active", DoneAtMeetingsOnly = false, SeededFromCompetitionId = i.CompetitionId,
                    CreatedAt = now, CreatedBy = i.MemberId, UpdatedAt = now, UpdatedBy = i.MemberId,
                };
                db.Insert(p);
                var root = new PlanningPartRow { PlanningId = p.Id, Kind = "Root", Name = p.Name, SortOrder = 0, CreatedAt = now };
                db.Insert(root);
                db.Execute("INSERT INTO PlanningActivity (PlanningId, ChangeSeq, ChangedAt) VALUES (@0, 1, @1)", p.Id, now);
                var me = new PlanningMemberRow
                {
                    PlanningId = p.Id, MemberId = i.MemberId, DisplayName = Clean(i.DisplayName, 200) ?? "Medlem",
                    Access = PlanningAccessLevel.Owner, NotifyMode = PlanningNotifyMode.Default,
                    RepresentsClubId = i.RepresentsClubId, RepresentsRegionId = i.RepresentsClubId > 0 ? null : i.RepresentsRegionId,
                    AddedAt = now, AddedBy = i.MemberId, LastSeenAt = now,
                };
                db.Insert(me);
                db.Insert(new PlanningReadRow { PlanningId = p.Id, ParticipantId = me.Id, LastReadMessageId = 0, LastDigestMessageId = 0 });
                db.Insert(new PlanningLinkRow
                {
                    PlanningId = p.Id, PlanningPartId = root.Id, TargetKind = PlanningLinkRow.Competition, TargetId = i.CompetitionId,
                    LinkedAt = now, LinkedBy = i.MemberId,
                });
                InsertSystem(db, p.Id, "planning-created", "Planeringen skapades.", me.Id, null, null, now);
                // P2b: tävlingens bemanning hör nu till planeringen.
                HpskSite.Services.Staffing.StaffingPlanningLink.Attach(db, p.Id, i.CompetitionId);
                var imported = import == null ? 0 : ImportWorkBreakdown(db, p.Id, i.CompetitionId, me.Id, import, now);
                if (imported > 0)
                    InsertSystem(db, p.Id, "prep-imported", $"Tävlingens förberedelser flyttades in: {imported} uppgifter.", null, null, null, now);
                return new CreateForCompetitionResult(p.Id, true, imported, null);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Någon hann före (det unika indexet på PlanningLink eller SeededFromCompetitionId).
                var won = ForCompetition(i.CompetitionId);
                return won != null ? new CreateForCompetitionResult(won.Id, false, 0, null)
                                   : new CreateForCompetitionResult(0, false, 0, "Tävlingen har redan en planering.");
            }
        }

        private static bool IsUniqueViolation(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is Microsoft.Data.SqlClient.SqlException sql && (sql.Number == 2627 || sql.Number == 2601)) return true;
            return false;
        }

        /// <summary>
        /// Kopplar en befintlig planering till en tävling. Bara en planering per tävling, och (tills delarna
        /// finns, P3) en tävling per planering.
        /// </summary>
        public string? LinkToCompetition(int planningId, int competitionId, int byMemberId)
        {
            var now = Now;
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                var db = s.Database;
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningLink WITH (UPDLOCK, HOLDLOCK) WHERE TargetKind = N'competition' AND TargetId = @0", competitionId) > 0)
                    return "Tävlingen hör redan till en planering.";
                // Omgång 3: bara tävlingskopplingen räknas — ett områdes delkonfiguration (faltconfig) är en annan sort.
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition'", planningId) > 0)
                    return "Planeringen är redan kopplad till en tävling. En planering med flera tävlingar kommer när planeringen kan delas upp.";
                var root = db.ExecuteScalar<int?>("SELECT TOP 1 Id FROM PlanningPart WHERE PlanningId = @0 AND Kind = N'Root' AND DeletedAt IS NULL ORDER BY Id", planningId);
                if (root == null) return "Planeringen saknar sin grunddel.";
                db.Insert(new PlanningLinkRow
                {
                    PlanningId = planningId, PlanningPartId = root.Value, TargetKind = PlanningLinkRow.Competition, TargetId = competitionId,
                    LinkedAt = now, LinkedBy = byMemberId,
                });
                InsertSystem(db, planningId, "competition-linked", "Planeringen kopplades till tävlingen.", null, null, (PlanningLinkRow.Competition, competitionId), now);
                // P2b: planeringens bemanning får tävlingen, och tävlingens tidigare bemanning tas in.
                HpskSite.Services.Staffing.StaffingPlanningLink.Attach(db, planningId, competitionId);
                Bump(db, planningId);
                return null;
            }
            catch (Exception ex) when (IsUniqueViolation(ex)) { return "Tävlingen hör redan till en planering."; }
        }

        /// <summary>
        /// Kopplar loss planeringen. De som kom in genom tävlingen tappar då tillgången (raden tas bort, men
        /// står kvar som författare och i historiken).
        /// </summary>
        public string? Unlink(int planningId, int byMemberId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var oldComp = db.ExecuteScalar<int?>("SELECT TOP 1 TargetId FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition'", planningId) ?? 0;
            var n = db.Execute("DELETE FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition'", planningId);
            if (n == 0) return "Planeringen är inte kopplad till någon tävling.";
            // P2b: bemanningen stannar i planeringen och tappar tävlingen (och därmed appbehörigheten till den).
            HpskSite.Services.Staffing.StaffingPlanningLink.Detach(db, planningId, oldComp);
            db.Execute("UPDATE PlanningMember SET RemovedAt = @1, RemovedBy = @2 WHERE PlanningId = @0 AND ViaCompetition = 1 AND RemovedAt IS NULL",
                planningId, now, byMemberId);
            InsertSystem(db, planningId, "competition-unlinked", "Planeringen kopplades loss från tävlingen.", null, null, null, now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>
        /// Den som har tillgång till den kopplade tävlingen men ingen rad: får en rad med nivån Kan ändra och
        /// <see cref="PlanningMemberRow.ViaCompetition"/>. En tidigare borttagen rad återaktiveras bara när den kom
        /// via tävlingen — den som tagits bort av den sammankallande släpps inte in bakvägen.
        /// Förvalt notisläge: bara när någon nämner hen (en klubbadmin som öppnat förberedelserna en gång ska inte
        /// få ett mejl varje kväll). Uppgifter hen får mejlas ändå.
        /// </summary>
        public PlanningMemberRow? EnsureCompetitionParticipant(int planningId, int memberId, string displayName)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var row = db.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND MemberId = @1", planningId, memberId);
            var maxId = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;
            if (row != null)
            {
                if (row.IsActive) return row;
                if (!row.ViaCompetition) return null;
                row.RemovedAt = null; row.RemovedBy = null; row.Access = PlanningAccessLevel.Editor;
                row.DisplayName = Clean(displayName, 200) ?? row.DisplayName; row.LastSeenAt = now;
                db.Update(row);
                EnsureRead(db, planningId, row.Id, maxId);
                Bump(db, planningId);
                return row;
            }
            row = new PlanningMemberRow
            {
                PlanningId = planningId, MemberId = memberId, DisplayName = Clean(displayName, 200) ?? "Medlem",
                Access = PlanningAccessLevel.Editor, NotifyMode = PlanningNotifyMode.Mentions, ViaCompetition = true,
                AddedAt = now, AddedBy = memberId, LastSeenAt = now,
            };
            db.Insert(row);
            EnsureRead(db, planningId, row.Id, maxId);
            Bump(db, planningId);
            return row;
        }

        /// <summary>Den som kom in via tävlingen och inte längre har tillgång till den: raden tas bort.</summary>
        public void DropCompetitionParticipant(int planningId, int participantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE PlanningMember SET RemovedAt = @2, RemovedBy = 0 WHERE Id = @0 AND PlanningId = @1 AND ViaCompetition = 1 AND RemovedAt IS NULL",
                participantId, planningId, Now);
        }

        /// <summary>
        /// En medlem som får en uppgift men inte är med: läggs till som Deltar (Stefan 2026-10-23). En tidigare
        /// borttagen rad återaktiveras. Svarar deltagarens id.
        /// </summary>
        public int EnsureParticipantForMember(int planningId, int memberId, string displayName, int byMemberId)
        {
            var existing = ParticipantForMember(planningId, memberId);
            if (existing is { IsActive: true }) return existing.Id;
            var r = AddParticipant(planningId, memberId, displayName, null, PlanningAccessLevel.Participant, null, null, null, byMemberId);
            return r.ParticipantId;
        }

        /// <summary>"Utförd beslutas på mötena" — den sammankallandes val.</summary>
        public void SetDoneAtMeetingsOnly(int planningId, bool value, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET DoneAtMeetingsOnly = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, value, Now, byMemberId);
            Bump(s.Database, planningId);
        }

        /// <summary>
        /// Uppgifter med "N dagar före" får sin sista dag räknad från <paramref name="anchor"/> (tävlingens dag, eller
        /// planeringens startdag). En UPDATE; körs när planeringen läses så att en flyttad tävling följer med.
        /// </summary>
        public int RealignRelativeDueDates(int planningId, DateTime? anchor)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            if (anchor == null)
                return s.Database.Execute(
                    "UPDATE PlanningTask SET DueDate = NULL WHERE PlanningId = @0 AND DueDaysBefore IS NOT NULL AND DueDate IS NOT NULL AND DeletedAt IS NULL", planningId);
            return s.Database.Execute(
                @"UPDATE PlanningTask SET DueDate = DATEADD(day, -DueDaysBefore, CAST(@1 AS date))
                  WHERE PlanningId = @0 AND DueDaysBefore IS NOT NULL AND DeletedAt IS NULL
                    AND (DueDate IS NULL OR DueDate <> DATEADD(day, -DueDaysBefore, CAST(@1 AS date)))",
                planningId, anchor.Value.Date);
        }

        // =====================================================================================
        //  Områden
        // =====================================================================================

        public List<PlanningAreaRow> Areas(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningAreaRow>(
                "SELECT * FROM PlanningArea WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", planningId);
        }

        public sealed record AreaResult(int AreaId, string? Error);

        /// <summary>Omgång 2 steg 4: det delarna hade. Värden är EN sak: en klubb, en krets eller fri text.</summary>
        /// <param name="Stations">P4: stationerna området ansvarar för ("3-6, 9"); null = rör inte, tom text = inga.</param>
        public sealed record AreaDetails(string? Description, int? HostClubId, int? HostRegionId, string? HostText, string? Stations = null);

        /// <summary>Nytt område (<paramref name="areaId"/> = 0) eller ändrat. <paramref name="details"/> null = rör inte beskrivning och värd.</summary>
        public AreaResult SaveArea(int planningId, int areaId, string name, int? responsiblePid, string? responsibleName, int byParticipantId,
            AreaDetails? details = null)
        {
            var n = Clean(name, MaxAreaName);
            if (n == null) return new AreaResult(areaId, "Ge området ett namn, till exempel \"Materiel\".");
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var err = AssigneeError(db, planningId, responsiblePid);
            if (err != null) return new AreaResult(areaId, err);
            var others = db.Fetch<PlanningAreaRow>("SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId);
            if (others.Any(a => a.Id != areaId && string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                return new AreaResult(areaId, $"Det finns redan ett område som heter \"{n}\".");
            // P4: stationerna — läsbara, och inte redan ett annat områdes (i planeringen eller dess delplaneringar).
            string? stations = null;
            if (details?.Stations != null)
            {
                var (list, perr) = PlanningStations.Parse(details.Stations);
                if (perr != null) return new AreaResult(areaId, perr);
                var conflict = StationConflict(db, planningId, areaId, list);
                if (conflict != null) return new AreaResult(areaId, conflict);
                stations = list.Count == 0 ? "" : PlanningStations.Format(list);
            }
            PlanningAreaRow row;
            if (areaId > 0)
            {
                row = others.FirstOrDefault(a => a.Id == areaId)!;
                if (row == null) return new AreaResult(areaId, "Området finns inte.");
                row.Name = n;
                row.ResponsibleParticipantId = responsiblePid > 0 ? responsiblePid : null;
                row.ResponsibleName = responsiblePid > 0 ? null : Clean(responsibleName, 200);
                ApplyAreaDetails(row, details, stations);
                db.Update(row);
            }
            else
            {
                row = new PlanningAreaRow
                {
                    PlanningId = planningId, Name = n, ResponsibleParticipantId = responsiblePid > 0 ? responsiblePid : null,
                    ResponsibleName = responsiblePid > 0 ? null : Clean(responsibleName, 200),
                    SortOrder = (others.Count == 0 ? 0 : others.Max(a => a.SortOrder)) + 1, CreatedAt = now, CreatedBy = byParticipantId,
                };
                ApplyAreaDetails(row, details, stations);
                db.Insert(row);
            }
            Bump(db, planningId);
            return new AreaResult(row.Id, null);
        }

        /// <summary>Tar bort området. Uppgifterna står kvar, utan område.</summary>
        public string? DeleteArea(int planningId, int areaId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var n = db.Execute("UPDATE PlanningArea SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", areaId, planningId, Now);
            if (n == 0) return "Området finns inte.";
            db.Execute("UPDATE PlanningTask SET AreaId = NULL WHERE PlanningId = @0 AND AreaId = @1", planningId, areaId);
            db.Execute("UPDATE PlanningMessage SET AreaId = NULL WHERE PlanningId = @0 AND AreaId = @1", planningId, areaId);
            db.Execute("UPDATE PlanningDocumentCategory SET AreaId = NULL WHERE PlanningId = @0 AND AreaId = @1", planningId, areaId);
            Bump(db, planningId);
            return null;
        }

        private static void ApplyAreaDetails(PlanningAreaRow row, AreaDetails? d, string? stations = null)
        {
            if (d == null) return;
            if (stations != null) row.Stations = stations.Length == 0 ? null : stations;
            row.Description = Clean(d.Description, 2000);
            row.HostClubId = d.HostClubId > 0 ? d.HostClubId : null;
            row.HostRegionId = row.HostClubId.HasValue ? null : (d.HostRegionId > 0 ? d.HostRegionId : null);
            row.HostText = row.HostClubId.HasValue || row.HostRegionId.HasValue ? null : Clean(d.HostText, 200);
        }

        /// <summary>Flyttar området ett steg upp (-1) eller ner (+1).</summary>
        public string? MoveArea(int planningId, int areaId, int direction)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var list = db.Fetch<PlanningAreaRow>("SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY SortOrder, Id", planningId);
            var i = list.FindIndex(a => a.Id == areaId);
            var j = i + Math.Sign(direction);
            if (i < 0) return "Området finns inte.";
            if (j < 0 || j >= list.Count) return null;
            (list[i], list[j]) = (list[j], list[i]);
            for (var k = 0; k < list.Count; k++)
                db.Execute("UPDATE PlanningArea SET SortOrder = @1 WHERE Id = @0", list[k].Id, k + 1);
            Bump(db, planningId);
            return null;
        }

        private static string? AreaError(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int? areaId)
        {
            if (!(areaId > 0)) return null;
            return db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningArea WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", areaId, planningId) == 1
                ? null : "Området finns inte.";
        }

        // =====================================================================================
        //  Kommentarer
        // =====================================================================================

        public string? AddTaskComment(int planningId, int taskId, int byParticipantId, string text, bool viaEmail)
        {
            var body = Clean(text, MaxComment);
            if (body == null) return "Skriv en kommentar först.";
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var t = db.SingleOrDefault<PlanningTaskRow>("SELECT * FROM PlanningTask WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", taskId, planningId);
            if (t == null) return "Uppgiften finns inte.";
            db.Insert(new PlanningTaskEventRow
            {
                PlanningId = planningId, TaskId = taskId, Kind = PlanningTaskEventKind.Comment, Note = body,
                ParticipantId = byParticipantId, ViaEmail = viaEmail, CreatedAt = now,
            });
            Bump(db, planningId);
            return null;
        }

        /// <summary>Antal kommentarer per uppgift (en fråga, för listan).</summary>
        public Dictionary<int, int> CommentCounts(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<CountRow>(
                    "SELECT TaskId AS [Key], COUNT(*) AS N FROM PlanningTaskEvent WHERE PlanningId = @0 AND Kind = N'Comment' GROUP BY TaskId", planningId)
                .ToDictionary(r => r.Key, r => r.N);
        }

        private sealed class CountRow { public int Key { get; set; } public int N { get; set; } }

        // =====================================================================================
        //  Mallar, kretsens checklista, stationer, inklistring
        // =====================================================================================

        private PlanningAreaRow EnsureArea(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, string name, int byPid, DateTime now)
        {
            var area = db.FirstOrDefault<PlanningAreaRow>(
                "SELECT * FROM PlanningArea WITH (UPDLOCK) WHERE PlanningId = @0 AND Name = @1 AND DeletedAt IS NULL", planningId, name);
            if (area != null) return area;
            area = new PlanningAreaRow
            {
                PlanningId = planningId, Name = name, CreatedAt = now, CreatedBy = byPid,
                SortOrder = db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM PlanningArea WHERE PlanningId = @0", planningId),
            };
            db.Insert(area);
            return area;
        }

        private static int NextTaskSort(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int? areaId) =>
            areaId > 0
                ? db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) FROM PlanningTask WHERE PlanningId = @0 AND AreaId = @1", planningId, areaId)
                : db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) FROM PlanningTask WHERE PlanningId = @0 AND AreaId IS NULL", planningId);

        private static PlanningTaskRow NewTask(int planningId, int? areaId, string title, string? description, int? daysBefore,
            DateTime? anchor, string? source, string? sourceKey, int sort, int byPid, DateTime now) => new()
        {
            PlanningId = planningId, AreaId = areaId, Title = title.Length > PlanningTaskRules.MaxTitle ? title.Substring(0, PlanningTaskRules.MaxTitle) : title,
            Description = Clean(description, 4000), DueDaysBefore = daysBefore,
            DueDate = PlanningTaskRules.DueFromDaysBefore(daysBefore, anchor), Status = PlanningTaskStatus.NotStarted,
            SourceKind = source, SourceKey = sourceKey, SortOrder = sort, CreatedAt = now, CreatedBy = byPid, UpdatedAt = now,
        };

        /// <summary>
        /// Fyller i från en standardmall: områden som inte redan finns (på namnet) läggs till med sina uppgifter,
        /// vars sista dag är "N dagar före" tävlingen. Ett område som redan finns rörs inte.
        /// </summary>
        internal (int Areas, int Tasks) SeedTemplate(int planningId, List<PrepArea> template, DateTime? anchor, int byParticipantId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var names = db.Fetch<string>("SELECT Name FROM PlanningArea WHERE PlanningId = @0 AND DeletedAt IS NULL", planningId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int areas = 0, tasks = 0;
            foreach (var def in template)
            {
                // Omgång 3 (K10): ett område som redan finns får mallens uppgifter som saknas där (en andra gren, eller en
                // större mall) — förut hoppades hela området över och den andra grenens uppgifter kom aldrig in.
                var existed = names.Contains(def.Area);
                var area = EnsureArea(db, planningId, def.Area, byParticipantId, now);
                if (!existed) areas++;
                var have = existed
                    ? db.Fetch<string>("SELECT Title FROM PlanningTask WHERE PlanningId = @0 AND AreaId = @1", planningId, area.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sort = existed ? NextTaskSort(db, planningId, area.Id) : 0;
                foreach (var item in def.Items)
                {
                    var title = item.TitleFromAnchor != null && anchor.HasValue ? item.TitleFromAnchor(anchor.Value.Date) : item.Title;
                    if (have.Contains(title) || have.Contains(item.Title)) continue;
                    var t = NewTask(planningId, area.Id, item.Title, item.Description, item.DaysBeforeComp, anchor, PlanningTaskSource.Template, null, ++sort, byParticipantId, now);
                    // P3: milstolparna, och förbundets frister ur tävlingens dag när den är känd.
                    t.IsMilestone = item.Milestone;
                    t.SendToForbundet = item.Milestone && item.ToForbundet;
                    if (item.DueFromAnchor != null && anchor.HasValue) t.DueDate = item.DueFromAnchor(anchor.Value.Date);
                    // P4: titeln kan bero på dagen (SM-annonsens nummer i tidningen).
                    if (item.TitleFromAnchor != null && anchor.HasValue) t.Title = item.TitleFromAnchor(anchor.Value.Date);
                    db.Insert(t);
                    TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, "Från mallen.", null, false, now);
                    tasks++;
                }
            }
            if (tasks > 0) Bump(db, planningId);
            return (areas, tasks);
        }

        public sealed record ChecklistTask(int ChecklistItemId, string Title, string? Description, int? DaysBefore);

        /// <summary>
        /// Kretsens checklista: punkterna blir uppgifter under området "Kretsens checklista — {krets}". En punkt
        /// läggs aldrig in två gånger (SourceKey = punktens id), även om den tagits bort och valts igen.
        /// </summary>
        public int AddChecklistTasks(int planningId, string areaName, IEnumerable<ChecklistTask> items, DateTime? anchor, int byParticipantId)
        {
            var list = items.ToList();
            if (list.Count == 0) return 0;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var area = EnsureArea(db, planningId, areaName, byParticipantId, now);
            var existing = db.Fetch<string>(
                "SELECT SourceKey FROM PlanningTask WHERE PlanningId = @0 AND SourceKind = N'KretsChecklist' AND SourceKey IS NOT NULL", planningId).ToHashSet();
            var sort = NextTaskSort(db, planningId, area.Id);
            var added = 0;
            foreach (var p in list)
            {
                var key = p.ChecklistItemId.ToString();
                if (existing.Contains(key)) continue;
                var t = NewTask(planningId, area.Id, p.Title, p.Description, p.DaysBefore, anchor, PlanningTaskSource.KretsChecklist, key, ++sort, byParticipantId, now);
                db.Insert(t);
                TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, "Från kretsens checklista.", null, false, now);
                added++;
            }
            if (added > 0) Bump(db, planningId);
            return added;
        }

        public const string CourseApprovalTaskTitle = "Banläggaren godkänner stationerna";

        /// <summary>
        /// "Bygg station N" för varje station som saknar en uppgift (fältskytte). Banan i planeringen (2026-10-24): uppgiften
        /// hamnar i det område som har stationen; en station utan område hamnar i <i>Stationsbygge</i>. En station som ett
        /// område i en annan planering i familjen har läggs in där (av den planeringen), inte här. Huvudplaneringen får
        /// dessutom milstolpen <see cref="CourseApprovalTaskTitle"/>. Inga dubbletter i familjen.
        /// </summary>
        public int SeedStationTasks(int planningId, int stationCount, int byParticipantId)
        {
            if (stationCount <= 0) return 0;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var family = StationFamily(db, planningId);
            var familyIds = family.Count == 0 ? planningId.ToString() : string.Join(",", family.Select(f => f.Id));
            var isRoot = family.Count == 0 || family.Any(f => f.Id == planningId && !(f.ParentPlanningId > 0));
            var owner = new Dictionary<int, PlanningAreaRow>();
            foreach (var a in db.Fetch<PlanningAreaRow>($"SELECT * FROM PlanningArea WHERE PlanningId IN ({familyIds}) AND DeletedAt IS NULL AND Stations IS NOT NULL"))
                foreach (var n in PlanningStations.Parse(a.Stations).Stations) owner.TryAdd(n, a);
            var have = db.Fetch<string>($"SELECT ScopeKey FROM PlanningTask WHERE PlanningId IN ({familyIds}) AND ScopeType = N'Station' AND DeletedAt IS NULL")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PlanningAreaRow? fallback = null;
            var sorts = new Dictionary<int, int>();
            var added = 0;
            for (var n = 1; n <= Math.Min(stationCount, PlanningStations.Max); n++)
            {
                if (have.Contains(n.ToString())) continue;
                int areaId;
                if (owner.TryGetValue(n, out var a))
                {
                    if (a.PlanningId != planningId) continue; // delplaneringen lägger in sina egna
                    areaId = a.Id;
                }
                else
                {
                    if (!isRoot) continue;
                    fallback ??= EnsureArea(db, planningId, "Stationsbygge", byParticipantId, now);
                    areaId = fallback.Id;
                }
                if (!sorts.TryGetValue(areaId, out var sort)) sort = NextTaskSort(db, planningId, areaId);
                sorts[areaId] = ++sort;
                var t = NewTask(planningId, areaId, $"Bygg station {n}", null, null, null, PlanningTaskSource.Station, n.ToString(), sort, byParticipantId, now);
                t.ScopeType = "Station"; t.ScopeKey = n.ToString();
                db.Insert(t);
                TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, null, null, false, now);
                added++;
            }
            if (isRoot && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningTask WHERE PlanningId = @0 AND Title = @1 AND DeletedAt IS NULL",
                    planningId, CourseApprovalTaskTitle) == 0)
            {
                fallback ??= EnsureArea(db, planningId, "Stationsbygge", byParticipantId, now);
                var t = NewTask(planningId, fallback.Id, CourseApprovalTaskTitle,
                    "Varje förenings del godkänns av en banläggare, sedan stationerna som helhet (under Stationerna i planeringen).",
                    null, null, PlanningTaskSource.Station, "course", NextTaskSort(db, planningId, fallback.Id) + 1, byParticipantId, now);
                t.IsMilestone = true;
                db.Insert(t);
                TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, null, null, false, now);
                added++;
            }
            if (added > 0) Bump(db, planningId);
            return added;
        }

        public sealed record PastedTask(string Title, int? AssigneeParticipantId, string? AssigneeName, DateTime? Due);

        /// <summary>Uppgifter inklistrade ur ett kalkylark, i ett område eller utan.</summary>
        /// <summary>Lägger in de inklistrade raderna. Returnerar de nya uppgifterna (omgång 3: för mejlen till de ansvariga).</summary>
        public List<PlanningTaskRow> AddPastedTasks(int planningId, int? areaId, IEnumerable<PastedTask> rows, int byParticipantId)
        {
            var added = new List<PlanningTaskRow>();
            var list = rows.Take(MaxPasteRows).ToList();
            if (list.Count == 0) return added;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (AreaError(db, planningId, areaId) != null) areaId = null;
            var sort = NextTaskSort(db, planningId, areaId);
            foreach (var r in list)
            {
                var t = NewTask(planningId, areaId > 0 ? areaId : null, r.Title, null, null, null, PlanningTaskSource.Paste, null, ++sort, byParticipantId, now);
                if (AssigneeError(db, planningId, r.AssigneeParticipantId) == null && r.AssigneeParticipantId > 0) t.AssigneeParticipantId = r.AssigneeParticipantId;
                else t.AssigneeName = Clean(r.AssigneeName, 200);
                t.DueDate = r.Due?.Date;
                db.Insert(t);
                TaskEvent(db, t, PlanningTaskEventKind.Created, byParticipantId, null, t.Status, "Inklistrad.", null, false, now);
                added.Add(t);
            }
            Bump(db, planningId);
            return added;
        }

        // =====================================================================================
        //  Flytten av de gamla förberedelserna (WorkArea/WorkItem/WorkLink/WorkItemComment)
        // =====================================================================================

        /// <summary>
        /// Flyttar tävlingens gamla förberedelser in i planeringen. Idempotent (ImportedFrom…Id har unika index).
        /// De gamla tabellerna rörs inte. Lägena: Planerad → Ej påbörjad, Pågår → Pågår, Blockerad → Behöver hjälp,
        /// Klar → Utförd. En ansvarig medlem blir deltagare (Deltar); ett fritt namn blir AssigneeName.
        /// </summary>
        private int ImportWorkBreakdown(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int competitionId,
            int byParticipantId, WorkBreakdownImport import, DateTime now)
        {
            var areas = db.Fetch<WorkArea>("SELECT * FROM WorkArea WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);
            var items = db.Fetch<WorkItem>("SELECT * FROM WorkItem WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);
            if (areas.Count == 0 && items.Count == 0) return 0;
            var links = db.Fetch<WorkLink>("SELECT * FROM WorkLink WHERE CompetitionId = @0 ORDER BY Id", competitionId);
            var comments = db.Fetch<WorkItemComment>("SELECT * FROM WorkItemComment WHERE CompetitionId = @0 ORDER BY Id", competitionId);
            var doneArea = db.Fetch<int>("SELECT ImportedFromWorkAreaId FROM PlanningArea WHERE ImportedFromWorkAreaId IS NOT NULL AND PlanningId = @0", planningId).ToHashSet();
            var doneItem = db.Fetch<int>("SELECT ImportedFromWorkItemId FROM PlanningTask WHERE ImportedFromWorkItemId IS NOT NULL AND PlanningId = @0", planningId).ToHashSet();
            var doneLink = db.Fetch<int>("SELECT ImportedFromWorkLinkId FROM PlanningDocument WHERE ImportedFromWorkLinkId IS NOT NULL AND PlanningId = @0", planningId).ToHashSet();

            // Medlem → deltagare, tyst (ingen systemrad per person).
            var pidByMember = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WHERE PlanningId = @0", planningId)
                .Where(p => p.MemberId > 0).GroupBy(p => p.MemberId!.Value).ToDictionary(g => g.Key, g => g.First());
            var maxMsg = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;
            int Participant(int memberId)
            {
                if (pidByMember.TryGetValue(memberId, out var row))
                {
                    if (!row.IsActive) { row.RemovedAt = null; row.RemovedBy = null; row.Access = PlanningAccessLevel.Participant; db.Update(row); }
                    return row.Id;
                }
                var name = "Medlem";
                try { name = import.MemberName(memberId); } catch { /* namnet är bara text */ }
                var r = new PlanningMemberRow
                {
                    PlanningId = planningId, MemberId = memberId, DisplayName = Clean(name, 200) ?? "Medlem",
                    Access = PlanningAccessLevel.Participant, NotifyMode = PlanningNotifyMode.Default, AddedAt = now, AddedBy = 0,
                };
                db.Insert(r);
                EnsureRead(db, planningId, r.Id, maxMsg);
                pidByMember[memberId] = r;
                return r.Id;
            }

            var areaMap = new Dictionary<int, int>();
            foreach (var a in areas)
            {
                var existing = db.FirstOrDefault<PlanningAreaRow>("SELECT * FROM PlanningArea WHERE ImportedFromWorkAreaId = @0", a.Id);
                if (existing != null) { areaMap[a.Id] = existing.Id; continue; }
                if (doneArea.Contains(a.Id)) continue;
                var row = new PlanningAreaRow
                {
                    PlanningId = planningId, Name = Clean(a.Name, MaxAreaName) ?? "Område", SortOrder = a.SortOrder,
                    ResponsibleParticipantId = a.ResponsibleMemberId > 0 ? Participant(a.ResponsibleMemberId.Value) : null,
                    ResponsibleName = a.ResponsibleMemberId > 0 ? null : Clean(a.ResponsibleName, 200),
                    CreatedAt = SwedishTime(a.CreatedDate, now), CreatedBy = 0, ImportedFromWorkAreaId = a.Id,
                };
                db.Insert(row);
                areaMap[a.Id] = row.Id;
            }

            var taskMap = new Dictionary<int, int>();
            var imported = 0;
            foreach (var w in items)
            {
                if (doneItem.Contains(w.Id))
                {
                    var tid = db.ExecuteScalar<int?>("SELECT Id FROM PlanningTask WHERE ImportedFromWorkItemId = @0", w.Id);
                    if (tid.HasValue) taskMap[w.Id] = tid.Value;
                    continue;
                }
                var status = w.Status switch
                {
                    WorkItemStatus.Pagar => PlanningTaskStatus.InProgress,
                    WorkItemStatus.Blockerad => PlanningTaskStatus.NeedsHelp,
                    WorkItemStatus.Klar => PlanningTaskStatus.Done,
                    _ => PlanningTaskStatus.NotStarted,
                };
                var isChecklist = string.Equals(w.ScopeType, "KretsChecklist", StringComparison.OrdinalIgnoreCase);
                var created = SwedishTime(w.CreatedDate, now);
                var modified = SwedishTime(w.ModifiedDate, now);
                var t = new PlanningTaskRow
                {
                    PlanningId = planningId, AreaId = areaMap.TryGetValue(w.WorkAreaId, out var aid) ? aid : null,
                    Title = (Clean(w.Title, PlanningTaskRules.MaxTitle) ?? "Uppgift"), Description = Clean(w.Description, 4000),
                    AssigneeParticipantId = w.AssignedMemberId > 0 ? Participant(w.AssignedMemberId.Value) : null,
                    AssigneeName = w.AssignedMemberId > 0 ? null : Clean(w.AssignedName, 200),
                    DueDate = w.DueDate?.Date, Status = status,
                    ScopeType = isChecklist ? null : Clean(w.ScopeType, 20), ScopeKey = isChecklist ? null : Clean(w.ScopeKey, 100),
                    SourceKind = isChecklist ? PlanningTaskSource.KretsChecklist : null, SourceKey = isChecklist ? Clean(w.ScopeKey, 100) : null,
                    EstimatedCost = w.EstimatedCost, SortOrder = w.SortOrder,
                    CreatedAt = created, CreatedBy = byParticipantId, UpdatedAt = modified,
                    DoneAt = status == PlanningTaskStatus.Done ? modified : null,
                    StatusBeforeDone = status == PlanningTaskStatus.Done ? PlanningTaskStatus.InProgress : null,
                    ImportedFromWorkItemId = w.Id,
                };
                db.Insert(t);
                TaskEvent(db, t, PlanningTaskEventKind.Created, null, null, t.Status, "Flyttad från tävlingens förberedelser.", null, false, created);
                taskMap[w.Id] = t.Id;
                imported++;
            }

            foreach (var c in comments)
            {
                if (!taskMap.TryGetValue(c.WorkItemId, out var tid) || doneItem.Contains(c.WorkItemId)) continue;
                var body = Clean(c.Body, MaxComment);
                if (body == null) continue;
                int? pid = c.AuthorMemberId > 0 && pidByMember.TryGetValue(c.AuthorMemberId, out var pr) ? pr.Id : null;
                if (pid == null && !string.IsNullOrWhiteSpace(c.AuthorName)) body = c.AuthorName!.Trim() + ": " + body;
                db.Insert(new PlanningTaskEventRow
                {
                    PlanningId = planningId, TaskId = tid,
                    Kind = c.Kind == WorkCommentKind.Audit ? PlanningTaskEventKind.Edited : PlanningTaskEventKind.Comment,
                    Note = body, ParticipantId = pid, CreatedAt = SwedishTime(c.CreatedDate, now),
                });
            }

            foreach (var l in links)
            {
                if (doneLink.Contains(l.Id)) continue;
                var isFile = !string.IsNullOrEmpty(l.StoredFileName);
                if (!isFile && string.IsNullOrWhiteSpace(l.Url)) continue;
                var doc = new PlanningDocumentRow
                {
                    PlanningId = planningId,
                    Title = Clean(l.Title, MaxDocTitle) ?? Clean(l.OriginalFileName, MaxDocTitle) ?? "Dokument",
                    Url = isFile ? null : Clean(l.Url, 1000), StoredFileName = isFile ? l.StoredFileName : null,
                    OriginalFileName = isFile ? Clean(l.OriginalFileName, 260) : null,
                    AddedAt = SwedishTime(l.CreatedDate, now), AddedBy = 0, ImportedFromWorkLinkId = l.Id,
                };
                db.Insert(doc);
                // Var länken hängde blir en bifogning: uppgiften, annars området, annars bara arkivet.
                if (l.WorkItemId.HasValue && taskMap.TryGetValue(l.WorkItemId.Value, out var lt))
                    InsertRef(db, planningId, doc.Id, PlanningDocTarget.Task, lt, 0, doc.AddedAt);
                else if (l.WorkAreaId.HasValue && areaMap.TryGetValue(l.WorkAreaId.Value, out var la))
                    InsertRef(db, planningId, doc.Id, PlanningDocTarget.Area, la, 0, doc.AddedAt);
            }
            return imported;
        }

        /// <summary>Förberedelsernas tidsstämplar är UTC (StoredTimeBasis); planeringens är svensk tid.</summary>
        private static DateTime SwedishTime(DateTime utc, DateTime fallback) =>
            utc.Year < 2000 ? fallback : HpskSite.Services.Hosting.SwedishTime.FromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
