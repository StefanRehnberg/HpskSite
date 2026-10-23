using HpskSite.Models.Staffing;
using HpskSite.Services;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Dedicated per-competition planning page at /tavlingsplanering?c={competitionId} — the home for the
    /// preparatory work of putting an event on: **Förberedelser** (work-breakdown) + **Bemanning** (the
    /// day-of functionary roster). Deliberately SEPARATE from the operational day-of hub (the "Funktionärer"
    /// tab in competition management): this work relates to the competition rather than being part of running
    /// it, like the Fältkonfigurator. Reached from the "Planering" button in the comp-management header.
    ///
    /// Routed controller, no backoffice node needed (mirrors SightPictureController / StyrelseController).
    /// Auth is the same four-tier competition-staff gate the Staffing endpoints enforce; this page-level
    /// check just decides whether to render the workspace or a friendly access message.
    /// See Documentation/COMPETITION_STAFFING_SYSTEM.md.
    /// </summary>
    [Route("tavlingsplanering")]
    public class TavlingsplaneringController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly StaffingService _staffing;
        private readonly WorkBreakdownService _work;
        private readonly StaffingGridService _grid;
        private readonly HpskSite.Services.Schedule.CompetitionAgendaService _agenda;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Planning.PlanningService _planningService;
        private readonly HpskSite.Services.Planning.PlanningLinkService _links;

        public TavlingsplaneringController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            StaffingService staffing,
            WorkBreakdownService work,
            StaffingGridService grid,
            HpskSite.Services.Schedule.CompetitionAgendaService agenda,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Planning.PlanningService planningService,
            HpskSite.Services.Planning.PlanningLinkService links)
        {
            _planningService = planningService;
            _links = links;
            _competitions = competitions;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _staffing = staffing;
            _work = work;
            _grid = grid;
            _agenda = agenda;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int c)
        {
            // Master.cshtml (UmbracoViewPage) calls Model.Root()/.Url()/.Children — needs an
            // IPublishedContent Model. Pass the site root so the shared layout renders normally.
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(c > 0 ? $"/tavlingsplanering?c={c}" : "/tavlingsplanering")}");

            // Utan tävling: de fristående planeringarna (P1, 2026-10-21) — "Mina planeringar".
            if (c <= 0) return View("TavlingsplaneringHub", rootNode);

            var competition = c > 0 ? _competitions.Get(c) : null;
            var isCompetition = competition != null;

            ViewData["CompetitionId"] = c;
            ViewData["CompetitionExists"] = isCompetition;
            ViewData["CompetitionName"] = isCompetition ? (competition!.CompetitionName ?? "Tävling") : "";
            ViewData["Discipline"] = isCompetition ? (competition!.CompetitionType ?? "Precision") : "Precision";
            ViewData["ManageUrl"] = $"/competitionmanagement?competitionId={c}";
            ViewData["CanAccess"] = isCompetition && await HasCompetitionAccessAsync(c);

            return View("Tavlingsplanering", rootNode);
        }

        /// <summary>
        /// En fristående planering (P1, 2026-10-21): /tavlingsplanering/{id}. Behörigheten prövas av
        /// API:t (PlanningController) — sidan är bara skalet; den som inte är med får "finns inte".
        /// </summary>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> Planning(int id)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/tavlingsplanering/{id}")}");
            ViewData["PlanningId"] = id;
            ViewData["PlanningToken"] = "";
            return View("Planering", rootNode);
        }

        /// <summary>
        /// Mejlets knappar (Svara, Svara på tiderna, Öppna planeringen) landar här, UTAN inloggning
        /// (Stefan 2026-10-21). Den signerade länken är deltagarens nyckel; gästen har ingen annan väg
        /// in. Sidan är samma planeringssida, i länkläge — varje anrop bär länken.
        /// </summary>
        [HttpGet("svar")]
        public IActionResult Svar(string? t, string? a, int? m)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");
            var target = _links.ParseMail(t);
            var row = target == null ? null : _planningService.Participant(target.PlanningId, target.ParticipantId);
            var planning = target == null ? null : _planningService.Get(target.PlanningId);
            if (target == null || row == null || !row.IsActive || planning == null)
            {
                ViewData["PlanningLinkInvalid"] = true;
                return View("Planering", rootNode);
            }
            _planningService.TouchSeen(row.Id);
            ViewData["PlanningId"] = target.PlanningId;
            ViewData["PlanningToken"] = t;
            ViewData["PlanningStartView"] = a switch { "svara" => "meddelanden", "rosta" => "moten", "notiser" => "notiser", "lamna" => "deltagare", "uppgifter" => "uppgifter", _ => "oversikt" };
            // "Lämna planeringen" i inbjudan: sidan frågar direkt (inbjudningstaket, Stefan 2026-10-23).
            ViewData["PlanningLeave"] = a == "lamna";
            ViewData["PlanningStartMeeting"] = m ?? 0;
            ViewData["PlanningReply"] = a == "svara" ? (m ?? 0) : 0;
            return View("Planering", rootNode);
        }

        /// <summary>
        /// Svar på en inbjudan med e-post, UTAN inloggning (Stefan 2026-10-23). Länken ger ingen tillgång:
        /// ett ja lägger förfrågan hos den sammankallande, som släpper in personen.
        /// </summary>
        [HttpGet("inbjudan")]
        public IActionResult Inbjudan(string? t)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");
            var target = _links.ParseInvite(t);
            var req = target == null ? null : _planningService.Request(target.PlanningId, target.ParticipantId);
            var planning = req == null ? null : _planningService.Get(req.PlanningId);
            if (req != null && planning != null && req.Kind == HpskSite.Models.Planning.PlanningRequestKind.Invite)
            {
                var inviter = _planningService.Participants(planning.Id, includeRemoved: true).FirstOrDefault(p => p.MemberId == req.CreatedBy);
                ViewData["InviteToken"] = t;
                ViewData["InvitePlanning"] = planning.Name;
                ViewData["InviteTarget"] = planning.TargetText ?? "";
                ViewData["InviteName"] = req.Name;
                ViewData["InviteBy"] = inviter?.DisplayName ?? "";
                ViewData["InviteMessage"] = req.Message ?? "";
                ViewData["InviteStatus"] = req.Status;
            }
            return View("PlaneringInbjudan", rootNode);
        }

        /// <summary>
        /// Planeringens möten som kalenderprenumeration. Länken är evig (en kalender som slutar
        /// uppdateras är värre än ingen) men prövar deltagandet vid varje hämtning — en borttagen
        /// deltagare får en tom kalender.
        /// </summary>
        [HttpGet("kalender.ics")]
        public IActionResult Kalender(string? t)
        {
            var target = _links.ParseIcs(t);
            var row = target == null ? null : _planningService.Participant(target.PlanningId, target.ParticipantId);
            var planning = target == null ? null : _planningService.Get(target.PlanningId);
            var meetings = row is { IsActive: true } && planning != null
                ? _planningService.ScheduledFor(planning.Id)
                : new System.Collections.Generic.List<HpskSite.Models.Planning.PlanningMeetingRow>();
            var name = planning?.Name ?? "Planering";
            var ics = HpskSite.Services.Planning.PlanningIcsBuilder.Build(name, meetings,
                $"{_links.SiteUrl}/tavlingsplanering/{planning?.Id ?? 0}", DateTime.Now);
            Response.Headers["Cache-Control"] = "no-store";
            return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "planering.ics");
        }

        /// <summary>Printable "vem gör vad"-blad — roster (Bemanning) + prep (Förberedelser) on one sheet.</summary>
        [HttpGet("blad")]
        public async Task<IActionResult> Blad(int c, string? vy = null)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/tavlingsplanering/blad?c={c}")}");

            var competition = c > 0 ? _competitions.Get(c) : null;
            var isCompetition = competition != null;
            var canAccess = isCompetition && await HasCompetitionAccessAsync(c);

            var model = new PlaneringBladModel
            {
                CompetitionId = c,
                Vy = PlaneringBladVy.Normalise(vy),
                Exists = isCompetition,
                CanAccess = canAccess,
                CompName = isCompetition ? (competition!.CompetitionName ?? "Tävling") : "",
                Discipline = isCompetition ? (competition!.CompetitionType ?? "Precision") : "Precision",
            };

            if (canAccess)
            {
                var d = competition!.CompetitionDate;
                if (d.HasValue)
                    model.CompDate = d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                // Build only what this sheet shows — the grid and the prep breakdown are both expensive.
                if (model.ShowBemanning)
                {
                    model.Roster = _staffing.BuildRoster(c, model.Discipline, canEdit: false);
                    // Same builder as the screen. A print that renders its own shape is how paper stopped
                    // resembling the plan the organiser actually built.
                    model.Grid = _grid.BuildGrid(c, model.Discipline, 0);
                }
                if (model.ShowForberedelser) model.Work = _work.Build(c, canEdit: false);
                if (model.ShowDagsprogram)
                {
                    try { model.Agenda = _agenda.GetForCompetition(c); }
                    catch { /* tabellen kan saknas — bladet klarar sig utan */ }
                }
            }
            return View("TavlingsplaneringBlad", model);
        }

        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted comps.
        /// Mirrors StaffingController / EventMessageController.
        /// </summary>
        private Task<bool> HasCompetitionAccessAsync(int competitionId) =>
            _auth.HasCompetitionStaffAccessAsync(competitionId);
    }
}
