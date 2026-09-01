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
        private readonly BoardMeetingTemplateService _templateService;

        public BoardMeetingService(IScopeProvider scopeProvider, IMemberService memberService,
            BoardRoleService boardRoleService, BoardMeetingTemplateService templateService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _boardRoleService = boardRoleService;
            _templateService = templateService;
        }

        // ---- Meetings -------------------------------------------------------

        /// <summary>Create a meeting, seeding agenda from the type template and attendees from the board roster.</summary>
        public BoardMeeting CreateMeeting(int ownerType, int ownerId, string meetingType, string title,
            DateTime meetingDate, string? location, int createdByMemberId)
        {
            // Resolve the typed agenda (saved club template or built-in default) before opening our scope.
            var agenda = _templateService.GetEffectiveAgenda(ownerType, ownerId, meetingType);

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
                CreatedDate = DateTime.Now,
                IsActive = true
            };
            db.Insert(meeting);

            // Seed typed agenda items from the effective template.
            for (int i = 0; i < agenda.Count; i++)
            {
                var d = agenda[i];
                db.Insert(new BoardMeetingAgendaItem
                {
                    MeetingId = meeting.Id,
                    SortOrder = i,
                    Heading = d.Heading,
                    ItemType = string.IsNullOrWhiteSpace(d.ItemType) ? "text" : d.ItemType,
                    ElectionRole = string.IsNullOrEmpty(d.ElectionRole) ? null : d.ElectionRole,
                    ElectionCount = d.ElectionCount < 1 ? 1 : d.ElectionCount,
                    ElectionSource = d.ElectionSource == "members" ? "members" : "attendees",
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
                    IsChairman = role.RoleKey == BoardRoleDefinitions.RoleOrdforande,
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
            // Never let a blank meetingType wipe the stored type (the date/title autosave omits it).
            if (!string.IsNullOrWhiteSpace(meetingType)) m.MeetingType = meetingType;
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

        public bool MarkKallelseSent(int id, int byMemberId, int recipientCount)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;
            m.KallelseSentDate = DateTime.Now;
            m.KallelseSentByMemberId = byMemberId;
            m.KallelseRecipientCount = recipientCount;
            db.Update(m);
            return true;
        }

        /// <summary>
        /// Lock the protokoll as justerat. Justerare count varies (0–2 besides ordförande/sekreterare,
        /// who always sign) — the selected attendees are flagged IsAdjuster; any previous flags are cleared.
        /// AdjusterMemberId keeps the first id for backward compatibility, but the IsAdjuster flags are the
        /// source of truth for who justerade.
        /// </summary>
        public bool SetJusterat(int id, IEnumerable<int> adjusterMemberIds)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(id);
            if (m == null) return false;

            var ids = (adjusterMemberIds ?? Enumerable.Empty<int>()).Where(x => x > 0).Distinct().ToList();
            db.Execute("UPDATE BoardMeetingAttendees SET IsAdjuster = 0 WHERE MeetingId = @0", id);
            foreach (var memberId in ids)
                db.Execute("UPDATE BoardMeetingAttendees SET IsAdjuster = 1 WHERE MeetingId = @0 AND MemberId = @1", id, memberId);

            m.AdjusterMemberId = ids.Count > 0 ? ids[0] : (int?)null;
            m.JustifiedDate = DateTime.Now;
            m.Status = "Justerat";
            db.Update(m);
            return true;
        }

        // ---- Digital justering (Phase 2) -----------------------------------
        // Required signers = ordförande + sekreterare + justerare (attendees with a role flag). Each
        // approves the protocol (on the spot via QR, or via an emailed link); when all have approved the
        // protocol becomes Justerat + locked. Statuses: Genomfört → VantarJustering → Justerat.

        public List<BoardMeetingAttendee> GetSigners(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<BoardMeetingAttendee>(
                "SELECT * FROM BoardMeetingAttendees WHERE MeetingId = @0 AND (IsChairman = 1 OR IsSecretary = 1 OR IsAdjuster = 1) ORDER BY IsChairman DESC, IsSecretary DESC, Id",
                meetingId);
            ResolveAttendeeNames(rows);
            return rows;
        }

        /// <summary>Send the protocol for justering: locks edits, resets approvals, status → VantarJustering.</summary>
        public bool SendForJustering(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (m == null) return false;
            db.Execute("UPDATE BoardMeetingAttendees SET ApprovedDate = NULL, ApprovedVia = NULL WHERE MeetingId = @0", meetingId);
            m.Status = "VantarJustering";
            m.JusteringRequestedDate = DateTime.Now;
            m.JustifiedDate = null;
            db.Update(m);
            return true;
        }

        /// <summary>
        /// A required signer approves the protocol. When the last signer approves, the protocol locks
        /// (Justerat). Returns whether it succeeded, whether it is now fully locked, and the tally.
        /// </summary>
        public (bool Ok, bool Locked, int Approved, int Total, string Message) ApproveByMember(int meetingId, int memberId, string via)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (m == null) return (false, false, 0, 0, "Mötet hittades inte");
            if (m.Status == "Justerat") return (false, true, 0, 0, "Protokollet är redan justerat");
            if (m.Status != "VantarJustering") return (false, false, 0, 0, "Protokollet är inte skickat för justering");

            var att = db.FirstOrDefault<BoardMeetingAttendee>(
                "SELECT * FROM BoardMeetingAttendees WHERE MeetingId = @0 AND MemberId = @1", meetingId, memberId);
            if (att == null || !(att.IsChairman || att.IsSecretary || att.IsAdjuster))
                return (false, false, 0, 0, "Du är inte vald att justera det här protokollet");

            if (att.ApprovedDate == null)
            {
                att.ApprovedDate = DateTime.Now;
                att.ApprovedVia = via;
                db.Update(att);
            }

            var signers = db.Fetch<BoardMeetingAttendee>(
                "SELECT * FROM BoardMeetingAttendees WHERE MeetingId = @0 AND (IsChairman = 1 OR IsSecretary = 1 OR IsAdjuster = 1)", meetingId);
            int total = signers.Count;
            int approved = signers.Count(s => s.ApprovedDate != null);
            bool locked = total > 0 && approved >= total;
            if (locked)
            {
                m.Status = "Justerat";
                m.JustifiedDate = DateTime.Now;
                db.Update(m);
            }
            return (true, locked, approved, total, "");
        }

        /// <summary>Reopen a sent/justerat protocol for editing — clears all approvals.</summary>
        public bool ReopenForEditing(int meetingId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var m = db.SingleOrDefaultById<BoardMeeting>(meetingId);
            if (m == null) return false;
            db.Execute("UPDATE BoardMeetingAttendees SET ApprovedDate = NULL, ApprovedVia = NULL WHERE MeetingId = @0", meetingId);
            m.Status = "Genomfört";
            m.JustifiedDate = null;
            m.JusteringRequestedDate = null;
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

        public BoardMeetingAgendaItem AddAgendaItem(int meetingId, string heading,
            string itemType = "text", string? electionRole = null, int electionCount = 1, string electionSource = "attendees")
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
                ItemType = itemType == "note" || itemType == "election" ? itemType : "text",
                ElectionRole = string.IsNullOrEmpty(electionRole) ? null : electionRole,
                ElectionCount = electionCount < 1 ? 1 : electionCount,
                ElectionSource = electionSource == "members" ? "members" : "attendees",
                IsActive = true
            };
            db.Insert(item);
            return item;
        }

        /// <summary>
        /// Move an agenda item one step up (direction &lt; 0) or down (direction &gt; 0). A newly added point
        /// always lands last — after "Mötet avslutas" — so the board needs to be able to pull it back up.
        /// Normalizes SortOrder to 0..n-1 while it's at it (legacy rows can share values).
        /// </summary>
        public bool MoveAgendaItem(int agendaItemId, int direction)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var item = db.SingleOrDefaultById<BoardMeetingAgendaItem>(agendaItemId);
            if (item == null) return false;

            var list = db.Fetch<BoardMeetingAgendaItem>(
                "SELECT * FROM BoardMeetingAgendaItems WHERE MeetingId = @0 AND IsActive = 1 ORDER BY SortOrder, Id",
                item.MeetingId);

            var idx = list.FindIndex(x => x.Id == agendaItemId);
            var target = idx + (direction < 0 ? -1 : 1);
            if (idx < 0 || target < 0 || target >= list.Count) return false;

            (list[idx], list[target]) = (list[target], list[idx]);

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].SortOrder == i) continue;
                list[i].SortOrder = i;
                db.Update(list[i]);
            }
            return true;
        }

        /// <summary>
        /// Record the persons chosen in an election agenda item. For role-mapped elections
        /// (chairman/secretary/adjuster) this also sets the matching attendee flag — which is what drives
        /// the protokoll signatures and (Phase 2) the justering approver set. Clears the flag on others first.
        /// </summary>
        public bool SaveAgendaElection(int agendaItemId, IEnumerable<int> memberIds)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var item = db.SingleOrDefaultById<BoardMeetingAgendaItem>(agendaItemId);
            if (item == null) return false;

            var ids = (memberIds ?? Enumerable.Empty<int>()).Where(x => x > 0).Distinct().ToList();
            item.ElectedMemberIds = ids.Count > 0 ? string.Join(",", ids) : null;
            db.Update(item);

            // Mirror to the attendee role flag so signatures/justering follow the election.
            var col = item.ElectionRole switch
            {
                "chairman" => "IsChairman",
                "secretary" => "IsSecretary",
                "adjuster" => "IsAdjuster",
                _ => null
            };
            if (col != null)
            {
                // A role-mapped electee must be on the attendee list (they're present + sign the protokoll).
                // For "members" elections the chosen person may not be a board attendee yet — add them.
                foreach (var mid in ids)
                    if (db.ExecuteScalar<int>("SELECT COUNT(1) FROM BoardMeetingAttendees WHERE MeetingId = @0 AND MemberId = @1", item.MeetingId, mid) == 0)
                        db.Insert(new BoardMeetingAttendee { MeetingId = item.MeetingId, MemberId = mid, AttendanceStatus = "Närvarande" });

                db.Execute($"UPDATE BoardMeetingAttendees SET {col} = 0 WHERE MeetingId = @0", item.MeetingId);
                foreach (var mid in ids)
                    db.Execute($"UPDATE BoardMeetingAttendees SET {col} = 1 WHERE MeetingId = @0 AND MemberId = @1", item.MeetingId, mid);
            }
            return true;
        }

        /// <summary>Owner (club/region) members for a "members"-source election picker, optionally filtered by query.</summary>
        public List<(int Id, string Name)> SearchOwnerMembers(int ownerType, int ownerId, string? query)
        {
            var all = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved);

            if (ownerType == DocumentOwnerType.Club)
            {
                var clubIdStr = ownerId.ToString();
                all = all.Where(m =>
                    m.GetValue("primaryClubId")?.ToString() == clubIdStr ||
                    (m.GetValue("memberClubIds")?.ToString()?.Split(',').Select(s => s.Trim()).Contains(clubIdStr) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(query))
                all = all.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            return all.OrderBy(m => m.Name).Take(30)
                .Select(m =>
                {
                    var first = m.GetValue<string>("firstName") ?? "";
                    var last = m.GetValue<string>("lastName") ?? "";
                    var name = $"{first} {last}".Trim();
                    return (m.Id, string.IsNullOrEmpty(name) ? m.Name : name);
                })
                .ToList();
        }

        // ---- Awards item (årsmötets utdelning) -----------------------------

        /// <summary>
        /// Läser dagordningspunktens utdelningssnapshot. null = ingen lista hämtad än.
        /// </summary>
        public BoardMeetingAwards? GetAgendaAwards(int agendaItemId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var item = scope.Database.SingleOrDefaultById<BoardMeetingAgendaItem>(agendaItemId);
            return BoardMeetingAwards.FromJson(item?.AwardsData);
        }

        /// <summary>
        /// Skriver snapshotten.
        ///
        /// <para><b>⚠️ Vägrar på en punkt som inte är en utdelningspunkt.</b> AwardsData är en
        /// NVARCHAR(MAX) på en tabell som delas av alla punkttyper, så en felriktad skrivning skulle
        /// annars lagras tyst och aldrig visas någonstans — vilket läser som att sparningen inte
        /// fungerade.</para>
        /// </summary>
        public bool SaveAgendaAwards(int agendaItemId, BoardMeetingAwards awards)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var item = db.SingleOrDefaultById<BoardMeetingAgendaItem>(agendaItemId);
            if (item == null || item.ItemType != "awards") return false;

            item.AwardsData = awards.ToJson();
            db.Update(item);
            return true;
        }

        /// <summary>
        /// Sätter status/anteckning på EN rad, matchad på (medlem, grupp, artikel).
        ///
        /// <para>Per rad och inte hela blobben, eftersom mötet prickar av en person i taget och två
        /// sekreterare kan ha punkten öppen samtidigt — en helblobbskrivning från den ena hade då
        /// skrivit över den andras avprickning.</para>
        /// </summary>
        public bool SetAgendaAwardStatus(int agendaItemId, int memberId, string group, string item,
                                         string? status, string? note)
        {
            if (!BoardMeetingAwards.IsValidStatus(status)) return false;

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.SingleOrDefaultById<BoardMeetingAgendaItem>(agendaItemId);
            if (row == null || row.ItemType != "awards") return false;

            var awards = BoardMeetingAwards.FromJson(row.AwardsData);
            if (awards == null) return false;

            var target = awards.Rows.FirstOrDefault(r =>
                r.MemberId == memberId &&
                string.Equals((r.Group ?? "").Trim(), (group ?? "").Trim(), StringComparison.Ordinal) &&
                string.Equals((r.Item ?? "").Trim(), (item ?? "").Trim(), StringComparison.Ordinal));
            if (target == null) return false;

            target.Status = status;
            target.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            row.AwardsData = awards.ToJson();
            db.Update(row);
            return true;
        }

        /// <summary>
        /// Vilket år en utdelningspunkt gäller som standard.
        ///
        /// <para><b>Ett årsmöte behandlar föregående verksamhetsår</b> — det är vad
        /// verksamhetsberättelsen, den ekonomiska berättelsen och ansvarsfriheten handlar om — så
        /// märkena som delas ut är föregående års skörd. Övriga möten föreslår sitt eget år.</para>
        ///
        /// <para>⚠️ Bara ett FÖRSLAG: sekreteraren kan byta år. Ett möte som hålls i efterhand, eller
        /// en klubb som delar ut två år på en gång, får inte låsas av vår gissning.</para>
        /// </summary>
        public static int DefaultAwardsYear(BoardMeeting meeting)
        {
            int y = meeting.MeetingDate.Year;
            return meeting.MeetingType is "Arsmote" or "ExtraArsmote" ? y - 1 : y;
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
                    IsChairman = role.RoleKey == BoardRoleDefinitions.RoleOrdforande,
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
                CreatedDate = DateTime.Now,
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
            a.CompletedDate = done ? DateTime.Now : null;
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
