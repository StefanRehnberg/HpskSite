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
    public class PlanningService
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
            "PlanningMeetingVote", "PlanningNotifyLog", "PlanningLink",
        };

        /// <summary>Tabeller som saknas. Kastar om databasen inte går att fråga — "vet inte" är inget svar.</summary>
        public List<string> MissingSchema()
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return Tables.Where(t => s.Database.ExecuteScalar<int>(
                "SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0).ToList();
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

        public sealed record CreateInput(string Name, string Kind, int? OwnerType, int? OwnerId, string? TargetText,
            DateTime? TargetStart, DateTime? TargetEnd, string Visibility, int MemberId, string DisplayName,
            int? RepresentsClubId, int? RepresentsRegionId);

        public static string? Validate(string? name, DateTime? start, DateTime? end, int? ownerType, int? ownerId, string visibility)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "Ge planeringen ett namn, till exempel \"SM 2028\".";
            if (n.Length > 200) return "Namnet är för långt (högst 200 tecken).";
            if (start.HasValue && end.HasValue && end.Value.Date < start.Value.Date) return "Slutdagen ligger före startdagen.";
            if ((ownerType.HasValue) != (ownerId > 0)) return "Välj föreningen planeringen hör till, eller ingen.";
            if (ownerType.HasValue && ownerType is not (0 or 1)) return "Okänd förening.";
            if (visibility == PlanningVisibility.OwnerOrg && !ownerType.HasValue)
                return "Planeringen kan bara synas för föreningen när den hör till en förening.";
            return null;
        }

        /// <summary>Skapar planeringen med grunddelen och den som skapar som <i>Kan ändra allt</i>.</summary>
        public (int PlanningId, int ParticipantId) Create(CreateInput i)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = new PlanningRow
            {
                Name = i.Name.Trim(),
                Kind = PlanningKind.Normalize(i.Kind),
                OwnerType = i.OwnerType,
                OwnerId = i.OwnerType.HasValue ? i.OwnerId : null,
                TargetText = Clean(i.TargetText, 200),
                TargetStart = i.TargetStart?.Date,
                TargetEnd = i.TargetEnd?.Date,
                Visibility = i.Visibility,
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

        public string? Update(int planningId, string name, string? targetText, DateTime? start, DateTime? end,
            string visibility, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return "Planeringen finns inte.";
            var err = Validate(name, start, end, p.OwnerType, p.OwnerId, visibility);
            if (err != null) return err;
            var renamed = !string.Equals(p.Name, name.Trim(), StringComparison.Ordinal);
            p.Name = name.Trim();
            p.TargetText = Clean(targetText, 200);
            p.TargetStart = start?.Date;
            p.TargetEnd = end?.Date;
            p.Visibility = visibility;
            p.UpdatedAt = Now;
            p.UpdatedBy = byMemberId;
            db.Update(p);
            if (renamed) db.Execute("UPDATE PlanningPart SET Name = @1 WHERE PlanningId = @0 AND Kind = N'Root'", planningId, p.Name);
            Bump(db, planningId);
            return null;
        }

        public void SetStatus(int planningId, string status, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET Status = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, status == "Archived" ? "Archived" : "Active", Now, byMemberId);
            Bump(s.Database, planningId);
        }

        /// <summary>Mjuk radering: raden står kvar, men planeringen syns ingenstans och länkarna slutar fungera.</summary>
        public void Delete(int planningId, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE Planning SET DeletedAt = @1, DeletedBy = @2 WHERE Id = @0 AND DeletedAt IS NULL",
                planningId, Now, byMemberId);
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
                + " ORDER BY CASE Access WHEN N'Owner' THEN 0 WHEN N'Editor' THEN 1 ELSE 2 END, DisplayName", planningId);
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
                return "Det måste finnas minst en som kan ändra allt. Ge någon annan den rätten först.";
            row.Access = access;
            row.RepresentsClubId = repClubId;
            row.RepresentsRegionId = repClubId > 0 ? null : repRegionId;
            row.RepresentsText = Clean(repText, 200);
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
                return "Det måste finnas minst en som kan ändra allt. Ge någon annan den rätten först.";
            db.Execute("UPDATE PlanningMember SET RemovedAt = @2, RemovedBy = @3 WHERE Id = @0 AND PlanningId = @1",
                participantId, planningId, Now, byMemberId);
            InsertSystem(db, planningId, "member-left", "", null, participantId, null, Now);
            Bump(db, planningId);
            return null;
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
                           AND ISNULL(m.ParticipantId, 0) <> pm.Id) AS Unread,
                       (SELECT COUNT(*) FROM PlanningMeeting mt
                         WHERE mt.PlanningId = p.Id AND mt.DeletedAt IS NULL AND mt.Status = N'Proposed'
                           AND NOT EXISTS (SELECT 1 FROM PlanningMeetingVote v WHERE v.PlanningMeetingId = mt.Id AND v.ParticipantId = pm.Id)) AS VotesOwed,
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
                    r.Access, r.Unread, r.VotesOwed, r.NextMeetingAt, nextTitle, r.LastActivityAt));
            }
            return list.OrderBy(p => p.Status == "Archived").ThenByDescending(p => p.LastActivityAt ?? DateTime.MinValue).ToList();
        }

        /// <summary>Startsidans kort: aktiva planeringar där något väntar på medlemmen, eller ett möte inom en vecka.</summary>
        public List<MyPlanning> HomeCards(int memberId) =>
            memberId <= 0 ? new List<MyPlanning>() : ForMember(memberId)
                .Where(p => p.Status == "Active" && (p.Unread > 0 || p.VotesOwed > 0
                    || (p.NextMeetingAt.HasValue && p.NextMeetingAt.Value <= Now.AddDays(7))))
                .Take(5).ToList();

        public sealed record MyPlanning(int PlanningId, string Name, string Status, string Kind, string? TargetText,
            DateTime? TargetStart, int ParticipantId, string Access, int Unread, int VotesOwed, DateTime? NextMeetingAt,
            string? NextMeetingTitle, DateTime? LastActivityAt);

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

        /// <summary>Planeringar som föreningens styrelse/administratörer får läsa (synliga för föreningen).</summary>
        public List<PlanningRow> VisibleToOwner(int ownerType, int ownerId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningRow>(
                "SELECT * FROM Planning WHERE OwnerType = @0 AND OwnerId = @1 AND Visibility = N'OwnerOrg' AND DeletedAt IS NULL ORDER BY UpdatedAt DESC",
                ownerType, ownerId);
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
        public PostResult Post(int planningId, int participantId, string body, int? replyToId, IEnumerable<int>? mentionIds, bool viaEmail)
        {
            var err = ValidateBody(body);
            if (err != null) return new PostResult(0, new List<int>(), err);
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (replyToId > 0)
            {
                var parent = db.SingleOrDefault<PlanningMessageRow>(
                    "SELECT * FROM PlanningMessage WHERE Id = @0 AND PlanningId = @1", replyToId, planningId);
                if (parent == null || parent.Kind != "Message") return new PostResult(0, new List<int>(), "Meddelandet du svarar på finns inte.");
                if (parent.ReplyToMessageId > 0) replyToId = parent.ReplyToMessageId; // en nivå: svar på ett svar hamnar under samma tråd
            }
            var row = new PlanningMessageRow
            {
                PlanningId = planningId, ParticipantId = participantId, Kind = "Message", Body = body.Trim(),
                ReplyToMessageId = replyToId > 0 ? replyToId : null, ViaEmail = viaEmail, CreatedAt = now,
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
