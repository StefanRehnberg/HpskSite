using Microsoft.AspNetCore.Mvc;
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
            ILogger<BoardMeetingController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _meetingService = meetingService;
            _boardRoleService = boardRoleService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
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
            var (present, total, required, isMet) = _meetingService.GetQuorum(meetingId);

            return Json(new
            {
                success = true,
                meeting = MeetingDetailDto(meeting),
                agenda = agenda.Select(a => new { a.Id, a.SortOrder, a.Heading, a.Discussion, a.Decision }),
                attendees = attendees.Select(AttendeeDto),
                actions = actions.Select(ActionDto),
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetJusterat(int meetingId, int adjusterMemberId)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var ok = _meetingService.SetJusterat(meetingId, adjusterMemberId);
            return Json(new { success = ok, message = ok ? "Protokoll justerat" : "Kunde inte justera" });
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
        public async Task<IActionResult> AddAgendaItem(int meetingId, string heading)
        {
            if (!await CanAccessMeeting(meetingId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (string.IsNullOrWhiteSpace(heading))
                return Json(new { success = false, message = "Rubrik krävs" });
            var item = _meetingService.AddAgendaItem(meetingId, heading);
            return Json(new { success = true, data = new { item.Id } });
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
            justifiedDate = m.JustifiedDate?.ToString("yyyy-MM-dd")
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
