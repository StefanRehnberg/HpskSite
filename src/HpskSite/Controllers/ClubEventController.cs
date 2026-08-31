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
    /// Sign-up and attendance for club and krets events (<c>clubSimpleEvent</c>, which both scopes
    /// share). Kept out of <see cref="ClubController"/>, which owns the event CONTENT — creating and
    /// editing an event is the arrangör's act, signing up and being ticked off is everyone else's.
    ///
    /// ⚠️ Two doctype properties are operator-added (<c>isMandatory</c>, <c>eventFee</c>). Reading a
    /// missing property is harmless (default), but <c>SetValue</c> on one is a SILENT no-op — so the
    /// write endpoints report the missing property instead of reporting a save that never happened.
    /// </summary>
    public class ClubEventController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ClubEventParticipationService _participation;
        private readonly MemberClubService _memberClubs;
        private readonly ILogger<ClubEventController> _logger;

        public ClubEventController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            ClubEventParticipationService participation,
            MemberClubService memberClubs,
            ILogger<ClubEventController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _participation = participation;
            _memberClubs = memberClubs;
            _logger = logger;
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var m = await _memberManager.GetCurrentMemberAsync();
            return m == null || !int.TryParse(m.Id, out var id) ? 0 : id;
        }

        // ── Member-facing ─────────────────────────────────────────────

        /// <summary>
        /// Everything the event page needs to render the sign-up block: the event's own settings,
        /// how many are signed up, and where the current member stands.
        /// GET /umbraco/surface/ClubEvent/GetSignupState?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSignupState(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            var member = me > 0 ? _memberService.GetById(me) : null;
            var roster = await _participation.BuildRosterAsync(ctx);
            bool canManage = me > 0 && await _participation.CanManageAsync(ctx, me);
            var mine = roster.Rows.FirstOrDefault(r => r.MemberId == me);

            // The roster is visible to the owning club's own members (it is their club's event) and
            // to functionaries. Everyone else sees the COUNT — that is what tells a visitor whether
            // there is room, without publishing who is going.
            bool eligible = _participation.IsEligible(ctx, member);
            bool showRoster = canManage || eligible;

            return Json(new
            {
                success = true,
                canManage,
                loggedIn = me > 0,
                eligible,
                signupOpen = ClubEventParticipationService.IsSignupOpen(ctx),
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    registrationRequired = ctx.RegistrationRequired,
                    registrationUrl = ctx.RegistrationUrl,
                    maxParticipants = ctx.MaxParticipants,
                    isMandatory = ctx.IsMandatory,
                    fee = ctx.Fee,
                    ownerName = ctx.OwnerName,
                    isRegion = ctx.IsRegionOwned
                },
                counts = new
                {
                    signedUp = roster.SignedUp,
                    seated = roster.Seated,
                    reserves = roster.Reserves,
                    seatsLeft = roster.SeatsLeft,
                    present = roster.Present
                },
                me = mine == null ? null : new
                {
                    signedUp = mine.SignedUpAt != null && !mine.Cancelled,
                    cancelled = mine.Cancelled,
                    isReserve = mine.IsReserve,
                    note = mine.Note,
                    attendanceStatus = mine.AttendanceStatus
                },
                roster = showRoster
                    ? roster.Rows.Where(r => !r.Cancelled && !r.IsWalkIn).Select(r => new
                    {
                        memberId = r.MemberId,
                        name = r.Name,
                        isReserve = r.IsReserve,
                        signedUpAt = r.SignedUpAt?.ToString("yyyy-MM-dd HH:mm")
                    })
                    : null
            });
        }

        /// <summary>POST /umbraco/surface/ClubEvent/SignUp</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad för att anmäla dig." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (!ctx.RegistrationRequired) return Json(new { success = false, message = "Det här evenemanget har ingen anmälan." });
            if (!ClubEventParticipationService.IsSignupOpen(ctx)) return Json(new { success = false, message = "Anmälan är stängd." });

            var member = _memberService.GetById(me);
            if (!_participation.IsEligible(ctx, member))
                return Json(new
                {
                    success = false,
                    message = ctx.IsRegionOwned
                        ? "Anmälan är öppen för medlemmar i kretsens klubbar."
                        : $"Anmälan är öppen för medlemmar i {ctx.OwnerName}."
                });

            var (ok, msg, isReserve) = await _participation.SignUpAsync(ctx, me, request?.Note, me);
            if (!ok) return Json(new { success = false, message = msg });

            return Json(new
            {
                success = true,
                isReserve,
                message = isReserve
                    ? "Du står som reserv — vi hör av oss om en plats blir ledig."
                    : "Du är anmäld."
            });
        }

        /// <summary>POST /umbraco/surface/ClubEvent/Cancel</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            // A member may withdraw themselves; a functionary may withdraw anyone on their event.
            int target = request?.MemberId > 0 ? request.MemberId : me;
            if (target != me && !await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (ok, msg) = await _participation.CancelAsync(ctx.EventId, target, me);
            return Json(new { success = ok, message = msg ?? "Anmälan avbokad." });
        }

        // ── Functionary: roll-call ────────────────────────────────────

        /// <summary>
        /// The full roster for the roll-call screen: signed up, reserves, walk-ins and cancellations,
        /// each with its attendance state.
        /// GET /umbraco/surface/ClubEvent/GetRoster?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRoster(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var roster = await _participation.BuildRosterAsync(ctx);
            return Json(new
            {
                success = true,
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    isMandatory = ctx.IsMandatory,
                    maxParticipants = ctx.MaxParticipants,
                    registrationRequired = ctx.RegistrationRequired,
                    ownerName = ctx.OwnerName
                },
                counts = new
                {
                    signedUp = roster.SignedUp,
                    seated = roster.Seated,
                    reserves = roster.Reserves,
                    cancelled = roster.Cancelled,
                    present = roster.Present,
                    notRecorded = roster.NotRecorded,
                    seatsLeft = roster.SeatsLeft
                },
                rows = roster.Rows.Select(r => new
                {
                    memberId = r.MemberId,
                    name = r.Name,
                    signedUpAt = r.SignedUpAt?.ToString("yyyy-MM-dd HH:mm"),
                    cancelled = r.Cancelled,
                    isReserve = r.IsReserve,
                    isWalkIn = r.IsWalkIn,
                    note = r.Note,
                    attendanceStatus = r.AttendanceStatus,
                    attendanceLabel = ClubEvents.AttendanceDisplay(r.AttendanceStatus),
                    attendanceNote = r.AttendanceNote,
                    fee = r.FeeAmount
                })
            });
        }

        /// <summary>
        /// Tick someone off — or clear the tick. <c>status</c> null puts the row back to
        /// "ej registrerad", which is a real third state: a mandatory event whose roll-call was
        /// never taken must not read as everyone having stayed away.
        /// POST /umbraco/surface/ClubEvent/SetAttendance
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAttendance([FromBody] AttendanceRequest request)
        {
            if (request == null || request.EventId <= 0 || request.MemberId <= 0)
                return Json(new { success = false, message = "Ogiltig begäran — evenemang och medlem måste anges." });

            var ctx = _participation.GetEventContext(request.EventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();
            var (ok, msg) = await _participation.SetAttendanceAsync(
                request.EventId, request.MemberId, status, request.Note, me);

            return Json(new { success = ok, message = msg, label = ClubEvents.AttendanceDisplay(status) });
        }

        /// <summary>
        /// Members who could be added at the door — the owning club's members (or, for a krets
        /// event, the krets's) minus those already on the list.
        /// GET /umbraco/surface/ClubEvent/SearchAddableMembers?eventId=1234&amp;q=and
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchAddableMembers(int eventId, string? q)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var already = (await _participation.GetParticipantsAsync(eventId)).Select(p => p.MemberId).ToHashSet();
            var term = (q ?? "").Trim();

            // Resolve the eligible clubs ONCE — the per-member overload would re-read the krets's
            // club list for every member in the register.
            var eligibleClubs = _participation.GetEligibleClubIds(ctx);

            var results = new List<object>();
            foreach (var member in _memberService.GetAll(0, int.MaxValue, out _))
            {
                if (already.Contains(member.Id)) continue;
                if (!_participation.IsEligible(eligibleClubs, member)) continue;
                if (term.Length > 0 && (member.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                results.Add(new { memberId = member.Id, name = member.Name ?? $"Medlem {member.Id}" });
                if (results.Count >= 50) break;
            }

            return Json(new { success = true, members = results });
        }

        // ── Request DTOs ──────────────────────────────────────────────
        public class SignUpRequest
        {
            public int EventId { get; set; }
            /// <summary>Only honoured for a functionary cancelling on someone's behalf.</summary>
            public int MemberId { get; set; }
            public string? Note { get; set; }
        }

        public class AttendanceRequest
        {
            public int EventId { get; set; }
            public int MemberId { get; set; }
            /// <summary>Present / Absent / Excused, or empty to clear.</summary>
            public string? Status { get; set; }
            public string? Note { get; set; }
        }
    }
}
