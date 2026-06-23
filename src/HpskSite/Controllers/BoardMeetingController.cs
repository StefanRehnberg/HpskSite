using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Board meeting endpoints (Phase 2). One access gate: site/club/regional admin OR an active
    /// board member of the owner (CanAccessBoardWork). See BOARD_WORK_PHASE2_MEETINGS.md.
    /// </summary>
    public class BoardMeetingController : SurfaceController
    {
        private readonly BoardMeetingService _meetingService;
        private readonly BoardRoleService _boardRoleService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly IDataProtector _protector;
        private readonly ILogger<BoardMeetingController> _logger;

        public BoardMeetingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardMeetingService meetingService,
            BoardRoleService boardRoleService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            EmailService emailService,
            ClubService clubService,
            IDataProtectionProvider dataProtection,
            ILogger<BoardMeetingController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _meetingService = meetingService;
            _boardRoleService = boardRoleService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _emailService = emailService;
            _clubService = clubService;
            _protector = dataProtection.CreateProtector("Board.Justering.v1");
            _logger = logger;
        }

        /// <summary>Protect/unprotect a meeting id into an opaque, non-enumerable QR/email token.</summary>
        public string ProtectMeetingToken(int meetingId) => _protector.Protect(meetingId.ToString());
        private int? UnprotectMeetingToken(string token)
        {
            try { return int.Parse(_protector.Unprotect(token)); } catch { return null; }
        }

        // ---- Meetings -------------------------------------------------------

        [HttpGet]
        public async Task<IActionResult> GetMeetings(int ownerType, int ownerId)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var meetings = _meetingService.GetMeetings(ownerType, ownerId);
            return Json(new { success = true, data = meetings.Select(MeetingSummaryDto) });
        }

        [HttpGet]
        public async Task<IActionResult> GetMeetingDetail(int meetingId)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !meeting.IsActive)
                return Json(new { success = false, message = "Mötet hittades inte" });
            if (!await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var agenda = _meetingService.GetAgenda(meetingId);
            var attendees = _meetingService.GetAttendees(meetingId);
            var actions = _meetingService.GetActionsForMeeting(meetingId);
            var links = _meetingService.GetLinksForMeeting(meetingId);
            var (present, total, required, isMet) = _meetingService.GetQuorum(meetingId);

            // Resolve elected-member names (electees may be non-attendees in "members"-source elections).
            var nameById = attendees.Where(a => !string.IsNullOrEmpty(a.MemberName))
                .GroupBy(a => a.MemberId).ToDictionary(g => g.Key, g => g.First().MemberName!);
            int[] ElectedIds(BoardMeetingAgendaItem a) => string.IsNullOrEmpty(a.ElectedMemberIds)
                ? Array.Empty<int>()
                : a.ElectedMemberIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToArray();
            foreach (var id in agenda.SelectMany(ElectedIds).Distinct().Where(id => !nameById.ContainsKey(id)))
            {
                var mem = _memberService.GetById(id);
                if (mem == null) continue;
                var nm = $"{mem.GetValue<string>("firstName")} {mem.GetValue<string>("lastName")}".Trim();
                nameById[id] = string.IsNullOrEmpty(nm) ? mem.Name : nm;
            }

            // Digital justering state (required signers + who has approved + whether *I* can approve).
            var meId = await GetCurrentMemberId();
            var signers = attendees.Where(a => a.IsChairman || a.IsSecretary || a.IsAdjuster).ToList();
            var justering = new
            {
                status = meeting.Status,
                requested = meeting.Status == "VantarJustering" || meeting.Status == "Justerat",
                requestedDate = meeting.JusteringRequestedDate?.ToString("yyyy-MM-dd HH:mm"),
                approvedCount = signers.Count(s => s.ApprovedDate != null),
                totalSigners = signers.Count,
                canApprove = meeting.Status == "VantarJustering" && signers.Any(s => s.MemberId == meId && s.ApprovedDate == null),
                signers = signers.Select(s => new
                {
                    s.MemberId, s.MemberName,
                    role = s.IsChairman ? "Ordförande" : s.IsSecretary ? "Sekreterare" : "Justerare",
                    approved = s.ApprovedDate != null,
                    approvedDate = s.ApprovedDate?.ToString("yyyy-MM-dd HH:mm"),
                    via = s.ApprovedVia ?? ""
                })
            };

            return Json(new
            {
                success = true,
                meeting = MeetingDetailDto(meeting),
                justering,
                agenda = agenda.Select(a =>
                {
                    var eids = ElectedIds(a);
                    return new
                    {
                        a.Id, a.SortOrder, a.Heading, a.Discussion, a.Decision,
                        a.ItemType, electionRole = a.ElectionRole ?? "", a.ElectionCount, a.ElectionSource,
                        electedMemberIds = eids,
                        // Aligned 1:1 with electedMemberIds (may contain "" if a member was deleted).
                        electedNames = eids.Select(id => nameById.TryGetValue(id, out var nm) ? nm : "").ToArray()
                    };
                }),
                attendees = attendees.Select(AttendeeDto),
                actions = actions.Select(ActionDto),
                links = links.Select(l => new { l.Id, l.AgendaItemId, l.Kind, l.RefId, l.Url, l.Label }),
                quorum = new { present, total, required, isMet }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateMeeting(int ownerType, int ownerId, string meetingType,
            string? title, string meetingDate, string? location)
        {
            try
            {
                if (!await CanAccessBoardWork(ownerType, ownerId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                var date = ParseDateTime(meetingDate);
                if (date == null)
                    return Json(new { success = false, message = "Ogiltigt datum" });

                var meId = await GetCurrentMemberId();
                var meeting = _meetingService.CreateMeeting(ownerType, ownerId, meetingType,
                    title ?? "", date.Value, location, meId);

                return Json(new { success = true, message = "Möte skapat", data = new { meeting.Id } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating board meeting");
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMeeting(int meetingId, string meetingType, string title,
            string meetingDate, string? location, int? quorumOverride, string? notes)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var date = ParseDateTime(meetingDate);
            if (date == null)
                return Json(new { success = false, message = "Ogiltigt datum" });

            var ok = _meetingService.UpdateMeeting(meetingId, meetingType, title, date.Value, location, quorumOverride, notes);
            return Json(new { success = ok, message = ok ? "Möte uppdaterat" : "Kunde inte uppdatera" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMeetingStatus(int meetingId, string status)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.SetStatus(meetingId, status);
            return Json(new { success = ok });
        }

        // ---- Digital justering (Phase 2) -----------------------------------

        /// <summary>Send the protocol for justering — locks edits, the signers then approve it.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendForJustering(int meetingId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (_meetingService.GetSigners(meetingId).Count == 0)
                return Json(new { success = false, message = "Inga justerare valda. Välj ordförande, sekreterare och justerare i dagordningen först." });
            var ok = _meetingService.SendForJustering(meetingId);
            return Json(new { success = ok, message = ok ? "Skickat för justering" : "Kunde inte skicka" });
        }

        /// <summary>The current member (a required signer) approves the protocol in-app.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveProtokoll(int meetingId)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var meId = await GetCurrentMemberId();
            var r = _meetingService.ApproveByMember(meetingId, meId, "web");
            return Json(new { success = r.Ok, locked = r.Locked, approved = r.Approved, total = r.Total, message = r.Ok ? "" : r.Message });
        }

        /// <summary>Approve via the QR/email token (identifies the meeting; the signer is the logged-in member).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveProtokollByToken(string t)
        {
            var meetingId = UnprotectMeetingToken(t);
            if (meetingId == null) return Json(new { success = false, message = "Ogiltig länk" });
            var meId = await GetCurrentMemberId();
            if (meId <= 0) return Json(new { success = false, message = "Inte inloggad" });
            var r = _meetingService.ApproveByMember(meetingId.Value, meId, "qr");
            return Json(new { success = r.Ok, locked = r.Locked, approved = r.Approved, total = r.Total, message = r.Ok ? "" : r.Message });
        }

        /// <summary>Justering summary for the chromeless QR sign-off page (read by token; login required).</summary>
        [HttpGet]
        public async Task<IActionResult> GetJusteringByToken(string t)
        {
            var meetingId = UnprotectMeetingToken(t);
            if (meetingId == null) return Json(new { success = false, message = "Ogiltig länk" });
            var meeting = _meetingService.GetMeeting(meetingId.Value);
            if (meeting == null || !meeting.IsActive) return Json(new { success = false, message = "Mötet hittades inte" });

            var meId = await GetCurrentMemberId();
            if (meId <= 0) return Json(new { success = false, needsLogin = true, message = "Logga in för att justera" });

            var signers = _meetingService.GetSigners(meeting.Id);
            var mine = signers.FirstOrDefault(s => s.MemberId == meId);
            return Json(new
            {
                success = true,
                title = meeting.Title,
                date = meeting.MeetingDate.ToString("yyyy-MM-dd HH:mm"),
                org = ResolveOrgName(meeting.OwnerType, meeting.OwnerId),
                status = meeting.Status,
                locked = meeting.Status == "Justerat",
                isSigner = mine != null,
                alreadyApproved = mine?.ApprovedDate != null,
                canApprove = meeting.Status == "VantarJustering" && mine != null && mine.ApprovedDate == null,
                approvedCount = signers.Count(s => s.ApprovedDate != null),
                totalSigners = signers.Count,
                protokollUrl = $"/styrelse/protokoll/{meeting.Id}",
                signers = signers.Select(s => new
                {
                    s.MemberName,
                    role = s.IsChairman ? "Ordförande" : s.IsSecretary ? "Sekreterare" : "Justerare",
                    approved = s.ApprovedDate != null
                })
            });
        }

        /// <summary>Reopen a sent/justerat protocol for editing (clears approvals).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReopenJustering(int meetingId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.ReopenForEditing(meetingId);
            return Json(new { success = ok, message = ok ? "Protokollet öppnat för redigering" : "Kunde inte öppna" });
        }

        /// <summary>QR PNG that opens the chromeless sign-off page for this protocol (on-site justering).</summary>
        [HttpGet]
        public async Task<IActionResult> GetJusteringQr(int meetingId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Content("Åtkomst nekad");
            var url = $"{Request.Scheme}://{Request.Host}/styrelse/justera?t={Uri.EscapeDataString(ProtectMeetingToken(meetingId))}";
            var png = QrPng(url);
            return png == null ? Content("QR-fel") : File(png, "image/png");
        }

        /// <summary>Email a justering link to the signers who haven't approved yet.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendJusteringEmails(int meetingId)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (meeting.Status != "VantarJustering")
                return Json(new { success = false, message = "Protokollet är inte skickat för justering" });

            var link = $"{Request.Scheme}://{Request.Host}/styrelse/justera?t={Uri.EscapeDataString(ProtectMeetingToken(meetingId))}";
            var orgName = ResolveOrgName(meeting.OwnerType, meeting.OwnerId);
            int sent = 0;
            foreach (var s in _meetingService.GetSigners(meetingId).Where(s => s.ApprovedDate == null))
            {
                var member = _memberService.GetById(s.MemberId);
                var email = member?.Email;
                if (string.IsNullOrWhiteSpace(email)) continue;
                var html = $@"<p>Hej {System.Net.WebUtility.HtmlEncode(s.MemberName ?? "")},</p>
<p>Protokollet för <strong>{System.Net.WebUtility.HtmlEncode(meeting.Title)}</strong> ({meeting.MeetingDate:yyyy-MM-dd}) i {System.Net.WebUtility.HtmlEncode(orgName)} är klart att justera.</p>
<p>Logga in och granska protokollet, klicka sedan på <strong>Godkänn protokollet</strong>:</p>
<p><a href=""{link}"">Öppna och justera protokollet</a></p>
<p>Hälsningar,<br>{System.Net.WebUtility.HtmlEncode(orgName)}</p>";
                if (await _emailService.SendHtmlEmailAsync(email, $"Justera protokoll – {meeting.Title}", html, orgName)) sent++;
            }
            return Json(new { success = true, sent });
        }

        private string ResolveOrgName(int ownerType, int ownerId)
        {
            if (ownerType == DocumentOwnerType.Club)
                return _clubService.GetClubNameById(ownerId) ?? "din förening";
            var node = UmbracoContext.Content?.GetById(ownerId);
            return node?.Value<string>("regionName") ?? node?.Name ?? "din krets";
        }

        /// <summary>Render a QR code PNG for a URL; null on failure. (Same approach as Faltskytte/Marken.)</summary>
        private byte[]? QrPng(string url)
        {
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);
                using var img = qr.GetGraphic(10, SixLabors.ImageSharp.Color.Black, SixLabors.ImageSharp.Color.White, true);
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Board justering QR generation failed");
                return null;
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMeeting(int meetingId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.DeleteMeeting(meetingId);
            return Json(new { success = ok, message = ok ? "Möte borttaget" : "Kunde inte ta bort" });
        }

        // ---- Agenda ---------------------------------------------------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAgendaItem(int meetingId, string heading,
            string? itemType, string? electionRole, int? electionCount, string? electionSource)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(heading))
                return Json(new { success = false, message = "Rubrik krävs" });
            var item = _meetingService.AddAgendaItem(meetingId, heading,
                itemType ?? "text", electionRole, electionCount ?? 1, electionSource ?? "attendees");
            return Json(new { success = true, data = new { item.Id } });
        }

        /// <summary>Owner members for a "members"-source election picker (e.g. årsmöte justerare).</summary>
        [HttpGet]
        public async Task<IActionResult> GetOwnerMembers(int meetingId, string? q)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var members = _meetingService.SearchOwnerMembers(meeting.OwnerType, meeting.OwnerId, q);
            return Json(new { success = true, data = members.Select(m => new { id = m.Id, name = m.Name }) });
        }

        /// <summary>Record the persons chosen in an election agenda item (e.g. Val av justerare).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAgendaElection(int agendaItemId, string? memberIds)
        {
            var mid = _meetingService.GetAgendaItemMeetingId(agendaItemId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ids = (memberIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0);
            var ok = _meetingService.SaveAgendaElection(agendaItemId, ids);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAgendaItem(int agendaItemId, string heading, string? discussion, string? decision)
        {
            var mid = _meetingService.GetAgendaItemMeetingId(agendaItemId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.UpdateAgendaItem(agendaItemId, heading, discussion, decision);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveAgendaItem(int agendaItemId)
        {
            var mid = _meetingService.GetAgendaItemMeetingId(agendaItemId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.RemoveAgendaItem(agendaItemId);
            return Json(new { success = ok });
        }

        // ---- Agenda attachments (links) ------------------------------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAgendaLink(int agendaItemId, string kind, int? refId, string? url, string label)
        {
            var mid = _meetingService.GetAgendaItemMeetingId(agendaItemId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(label))
                return Json(new { success = false, message = "Text krävs" });
            var link = _meetingService.AddAgendaLink(agendaItemId, kind, refId, url, label);
            return Json(new { success = true, data = new { link.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveAgendaLink(int linkId)
        {
            var mid = _meetingService.GetLinkMeetingId(linkId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.RemoveAgendaLink(linkId);
            return Json(new { success = ok });
        }

        // ---- Attendees ------------------------------------------------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAttendee(int meetingId, int memberId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.AddAttendee(meetingId, memberId);
            return Json(new { success = ok, message = ok ? "" : "Personen är redan tillagd" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncAttendees(int meetingId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var added = _meetingService.SyncBoardAttendees(meetingId);
            return Json(new { success = true, added });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAttendance(int attendeeId, string attendanceStatus)
        {
            var mid = _meetingService.GetAttendeeMeetingId(attendeeId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.SetAttendance(attendeeId, attendanceStatus);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAttendee(int attendeeId, string attendanceStatus,
            bool isChairman, bool isSecretary, bool isAdjuster)
        {
            var mid = _meetingService.GetAttendeeMeetingId(attendeeId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.UpdateAttendee(attendeeId, attendanceStatus, isChairman, isSecretary, isAdjuster);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveAttendee(int attendeeId)
        {
            var mid = _meetingService.GetAttendeeMeetingId(attendeeId);
            if (mid == null || !await CanAccessMeeting(mid.Value))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.RemoveAttendee(attendeeId);
            return Json(new { success = ok });
        }

        // ---- Actions (åtgärder) --------------------------------------------

        [HttpGet]
        public async Task<IActionResult> GetOpenActions(int ownerType, int ownerId)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var actions = _meetingService.GetOpenActions(ownerType, ownerId);
            return Json(new { success = true, data = actions.Select(ActionDto) });
        }

        /// <summary>The current member's own open actions across all clubs/regions. Login only.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyActions()
        {
            var meId = await GetCurrentMemberId();
            if (meId <= 0) return Json(new { success = false, message = "Inte inloggad" });
            var actions = _meetingService.GetMyActions(meId);
            return Json(new { success = true, data = actions.Select(ActionDto) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAction(int ownerType, int ownerId, int? meetingId, int? agendaItemId,
            string description, int? assignedToMemberId, string? dueDate)
        {
            if (!await CanAccessBoardWork(ownerType, ownerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(description))
                return Json(new { success = false, message = "Beskrivning krävs" });

            var meId = await GetCurrentMemberId();
            var action = _meetingService.AddAction(ownerType, ownerId, meetingId, agendaItemId,
                description, assignedToMemberId, ParseDateTime(dueDate), meId);
            return Json(new { success = true, data = new { action.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAction(int actionId, string description, int? assignedToMemberId, string? dueDate)
        {
            var action = _meetingService.GetAction(actionId);
            if (action == null || !await CanAccessBoardWork(action.OwnerType, action.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.UpdateAction(actionId, description, assignedToMemberId, ParseDateTime(dueDate));
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActionDone(int actionId, bool done)
        {
            var action = _meetingService.GetAction(actionId);
            if (action == null || !await CanAccessBoardWork(action.OwnerType, action.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.SetActionDone(actionId, done);
            return Json(new { success = ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveAction(int actionId)
        {
            var action = _meetingService.GetAction(actionId);
            if (action == null || !await CanAccessBoardWork(action.OwnerType, action.OwnerId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.RemoveAction(actionId);
            return Json(new { success = ok });
        }

        // ---- DTOs -----------------------------------------------------------

        private static object MeetingSummaryDto(BoardMeeting m) => new
        {
            m.Id,
            m.Title,
            m.MeetingType,
            typeLabel = m.TypeLabel,
            m.Status,
            m.Location,
            meetingDate = m.MeetingDate.ToString("yyyy-MM-dd HH:mm"),
            isPast = m.MeetingDate < DateTime.Now
        };

        private static object MeetingDetailDto(BoardMeeting m) => new
        {
            m.Id,
            m.OwnerType,
            m.OwnerId,
            m.Title,
            m.MeetingType,
            typeLabel = m.TypeLabel,
            m.Status,
            m.Location,
            m.Notes,
            m.QuorumOverride,
            meetingDate = m.MeetingDate.ToString("yyyy-MM-dd HH:mm"),
            m.AdjusterMemberId,
            justifiedDate = m.JustifiedDate?.ToString("yyyy-MM-dd"),
            kallelseSentDate = m.KallelseSentDate?.ToString("yyyy-MM-dd"),
            m.KallelseRecipientCount
        };

        private static object AttendeeDto(BoardMeetingAttendee a) => new
        {
            a.Id,
            a.MemberId,
            a.MemberName,
            a.RoleTitle,
            a.AttendanceStatus,
            a.IsChairman,
            a.IsSecretary,
            a.IsAdjuster
        };

        private static object ActionDto(BoardMeetingAction a) => new
        {
            a.Id,
            a.Description,
            a.AssignedToMemberId,
            a.AssignedToName,
            a.Status,
            a.IsDone,
            a.IsOverdue,
            a.MeetingId,
            a.AgendaItemId,
            dueDate = a.DueDate?.ToString("yyyy-MM-dd")
        };

        // ---- Auth + helpers -------------------------------------------------

        private async Task<bool> CanAccessMeeting(int meetingId)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null) return false;
            return await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId);
        }

        /// <summary>
        /// Site admin OR club/regional admin for the owner OR an active board member of the owner.
        /// </summary>
        private async Task<bool> CanAccessBoardWork(int ownerType, int ownerId)
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;

            if (ownerType == DocumentOwnerType.Club)
            {
                if (await _authorizationService.IsClubAdminForClub(ownerId)) return true;
            }
            else if (ownerType == DocumentOwnerType.Region)
            {
                var content = UmbracoContext.Content?.GetById(ownerId);
                var regionCode = content?.Value<string>("regionCode") ?? "";
                if (!string.IsNullOrEmpty(regionCode) &&
                    await _authorizationService.IsRegionalAdminForRegion(regionCode))
                    return true;
            }

            // Board members get the same access (no per-post permissions).
            var meId = await GetCurrentMemberId();
            return meId > 0 && _boardRoleService.IsBoardMemberOf(ownerType, ownerId, meId);
        }

        private async Task<int> GetCurrentMemberId()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null) return 0;
            var data = _memberService.GetByEmail(currentMember.Email);
            return data?.Id ?? 0;
        }

        private static DateTime? ParseDateTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
            return DateTime.TryParseExact(value, formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d : (DateTime?)null;
        }
    }
}
