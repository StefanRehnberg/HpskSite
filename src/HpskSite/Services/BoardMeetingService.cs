using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Board meeting lifecycle: meetings, agenda, attendance/quorum, protokoll (decisions), and actions.
    /// Club/region-scoped via OwnerType (0=Club, 1=Region) / OwnerId. See BOARD_WORK_PHASE2_MEETINGS.md.
    /// </summary>
    public class BoardMeetingService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly BoardRoleService _boardRoleService;

        public BoardMeetingService(IScopeProvider scopeProvider, IMemberService memberService, BoardRoleService boardRoleService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _boardRoleService = boardRoleService;
        }

        // ---- Meetings -------------------------------------------------------

        /// <summary>Create a meeting, seeding agenda from the type template and attendees from the board roster.</summary>
        public BoardMeeting CreateMeeting(int ownerType, int ownerId, string meetingType, string title,
            DateTime meetingDate, string? location, int createdByMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var meeting = new BoardMeeting
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                MeetingType = meetingType,
                Title = string.IsNullOrWhiteSpace(title) ? BoardMeetingTemplates.GetLabel(meetingType) : title,
                MeetingDate = meetingDate,
                Location = location,
                Status = "Planerat",
                CreatedByMemberId = createdByMemberId,
                CreatedDate = DateTime.UtcNow,
                IsActive = true
            };
            db.Insert(meeting);

            // Seed agenda from the template
            var agenda = BoardMeetingTemplates.GetAgenda(meetingType);
            for (int i = 0; i < agenda.Length; i++)
            {
                db.Insert(new BoardMeetingAgendaItem
                {
                    MeetingId = meeting.Id,
                    SortOrder = i,
                    Heading = agenda[i],
                    IsActive = true
                });
            }

            // Seed attendees from the current board roster (board members only)
            var board = _boardRoleService.GetBoardMembers(ownerType, ownerId, boardOnly: true);
            foreach (var role in board)
            {
                // One row per member even if they hold several roles
                if (db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0 AND MemberId = @1",
                        meeting.Id, role.MemberId) > 0)
                    continue;

                db.Insert(new BoardMeetingAttendee
                {
                    MeetingId = meeting.Id,
                    MemberId = role.MemberId,
                    RoleTitle = role.DisplayTitle,
                    AttendanceStatus = "Närvarande",
                    IsChairman = role.RoleKey == "Ordforande",
                    IsSecretary = role.RoleKey == "Sekreterare",
                    IsAdjuster = false
                });
            }

            return meeting;
        }

        public List<BoardMeeting> GetMeetings(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardMeeting>(
                "SELECT * FROM BoardMeetings WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 ORDER BY MeetingDate DESC",
                ownerType, ownerId);
        }

        public BoardMeeting? GetMeeting(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardMeeting>(id);
        }

        public bool UpdateMeeting(int id, string meetingType, string title, DateTime meetingDate,
            string? location, int? quorumOverride, string? notes)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;
            m.MeetingType = meetingType;
            m.Title = title;
            m.MeetingDate = meetingDate;
            m.Location = location;
            m.QuorumOverride = quorumOverride;
            m.Notes = notes;
            db.Update(m);
            return true;
        }

        public bool SetStatus(int id, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;
            m.Status = status;
            db.Update(m);
            return true;
        }

        public bool SetJusterat(int id, int adjusterMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;
            m.AdjusterMemberId = adjusterMemberId;
            m.JustifiedDate = DateTime.UtcNow;
            m.Status = "Justerat";
            db.Update(m);
            return true;
        }

        public bool DeleteMeeting(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;
            m.IsActive = false;
            db.Update(m);
            return true;
        }

        // ---- Agenda ---------------------------------------------------------

        public List<BoardMeetingAgendaItem> GetAgenda(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardMeetingAgendaItem>(
                "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 ORDER BY SortOrder, Id",
                meetingId);
        }

        public BoardMeetingAgendaItem AddAgendaItem(int meetingId, string heading)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var nextSort = db.ExecuteScalar<int?>(
                "SELECT MAX(SortOrder) FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1", meetingId) ?? -1;
            var item = new BoardMeetingAgendaItem
            {
                MeetingId = meetingId,
                SortOrder = nextSort + 1,
                Heading = heading,
                IsActive = true
            };
            db.Insert(item);
            return item;
        }

        public bool UpdateAgendaItem(int id, string heading, string? discussion, string? decision)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var item = db.SingleOrDefaultById<BoardMeetingAgendaItem>(id);
            if (item == null) return false;
            item.Heading = heading;
            item.Discussion = discussion;
            item.Decision = decision;
            db.Update(item);
            return true;
        }

        // ---- Agenda attachments (links) ------------------------------------

        public List<BoardMeetingAgendaLink> GetLinksForMeeting(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardMeetingAgendaLink>(
                "SELECT l.* FROM BoardMeetingAgendaLinks l " +
                "JOIN BoardMeetingAgendaItems a ON a.Id = l.AgendaItemId " +
                "WHERE a.MeetingId = @0 AND l.IsActive = 1 ORDER BY l.Id", meetingId);
        }

        public BoardMeetingAgendaLink AddAgendaLink(int agendaItemId, string kind, int? refId, string? url, string label)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var link = new BoardMeetingAgendaLink
            {
                AgendaItemId = agendaItemId,
                Kind = kind,
                RefId = refId,
                Url = url,
                Label = label,
                IsActive = true
            };
            db.Insert(link);
            return link;
        }

        public bool RemoveAgendaLink(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var l = db.SingleOrDefaultById<BoardMeetingAgendaLink>(id);
            if (l == null) return false;
            l.IsActive = false;
            db.Update(l);
            return true;
        }

        public int? GetLinkMeetingId(int linkId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var l = db.SingleOrDefaultById<BoardMeetingAgendaLink>(linkId);
            if (l == null) return null;
            return db.SingleOrDefaultById<BoardMeetingAgendaItem>(l.AgendaItemId)?.MeetingId;
        }

        public int? GetAgendaItemMeetingId(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardMeetingAgendaItem>(id)?.MeetingId;
        }

        public bool RemoveAgendaItem(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var item = db.SingleOrDefaultById<BoardMeetingAgendaItem>(id);
            if (item == null) return false;
            item.IsActive = false;
            db.Update(item);
            return true;
        }

        // ---- Attendees ------------------------------------------------------

        public List<BoardMeetingAttendee> GetAttendees(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardMeetingAttendee>(
                "SELECT * FROM BoardMeetingAttendees WHERE MeetingId = @0 ORDER BY IsChairman DESC, IsSecretary DESC, Id",
                meetingId);
            ResolveAttendeeNames(rows);
            return rows;
        }

        public bool AddAttendee(int meetingId, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            if (db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0 AND MemberId = @1",
                    meetingId, memberId) > 0)
                return false;
            db.Insert(new BoardMeetingAttendee
            {
                MeetingId = meetingId,
                MemberId = memberId,
                AttendanceStatus = "Närvarande"
            });
            return true;
        }

        /// <summary>Update only the attendance status (preserves chairman/secretary/adjuster flags).</summary>
        public bool SetAttendance(int id, string attendanceStatus)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAttendee>(id);
            if (a == null) return false;
            a.AttendanceStatus = attendanceStatus;
            db.Update(a);
            return true;
        }

        public bool UpdateAttendee(int id, string attendanceStatus, bool isChairman, bool isSecretary, bool isAdjuster)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAttendee>(id);
            if (a == null) return false;
            a.AttendanceStatus = attendanceStatus;
            a.IsChairman = isChairman;
            a.IsSecretary = isSecretary;
            a.IsAdjuster = isAdjuster;
            db.Update(a);
            return true;
        }

        /// <summary>
        /// Add any current board members who aren't yet on this meeting's attendee list (e.g. board
        /// changed after the meeting was created). Never removes. Returns how many were added.
        /// </summary>
        public int SyncBoardAttendees(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var meeting = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (meeting == null) return 0;

            var board = _boardRoleService.GetBoardMembers(meeting.OwnerType, meeting.OwnerId, boardOnly: true);
            int added = 0;
            foreach (var role in board)
            {
                if (db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0 AND MemberId = @1",
                        meetingId, role.MemberId) > 0)
                    continue;
                db.Insert(new BoardMeetingAttendee
                {
                    MeetingId = meetingId,
                    MemberId = role.MemberId,
                    RoleTitle = role.DisplayTitle,
                    AttendanceStatus = "Närvarande",
                    IsChairman = role.RoleKey == "Ordforande",
                    IsSecretary = role.RoleKey == "Sekreterare"
                });
                added++;
            }
            return added;
        }

        public int? GetAttendeeMeetingId(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardMeetingAttendee>(id)?.MeetingId;
        }

        public bool RemoveAttendee(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAttendee>(id);
            if (a == null) return false;
            db.Delete(a);
            return true;
        }

        // ---- Quorum ---------------------------------------------------------

        /// <summary>
        /// Beslutsförhet: present count, required count (override, else majority of attendees on the list),
        /// and whether quorum is met.
        /// </summary>
        public (int Present, int Total, int Required, bool IsMet) GetQuorum(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var total = db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0", meetingId);
            var present = db.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0 AND AttendanceStatus = N'Närvarande'", meetingId);
            var meeting = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            // Default required = simple majority of the board on the list (mer än hälften).
            var required = meeting?.QuorumOverride ?? (total / 2 + 1);
            return (present, total, required, present >= required && required > 0);
        }

        // ---- Actions (åtgärder) --------------------------------------------

        public List<BoardMeetingAction> GetActionsForMeeting(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardMeetingAction>(
                "SELECT * FROM BoardMeetingActions WHERE MeetingId = @0 AND IsActive = 1 ORDER BY Status, DueDate, Id", meetingId);
            ResolveActionNames(rows);
            return rows;
        }

        public List<BoardMeetingAction> GetOpenActions(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardMeetingAction>(
                "SELECT * FROM BoardMeetingActions WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 AND Status = N'Öppen' ORDER BY DueDate, Id",
                ownerType, ownerId);
            ResolveActionNames(rows);
            return rows;
        }

        public List<BoardMeetingAction> GetMyActions(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardMeetingAction>(
                "SELECT * FROM BoardMeetingActions WHERE AssignedToMemberId = @0 AND IsActive = 1 AND Status = N'Öppen' ORDER BY DueDate, Id",
                memberId);
            ResolveActionNames(rows);
            return rows;
        }

        public BoardMeetingAction AddAction(int ownerType, int ownerId, int? meetingId, int? agendaItemId,
            string description, int? assignedToMemberId, DateTime? dueDate, int createdByMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var action = new BoardMeetingAction
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                MeetingId = meetingId,
                AgendaItemId = agendaItemId,
                Description = description,
                AssignedToMemberId = assignedToMemberId,
                DueDate = dueDate,
                Status = "Öppen",
                CreatedByMemberId = createdByMemberId,
                CreatedDate = DateTime.UtcNow,
                IsActive = true
            };
            db.Insert(action);
            return action;
        }

        public bool UpdateAction(int id, string description, int? assignedToMemberId, DateTime? dueDate)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAction>(id);
            if (a == null) return false;
            a.Description = description;
            a.AssignedToMemberId = assignedToMemberId;
            a.DueDate = dueDate;
            db.Update(a);
            return true;
        }

        public bool SetActionDone(int id, bool done)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAction>(id);
            if (a == null) return false;
            a.Status = done ? "Klar" : "Öppen";
            a.CompletedDate = done ? DateTime.UtcNow : null;
            db.Update(a);
            return true;
        }

        public bool RemoveAction(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefaultById<BoardMeetingAction>(id);
            if (a == null) return false;
            a.IsActive = false;
            db.Update(a);
            return true;
        }

        public BoardMeetingAction? GetAction(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardMeetingAction>(id);
        }

        // ---- Name resolution (batched, no N+1) ------------------------------

        private Dictionary<int, string> ResolveNames(IEnumerable<int> memberIds)
        {
            var byId = new Dictionary<int, string>();
            foreach (var memberId in memberIds.Distinct())
            {
                if (memberId <= 0) continue;
                var member = _memberService.GetById(memberId);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[memberId] = string.IsNullOrEmpty(name) ? member.Name : name;
            }
            return byId;
        }

        private void ResolveAttendeeNames(List<BoardMeetingAttendee> rows)
        {
            var names = ResolveNames(rows.Select(r => r.MemberId));
            foreach (var r in rows)
                if (names.TryGetValue(r.MemberId, out var n)) r.MemberName = n;
        }

        private void ResolveActionNames(List<BoardMeetingAction> rows)
        {
            var names = ResolveNames(rows.Where(r => r.AssignedToMemberId.HasValue).Select(r => r.AssignedToMemberId!.Value));
            foreach (var r in rows)
                if (r.AssignedToMemberId.HasValue && names.TryGetValue(r.AssignedToMemberId.Value, out var n))
                    r.AssignedToName = n;
        }
    }
}
