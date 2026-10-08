using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Styrelsearbetets översikt, ärendekön och motionerna (2026-10-08).
    /// Behörigheten bor i <see cref="BoardWorkAccess"/>; reglerna i <see cref="BoardIssueRules"/> och
    /// <see cref="BoardMotionRules"/>; skrivningarna i <see cref="BoardWorkService"/>.
    /// </summary>
    public class BoardWorkController : SurfaceController
    {
        private readonly BoardWorkService _work;
        private readonly BoardWorkAccess _access;
        private readonly BoardWorkNotifier _notify;
        private readonly BoardMeetingService _meetings;
        private readonly BoardRoleService _roles;
        private readonly BoardGovernanceService _gov;
        private readonly MemberClubService _memberClubs;
        private readonly IMemberService _memberService;
        private readonly DocumentService _documents;
        private readonly ILogger<BoardWorkController> _logger;

        public BoardWorkController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardWorkService work, BoardWorkAccess access, BoardWorkNotifier notify,
            BoardMeetingService meetings, BoardRoleService roles, BoardGovernanceService gov,
            MemberClubService memberClubs, IMemberService memberService, DocumentService documents,
            ILogger<BoardWorkController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _work = work;
            _access = access;
            _notify = notify;
            _meetings = meetings;
            _roles = roles;
            _gov = gov;
            _memberClubs = memberClubs;
            _memberService = memberService;
            _documents = documents;
            _logger = logger;
        }

        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";
        private static string D(DateTime d) => d.ToString("yyyy-MM-dd");
        private static string DT(DateTime d) => d.ToString("yyyy-MM-dd HH:mm");
        private IActionResult Denied() => Json(new { success = false, message = "Åtkomst nekad" });
        private static bool Flag(string? v) => v is "1" or "true" or "on";

        // =====================================================================
        //  Översikt — "väntar något på mig?"
        // =====================================================================

        [HttpGet]
        public async Task<IActionResult> GetOverview(int ownerType, int ownerId)
        {
            if (!await _access.CanAccessBoardWorkAsync(ownerType, ownerId)) return Denied();
            var me = await _access.CurrentMemberIdAsync();
            var canPlace = await _access.CanPlaceAsync(ownerType, ownerId);
            var isAdmin = await _access.IsAdminAsync(ownerType, ownerId);

            var next = _work.GetNextMeeting(ownerType, ownerId);
            object? nextDto = null;
            if (next != null)
            {
                var (tops, issues, motions) = _work.CountAgenda(next.Id);
                nextDto = new
                {
                    next.Id, next.Title, next.Location, typeLabel = next.TypeLabel, next.Status,
                    meetingDate = DT(next.MeetingDate),
                    agendaCount = tops, placedIssues = issues, placedMotions = motions,
                    kallelseSent = next.KallelseSentDate.HasValue,
                    kallelseSentDate = next.KallelseSentDate.HasValue ? D(next.KallelseSentDate.Value) : null,
                    waitingForIt = canPlace ? _work.GetWaitingIssuesForMeeting(next).Count : 0
                };
            }

            var allIssues = canPlace ? _work.GetIssues(ownerType, ownerId) : new List<BoardWorkService.IssueView>();
            var mine = _work.GetIssues(ownerType, ownerId, me);
            var motionsAll = _work.GetMotions(ownerType, ownerId, me);
            var annual = _work.GetUpcomingAnnualMeeting(ownerType, ownerId);
            var actions = _meetings.GetOpenActions(ownerType, ownerId);
            var expiring = isAdmin ? _roles.GetExpiringRoles(ownerType, ownerId, DateTime.Today.AddDays(90)) : new List<BoardRole>();
            var wheel = _gov.GetYearWheel(ownerType, ownerId, DateTime.Today.Year)
                .Where(w => !w.Done).OrderBy(w => w.TargetDate ?? DateTime.MaxValue).Take(4).ToList();

            return Json(new
            {
                success = true,
                canPlace,
                isAdmin,
                nextMeeting = nextDto,
                issuesToPlace = allIssues.Count(v => v.State == BoardIssueState.Waiting),
                justering = _work.GetMeetingsAwaitingMyApproval(ownerType, ownerId, me)
                    .Select(m => new { m.Id, m.Title, meetingDate = D(m.MeetingDate) }),
                motionsWithoutOpinion = motionsAll.Count(v => v.State == BoardMotionState.Received),
                annualMeeting = annual == null ? null : new
                {
                    annual.Id, annual.Title, meetingDate = D(annual.MeetingDate),
                    motionDeadline = annual.MotionDeadline.HasValue ? D(annual.MotionDeadline.Value) : null
                },
                myIssues = mine.Where(v => v.State != BoardIssueState.Withdrawn).Take(5).Select(IssueDto),
                expiring = expiring.Select(r => new { r.MemberName, title = r.DisplayTitle, termEndsDate = r.TermEndsDate.HasValue ? D(r.TermEndsDate.Value) : null }),
                actions = actions.Select(a => new
                {
                    a.Id, a.Description, a.AssignedToName, a.IsOverdue,
                    mine = a.AssignedToMemberId == me,
                    dueDate = a.DueDate.HasValue ? D(a.DueDate.Value) : null
                }),
                wheel = wheel.Select(w => new { w.Id, w.Title, w.IsOverdue, targetDate = w.TargetDate.HasValue ? D(w.TargetDate.Value) : null })
            });
        }

        // =====================================================================
        //  Ärendekön
        // =====================================================================

        private object IssueDto(BoardWorkService.IssueView v) => new
        {
            v.Issue.Id,
            v.Issue.Title,
            v.Issue.Body,
            v.Issue.Kind,
            kindLabel = BoardIssueKinds.Label(v.Issue.Kind),
            v.Issue.SourceKind,
            v.Issue.SourceRefId,
            submittedBy = v.SubmittedByName,
            submittedByMemberId = v.Issue.SubmittedByMemberId,
            submittedDate = D(v.Issue.SubmittedDate),
            v.Issue.WishMeetingId,
            wishMeeting = v.WishMeetingLabel,
            state = v.State.ToString(),
            stateLabel = BoardIssueRules.StateLabel(v.State),
            closedReason = v.Issue.ClosedReason,
            closedDate = v.Issue.ClosedDate.HasValue ? D(v.Issue.ClosedDate.Value) : null,
            placement = v.Placement == null ? null : new
            {
                v.Placement.MeetingId, v.Placement.MeetingTitle, v.Placement.Paragraph,
                meetingDate = D(v.Placement.MeetingDate), v.Placement.MeetingStatus,
                decision = v.State == BoardIssueState.Handled ? v.Placement.Decision : null
            }
        };

        [HttpGet]
        public async Task<IActionResult> GetIssues(int ownerType, int ownerId)
        {
            if (!await _access.CanAccessBoardWorkAsync(ownerType, ownerId)) return Denied();
            var me = await _access.CurrentMemberIdAsync();
            var canPlace = await _access.CanPlaceAsync(ownerType, ownerId);
            // ⚠️ En ledamot ser sina EGNA ärenden; den som placerar ser hela kön.
            var list = canPlace ? _work.GetIssues(ownerType, ownerId) : _work.GetIssues(ownerType, ownerId, me);
            return Json(new
            {
                success = true,
                canPlace,
                me,
                issues = list.Select(IssueDto),
                wishMeetings = _work.GetPlaceableMeetings(ownerType, ownerId)
                    .Select(x => new { x.Meeting.Id, x.Meeting.Title, meetingDate = DT(x.Meeting.MeetingDate) })
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateIssue(int ownerType, int ownerId, string? title, string? body,
            string? kind, int? wishMeetingId, string? sourceKind, int? sourceRefId)
        {
            if (!await _access.CanAccessBoardWorkAsync(ownerType, ownerId)) return Denied();
            if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Skriv en rubrik." });
            var me = await _access.CurrentMemberIdAsync();

            // En inkommen skrivelse läggs in av den som placerar; ledamöternas ärenden av vem som helst i styrelsen.
            var src = sourceKind == BoardIssueSources.Letter && await _access.CanPlaceAsync(ownerType, ownerId)
                ? BoardIssueSources.Letter : BoardIssueSources.Member;
            if (wishMeetingId > 0)
            {
                var wm = _meetings.GetMeeting(wishMeetingId.Value);
                if (wm == null || wm.OwnerType != ownerType || wm.OwnerId != ownerId) wishMeetingId = null;
            }

            var issue = _work.CreateIssue(ownerType, ownerId, title, body, kind ?? "", wishMeetingId, me, src, null);
            int notified = 0;
            if (src == BoardIssueSources.Member) notified = await _notify.IssueSubmittedAsync(issue, BaseUrl);
            return Json(new { success = true, id = issue.Id, notified });
        }

        private async Task<(BoardIssue? Issue, IActionResult? Error)> LoadIssueAsync(int issueId)
        {
            var i = _work.GetIssue(issueId);
            if (i == null) return (null, Json(new { success = false, message = "Ärendet hittades inte." }));
            if (!await _access.CanAccessBoardWorkAsync(i.OwnerType, i.OwnerId)) return (null, Denied());
            return (i, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateIssue(int issueId, string? title, string? body, string? kind, int? wishMeetingId)
        {
            var (i, err) = await LoadIssueAsync(issueId); if (err != null) return err;
            var me = await _access.CurrentMemberIdAsync();
            if (i!.SubmittedByMemberId != me && !await _access.CanPlaceAsync(i.OwnerType, i.OwnerId)) return Denied();
            if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Skriv en rubrik." });
            var r = _work.UpdateIssue(issueId, title, body, kind ?? "", wishMeetingId);
            return Json(new { success = r.Ok, message = r.Message });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawIssue(int issueId)
        {
            var (i, err) = await LoadIssueAsync(issueId); if (err != null) return err;
            var me = await _access.CurrentMemberIdAsync();
            if (i!.SubmittedByMemberId != me) return Json(new { success = false, message = "Bara den som anmälde ärendet kan återkalla det." });
            var r = _work.CloseIssue(issueId, BoardIssueClosed.Withdrawn, null, me);
            return Json(new { success = r.Ok, message = r.Message });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectIssue(int issueId, string? reason, string? notify)
        {
            var (i, err) = await LoadIssueAsync(issueId); if (err != null) return err;
            if (!await _access.CanPlaceAsync(i!.OwnerType, i.OwnerId)) return Denied();
            if (string.IsNullOrWhiteSpace(reason)) return Json(new { success = false, message = "Skriv ett skäl. Den som anmälde ärendet får läsa det." });
            var me = await _access.CurrentMemberIdAsync();
            var r = _work.CloseIssue(issueId, BoardIssueClosed.Rejected, reason, me);
            if (!r.Ok) return Json(new { success = false, message = r.Message });
            bool notified = Flag(notify ?? "1") && await _notify.IssueRejectedAsync(_work.GetIssue(issueId)!, me, BaseUrl);
            return Json(new { success = true, notified });
        }

        [HttpGet]
        public async Task<IActionResult> GetPlaceTargets(int ownerType, int ownerId, string? annualOnly)
        {
            if (!await _access.CanPlaceAsync(ownerType, ownerId)) return Denied();
            var list = _work.GetPlaceableMeetings(ownerType, ownerId, Flag(annualOnly));
            return Json(new
            {
                success = true,
                meetings = list.Select(x => new
                {
                    x.Meeting.Id, x.Meeting.Title, typeLabel = x.Meeting.TypeLabel,
                    meetingDate = DT(x.Meeting.MeetingDate), isAnnual = x.Meeting.IsAnnualMeeting,
                    items = x.Tops.Select(t => new { t.Item.Id, t.Item.Heading, label = t.Label, t.Item.ItemType })
                })
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PlaceIssue(int issueId, int meetingId, int? parentItemId, string? notify)
        {
            var (i, err) = await LoadIssueAsync(issueId); if (err != null) return err;
            if (!await _access.CanPlaceAsync(i!.OwnerType, i.OwnerId)) return Denied();
            if (i.ClosedStatus != null) return Json(new { success = false, message = "Ärendet är stängt." });
            var meeting = _meetings.GetMeeting(meetingId);
            if (meeting == null || meeting.OwnerType != i.OwnerType || meeting.OwnerId != i.OwnerId)
                return Json(new { success = false, message = "Mötet hör inte till samma styrelse." });

            var itemType = i.Kind == BoardIssueKinds.Information ? "note" : "text";
            var r = _work.PlaceOnMeeting("IssueId", issueId, i.Title, itemType, meetingId, parentItemId > 0 ? parentItemId : null);
            if (!r.Ok) return Json(new { success = false, message = r.Message });

            var placement = _work.GetIssuePlacement(issueId);
            var me = await _access.CurrentMemberIdAsync();
            bool notified = placement != null && Flag(notify ?? "1") && await _notify.IssuePlacedAsync(i, placement, me, BaseUrl);
            return Json(new { success = true, paragraph = placement?.Paragraph, meetingTitle = placement?.MeetingTitle, notified });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnplaceIssue(int issueId)
        {
            var (i, err) = await LoadIssueAsync(issueId); if (err != null) return err;
            if (!await _access.CanPlaceAsync(i!.OwnerType, i.OwnerId)) return Denied();
            var r = _work.Unplace("IssueId", issueId);
            return Json(new { success = r.Ok, message = r.Message });
        }

        // =====================================================================
        //  Motioner — styrelsens sida
        // =====================================================================

        private object MotionDto(BoardWorkService.MotionView v, bool includeOpinion) => new
        {
            v.Motion.Id,
            number = v.Motion.NumberLabel,
            v.Motion.OwnerType,
            v.Motion.OwnerId,
            v.Motion.Title,
            v.Motion.Background,
            proposals = v.Motion.ProposalList,
            v.Motion.MotionerKind,
            v.Motion.MotionerName,
            v.Motion.MotionerMemberId,
            v.Motion.MotionerClubId,
            v.Motion.SignedByName,
            v.Motion.SourceReference,
            submittedDate = D(v.Motion.SubmittedDate),
            v.IsLate,
            deadline = v.Deadline.HasValue ? D(v.Deadline.Value) : null,
            meeting = v.MeetingLabel,
            v.Motion.MeetingId,
            state = v.State.ToString(),
            stateLabel = BoardMotionRules.StateLabel(v.State),
            coSigners = v.CoSigners,
            up = v.Up,
            down = v.Down,
            upNames = v.UpNames,
            downNames = v.DownNames,
            mySupport = v.MySupport,
            // Styrelsens yttrande är en del av årsmöteshandlingarna och syns för medlemmarna när det
            // är skrivet; ett halvskrivet yttrande syns inte, men det finns inget "utkast"-läge —
            // det som sparas är yttrandet.
            boardOpinion = includeOpinion ? v.Motion.BoardOpinion : null,
            boardProposal = includeOpinion ? v.Motion.BoardProposal : null,
            boardProposalLabel = includeOpinion ? BoardMotionProposals.Label(v.Motion.BoardProposal) : null,
            decision = v.Decision,
            attachments = v.Attachments.Select(a => new
            {
                a.Id, a.FileName, size = a.FileSize,
                url = "/umbraco/surface/BoardWork/MotionAttachment?id=" + a.Id
            }),
            placement = v.Placement == null ? null : new
            {
                v.Placement.MeetingId, v.Placement.MeetingTitle, v.Placement.Paragraph,
                meetingDate = D(v.Placement.MeetingDate), v.Placement.MeetingStatus
            }
        };

        [HttpGet]
        public async Task<IActionResult> GetBoardMotions(int ownerType, int ownerId)
        {
            if (!await _access.CanAccessBoardWorkAsync(ownerType, ownerId)) return Denied();
            var me = await _access.CurrentMemberIdAsync();
            var canPlace = await _access.CanPlaceAsync(ownerType, ownerId);
            var incoming = _work.GetMotions(ownerType, ownerId, me);
            var annual = _work.GetUpcomingAnnualMeeting(ownerType, ownerId);

            object? toKrets = null;
            if (ownerType == DocumentOwnerType.Club)
            {
                var regionId = _access.RegionIdForClub(ownerId);
                var (regionName, _) = regionId > 0 ? _work.OwnerContact(DocumentOwnerType.Region, regionId) : ("", null);
                var kretsAnnual = regionId > 0 ? _work.GetUpcomingAnnualMeeting(DocumentOwnerType.Region, regionId) : null;
                toKrets = new
                {
                    regionId,
                    regionName,
                    annualMeeting = kretsAnnual == null ? null : new
                    {
                        kretsAnnual.Id, meetingDate = D(kretsAnnual.MeetingDate),
                        motionDeadline = kretsAnnual.MotionDeadline.HasValue ? D(kretsAnnual.MotionDeadline.Value) : null
                    },
                    motions = _work.GetMotions(null, null, me, motionerClubId: ownerId).Select(v => MotionDto(v, true))
                };
            }

            return Json(new
            {
                success = true,
                canPlace,
                isClub = ownerType == DocumentOwnerType.Club,
                annualMeeting = annual == null ? null : new
                {
                    annual.Id, annual.Title, meetingDate = DT(annual.MeetingDate),
                    motionDeadline = annual.MotionDeadline.HasValue ? D(annual.MotionDeadline.Value) : null
                },
                annualMeetings = _work.GetPlaceableMeetings(ownerType, ownerId, annualOnly: true)
                    .Select(x => new
                    {
                        x.Meeting.Id, x.Meeting.Title, meetingDate = DT(x.Meeting.MeetingDate),
                        motionDeadline = x.Meeting.MotionDeadline.HasValue ? D(x.Meeting.MotionDeadline.Value) : null
                    }),
                motions = incoming.Select(v => MotionDto(v, true)),
                toKrets,
                motionPageUrl = BoardWorkNotifier.MotionPageLink("", ownerType, ownerId)
            });
        }

        private async Task<(BoardMotion? Motion, IActionResult? Error)> LoadMotionForPlacerAsync(int motionId)
        {
            var m = _work.GetMotion(motionId);
            if (m == null) return (null, Json(new { success = false, message = "Motionen hittades inte." }));
            if (!await _access.CanPlaceAsync(m.OwnerType, m.OwnerId)) return (null, Denied());
            return (m, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMotionOpinion(int motionId, string? opinion, string? proposal)
        {
            var (m, err) = await LoadMotionForPlacerAsync(motionId); if (err != null) return err;
            var me = await _access.CurrentMemberIdAsync();
            var r = _work.SaveOpinion(m!.Id, opinion, proposal, me);
            return Json(new { success = r.Ok, message = r.Message });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PlaceMotion(int motionId, int meetingId, int? parentItemId)
        {
            var (m, err) = await LoadMotionForPlacerAsync(motionId); if (err != null) return err;
            if (m!.WithdrawnDate.HasValue) return Json(new { success = false, message = "Motionen är återkallad." });
            var meeting = _meetings.GetMeeting(meetingId);
            if (meeting == null || meeting.OwnerType != m.OwnerType || meeting.OwnerId != m.OwnerId)
                return Json(new { success = false, message = "Mötet hör inte till samma förening." });
            var r = _work.PlaceOnMeeting("MotionId", m.Id, $"{m.NumberLabel} {m.Title}", "text", meetingId, parentItemId > 0 ? parentItemId : null);
            var p = r.Ok ? _work.GetMotionPlacement(m.Id) : null;
            return Json(new { success = r.Ok, message = r.Message, paragraph = p?.Paragraph });
        }

        /// <summary>
        /// Lägger årets alla motioner (de som inte återkallats och inte redan ligger på ett möte) som
        /// underpunkter under årsmötets punkt om motioner. Hittas ingen sådan punkt säger svaret det —
        /// då väljer sekreteraren punkt för hand per motion.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PlaceAllMotions(int ownerType, int ownerId, int meetingId)
        {
            if (!await _access.CanPlaceAsync(ownerType, ownerId)) return Denied();
            var target = _work.GetPlaceableMeetings(ownerType, ownerId, annualOnly: true).FirstOrDefault(x => x.Meeting.Id == meetingId);
            if (target.Meeting == null) return Json(new { success = false, message = "Årsmötet går inte att lägga motioner på (finns inte eller är låst)." });
            var parent = target.Tops.FirstOrDefault(t => t.Item.Heading.Contains("motion", StringComparison.OrdinalIgnoreCase));
            if (parent.Item == null)
                return Json(new { success = false, message = "Årsmötets dagordning har ingen punkt om motioner. Lägg till punkten \"Behandling av motioner och propositioner\" först, eller placera motionerna en och en." });

            var me = await _access.CurrentMemberIdAsync();
            var toPlace = _work.GetMotions(ownerType, ownerId, me)
                .Where(v => v.State is BoardMotionState.Received or BoardMotionState.OpinionReady
                            && (v.Motion.MeetingId == null || v.Motion.MeetingId == meetingId))
                .OrderBy(v => v.Motion.Year).ThenBy(v => v.Motion.Number).ToList();
            int placed = 0;
            var failed = new List<string>();
            foreach (var v in toPlace)
            {
                var r = _work.PlaceOnMeeting("MotionId", v.Motion.Id, $"{v.Motion.NumberLabel} {v.Motion.Title}", "text", meetingId, parent.Item.Id);
                if (r.Ok) placed++; else failed.Add($"{v.Motion.NumberLabel}: {r.Message}");
            }
            var withoutOpinion = toPlace.Count(v => v.State == BoardMotionState.Received);
            return Json(new { success = true, placed, failed, withoutOpinion, paragraph = parent.Label });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnplaceMotion(int motionId)
        {
            var (m, err) = await LoadMotionForPlacerAsync(motionId); if (err != null) return err;
            var r = _work.Unplace("MotionId", m!.Id);
            return Json(new { success = r.Ok, message = r.Message });
        }

        /// <summary>
        /// Lägger motionen i ärendekön så att styrelsen kan bereda yttrandet på ett styrelsemöte.
        /// Samma kö som ledamöternas ärenden — då förbereds dagordningen på ETT ställe.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TakeMotionToBoard(int motionId)
        {
            var (m, err) = await LoadMotionForPlacerAsync(motionId); if (err != null) return err;
            var me = await _access.CurrentMemberIdAsync();
            var existing = _work.GetIssues(m!.OwnerType, m.OwnerId)
                .FirstOrDefault(v => v.Issue.SourceKind == BoardIssueSources.Motion && v.Issue.SourceRefId == m.Id
                                     && v.State is BoardIssueState.Waiting or BoardIssueState.Placed);
            if (existing != null) return Json(new { success = true, id = existing.Issue.Id, already = true });
            var issue = _work.CreateIssue(m.OwnerType, m.OwnerId, $"Yttrande över motion {m.NumberLabel}: {m.Title}",
                $"Motionär: {m.MotionerName}", BoardIssueKinds.Decision, null, me, BoardIssueSources.Motion, m.Id);
            return Json(new { success = true, id = issue.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMotionDeadline(int meetingId, string? deadline)
        {
            var meeting = _meetings.GetMeeting(meetingId);
            if (meeting == null || !meeting.IsActive) return Json(new { success = false, message = "Mötet hittades inte." });
            if (!await _access.CanPlaceAsync(meeting.OwnerType, meeting.OwnerId)) return Denied();
            if (!meeting.IsAnnualMeeting) return Json(new { success = false, message = "Sista dag för motioner gäller bara årsmöten." });
            DateTime? d = null;
            if (!string.IsNullOrWhiteSpace(deadline))
            {
                if (!DateTime.TryParseExact(deadline, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    return Json(new { success = false, message = "Ogiltigt datum." });
                if (parsed.Date > meeting.MeetingDate.Date)
                    return Json(new { success = false, message = "Sista dag kan inte ligga efter årsmötet." });
                d = parsed;
            }
            var ok = _work.SetMotionDeadline(meetingId, d);
            return Json(new { success = ok });
        }

        /// <summary>
        /// Motion som kommit in på papper eller via e-post: sekreteraren för in den så att den finns
        /// med i handlingarna. Motionären anges i klartext (och som medlem om hen är det).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddPaperMotion(int ownerType, int ownerId, string? motionerName, int? motionerMemberId,
            string? title, string? background, string? proposalsJson, string? submittedDate)
        {
            if (!await _access.CanPlaceAsync(ownerType, ownerId)) return Denied();
            if (string.IsNullOrWhiteSpace(motionerName) && !(motionerMemberId > 0))
                return Json(new { success = false, message = "Ange vem som lämnat motionen." });
            if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Skriv motionens rubrik." });
            var proposals = ParseProposals(proposalsJson);
            if (proposals.Count == 0) return Json(new { success = false, message = "Skriv minst ett förslag till beslut." });
            var me = await _access.CurrentMemberIdAsync();
            string name = motionerName?.Trim() ?? "";
            if (motionerMemberId > 0 && name.Length == 0) name = _work.MemberContact(motionerMemberId.Value).Name;

            var motion = _work.SubmitMotion(new BoardMotion
            {
                OwnerType = ownerType, OwnerId = ownerId,
                MotionerKind = BoardMotionerKinds.Member,
                MotionerMemberId = motionerMemberId > 0 ? motionerMemberId : null,
                MotionerName = name,
                Title = title, Background = background,
                Proposals = BoardMotionRules.SerializeProposals(proposals),
                SubmittedByMemberId = me
            });
            // Inkomstdagen är den dag motionen kom in, inte dagen den fördes in.
            if (DateTime.TryParseExact(submittedDate ?? "", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var sd) && sd.Date <= DateTime.Today)
                _work.SetSubmittedDate(motion.Id, sd.Date.AddHours(12));
            return Json(new { success = true, id = motion.Id, number = motion.NumberLabel });
        }

        // ---- Klubb till krets ------------------------------------------------------------------

        /// <summary>
        /// Klubbens beslut som en motion till kretsen kan hänvisa till: punkter med ett antecknat
        /// beslut på klubbens möten de senaste två åren.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDecisionReferences(int clubId)
        {
            if (!await _access.CanAccessBoardWorkAsync(DocumentOwnerType.Club, clubId)) return Denied();
            var since = DateTime.Today.AddYears(-2);
            var list = new List<object>();
            foreach (var meeting in _meetings.GetMeetings(DocumentOwnerType.Club, clubId).Where(m => m.MeetingDate >= since))
            {
                var ordered = BoardIssueRules.Ordered(_meetings.GetAgenda(meeting.Id));
                foreach (var (item, label, _) in ordered.Where(x => !string.IsNullOrWhiteSpace(x.Item.Decision)))
                    list.Add(new
                    {
                        agendaItemId = item.Id, meetingId = meeting.Id,
                        text = $"{meeting.Title} {D(meeting.MeetingDate)}, {label} {item.Heading}",
                        decision = item.Decision,
                        justerat = meeting.Status == "Justerat"
                    });
            }
            return Json(new { success = true, references = list });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitClubMotion(int clubId, string? title, string? background,
            string? proposalsJson, int sourceAgendaItemId)
        {
            if (!await _access.CanAccessBoardWorkAsync(DocumentOwnerType.Club, clubId)) return Denied();
            if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Skriv motionens rubrik." });
            var proposals = ParseProposals(proposalsJson);
            if (proposals.Count == 0) return Json(new { success = false, message = "Skriv minst ett förslag till beslut." });

            // ⚠️ KRAV (Stefan 2026-10-08): en motion från klubben ska hänvisa till styrelsens beslut.
            var meetingId = _meetings.GetAgendaItemMeetingId(sourceAgendaItemId);
            var sourceMeeting = meetingId.HasValue ? _meetings.GetMeeting(meetingId.Value) : null;
            if (sourceMeeting == null || !sourceMeeting.IsActive
                || sourceMeeting.OwnerType != DocumentOwnerType.Club || sourceMeeting.OwnerId != clubId)
                return Json(new { success = false, message = "Välj det beslut i klubbens protokoll som motionen bygger på." });
            var ordered = BoardIssueRules.Ordered(_meetings.GetAgenda(sourceMeeting.Id));
            var src = ordered.FirstOrDefault(x => x.Item.Id == sourceAgendaItemId);
            if (src.Item == null || string.IsNullOrWhiteSpace(src.Item.Decision))
                return Json(new { success = false, message = "Punkten har inget antecknat beslut. Skriv beslutet i protokollet först." });

            var regionId = _access.RegionIdForClub(clubId);
            if (regionId <= 0) return Json(new { success = false, message = "Klubbens krets kunde inte hittas." });

            var me = await _access.CurrentMemberIdAsync();
            var (clubName, _) = _work.OwnerContact(DocumentOwnerType.Club, clubId);
            var chair = _roles.GetActiveRoleHolders(DocumentOwnerType.Club, clubId, BoardRoleDefinitions.RoleOrdforande).FirstOrDefault();

            var motion = _work.SubmitMotion(new BoardMotion
            {
                OwnerType = DocumentOwnerType.Region, OwnerId = regionId,
                MotionerKind = BoardMotionerKinds.Club,
                MotionerClubId = clubId,
                MotionerName = clubName,
                SignedByName = chair?.MemberName,
                Title = title, Background = background,
                Proposals = BoardMotionRules.SerializeProposals(proposals),
                SourceMeetingId = sourceMeeting.Id,
                SourceAgendaItemId = sourceAgendaItemId,
                SourceReference = $"{sourceMeeting.Title} {D(sourceMeeting.MeetingDate)}, {src.Label}",
                SubmittedByMemberId = me
            });
            var (receipt, board) = await _notify.MotionSubmittedAsync(motion, BaseUrl);
            return Json(new { success = true, id = motion.Id, number = motion.NumberLabel, notified = board });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawClubMotion(int motionId)
        {
            var m = _work.GetMotion(motionId);
            if (m == null || m.MotionerKind != BoardMotionerKinds.Club || !m.MotionerClubId.HasValue)
                return Json(new { success = false, message = "Motionen hittades inte." });
            if (!await _access.CanAccessBoardWorkAsync(DocumentOwnerType.Club, m.MotionerClubId.Value)) return Denied();
            var r = _work.WithdrawMotion(motionId);
            return Json(new { success = r.Ok, message = r.Message });
        }

        // =====================================================================
        //  Motioner — medlemmens sida (/motion)
        // =====================================================================

        [HttpGet]
        public async Task<IActionResult> GetMotionPage(int ownerType, int ownerId)
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, needsLogin = true, message = "Logga in för att läsa motionerna." });
            if (!await _access.CanReadMotionsAsync(ownerType, ownerId))
                return Json(new { success = false, message = ownerType == DocumentOwnerType.Club
                    ? "Motionerna visas för klubbens medlemmar."
                    : "Motionerna visas för medlemmar i kretsens klubbar." });

            var (orgName, _) = _work.OwnerContact(ownerType, ownerId);
            var annual = _work.GetUpcomingAnnualMeeting(ownerType, ownerId);
            var all = _work.GetMotions(ownerType, ownerId, me);
            // Medlemmarna ser motioner som inte återkallats: de kommande och de som behandlats det senaste året.
            var cutoff = DateTime.Today.AddYears(-1);
            var visible = all.Where(v => v.State != BoardMotionState.Withdrawn
                && (v.State != BoardMotionState.Decided || (v.Placement?.MeetingDate ?? v.Motion.SubmittedDate) >= cutoff)).ToList();
            var isMember = await _access.IsMemberOfOwnerAsync(ownerType, ownerId);

            return Json(new
            {
                success = true,
                me,
                orgName,
                isClub = ownerType == DocumentOwnerType.Club,
                // Medlemmar lämnar motioner till sin klubb; till kretsen motionerar klubbarna.
                canSubmit = ownerType == DocumentOwnerType.Club && isMember,
                canReact = ownerType == DocumentOwnerType.Club && isMember,
                annualMeeting = annual == null ? null : new
                {
                    annual.Id, annual.Title, meetingDate = D(annual.MeetingDate),
                    motionDeadline = annual.MotionDeadline.HasValue ? D(annual.MotionDeadline.Value) : null,
                    deadlinePassed = annual.MotionDeadline.HasValue && DateTime.Today > annual.MotionDeadline.Value.Date
                },
                motions = visible.Select(v => new
                {
                    dto = MotionDto(v, v.State != BoardMotionState.Received),
                    mine = v.Motion.MotionerMemberId == me,
                    canWithdraw = v.Motion.MotionerMemberId == me && BoardMotionRules.CanWithdraw(v.State),
                    canSupport = ownerType == DocumentOwnerType.Club && isMember
                                 && BoardMotionRules.CanSupport(v.State, v.Motion.MotionerMemberId == me),
                    // Varför tummarna inte går att trycka på — en knapp som inte svarar utan förklaring
                    // läses som ett fel (Stefans test: hans egen motion).
                    supportNote = ownerType != DocumentOwnerType.Club ? "Motioner till kretsen kan inte få tummar."
                        : !isMember ? "Bara klubbens medlemmar kan tycka till."
                        : v.Motion.MotionerMemberId == me ? "Det här är din motion. Andra medlemmar kan sätta sitt namn på den eller ge tummen upp eller ner."
                        : v.State is BoardMotionState.Decided or BoardMotionState.Withdrawn ? "Motionen är avgjord — det går inte längre att tycka till."
                        : null
                }),
                myClubs = ownerType == DocumentOwnerType.Club
                    ? _memberClubs.GetClubOptions(me).Select(c => new { c.Id, c.Name })
                    : null
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitMemberMotion(int clubId, string? title, string? background, string? proposalsJson)
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in för att lämna en motion." });
            if (!await _access.IsMemberOfOwnerAsync(DocumentOwnerType.Club, clubId))
                return Json(new { success = false, message = "Du kan lämna motioner till klubbar du är medlem i." });
            if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Skriv en rubrik." });
            var proposals = ParseProposals(proposalsJson);
            if (proposals.Count == 0) return Json(new { success = false, message = "Skriv minst ett förslag till beslut, som börjar med \"att\"." });

            var (name, _) = _work.MemberContact(me);
            var motion = _work.SubmitMotion(new BoardMotion
            {
                OwnerType = DocumentOwnerType.Club, OwnerId = clubId,
                MotionerKind = BoardMotionerKinds.Member,
                MotionerMemberId = me,
                MotionerName = name,
                Title = title, Background = background,
                Proposals = BoardMotionRules.SerializeProposals(proposals),
                SubmittedByMemberId = me
            });
            var (receipt, board) = await _notify.MotionSubmittedAsync(motion, BaseUrl);
            var annual = motion.MeetingId.HasValue ? _meetings.GetMeeting(motion.MeetingId.Value) : null;
            return Json(new
            {
                success = true,
                id = motion.Id,
                number = motion.NumberLabel,
                receiptSent = receipt,
                late = BoardMotionRules.IsLate(motion.SubmittedDate, annual?.MotionDeadline)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMotionSupport(int motionId, string? kind)
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in först." });
            var m = _work.GetMotion(motionId);
            if (m == null) return Json(new { success = false, message = "Motionen hittades inte." });
            // Stöd ges till motioner till en KLUBB, av klubbens medlemmar.
            if (m.OwnerType != DocumentOwnerType.Club || !await _access.IsMemberOfOwnerAsync(DocumentOwnerType.Club, m.OwnerId))
                return Denied();
            var view = _work.GetMotions(m.OwnerType, m.OwnerId, me).FirstOrDefault(v => v.Motion.Id == motionId);
            if (view == null || !BoardMotionRules.CanSupport(view.State, m.MotionerMemberId == me))
                return Json(new { success = false, message = "Det går inte att ändra stödet för den här motionen nu." });
            string? k = string.IsNullOrWhiteSpace(kind) ? null : kind;
            if (k != null && !BoardMotionSupportKinds.IsValid(k)) return Json(new { success = false, message = "Okänt val." });
            _work.SetSupport(motionId, me, k);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawMyMotion(int motionId)
        {
            var me = await _access.CurrentMemberIdAsync();
            var m = _work.GetMotion(motionId);
            if (m == null || me <= 0 || m.MotionerMemberId != me)
                return Json(new { success = false, message = "Bara motionären kan återkalla motionen." });
            var r = _work.WithdrawMotion(motionId);
            return Json(new { success = r.Ok, message = r.Message });
        }

        // =====================================================================
        //  Bilagor till motioner (2026-10-08)
        // =====================================================================

        /// <summary>
        /// Läsrätt till en motions bilagor = samma som till motionen: medlemmarna (motionssidan),
        /// mottagarens styrelse, och för en klubbs motion till kretsen även klubbens styrelse.
        /// </summary>
        private async Task<bool> CanReadMotionFilesAsync(BoardMotion m) =>
            await _access.CanReadMotionsAsync(m.OwnerType, m.OwnerId)
            || await _access.CanAccessBoardWorkAsync(m.OwnerType, m.OwnerId)
            || (m.MotionerClubId.HasValue && await _access.CanAccessBoardWorkAsync(DocumentOwnerType.Club, m.MotionerClubId.Value));

        /// <summary>
        /// Bifoga och ta bort: motionären (medlemmen själv, eller klubbens sekreterare/ordförande för en
        /// klubbmotion) så länge motionen inte är behandlad eller återkallad, och mottagarens
        /// sekreterare/ordförande (t.ex. en motion som kom in på papper).
        /// </summary>
        private async Task<string?> AttachmentRefusalAsync(BoardMotion m, int me)
        {
            var view = _work.GetMotions(m.OwnerType, m.OwnerId, me).FirstOrDefault(v => v.Motion.Id == m.Id);
            if (view == null) return "Motionen hittades inte.";
            if (view.State is BoardMotionState.Decided or BoardMotionState.Withdrawn)
                return "Motionen är behandlad eller återkallad — bilagorna går inte att ändra.";
            if (await _access.CanPlaceAsync(m.OwnerType, m.OwnerId)) return null;
            if (m.MotionerMemberId.HasValue && m.MotionerMemberId == me) return null;
            if (m.MotionerClubId.HasValue && await _access.CanPlaceAsync(DocumentOwnerType.Club, m.MotionerClubId.Value)) return null;
            return "Bara motionären och styrelsens sekreterare eller ordförande kan ändra bilagorna.";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(30_000_000)]
        public async Task<IActionResult> AddMotionAttachment(int motionId, IFormFile? file)
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in först." });
            var m = _work.GetMotion(motionId);
            if (m == null) return Json(new { success = false, message = "Motionen hittades inte." });
            var refusal = await AttachmentRefusalAsync(m, me);
            if (refusal != null) return Json(new { success = false, message = refusal });
            if (file == null || file.Length == 0) return Json(new { success = false, message = "Välj en fil." });
            if (_work.CountAttachments(motionId) >= BoardWorkService.MaxAttachmentsPerMotion)
                return Json(new { success = false, message = $"En motion kan ha högst {BoardWorkService.MaxAttachmentsPerMotion} bilagor." });
            var (ok, error) = _documents.ValidateFile(file.FileName, file.Length);
            if (!ok) return Json(new { success = false, message = error });

            string stored;
            using (var stream = file.OpenReadStream()) stored = await _documents.SaveFileAsync(stream, file.FileName);
            var a = _work.AddAttachment(motionId, Path.GetFileName(file.FileName), stored, file.Length, me);
            return Json(new { success = true, id = a.Id, fileName = a.FileName });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMotionAttachment(int attachmentId)
        {
            var me = await _access.CurrentMemberIdAsync();
            var a = _work.GetAttachment(attachmentId);
            var m = a == null ? null : _work.GetMotion(a.MotionId);
            if (m == null || me <= 0) return Json(new { success = false, message = "Bilagan hittades inte." });
            var refusal = await AttachmentRefusalAsync(m, me);
            if (refusal != null) return Json(new { success = false, message = refusal });
            _work.RemoveAttachment(attachmentId);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> MotionAttachment(int id)
        {
            var a = _work.GetAttachment(id);
            var m = a == null ? null : _work.GetMotion(a.MotionId);
            if (m == null) return NotFound();
            if (await _access.CurrentMemberIdAsync() <= 0 || !await CanReadMotionFilesAsync(m)) return StatusCode(403);
            var path = _documents.GetFilePath(a!.StoredFileName);
            if (path == null) return NotFound();
            var ct = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider()
                .TryGetContentType(a.FileName, out var t) ? t : "application/octet-stream";
            return PhysicalFile(path, ct, a.FileName);
        }

        private static List<string> ParseProposals(string? json)
        {
            List<string?> raw;
            try { raw = string.IsNullOrWhiteSpace(json) ? new() : System.Text.Json.JsonSerializer.Deserialize<List<string?>>(json) ?? new(); }
            catch { raw = (json ?? "").Split('\n').Select(s => (string?)s).ToList(); }
            return BoardMotionRules.NormalizeProposals(raw);
        }
    }
}
