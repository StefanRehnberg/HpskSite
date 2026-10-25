using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Planeringens data: planeringen, deltagarna, meddelandena, läsmarkeringen och mötena.
    /// Behörigheten prövas av anroparen med <see cref="PlanningAccess"/>; här prövas att varje id
    /// hör till RÄTT planering (<c>WHERE Id = @0 AND PlanningId = @1</c>) — ett id från planering A
    /// i ett anrop för planering B ska aldrig träffa något.
    ///
    /// <para><b>⚠️ Notiser skickas aldrig härifrån.</b> Skrivningen sker i ett scope; mejl och push
    /// skickas av <see cref="PlanningNotifier"/> EFTER att scopet stängts (en egen anslutning eller ett
    /// utskick inne i någon annans scope har låst prod förut).</para>
    /// </summary>
    public partial class PlanningService
    {
        public const int MaxBodyLength = 4000;
        public const int MaxOptions = 8;

        private readonly IScopeProvider _scopes;
        private readonly ILogger<PlanningService> _logger;

        public PlanningService(IScopeProvider scopes, ILogger<PlanningService> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        private static DateTime Now => DateTime.Now;

        // =====================================================================================
        //  Schemat
        // =====================================================================================

        public static readonly string[] Tables =
        {
            "Planning", "PlanningActivity", "PlanningPart", "PlanningMember", "PlanningMessage",
            "PlanningMessageMention", "PlanningMessageRead", "PlanningMeeting", "PlanningMeetingOption",
            "PlanningMeetingVote", "PlanningNotifyLog", "PlanningLink", "PlanningRequest",
            "PlanningTask", "PlanningTaskEvent", "PlanningMeetingTopic", "PlanningRole",
            "PlanningArea", "PlanningDocument", "PlanningDocumentCategory", "PlanningDocumentRef",
            "PlanningDecision", "PlanningDecisionResponse", "PlanningSurplus",
        };

        /// <summary>Tabeller som saknas. Kastar om databasen inte går att fråga — "vet inte" är inget svar.</summary>
        public List<string> MissingSchema()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var missing = Tables.Where(t => s.Database.ExecuteScalar<int>(
                "SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0).ToList();
            // NPoco skriver kolumnen vid varje sparning av en planering — utan den faller allt.
            if (!missing.Contains("Planning") && s.Database.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH('dbo.Planning','ListedForMembers') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("Planning.ListedForMembers");
            // P2: kolumnerna som NPoco skriver vid varje sparning (alter-planning-p2-tasks.sql).
            // P2b: bemanningen i planeringen (alter-staffing-planning-p2b.sql + Planning.DisciplineKey).
            foreach (var (table, col) in new[] { ("Planning", "DoneAtMeetingsOnly"), ("PlanningMember", "ViaCompetition"), ("PlanningTask", "ImportedFromWorkItemId"),
                                                 ("Planning", "DisciplineKey"), ("StaffAssignment", "PlanningId"),
                                                 ("Planning", "SignupRoleChoice"), ("StaffCrewNeed", "DayDate"),
                                                 ("PlanningTask", "IsMilestone"), ("PlanningTask", "SentToForbundetBy"),
                                                 ("PlanningPart", "HostText"), ("PlanningArea", "HostText"), ("PlanningMessage", "AreaId"),
                                                 ("PlanningDocumentCategory", "AreaId"), ("Planning", "ParentPlanningId"), ("Planning", "GroupName"), ("PlanningArea", "Stations"),
                                                 ("PlanningDecision", "DeciderKind"), ("CompetitionApplication", "PlanningId"),
                                                 ("StaffAssignment", "InviteNonce"), ("Planning", "StaffsForCompetitionId") })
                if (!missing.Contains(table) && s.Database.ExecuteScalar<int>($"SELECT CASE WHEN COL_LENGTH('dbo.{table}','{col}') IS NULL THEN 0 ELSE 1 END") == 0)
                    missing.Add(table + "." + col);
            return missing;
        }

        // =====================================================================================
        //  Planeringen
        // =====================================================================================

        public PlanningRow? Get(int planningId)
        {
            if (planningId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningRow>(
                "SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
        }

        public sealed record CreateInput(string Name, string Kind, string? TargetText,
            DateTime? TargetStart, DateTime? TargetEnd, int MemberId, string DisplayName,
            int? RepresentsClubId, int? RepresentsRegionId);

        public static string? Validate(string? name, DateTime? start, DateTime? end)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Ge planeringen ett namn, till exempel \"SM 2028\".";
            if (n.Length > 200) return "Namnet är för långt (högst 200 tecken).";
            if (start.HasValue && end.HasValue && end.Value.Date < start.Value.Date) return "Slutdagen ligger före startdagen.";
            return null;
        }

        /// <summary>Skapar planeringen med grunddelen och den som skapar som <i>Sammankallande</i>.</summary>
        public (int PlanningId, int ParticipantId) Create(CreateInput i)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = new PlanningRow
            {
                Name = i.Name.Trim(),
                Kind = PlanningKind.Normalize(i.Kind),
                TargetText = Clean(i.TargetText, 200),
                TargetStart = i.TargetStart?.Date,
                TargetEnd = i.TargetEnd?.Date,
                Status = "Active",
                CreatedAt = now, CreatedBy = i.MemberId, UpdatedAt = now, UpdatedBy = i.MemberId,
            };
            db.Insert(p);
            db.Insert(new PlanningPartRow { PlanningId = p.Id, Kind = "Root", Name = p.Name, SortOrder = 0, CreatedAt = now });
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
            InsertSystem(db, p.Id, "planning-created", $"Planeringen skapades.", me.Id, null, null, now);
            return (p.Id, me.Id);
        }

        /// <summary>Hur många planeringar medlemmen skapat det senaste dygnet (även raderade — taket ska inte gå att runda).</summary>
        public int CreatedLastDay(int memberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM Planning WHERE CreatedBy = @0 AND CreatedAt > DATEADD(day, -1, @1)", memberId, Now);
        }

        public string? Update(int planningId, string name, string? targetText, DateTime? start, DateTime? end, int byMemberId, bool? listed = null, string? groupName = null)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return "Planeringen finns inte.";
            var err = Validate(name, start, end);
            if (err != null) return err;
            var renamed = !string.Equals(p.Name, name.Trim(), StringComparison.Ordinal);
            p.Name = name.Trim();
            p.TargetText = Clean(targetText, 200);
            p.TargetStart = start?.Date;
            p.TargetEnd = end?.Date;
            if (listed.HasValue) p.ListedForMembers = listed.Value;
            // P4: null = rör inte; tom text = inget eget namn.
            if (groupName != null) p.GroupName = Clean(groupName, 100);
            p.UpdatedAt = Now;
            p.UpdatedBy = byMemberId;
            db.Update(p);
            if (renamed) db.Execute("UPDATE PlanningPart SET Name = @1 WHERE PlanningId = @0 AND Kind = N'Root'", planningId, p.Name);
            Bump(db, planningId);
            return null;
        }

        /// <summary>P2b: grenen (CompetitionTypes-id, eller null). Styr bemanningens roller när planeringen saknar tävling.</summary>
        public void SetDisciplineKey(int planningId, string? key, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET DisciplineKey = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, key, Now, byMemberId);
            Bump(s.Database, planningId);
        }

        /// <summary>Om medlemmen får välja roll vid anmälan till bemanningen.</summary>
        public void SetSignupRoleChoice(int planningId, bool value, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET SignupRoleChoice = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, value, Now, byMemberId);
            Bump(s.Database, planningId);
        }

        public void SetStatus(int planningId, string status, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET Status = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, status == "Archived" ? "Archived" : "Active", Now, byMemberId);
            Bump(s.Database, planningId);
        }

        public sealed record DeleteResult(string? Error, List<int> ReleasedCompetitionIds);

        /// <summary>
        /// Mjuk radering: raden står kvar, men planeringen syns ingenstans och länkarna slutar fungera.
        /// <para>⚠️ Omgång 3 (Expertgruppen K7, Stefan 2026-10-24):</para>
        /// <list type="bullet">
        /// <item>En planering med delplaneringar går inte att ta bort — de hade blivit föräldralösa.</item>
        /// <item>Kopplingen tas bort (annars kan tävlingen aldrig få en ny planering), bemanningen tappar tävlingen och
        ///   appbehörigheten (annars behöll funktionärerna den), och <c>SeededFromCompetitionId</c> nollas (dess unika
        ///   index räknar även raderade planeringar).</item>
        /// </list>
        /// Anroparen speglar om de tävlingar som släpptes (<see cref="DeleteResult.ReleasedCompetitionIds"/>).
        /// </summary>
        public DeleteResult Delete(int planningId, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var kids = db.ExecuteScalar<int>("SELECT COUNT(*) FROM Planning WHERE ParentPlanningId = @0 AND DeletedAt IS NULL", planningId);
            if (kids > 0)
                return new DeleteResult(kids == 1
                    ? "Planeringen har en delplanering. Ta bort delplaneringen först."
                    : $"Planeringen har {kids} delplaneringar. Ta bort dem först.", new());
            var comps = db.Fetch<int>(@"SELECT DISTINCT CompetitionId FROM StaffAssignment WHERE PlanningId = @0 AND CompetitionId IS NOT NULL
                                        UNION SELECT TargetId FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition'", planningId);
            db.Execute("DELETE FROM PlanningLink WHERE PlanningId = @0", planningId);
            foreach (var t in HpskSite.Services.Staffing.StaffingPlanningLink.AllTables)
                db.Execute($"UPDATE {t} SET CompetitionId = NULL WHERE PlanningId = @0", planningId);
            db.Execute("UPDATE StaffAssignment SET HasAdminAccess = 0 WHERE PlanningId = @0 AND HasAdminAccess = 1", planningId);
            db.Execute("UPDATE Planning SET DeletedAt = @1, DeletedBy = @2, SeededFromCompetitionId = NULL WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, Now, byMemberId);
            // Syskon som bemannade den här planeringens tävling ("Bemannar för") faller tillbaka på huvudplaneringens.
            HpskSite.Services.Staffing.StaffingPlanningLink.RecomputeFamily(db, planningId);
            return new DeleteResult(null, comps);
        }

        public long Version(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<long?>("SELECT ChangeSeq FROM PlanningActivity WHERE PlanningId = @0", planningId) ?? 0;
        }

        private static void Bump(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId) =>
            // ⚠️ Inget @@ROWCOUNT här: NPoco läser "@@" som ett escapat "@" och letar efter parametern @ROWCOUNT.
            db.Execute(@"IF EXISTS (SELECT 1 FROM PlanningActivity WHERE PlanningId = @0)
                             UPDATE PlanningActivity SET ChangeSeq = ChangeSeq + 1, ChangedAt = @1 WHERE PlanningId = @0
                         ELSE INSERT INTO PlanningActivity (PlanningId, ChangeSeq, ChangedAt) VALUES (@0, 1, @1);",
                planningId, Now);

        // =====================================================================================
        //  Deltagarna
        // =====================================================================================

        public List<PlanningMemberRow> Participants(int planningId, bool includeRemoved = false)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WHERE PlanningId = @0" + (includeRemoved ? "" : " AND RemovedAt IS NULL")
                + " ORDER BY CASE Access WHEN N'Owner' THEN 0 WHEN N'Editor' THEN 1 WHEN N'Participant' THEN 2 ELSE 3 END, DisplayName", planningId);
        }

        public PlanningMemberRow? Participant(int planningId, int participantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WHERE Id = @0 AND PlanningId = @1", participantId, planningId);
        }

        public PlanningMemberRow? ParticipantForMember(int planningId, int memberId)
        {
            if (memberId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningMemberRow>(
                "SELECT * FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1", planningId, memberId);
        }

        public sealed record AddResult(int ParticipantId, bool Created, bool Reactivated, string? Error);

        /// <summary>
        /// Lägger till en medlem, eller en gäst med bara e-post (<paramref name="memberId"/> = 0).
        /// En tidigare borttagen deltagare blir aktiv igen med sin gamla rad, så hens gamla
        /// meddelanden behåller sin författare.
        /// </summary>
        public AddResult AddParticipant(int planningId, int memberId, string displayName, string? guestEmail,
            string access, int? repClubId, int? repRegionId, string? repText, int byMemberId)
        {
            var name = Clean(displayName, 200);
            var email = Clean(guestEmail, 256)?.ToLowerInvariant();
            if (name == null) return new AddResult(0, false, false, "Namnet saknas.");
            if (memberId <= 0 && (email == null || !email.Contains('@') || email.Contains(' ')))
                return new AddResult(0, false, false, "Ange en giltig e-postadress.");

            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var existing = memberId > 0
                ? db.SingleOrDefault<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND MemberId = @1", planningId, memberId)
                : db.SingleOrDefault<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND MemberId IS NULL AND GuestEmail = @1", planningId, email);
            var maxId = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;

            if (existing != null)
            {
                if (existing.IsActive) return new AddResult(existing.Id, false, false, null);
                existing.RemovedAt = null; existing.RemovedBy = null;
                existing.Access = access; existing.DisplayName = name;
                existing.RepresentsClubId = repClubId; existing.RepresentsRegionId = repClubId > 0 ? null : repRegionId;
                existing.RepresentsText = Clean(repText, 200);
                existing.AddedAt = now; existing.AddedBy = byMemberId;
                // Omgång 3: tillagd igen av någon annan = inbjuden igen, tills hen själv öppnat planeringen (kontaktuppgifterna).
                if (memberId > 0 && byMemberId != memberId) existing.LastSeenAt = null;
                db.Update(existing);
                EnsureRead(db, planningId, existing.Id, maxId);
                InsertSystem(db, planningId, "member-joined", "", null, existing.Id, null, now);
                Bump(db, planningId);
                return new AddResult(existing.Id, false, true, null);
            }

            var row = new PlanningMemberRow
            {
                PlanningId = planningId, MemberId = memberId > 0 ? memberId : null, DisplayName = name,
                GuestEmail = memberId > 0 ? null : email, Access = access,
                RepresentsClubId = repClubId, RepresentsRegionId = repClubId > 0 ? null : repRegionId,
                RepresentsText = Clean(repText, 200),
                NotifyMode = PlanningNotifyMode.Default, AddedAt = now, AddedBy = byMemberId,
            };
            db.Insert(row);
            // En nykomling börjar "ikapp": dagens mejl ska inte bära hela planeringens historik.
            EnsureRead(db, planningId, row.Id, maxId);
            InsertSystem(db, planningId, "member-joined", "", null, row.Id, null, now);
            Bump(db, planningId);
            return new AddResult(row.Id, true, false, null);
        }

        private static void EnsureRead(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int participantId, int maxId) =>
            db.Execute(@"IF NOT EXISTS (SELECT 1 FROM PlanningMessageRead WHERE PlanningId = @0 AND ParticipantId = @1)
                           INSERT INTO PlanningMessageRead (PlanningId, ParticipantId, LastReadMessageId, LastDigestMessageId) VALUES (@0, @1, @2, @2)
                         ELSE UPDATE PlanningMessageRead SET LastReadMessageId = CASE WHEN LastReadMessageId < @2 THEN @2 ELSE LastReadMessageId END,
                                                            LastDigestMessageId = CASE WHEN LastDigestMessageId < @2 THEN @2 ELSE LastDigestMessageId END
                              WHERE PlanningId = @0 AND ParticipantId = @1",
                planningId, participantId, maxId);

        public string? UpdateParticipant(int planningId, int participantId, string access, int? repClubId, int? repRegionId, string? repText)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var all = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND RemovedAt IS NULL", planningId);
            var row = all.FirstOrDefault(r => r.Id == participantId);
            if (row == null) return "Personen är inte med i planeringen.";
            if (PlanningAccess.WouldLeaveNoOwner(all, participantId, access))
                return NoOwnerLeft;
            row.Access = access;
            row.RepresentsClubId = repClubId;
            row.RepresentsRegionId = repClubId > 0 ? null : repRegionId;
            row.RepresentsText = Clean(repText, 200);
            // Den sammankallande har tagit ställning till personen: hen är nu en vanlig deltagare, inte
            // längre med bara genom tävlingens behörighet.
            row.ViaCompetition = false;
            db.Update(row);
            Bump(db, planningId);
            return null;
        }

        public string? RemoveParticipant(int planningId, int participantId, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var all = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0 AND RemovedAt IS NULL", planningId);
            if (all.All(r => r.Id != participantId)) return "Personen är inte med i planeringen.";
            if (PlanningAccess.WouldLeaveNoOwner(all, participantId, null))
                return NoOwnerLeft;
            db.Execute("UPDATE PlanningMember SET RemovedAt = @2, RemovedBy = @3 WHERE Id = @0 AND PlanningId = @1",
                participantId, planningId, Now, byMemberId);
            InsertSystem(db, planningId, "member-left", "", null, participantId, null, Now);
            Bump(db, planningId);
            return null;
        }

        // =====================================================================================
        //  Förfrågningar: inbjudan med e-post och "be om att få vara med" (Stefan 2026-10-23)
        //  ⚠️ En förfrågan ger ALDRIG tillgång. Först Approve skapar en deltagarrad.
        // =====================================================================================

        /// <summary>Hur många som bjudits in det senaste dygnet (medlemmar som lagts till av någon annan + e-postinbjudningar).</summary>
        public int InvitesLastDay(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return InvitesLastDayIn(s.Database, planningId);
        }

        public PlanningRequestRow? Request(int planningId, int requestId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningRequestRow>(
                "SELECT * FROM PlanningRequest WHERE Id = @0 AND PlanningId = @1", requestId, planningId);
        }

        public PlanningRequestRow? RequestById(int requestId)
        {
            if (requestId <= 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningRequestRow>("SELECT * FROM PlanningRequest WHERE Id = @0", requestId);
        }

        /// <summary>Öppna förfrågningar (väntar på svar eller godkännande), äldst först.</summary>
        public List<PlanningRequestRow> OpenRequests(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningRequestRow>(
                "SELECT * FROM PlanningRequest WHERE PlanningId = @0 AND Status IN (N'Sent', N'Pending') ORDER BY CreatedAt", planningId);
        }

        /// <summary>Antal som väntar på godkännande, per planering (en fråga för hela listan).</summary>
        public Dictionary<int, int> PendingCounts(IEnumerable<int> planningIds)
        {
            var ids = planningIds.Distinct().ToList();
            var map = new Dictionary<int, int>();
            if (ids.Count == 0) return map;
            using var s = _scopes.CreateScope(autoComplete: true);
            foreach (var r in s.Database.Fetch<IdCount>(
                "SELECT PlanningId AS Id, COUNT(*) AS N FROM PlanningRequest WHERE Status = N'Pending' AND PlanningId IN (@0) GROUP BY PlanningId", ids))
                map[r.Id] = r.N;
            return map;
        }

        /// <summary>Alla planeringar med förfrågningar som väntar på godkännande (sajtadmins supportlista).</summary>
        public Dictionary<int, int> AllPendingCounts()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<IdCount>(@"SELECT r.PlanningId AS Id, COUNT(*) AS N FROM PlanningRequest r
                    JOIN Planning p ON p.Id = r.PlanningId AND p.DeletedAt IS NULL
                   WHERE r.Status = N'Pending' GROUP BY r.PlanningId")
                .ToDictionary(r => r.Id, r => r.N);
        }

        /// <summary>Medlemmens egna öppna förfrågningar, per planering.</summary>
        public Dictionary<int, PlanningRequestRow> OpenRequestsByMember(int memberId)
        {
            if (memberId <= 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningRequestRow>(
                    "SELECT * FROM PlanningRequest WHERE MemberId = @0 AND Kind = N'Request' AND Status = N'Pending'", memberId)
                .GroupBy(r => r.PlanningId).ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>Planeringar som den sammankallande valt att visa för alla medlemmar (aktiva).</summary>
        public List<PlanningRow> Listed()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningRow>(
                "SELECT * FROM Planning WHERE ListedForMembers = 1 AND Status = N'Active' AND DeletedAt IS NULL ORDER BY UpdatedAt DESC");
        }

        /// <summary>De sammankallande per planering (namnen), för listan.</summary>
        public Dictionary<int, List<string>> ConvenerNames(IEnumerable<int> planningIds)
        {
            var ids = planningIds.Distinct().ToList();
            if (ids.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMemberRow>(
                    "SELECT * FROM PlanningMember WHERE Access = N'Owner' AND RemovedAt IS NULL AND PlanningId IN (@0) ORDER BY DisplayName", ids)
                .GroupBy(r => r.PlanningId).ToDictionary(g => g.Key, g => g.Select(r => r.DisplayName).ToList());
        }

        public sealed record RequestResult(int RequestId, string? Error);

        /// <summary>En inbjudan med e-post: väntar på svar. Ingen tillgång förrän den godkänts.</summary>
        public RequestResult CreateInvite(int planningId, string name, string email, string? message, int byMemberId)
        {
            var n = Clean(name, 200);
            var e = Clean(email, 256)?.ToLowerInvariant();
            if (n == null) return new RequestResult(0, "Skriv personens namn.");
            if (e == null || !e.Contains('@') || e.Contains(' ')) return new RequestResult(0, "Ange en giltig e-postadress.");
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            db.Execute("SELECT Id FROM Planning WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0", planningId);
            if (InvitesLastDayIn(db, planningId) >= PlanningInviteRules.MaxPerDay) return new RequestResult(0, PlanningInviteRules.LimitMessage);
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND MemberId IS NULL AND GuestEmail = @1 AND RemovedAt IS NULL", planningId, e) > 0)
                return new RequestResult(0, "Personen är redan med i planeringen.");
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningRequest WHERE PlanningId = @0 AND Email = @1 AND Status IN (N'Sent', N'Pending')", planningId, e) > 0)
                return new RequestResult(0, "Personen är redan inbjuden och har inte svarat än.");
            var row = new PlanningRequestRow
            {
                PlanningId = planningId, Kind = PlanningRequestKind.Invite, Name = n, Email = e, Message = Clean(message, 1000),
                Status = PlanningRequestStatus.Sent, CreatedAt = Now, CreatedBy = byMemberId,
            };
            db.Insert(row);
            Bump(db, planningId);
            return new RequestResult(row.Id, null);
        }

        private static int InvitesLastDayIn(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId) =>
            db.ExecuteScalar<int>(@"
                SELECT (SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND AddedAt >= @1 AND (MemberId IS NULL OR MemberId <> AddedBy))
                     + (SELECT COUNT(*) FROM PlanningRequest WHERE PlanningId = @0 AND Kind = N'Invite' AND CreatedAt >= @1)", planningId, Now.AddDays(-1));

        /// <summary>En medlem ber om att få vara med i en planering som visas för alla medlemmar.</summary>
        public RequestResult CreateMemberRequest(int planningId, int memberId, string name, string? message)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null || !p.ListedForMembers || p.Status != "Active") return new RequestResult(0, "Planeringen tar inte emot förfrågningar.");
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE PlanningId = @0 AND MemberId = @1 AND RemovedAt IS NULL", planningId, memberId) > 0)
                return new RequestResult(0, "Du är redan med i planeringen.");
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningRequest WHERE PlanningId = @0 AND MemberId = @1 AND Status = N'Pending'", planningId, memberId) > 0)
                return new RequestResult(0, "Du har redan bett om att få vara med. Den sammankallande har inte svarat än.");
            var row = new PlanningRequestRow
            {
                PlanningId = planningId, Kind = PlanningRequestKind.Request, MemberId = memberId, Name = Clean(name, 200) ?? "Medlem",
                Message = Clean(message, 1000), Status = PlanningRequestStatus.Pending, CreatedAt = Now, CreatedBy = memberId,
            };
            db.Insert(row);
            Bump(db, planningId);
            return new RequestResult(row.Id, null);
        }

        /// <summary>Den inbjudna svarar. Ja → väntar på godkännande; nej → avböjd.</summary>
        public string? AnswerInvite(int requestId, bool accept, string? proposedRepresents)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var r = db.SingleOrDefault<PlanningRequestRow>("SELECT * FROM PlanningRequest WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0", requestId);
            if (r == null || r.Kind != PlanningRequestKind.Invite) return "Inbjudan finns inte.";
            if (r.Status != PlanningRequestStatus.Sent) return "Inbjudan är redan besvarad.";
            r.Status = accept ? PlanningRequestStatus.Pending : PlanningRequestStatus.Declined;
            r.RespondedAt = Now;
            if (accept) r.ProposedRepresents = Clean(proposedRepresents, 200);
            db.Update(r);
            Bump(db, r.PlanningId);
            return null;
        }

        /// <summary>Den som bjöd in drar tillbaka inbjudan, eller medlemmen sin förfrågan.</summary>
        public string? WithdrawRequest(int planningId, int requestId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute(
                "UPDATE PlanningRequest SET Status = N'Withdrawn', DecidedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND Status IN (N'Sent', N'Pending')",
                requestId, planningId, Now);
            if (n == 0) return "Förfrågan är redan avgjord.";
            Bump(s.Database, planningId);
            return null;
        }

        public string? RejectRequest(int planningId, int requestId, string? note, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute(
                "UPDATE PlanningRequest SET Status = N'Rejected', DecidedAt = @2, DecidedBy = @3, DecisionNote = @4 WHERE Id = @0 AND PlanningId = @1 AND Status = N'Pending'",
                requestId, planningId, Now, byMemberId, Clean(note, 500));
            if (n == 0) return "Förfrågan väntar inte på godkännande.";
            Bump(s.Database, planningId);
            return null;
        }

        /// <summary>
        /// Godkänn: deltagarraden skapas med nivå och Företräder, och förfrågan pekar på den. EN transaktion.
        /// </summary>
        public AddResult ApproveRequest(int planningId, int requestId, string access, int? repClubId, int? repRegionId, string? repText,
            string memberDisplayName, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var r = db.SingleOrDefault<PlanningRequestRow>(
                "SELECT * FROM PlanningRequest WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0 AND PlanningId = @1", requestId, planningId);
            if (r == null) return new AddResult(0, false, false, "Förfrågan finns inte.");
            if (r.Status != PlanningRequestStatus.Pending) return new AddResult(0, false, false, "Förfrågan väntar inte på godkännande.");
            var add = r.Kind == PlanningRequestKind.Request
                ? AddParticipant(planningId, r.MemberId ?? 0, memberDisplayName, null, access, repClubId, repRegionId, repText, byMemberId)
                : AddParticipant(planningId, 0, r.Name, r.Email, access, repClubId, repRegionId, repText, byMemberId);
            if (add.Error != null) return add;
            r.Status = PlanningRequestStatus.Approved;
            r.DecidedAt = Now; r.DecidedBy = byMemberId; r.ParticipantId = add.ParticipantId;
            db.Update(r);
            return add;
        }

        private sealed class IdCount { public int Id { get; set; } public int N { get; set; } }

        private const string NoOwnerLeft = "Det måste finnas minst en sammankallande. Gör någon annan till sammankallande först.";

        /// <summary>
        /// Byt ut en deltagare (Stefan 2026-10-23: "byta ut medlem om hen inte kan va med längre"). Den nya
        /// får samma nivå och samma <i>Företräder</i>; den gamla lämnar gruppen men hens meddelanden står
        /// kvar under hens namn. EN transaktion, och EN rad i flödet ("X ersatte Y") i stället för två.
        /// Den nya är en medlem (<paramref name="newMemberId"/>) eller en gäst med e-post.
        /// </summary>
        public AddResult ReplaceParticipant(int planningId, int oldParticipantId, int newMemberId, string newName,
            string? newGuestEmail, int byMemberId)
        {
            var name = Clean(newName, 200);
            var email = Clean(newGuestEmail, 256)?.ToLowerInvariant();
            if (name == null) return new AddResult(0, false, false, "Namnet saknas.");
            if (newMemberId <= 0 && (email == null || !email.Contains('@') || email.Contains(' ')))
                return new AddResult(0, false, false, "Ange en giltig e-postadress.");

            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var all = db.Fetch<PlanningMemberRow>("SELECT * FROM PlanningMember WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0", planningId);
            var old = all.FirstOrDefault(r => r.Id == oldParticipantId && r.IsActive);
            if (old == null) return new AddResult(0, false, false, "Personen är inte med i planeringen.");
            var existing = newMemberId > 0
                ? all.FirstOrDefault(r => r.MemberId == newMemberId)
                : all.FirstOrDefault(r => r.MemberId == null && string.Equals(r.GuestEmail, email, StringComparison.OrdinalIgnoreCase));
            if (existing?.Id == old.Id) return new AddResult(0, false, false, "Välj en annan person än den som byts ut.");
            if (existing is { IsActive: true }) return new AddResult(0, false, false, $"{existing.DisplayName} är redan med i planeringen.");

            var maxId = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;
            var row = existing ?? new PlanningMemberRow
            {
                PlanningId = planningId, MemberId = newMemberId > 0 ? newMemberId : null,
                GuestEmail = newMemberId > 0 ? null : email, NotifyMode = PlanningNotifyMode.Default,
            };
            row.DisplayName = name;
            row.Access = old.Access;
            row.RepresentsClubId = old.RepresentsClubId;
            row.RepresentsRegionId = old.RepresentsRegionId;
            row.RepresentsText = old.RepresentsText;
            row.AddedAt = now; row.AddedBy = byMemberId;
            row.RemovedAt = null; row.RemovedBy = null;
            if (existing == null) db.Insert(row); else db.Update(row);
            EnsureRead(db, planningId, row.Id, maxId);

            db.Execute("UPDATE PlanningMember SET RemovedAt = @2, RemovedBy = @3 WHERE Id = @0 AND PlanningId = @1",
                old.Id, planningId, now, byMemberId);
            // Omgång 3 (Expertgruppen, persona 3): den nya tar över arbetet — öppna uppgifter, områdesansvar och roller.
            // Förut stod de kvar på en borttagen deltagare, och ingen såg att någon skulle göra dem. Avslutade uppgifter
            // behåller den som gjorde dem (historiken).
            MoveWork(db, planningId, old.Id, row.Id, all.FirstOrDefault(r => r.MemberId == byMemberId && r.IsActive)?.Id, now);
            InsertSystem(db, planningId, "member-replaced", "", null, row.Id, ("participant", old.Id), now);
            Bump(db, planningId);
            return new AddResult(row.Id, existing == null, existing != null, null);
        }

        /// <summary>Byt ut: öppna uppgifter, områdesansvar och aktiva roller går från den gamla deltagaren till den nya.</summary>
        private static void MoveWork(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int fromPid, int toPid, int? actorPid, DateTime now)
        {
            db.Execute(@"UPDATE PlanningTask SET AssigneeParticipantId = @2
                         WHERE PlanningId = @0 AND AssigneeParticipantId = @1 AND DeletedAt IS NULL AND Status <> N'Done'",
                planningId, fromPid, toPid);
            db.Execute("UPDATE PlanningArea SET ResponsibleParticipantId = @2 WHERE PlanningId = @0 AND ResponsibleParticipantId = @1 AND DeletedAt IS NULL",
                planningId, fromPid, toPid);
            // Stationerna den gamla var ansvarig för (eller kontakt för) går till den nya.
            db.Execute("UPDATE PlanningStationResponsible SET ParticipantId = @2 WHERE PlanningId = @0 AND ParticipantId = @1",
                planningId, fromPid, toPid);
            // En roll den nya redan har (samma namn) läggs inte till en gång till; den gamla avslutas i båda fallen.
            db.Execute(@"INSERT INTO PlanningRole (PlanningId, ParticipantId, RoleKey, Name, AddedAt, AddedBy)
                         SELECT r.PlanningId, @2, r.RoleKey, r.Name, @3, r.AddedBy FROM PlanningRole r
                         WHERE r.PlanningId = @0 AND r.ParticipantId = @1 AND r.RemovedAt IS NULL
                           AND NOT EXISTS (SELECT 1 FROM PlanningRole x WHERE x.PlanningId = r.PlanningId AND x.ParticipantId = @2
                                           AND x.RemovedAt IS NULL AND x.Name = r.Name)",
                planningId, fromPid, toPid, now);
            db.Execute("UPDATE PlanningRole SET RemovedAt = @2, RemovedBy = @3 WHERE PlanningId = @0 AND ParticipantId = @1 AND RemovedAt IS NULL",
                planningId, fromPid, now, actorPid);
        }

        public void SetNotifyMode(int planningId, int participantId, string mode)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE PlanningMember SET NotifyMode = @2 WHERE Id = @0 AND PlanningId = @1",
                participantId, planningId, PlanningNotifyMode.Normalize(mode));
        }

        public void MarkInviteSent(int participantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE PlanningMember SET InviteSentAt = @1 WHERE Id = @0", participantId, Now);
        }

        public void TouchSeen(int participantId)
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                s.Database.Execute(
                    "UPDATE PlanningMember SET LastSeenAt = @1 WHERE Id = @0 AND (LastSeenAt IS NULL OR LastSeenAt < DATEADD(minute, -5, @1))",
                    participantId, Now);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Planering: kunde inte stämpla senast sedd."); }
        }

        /// <summary>
        /// Planeringar där medlemmen är aktiv deltagare, med det som väntar på hen. EN fråga per del
        /// för hela listan — startsidan anropar den vid varje sidladdning.
        /// </summary>
        public List<MyPlanning> ForMember(int memberId)
        {
            var list = new List<MyPlanning>();
            if (memberId <= 0) return list;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var rows = db.Fetch<MyPlanningRaw>(@"
                SELECT p.Id AS PlanningId, p.Name, p.Status, p.TargetText, p.TargetStart, p.Kind,
                       pm.Id AS ParticipantId, pm.Access,
                       (SELECT COUNT(*) FROM PlanningMessage m
                         WHERE m.PlanningId = p.Id AND m.DeletedAt IS NULL AND m.Id > ISNULL(r.LastReadMessageId, 0)
                           AND m.Kind = N'Message' AND ISNULL(m.ParticipantId, 0) <> pm.Id) AS Unread,
                       (SELECT COUNT(*) FROM PlanningTask t
                         WHERE t.PlanningId = p.Id AND t.DeletedAt IS NULL AND t.AssigneeParticipantId = pm.Id AND t.Status <> N'Done') AS OpenTasks,
                       CASE WHEN pm.Access = N'Follower' THEN 0 ELSE
                       (SELECT COUNT(*) FROM PlanningMeeting mt
                         WHERE mt.PlanningId = p.Id AND mt.DeletedAt IS NULL AND mt.Status = N'Proposed'
                           AND NOT EXISTS (SELECT 1 FROM PlanningMeetingVote v WHERE v.PlanningMeetingId = mt.Id AND v.ParticipantId = pm.Id)) END AS VotesOwed,
                       (SELECT MIN(mt.StartsAt) FROM PlanningMeeting mt
                         WHERE mt.PlanningId = p.Id AND mt.DeletedAt IS NULL AND mt.Status = N'Scheduled' AND mt.StartsAt >= @1) AS NextMeetingAt,
                       (SELECT MAX(m.CreatedAt) FROM PlanningMessage m WHERE m.PlanningId = p.Id) AS LastActivityAt
                  FROM PlanningMember pm
                  JOIN Planning p ON p.Id = pm.PlanningId AND p.DeletedAt IS NULL
                  LEFT JOIN PlanningMessageRead r ON r.PlanningId = p.Id AND r.ParticipantId = pm.Id
                 WHERE pm.MemberId = @0 AND pm.RemovedAt IS NULL",
                memberId, Now.AddHours(-2));
            foreach (var r in rows)
            {
                string? nextTitle = null;
                if (r.NextMeetingAt.HasValue)
                    nextTitle = db.ExecuteScalar<string>(
                        "SELECT TOP 1 Title FROM PlanningMeeting WHERE PlanningId = @0 AND DeletedAt IS NULL AND Status = N'Scheduled' AND StartsAt = @1",
                        r.PlanningId, r.NextMeetingAt.Value);
                list.Add(new MyPlanning(r.PlanningId, r.Name, r.Status, r.Kind, r.TargetText, r.TargetStart, r.ParticipantId,
                    r.Access, r.Unread, r.VotesOwed, r.NextMeetingAt, nextTitle, r.LastActivityAt, r.OpenTasks));
            }
            return list.OrderBy(p => p.Status == "Archived").ThenByDescending(p => p.LastActivityAt ?? DateTime.MinValue).ToList();
        }

        /// <summary>
        /// Startsidans kort: aktiva planeringar där något väntar på medlemmen — olästa meddelanden, en tid att svara på,
        /// ett möte inom en vecka, eller en egen öppen uppgift (planeringstestet k2, S4 T4-6b: kortet försvann när inget
        /// var oläst, och den som hade en uppgift hittade inte tillbaka). Bara riktiga meddelanden räknas som olästa.
        /// </summary>
        public List<MyPlanning> HomeCards(int memberId) =>
            memberId <= 0 ? new List<MyPlanning>() : ForMember(memberId)
                .Where(p => p.Status == "Active" && (p.Unread > 0 || p.VotesOwed > 0 || p.OpenTasks > 0
                    || (p.NextMeetingAt.HasValue && p.NextMeetingAt.Value <= Now.AddDays(7))))
                .Take(5).ToList();

        public sealed record MyPlanning(int PlanningId, string Name, string Status, string Kind, string? TargetText,
            DateTime? TargetStart, int ParticipantId, string Access, int Unread, int VotesOwed, DateTime? NextMeetingAt,
            string? NextMeetingTitle, DateTime? LastActivityAt, int OpenTasks = 0);

        private sealed class MyPlanningRaw
        {
            public int PlanningId { get; set; }
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";
            public string? TargetText { get; set; }
            public DateTime? TargetStart { get; set; }
            public string Kind { get; set; } = "";
            public int ParticipantId { get; set; }
            public string Access { get; set; } = "";
            public int Unread { get; set; }
            public int VotesOwed { get; set; }
            public int OpenTasks { get; set; }
            public DateTime? NextMeetingAt { get; set; }
            public DateTime? LastActivityAt { get; set; }
        }

        /// <summary>Bokade möten i medlemmens planeringar inom ett fönster — Mitt schema (egen sektion,
        /// eftersom Mitt schema i övrigt är byggt per tävling).</summary>
        public List<UpcomingMeeting> UpcomingMeetingsForMember(int memberId, DateTime from, DateTime to)
        {
            if (memberId <= 0) return new List<UpcomingMeeting>();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<UpcomingMeeting>(@"
                SELECT m.Id AS MeetingId, m.PlanningId, p.Name AS PlanningName, m.Title, m.StartsAt, m.EndsAt, m.Place, m.OnlineUrl
                  FROM PlanningMeeting m
                  JOIN Planning p ON p.Id = m.PlanningId AND p.DeletedAt IS NULL
                  JOIN PlanningMember pm ON pm.PlanningId = p.Id AND pm.MemberId = @0 AND pm.RemovedAt IS NULL
                                        AND pm.Access <> N'Follower' -- den som följer är inte kallad till mötena
                 WHERE m.Status = N'Scheduled' AND m.DeletedAt IS NULL AND m.StartsAt >= @1 AND m.StartsAt < @2
                 ORDER BY m.StartsAt", memberId, from, to);
        }

        public sealed class UpcomingMeeting
        {
            public int MeetingId { get; set; }
            public int PlanningId { get; set; }
            public string PlanningName { get; set; } = "";
            public string Title { get; set; } = "";
            public DateTime StartsAt { get; set; }
            public DateTime? EndsAt { get; set; }
            public string? Place { get; set; }
            public string? OnlineUrl { get; set; }
        }

        // =====================================================================================
        //  Meddelandena
        // =====================================================================================

        public List<PlanningMessageRow> Messages(int planningId, int afterId, int take)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            if (afterId > 0)
                return s.Database.Fetch<PlanningMessageRow>(
                    "SELECT TOP (@2) * FROM PlanningMessage WHERE PlanningId = @0 AND Id > @1 ORDER BY Id", planningId, afterId, take);
            var rows = s.Database.Fetch<PlanningMessageRow>(
                "SELECT TOP (@1) * FROM PlanningMessage WHERE PlanningId = @0 ORDER BY Id DESC", planningId, take);
            rows.Reverse();
            return rows;
        }

        /// <summary>Ändrade meddelanden (redigerade/borttagna) sedan en tid — pollningen hämtar dem också.</summary>
        public List<PlanningMessageRow> ChangedSince(int planningId, DateTime since)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMessageRow>(
                "SELECT * FROM PlanningMessage WHERE PlanningId = @0 AND (EditedAt >= @1 OR DeletedAt >= @1)", planningId, since);
        }

        public PlanningMessageRow? Message(int planningId, int messageId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningMessageRow>(
                "SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1", messageId, planningId);
        }

        public List<PlanningMentionRow> MentionsFor(int planningId, IEnumerable<int> messageIds)
        {
            var ids = messageIds.Distinct().ToList();
            if (ids.Count == 0) return new List<PlanningMentionRow>();
            using var s = _scopes.CreateScope(autoComplete: true);
            var result = new List<PlanningMentionRow>();
            foreach (var chunk in ids.Chunk(1000))
                result.AddRange(s.Database.Fetch<PlanningMentionRow>(
                    "SELECT * FROM PlanningMessageMention WHERE PlanningId = @0 AND MessageId IN (@1)", planningId, chunk));
            return result;
        }

        public List<PlanningReadRow> ReadRows(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningReadRow>("SELECT * FROM PlanningMessageRead WHERE PlanningId = @0", planningId);
        }

        public sealed record PostResult(int MessageId, List<int> MentionedParticipantIds, string? Error);

        public static string? ValidateBody(string? body)
        {
            var t = (body ?? "").Trim();
            if (t.Length == 0) return "Skriv något först.";
            if (t.Length > MaxBodyLength) return $"Meddelandet är för långt (högst {MaxBodyLength} tecken).";
            return null;
        }

        /// <summary>
        /// Skriver ett meddelande. Omnämnandena är DATA (deltagarens id), aldrig tolkade ur texten —
        /// två kan heta Anna, och ett omnämnande av någon utanför planeringen hade läckt planeringens
        /// namn i en notis. Bara aktiva deltagare i SAMMA planering, aldrig författaren själv.
        /// Författarens läsmärke flyttas fram — man har läst det man själv skrev.
        /// </summary>
        public PostResult Post(int planningId, int participantId, string body, int? replyToId, IEnumerable<int>? mentionIds, bool viaEmail,
            int? areaId = null)
        {
            var err = ValidateBody(body);
            if (err != null) return new PostResult(0, new List<int>(), err);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (AreaError(db, planningId, areaId) is { } pe) return new PostResult(0, new List<int>(), pe);
            if (replyToId > 0)
            {
                var parent = db.SingleOrDefault<PlanningMessageRow>(
                    "SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1", replyToId, planningId);
                if (parent == null || parent.Kind != "Message") return new PostResult(0, new List<int>(), "Meddelandet du svarar på finns inte.");
                if (parent.ReplyToMessageId > 0) replyToId = parent.ReplyToMessageId; // en nivå: svar på ett svar hamnar under samma tråd
                // Ett svar hör till samma del som tråden.
                var thread = parent.ReplyToMessageId > 0
                    ? db.SingleOrDefault<PlanningMessageRow>("SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1", parent.ReplyToMessageId, planningId)
                    : parent;
                areaId = thread?.AreaId;
            }
            var row = new PlanningMessageRow
            {
                PlanningId = planningId, ParticipantId = participantId, Kind = "Message", Body = body.Trim(),
                ReplyToMessageId = replyToId > 0 ? replyToId : null, ViaEmail = viaEmail, CreatedAt = now,
                AreaId = areaId > 0 ? areaId : null,
            };
            db.Insert(row);

            var wanted = (mentionIds ?? Enumerable.Empty<int>()).Where(i => i > 0 && i != participantId).Distinct().ToList();
            var mentioned = new List<int>();
            if (wanted.Count > 0)
            {
                var valid = db.Fetch<int>("SELECT Id FROM PlanningMember WHERE PlanningId = @0 AND RemovedAt IS NULL AND Id IN (@1)",
                    planningId, wanted);
                foreach (var pid in valid)
                {
                    db.Insert(new PlanningMentionRow { MessageId = row.Id, PlanningId = planningId, ParticipantId = pid });
                    mentioned.Add(pid);
                }
            }
            db.Execute(@"UPDATE PlanningMessageRead SET LastReadMessageId = @2, ReadAt = @3
                          WHERE PlanningId = @0 AND ParticipantId = @1 AND LastReadMessageId < @2",
                planningId, participantId, row.Id, now);
            Bump(db, planningId);
            return new PostResult(row.Id, mentioned, null);
        }

        public string? Edit(int planningId, int messageId, string body)
        {
            var err = ValidateBody(body);
            if (err != null) return err;
            using var s = _scopes.CreateScope(autoComplete: true);
            var n = s.Database.Execute(
                "UPDATE PlanningMessage SET Body = @2, EditedAt = @3 WHERE Id = @0 AND PlanningId = @1 AND Kind = N'Message' AND DeletedAt IS NULL",
                messageId, planningId, body.Trim(), Now);
            if (n == 0) return "Meddelandet finns inte.";
            Bump(s.Database, planningId);
            return null;
        }

        /// <summary>Borttaget: texten töms, raden står kvar så att tråden håller ihop ("Meddelandet togs bort").</summary>
        public void Remove(int planningId, int messageId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute(
                "UPDATE PlanningMessage SET Body = N'', DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1 AND Kind = N'Message'",
                messageId, planningId, Now);
            s.Database.Execute("DELETE FROM PlanningMessageMention WHERE MessageId = @0 AND PlanningId = @1", messageId, planningId);
            Bump(s.Database, planningId);
        }

        /// <summary>
        /// Flyttar läsmärket fram, aldrig bakåt (två flikar kan läsa i omvänd ordning), och aldrig förbi
        /// planeringens senaste meddelande (ett påhittat id ska inte kunna "läsa" framtida meddelanden).
        /// </summary>
        public void MarkRead(int planningId, int participantId, int upToId)
        {
            if (upToId <= 0) return;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var max = db.ExecuteScalar<int?>("SELECT MAX(Id) FROM PlanningMessage WHERE PlanningId = @0", planningId) ?? 0;
            var to = Math.Min(upToId, max);
            if (to <= 0) return;
            db.Execute(@"UPDATE PlanningMessageRead SET LastReadMessageId = @2, ReadAt = @3
                          WHERE PlanningId = @0 AND ParticipantId = @1 AND LastReadMessageId < @2",
                planningId, participantId, to, Now);
        }

        public void AddSystemMessage(int planningId, string key, string body, int? subjectParticipantId, string? refKind, int? refId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            InsertSystem(s.Database, planningId, key, body, null, subjectParticipantId, (refKind, refId), Now);
            Bump(s.Database, planningId);
        }

        private static void InsertSystem(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, string key,
            string body, int? actor, int? subject, (string? Kind, int? Id)? reference, DateTime now)
        {
            db.Insert(new PlanningMessageRow
            {
                PlanningId = planningId, ParticipantId = null, Kind = "System", SystemKey = key, Body = body ?? "",
                SubjectParticipantId = subject ?? actor, RefKind = reference?.Kind, RefId = reference?.Id, CreatedAt = now,
            });
        }

        // =====================================================================================
        //  Mötena
        // =====================================================================================

        public List<PlanningMeetingRow> Meetings(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY COALESCE(StartsAt, CreatedAt) DESC", planningId);
        }

        public PlanningMeetingRow? Meeting(int planningId, int meetingId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
        }

        public List<PlanningMeetingOptionRow> Options(IEnumerable<int> meetingIds)
        {
            var ids = meetingIds.Distinct().ToList();
            if (ids.Count == 0) return new List<PlanningMeetingOptionRow>();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMeetingOptionRow>(
                "SELECT * FROM PlanningMeetingOption WHERE PlanningMeetingId IN (@0) ORDER BY StartsAt", ids);
        }

        public List<PlanningMeetingVoteRow> Votes(IEnumerable<int> meetingIds)
        {
            var ids = meetingIds.Distinct().ToList();
            if (ids.Count == 0) return new List<PlanningMeetingVoteRow>();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMeetingVoteRow>(
                "SELECT * FROM PlanningMeetingVote WHERE PlanningMeetingId IN (@0)", ids);
        }

        public sealed record MeetingInput(string Title, List<(DateTime Start, DateTime End)> Options, string? Place,
            string? OnlineUrl, string? Agenda, DateTime? VoteDeadline);

        public static string? ValidateMeeting(MeetingInput m, bool requireOptions)
        {
            var t = (m.Title ?? "").Trim();
            if (t.Length == 0) return "Ge mötet en rubrik.";
            if (t.Length > 200) return "Rubriken är för lång (högst 200 tecken).";
            if (requireOptions && m.Options.Count == 0) return "Föreslå minst en tid.";
            if (m.Options.Count > MaxOptions) return $"Högst {MaxOptions} tider.";
            foreach (var (st, en) in m.Options)
                if (en <= st) return "En tid slutar innan den börjar.";
            if (m.Options.Select(o => o.Start).Distinct().Count() != m.Options.Count) return "Samma tid står två gånger.";
            var url = (m.OnlineUrl ?? "").Trim();
            if (url.Length > 0 && !IsSafeUrl(url)) return "Länken till mötet måste börja med https://";
            return null;
        }

        /// <summary>Bara https — en <c>javascript:</c>-länk i ett möte vore ett sätt att köra kod hos alla deltagare.</summary>
        public static bool IsSafeUrl(string url) =>
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url.Length <= 1000;

        public int CreateMeeting(int planningId, int byParticipantId, MeetingInput m)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var row = new PlanningMeetingRow
            {
                PlanningId = planningId, Title = m.Title.Trim(), Status = PlanningMeetingStatus.Proposed,
                Place = Clean(m.Place, 200), OnlineUrl = Clean(m.OnlineUrl, 1000), Agenda = Clean(m.Agenda, 20000),
                VoteDeadline = m.VoteDeadline, Sequence = 0, CreatedAt = now, CreatedBy = byParticipantId, UpdatedAt = now,
            };
            db.Insert(row);
            var i = 0;
            foreach (var (st, en) in m.Options.OrderBy(o => o.Start))
                db.Insert(new PlanningMeetingOptionRow { PlanningMeetingId = row.Id, StartsAt = st, EndsAt = en, SortOrder = i++ });
            InsertSystem(db, planningId, "meeting-proposed", row.Title, byParticipantId, null, ("meeting", row.Id), now);
            Bump(db, planningId);
            return row.Id;
        }

        public string? UpdateMeeting(int planningId, int meetingId, string title, string? place, string? url, string? agenda,
            string? notes, DateTime? voteDeadline)
        {
            if ((title ?? "").Trim().Length == 0) return "Ge mötet en rubrik.";
            if (!string.IsNullOrWhiteSpace(url) && !IsSafeUrl(url)) return "Länken till mötet måste börja med https://";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            var changedForCalendar = m.Status == PlanningMeetingStatus.Scheduled
                && (m.Title != title!.Trim() || (m.Place ?? "") != (place ?? "").Trim() || (m.OnlineUrl ?? "") != (url ?? "").Trim());
            m.Title = title!.Trim();
            m.Place = Clean(place, 200);
            m.OnlineUrl = Clean(url, 1000);
            m.Agenda = Clean(agenda, 20000);
            m.Notes = Clean(notes, 50000);
            m.VoteDeadline = voteDeadline;
            m.UpdatedAt = Now;
            if (changedForCalendar) m.Sequence++;
            db.Update(m);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Lägger till en tid i ett möte som ännu inte är bokat.</summary>
        public string? AddOption(int planningId, int meetingId, DateTime start, DateTime end)
        {
            if (end <= start) return "Tiden slutar innan den börjar.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            if (m.Status != PlanningMeetingStatus.Proposed) return "Mötet är redan bokat eller inställt.";
            var n = db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMeetingOption WHERE PlanningMeetingId = @0", meetingId);
            if (n >= MaxOptions) return $"Högst {MaxOptions} tider.";
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMeetingOption WHERE PlanningMeetingId = @0 AND StartsAt = @1", meetingId, start) > 0)
                return "Den tiden finns redan.";
            db.Insert(new PlanningMeetingOptionRow { PlanningMeetingId = meetingId, StartsAt = start, EndsAt = end, SortOrder = n });
            Bump(db, planningId);
            return null;
        }

        /// <summary>
        /// Ett svar på en föreslagen tid. Bara medan mötet är föreslaget — efter bokningen gäller den
        /// bokade tiden, och ett sent svar hade bara förvirrat.
        /// </summary>
        public string? Vote(int planningId, int meetingId, int optionId, int participantId, string answer, bool viaEmail)
        {
            var a = PlanningVoteAnswer.Normalize(answer);
            if (a == null) return "Okänt svar.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            if (m.Status != PlanningMeetingStatus.Proposed) return "Mötet är redan bokat — svaren är stängda.";
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMeetingOption WHERE Id = @0 AND PlanningMeetingId = @1", optionId, meetingId) == 0)
                return "Tiden finns inte.";
            var now = Now;
            var n = db.Execute(@"UPDATE PlanningMeetingVote SET Answer = @3, AnsweredAt = @4, ViaEmail = @5
                                  WHERE PlanningMeetingOptionId = @1 AND ParticipantId = @2",
                meetingId, optionId, participantId, a, now, viaEmail);
            if (n == 0)
                db.Insert(new PlanningMeetingVoteRow
                {
                    PlanningMeetingId = meetingId, PlanningMeetingOptionId = optionId, ParticipantId = participantId,
                    Answer = a, AnsweredAt = now, ViaEmail = viaEmail,
                });
            Bump(db, planningId);
            return null;
        }

        public sealed record BookResult(string? Error, bool WasRebooked);

        /// <summary>
        /// Bokar mötet på en föreslagen tid, eller på en egen tid (inget tidsförslag). En ombokning
        /// höjer kalenderfilens SEQUENCE så att kalendrarna flyttar mötet i stället för att visa två.
        /// </summary>
        public BookResult Book(int planningId, int meetingId, int? optionId, DateTime? start, DateTime? end, int byParticipantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return new BookResult("Mötet finns inte.", false);
            if (m.Status == PlanningMeetingStatus.Cancelled) return new BookResult("Mötet är inställt.", false);
            DateTime st, en;
            if (optionId > 0)
            {
                var o = db.SingleOrDefault<PlanningMeetingOptionRow>(
                    "SELECT * FROM PlanningMeetingOption WHERE Id = @0 AND PlanningMeetingId = @1", optionId, meetingId);
                if (o == null) return new BookResult("Tiden finns inte.", false);
                st = o.StartsAt; en = o.EndsAt;
            }
            else
            {
                if (!start.HasValue || !end.HasValue || end <= start) return new BookResult("Ange en tid som slutar efter att den börjar.", false);
                st = start.Value; en = end.Value;
            }
            var rebooked = m.Status == PlanningMeetingStatus.Scheduled;
            if (rebooked && m.StartsAt == st && m.EndsAt == en) return new BookResult(null, false);
            m.Status = PlanningMeetingStatus.Scheduled;
            m.ChosenOptionId = optionId > 0 ? optionId : null;
            m.StartsAt = st; m.EndsAt = en;
            m.BookedAt = Now; m.BookedBy = byParticipantId;
            m.UpdatedAt = Now;
            m.Sequence++;
            db.Update(m);
            InsertSystem(db, planningId, rebooked ? "meeting-rebooked" : "meeting-booked", m.Title, byParticipantId, null, ("meeting", m.Id), Now);
            Bump(db, planningId);
            return new BookResult(null, rebooked);
        }

        public string? Cancel(int planningId, int meetingId, int byParticipantId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            if (m.Status == PlanningMeetingStatus.Cancelled) return null;
            m.Status = PlanningMeetingStatus.Cancelled;
            m.CancelledAt = Now;
            m.UpdatedAt = Now;
            m.Sequence++;
            db.Update(m);
            InsertSystem(db, planningId, "meeting-cancelled", m.Title, byParticipantId, null, ("meeting", m.Id), Now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Ta bort ett möte helt — bara ett föreslaget möte (ett bokat ställs in, så att kalendrarna får veta).</summary>
        public string? DeleteMeeting(int planningId, int meetingId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var m = db.SingleOrDefault<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", meetingId, planningId);
            if (m == null) return "Mötet finns inte.";
            if (m.Status == PlanningMeetingStatus.Scheduled) return "Ett bokat möte tas inte bort — ställ in det, så får alla veta.";
            db.Execute("UPDATE PlanningMeeting SET DeletedAt = @2 WHERE Id = @0 AND PlanningId = @1", meetingId, planningId, Now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Bokade möten för alla planeringar där deltagaren är med — kalenderfilen och startsidan.</summary>
        public List<PlanningMeetingRow> ScheduledFor(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningMeetingRow>(
                "SELECT * FROM PlanningMeeting WHERE PlanningId = @0 AND DeletedAt IS NULL AND Status IN (N'Scheduled', N'Cancelled') AND StartsAt IS NOT NULL",
                planningId);
        }

        // =====================================================================================
        //  Hjälpare
        // =====================================================================================

        private static string? Clean(string? v, int max)
        {
            var t = (v ?? "").Trim();
            if (t.Length == 0) return null;
            return t.Length > max ? t.Substring(0, max) : t;
        }
    }
}
