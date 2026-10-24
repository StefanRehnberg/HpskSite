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
        private readonly StaffOwnerResolver _owners;
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
            StaffOwnerResolver owners,
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
            _owners = owners;
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

            // En planering, inte två (Stefan 2026-10-23: "de ska bli en"). Har tävlingen en planering leder
            // tävlingens Planering-knapp dit; tävlingens funktionärer kommer in via kopplingen. ?vy= tar med
            // den del man ville till (gamla länkar och bokmärken).
            if (isCompetition && await HasCompetitionAccessAsync(c) && _planningService.ForCompetition(c) is { } linked)
            {
                var vy = (Request.Query["vy"].ToString() ?? "").ToLowerInvariant();
                return Redirect(vy switch
                {
                    "bemanning" => $"/tavlingsplanering/{linked.Id}#vy=bemanning",
                    "dagsprogram" => $"/tavlingsplanering/{linked.Id}/dagsprogram",
                    _ => $"/tavlingsplanering/{linked.Id}",
                });
            }

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
        /// Omgång 2 (2026-10-24): bemanningen är en del i planeringen (<c>#vy=bemanning</c>, planering-bemanning.js),
        /// inte en egen sida. Den gamla adressen (bokmärken, mejl, tävlingens Bemanning-knapp) leder dit.
        /// Behörigheten prövas av planeringen och av StaffingController för varje anrop.
        /// </summary>
        [HttpGet("{id:int}/bemanning")]
        public IActionResult PlanningStaffing(int id) => Redirect($"/tavlingsplanering/{id}#vy=bemanning");

        /// <summary>
        /// Planeringens dagsprogram (Stefan 2026-10-23): dagsprogrammet hör till tävlingen, så det finns i
        /// planeringen när den är kopplad. Samma delvy som förut; anropen bär planningId och prövas mot
        /// kopplingen i StaffingController.
        /// </summary>
        [HttpGet("{id:int}/dagsprogram")]
        public async Task<IActionResult> PlanningAgenda(int id)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/tavlingsplanering/{id}/dagsprogram")}");

            var owner = _owners.ForPlanning(id);
            var canAccess = owner != null && await CanStaffPlanningAsync(owner, currentMember.Email);
            ViewData["PlanningId"] = id;
            ViewData["Exists"] = owner != null;
            ViewData["CanAccess"] = canAccess;
            ViewData["CompetitionId"] = owner?.Comp ?? 0;
            ViewData["PlanningName"] = owner == null ? "" : (_owners.Planning(id)?.Name ?? "");
            ViewData["CompetitionName"] = owner is { HasCompetition: true } ? _owners.Name(owner) : "";
            ViewData["Discipline"] = owner == null ? "Precision" : (_owners.Discipline(owner) is { Length: > 0 } d ? d : "Precision");
            return View("PlaneringDagsprogram", rootNode);
        }

        /// <summary>Samma prövning som StaffingController.RowOwnerAsync — för sidan, inte för data.</summary>
        private async Task<bool> CanStaffPlanningAsync(HpskSite.Models.Staffing.StaffOwner owner, string email)
        {
            if (owner.HasCompetition && await HasCompetitionAccessAsync(owner.Comp)) return true;
            var md = _memberService.GetByEmail(email);
            if (md == null) return false;
            var row = _planningService.ParticipantForMember(owner.PlanningId, md.Id);
            return HpskSite.Services.Planning.PlanningAccess.CanEditStaffing(
                new HpskSite.Services.Planning.PlanningCaller(row is { IsActive: true } ? row : null, false));
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
        /// Omgång 3 steg 10 (K5): "Skicka mig en ny länk" på sidan för en länk som gått ut. Ingen inloggning — gästen
        /// har inget konto. Svaret är ALLTID detsamma, så sidan avslöjar aldrig om en adress finns i en planering, och
        /// samma adress kan fråga högst en gång i kvarten.
        /// </summary>
        [HttpPost("ny-lank")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NyLank(string? email)
        {
            const string neutral = "Om adressen hör till en gäst i en planering har vi skickat en ny länk dit. Titta i inkorgen om en stund.";
            var addr = (email ?? "").Trim().ToLowerInvariant();
            if (addr.Length < 3 || !addr.Contains('@'))
                return Json(new { success = false, message = "Skriv e-postadressen som inbjudan skickades till." });
            var rate = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.Hosting.RateCounterService)) as HpskSite.Services.Hosting.RateCounterService;
            if (rate != null && !rate.TryClaimOnce("planning-relink:" + addr, TimeSpan.FromMinutes(15)))
                return Json(new { success = true, message = neutral });
            var notify = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.Planning.PlanningNotifier)) as HpskSite.Services.Planning.PlanningNotifier;
            try { if (notify != null) await notify.RenewGuestLinksAsync(addr); }
            catch (Exception ex) { (HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<TavlingsplaneringController>)) as Microsoft.Extensions.Logging.ILogger)?.LogError(ex, "Tävlingsplaneringen: en ny länk kunde inte skickas."); }
            return Json(new { success = true, message = neutral });
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
        public async Task<IActionResult> Blad(int c, string? vy = null, int p = 0)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(p > 0 ? $"/tavlingsplanering/blad?p={p}&vy={vy}" : $"/tavlingsplanering/blad?c={c}")}");

            // En planerings blad (bemanning, förberedelser, dagsprogram), även utan tävling. Den som får
            // ändra i planeringen får skriva ut den — också när den är kopplad (tävlingens dagsprogram via kopplingen).
            var planningOk = false;
            if (p > 0)
            {
                var po = _owners.ForPlanning(p);
                planningOk = po != null && await CanStaffPlanningAsync(po, currentMember.Email);
                if (po is { HasCompetition: true }) c = po.Comp;
                else
                {
                    var pm = new PlaneringBladModel
                    {
                        CompetitionId = 0,
                        PlanningId = p,
                        AgendaAvailable = false,
                        Vy = PlaneringBladVy.Normalise(vy),
                        Exists = po != null,
                        CanAccess = planningOk,
                        CompName = po == null ? "" : (_owners.Planning(p)?.Name ?? _owners.Name(po)),
                        Discipline = po == null ? "Precision" : (_owners.Discipline(po) is { Length: > 0 } d ? d : "Precision"),
                    };
                    if (planningOk)
                    {
                        if (_owners.Span(po!).Start is { } sd && sd != default)
                            pm.CompDate = sd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                        if (pm.ShowBemanning)
                        {
                            pm.Roster = _staffing.BuildRoster(po!, pm.Discipline, canEdit: false);
                            pm.Grid = _grid.BuildGrid(po!, pm.Discipline, 0);
                        }
                        if (pm.ShowForberedelser) pm.Work = _work.BuildForPlanning(p, canEdit: false);
                    }
                    return View("TavlingsplaneringBlad", pm);
                }
            }

            var competition = c > 0 ? _competitions.Get(c) : null;
            var isCompetition = competition != null;
            var canAccess = isCompetition && (planningOk || await HasCompetitionAccessAsync(c));

            var model = new PlaneringBladModel
            {
                CompetitionId = c,
                PlanningId = p,
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
                    // P2b: tävlingens bemanning är dess planerings, när den har en.
                    var owner = _owners.ForCompetition(c);
                    model.Roster = _staffing.BuildRoster(owner, model.Discipline, canEdit: false);
                    // Same builder as the screen. A print that renders its own shape is how paper stopped
                    // resembling the plan the organiser actually built.
                    model.Grid = _grid.BuildGrid(owner, model.Discipline, 0);
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
