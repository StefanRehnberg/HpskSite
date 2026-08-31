using Microsoft.AspNetCore.DataProtection;
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
        private readonly ITimeLimitedDataProtector _attendanceProtector;

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
            ILogger<ClubEventController> logger,
            IDataProtectionProvider dataProtectionProvider)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _participation = participation;
            _memberClubs = memberClubs;
            _logger = logger;
            _attendanceProtector = dataProtectionProvider
                .CreateProtector("ClubEvent.AttendanceQr.v1").ToTimeLimitedDataProtector();
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
                    selfRegistered = r.SelfRegistered,
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


        // ── Närvaro via QR-kod ────────────────────────────────────────

        /// <summary>
        /// How long a printed attendance QR stays valid. NOT a fixed short window like the Märken
        /// verify token: this one is printed and taped to a wall before the event, so a 30-minute
        /// lifetime would make it useless. It is instead tied to the EVENT — valid until the end of
        /// the event day plus a margin — so a photographed code cannot be redeemed next month.
        /// </summary>
        private static TimeSpan AttendanceTokenLifetime(ClubEventContext ctx)
        {
            var last = ctx.EventEndDate ?? ctx.EventDate;
            if (last == null) return TimeSpan.FromDays(2);
            var until = last.Value.Date.AddDays(1).AddHours(12);
            var span = until - DateTime.Now;
            return span < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1)
                 : span > TimeSpan.FromDays(120) ? TimeSpan.FromDays(120)
                 : span;
        }

        /// <summary>
        /// ⚠️ Second gate, on purpose. The token's lifetime says "not next month"; this says "not the
        /// day before either". Self-registration is only accepted while the event is actually
        /// happening — from 12 h before it starts until 12 h after it ends. An undated event has no
        /// window to check against and is therefore accepted whenever the token is still alive.
        /// </summary>
        private static bool IsCheckInWindowOpen(ClubEventContext ctx, DateTime? now = null)
        {
            if (ctx.EventDate == null) return true;
            var t = now ?? DateTime.Now;
            var from = ctx.EventDate.Value.AddHours(-12);
            var to = (ctx.EventEndDate ?? ctx.EventDate).Value.Date.AddDays(1).AddHours(12);
            return t >= from && t <= to;
        }

        private byte[]? QrPng(string url)
        {
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);
                using var img = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: SixLabors.ImageSharp.Color.Black,
                    lightColor: SixLabors.ImageSharp.Color.White,
                    drawQuietZones: true);
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte generera QR-kod för närvaro");
                return null;
            }
        }

        private string BuildCheckInUrl(int eventId, ClubEventContext ctx)
        {
            var token = _attendanceProtector.Protect(eventId.ToString(), AttendanceTokenLifetime(ctx));
            return $"{Request.Scheme}://{Request.Host}/evenemang/narvaro?t={Uri.EscapeDataString(token)}";
        }

        /// <summary>
        /// Printable poster with the attendance QR. Staff-gated — the code IS the check-in, so it is
        /// the arrangör who decides it exists.
        /// GET /umbraco/surface/ClubEvent/PrintAttendanceQr?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PrintAttendanceQr(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Content("Evenemanget hittades inte.");

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me)) return Content("Åtkomst nekad.");

            var url = BuildCheckInUrl(eventId, ctx);
            var png = QrPng(url);
            if (png == null) return Content("Kunde inte generera QR-koden.");

            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var img = "data:image/png;base64," + Convert.ToBase64String(png);
            var when = ctx.EventDate?.ToString("dddd d MMMM yyyy, HH:mm",
                new System.Globalization.CultureInfo("sv-SE")) ?? "";

            var sb = new System.Text.StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='sv'><head><meta charset='utf-8'>");
            sb.Append("<title>Närvaro – ").Append(Enc(ctx.EventName)).Append("</title>");
            sb.Append("<style>body{font-family:Arial,Helvetica,sans-serif;margin:2rem;color:#111;text-align:center}");
            sb.Append("h1{font-size:2rem;margin:.2rem 0}h2{font-size:1.2rem;font-weight:normal;color:#444;margin:.2rem 0 1.2rem}");
            // Skarpa kanter på modulerna när bilden skalas upp — en mjukskalad QR blir svårläst.
            sb.Append("img{width:11cm;height:11cm;image-rendering:pixelated;border:1px solid #ddd}");
            sb.Append(".steps{max-width:16cm;margin:1.2rem auto 0;text-align:left;font-size:1.05rem;line-height:1.6}");
            sb.Append(".muted{color:#666;font-size:.85rem;margin-top:1.5rem}");
            sb.Append("@media print{button{display:none}}</style></head><body>");
            sb.Append("<button onclick='window.print()'>Skriv ut</button>");
            sb.Append("<h1>Registrera din närvaro</h1>");
            sb.Append("<h2>").Append(Enc(ctx.EventName));
            if (!string.IsNullOrEmpty(when)) sb.Append("<br>").Append(Enc(when));
            sb.Append("</h2>");
            // data-checkin-url gör vad koden pekar på läsbart utan att skräpa ner affischen: det är
            // enda sättet att felsöka en QR som inte fungerar, och det är vad verifieringssviten
            // följer för att kunna prova hela skanningsvägen. Ingen hemlighet läcker — den som har
            // affischen framför sig har redan koden.
            sb.Append("<img alt='QR-kod för närvaroregistrering' data-checkin-url='")
              .Append(Enc(url)).Append("' src='").Append(img).Append("'>");
            sb.Append("<div class='steps'><ol>");
            sb.Append("<li>Skanna koden med telefonens kamera.</li>");
            sb.Append("<li>Logga in på pistol.nu om du inte redan är det.</li>");
            sb.Append("<li>Tryck <strong>Registrera min närvaro</strong>.</li>");
            sb.Append("</ol></div>");
            if (ctx.IsMandatory)
            {
                sb.Append("<p class='muted'><strong>Obligatoriskt evenemang.</strong> Närvaron är underlag för klubbens beslut om Föreningsintyg.</p>");
            }
            sb.Append("<p class='muted'>Koden gäller bara i anslutning till evenemanget. Går det inte — säg till en funktionär, som kan pricka av dig för hand.</p>");
            sb.Append("</body></html>");

            return Content(sb.ToString(), "text/html; charset=utf-8");
        }

        /// <summary>
        /// What the scanned page needs before the member presses the button: which event this is, and
        /// whether they may register at all. Deliberately does NOT register anything — a QR opened by
        /// accident in a camera preview must not tick someone off.
        /// GET /umbraco/surface/ClubEvent/GetCheckInState?t=...
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCheckInState(string? t)
        {
            var ctx = ResolveCheckInToken(t, out var tokenError);
            if (ctx == null) return Json(new { success = false, message = tokenError });

            int me = await CurrentMemberIdAsync();
            var member = me > 0 ? _memberService.GetById(me) : null;
            var existing = me > 0 ? await _participation.GetParticipantAsync(ctx.EventId, me) : null;

            return Json(new
            {
                success = true,
                loggedIn = me > 0,
                eligible = _participation.IsEligible(ctx, member),
                windowOpen = IsCheckInWindowOpen(ctx),
                alreadyPresent = existing?.AttendanceStatus == ClubEvents.AttendancePresent,
                signedUp = existing?.SignedUpAt != null && existing.CancelledAt == null,
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    venue = ctx.Venue,
                    isMandatory = ctx.IsMandatory,
                    ownerName = ctx.OwnerName
                }
            });
        }

        /// <summary>
        /// The member registers their own attendance by scanning the poster.
        /// POST /umbraco/surface/ClubEvent/SelfCheckIn
        ///
        /// ⚠️ A self-scan is NOT the same evidence as a functionary's roll-call — the poster can be
        /// photographed and passed on. The row therefore records the member as their own recorder,
        /// and the roster labels it "självregistrerad" so the board can tell the two apart when the
        /// attendance is used for a Föreningsintyg.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SelfCheckIn([FromBody] CheckInRequest request)
        {
            var ctx = ResolveCheckInToken(request?.Token, out var tokenError);
            if (ctx == null) return Json(new { success = false, message = tokenError });

            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad för att registrera närvaro." });

            if (!IsCheckInWindowOpen(ctx))
                return Json(new { success = false, message = "Koden gäller bara i anslutning till evenemanget. Be en funktionär pricka av dig." });

            var member = _memberService.GetById(me);
            if (!_participation.IsEligible(ctx, member))
                return Json(new
                {
                    success = false,
                    message = ctx.IsRegionOwned
                        ? "Närvaroregistrering är öppen för medlemmar i kretsens klubbar."
                        : $"Närvaroregistrering är öppen för medlemmar i {ctx.OwnerName}."
                });

            var (ok, msg) = await _participation.SetAttendanceAsync(
                ctx.EventId, me, ClubEvents.AttendancePresent, null, me);

            return Json(new { success = ok, message = ok ? "Din närvaro är registrerad." : msg });
        }

        /// <summary>Decodes the poster token. A dead token is the common case (the event has passed),
        /// so it gets its own message rather than a bare "ogiltig länk".</summary>
        private ClubEventContext? ResolveCheckInToken(string? token, out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(token)) { message = "Länken saknar kod."; return null; }

            string payload;
            try
            {
                payload = _attendanceProtector.Unprotect(token);
            }
            catch
            {
                message = "Koden är inte längre giltig. Be en funktionär pricka av dig.";
                return null;
            }

            if (!int.TryParse(payload, out var eventId)) { message = "Ogiltig kod."; return null; }

            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) { message = "Evenemanget hittades inte."; return null; }
            return ctx;
        }

        public class CheckInRequest
        {
            public string? Token { get; set; }
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
