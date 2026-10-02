using System.Globalization;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services;
using HpskSite.Services.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kretsgranskning fas 2 — kretsens granskning av resultatlistan.
    ///
    /// <para><b>En STÄMPEL, inte en grind</b> (beslut 2026-10-02). Standardmedaljerna verifieras vid
    /// publiceringen som i dag; en godkänd lista får markeringen "Granskad av …krets". Bara en krets
    /// som själv slagit på <c>RequireResultApproval</c> håller inne platsmedaljerna tills listan är
    /// godkänd — se <see cref="ResultReviewGate"/>.</para>
    ///
    /// <para>Arrangörens sida: tävlingens staff (<c>HasCompetitionStaffAccessAsync</c>, båda
    /// värdformerna). Kretsens sida: uppdraget Resultatgranskare, kretsadmin eller sajtadmin — och
    /// rollen stämplas på beslutet. Utan granskare: länkläget, precis som tävlingsansökan.</para>
    /// </summary>
    public class ResultReviewController : SurfaceController
    {
        private readonly ResultReviewService _reviews;
        private readonly ResultReviewGate _gate;
        private readonly ResultReviewHooks _hooks;
        private readonly CompetitionApplicationService _apps;
        private readonly KretsCalendarService _calendar;
        private readonly KretsUppdragService _uppdrag;
        private readonly KretsLinkTokenService _tokens;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyTo;
        private readonly ClubService _clubs;
        private readonly ILogger<ResultReviewController> _logger;

        public ResultReviewController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            ResultReviewService reviews,
            ResultReviewGate gate,
            ResultReviewHooks hooks,
            CompetitionApplicationService apps,
            KretsCalendarService calendar,
            KretsUppdragService uppdrag,
            KretsLinkTokenService tokens,
            AdminAuthorizationService auth,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            EmailService email,
            ReplyContactResolver replyTo,
            ClubService clubs,
            ILogger<ResultReviewController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _reviews = reviews;
            _gate = gate;
            _hooks = hooks;
            _apps = apps;
            _calendar = calendar;
            _uppdrag = uppdrag;
            _tokens = tokens;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _email = email;
            _replyTo = replyTo;
            _clubs = clubs;
            _logger = logger;
        }

        private const string NoTables = "Granskningstabellerna saknas. Kör Migrations/create-competition-result-review-tables.sql.";

        // ════════════════════════════════════════════════════════════════════════════════
        //  Arrangörens sida (Resultat-fliken)
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>Kortet på Resultat-fliken: erbjuds granskning, läget, kretsens kommentar.</summary>
        [HttpGet]
        public async Task<IActionResult> GetForCompetition(int competitionId)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });

            var c = Context(competitionId);
            if (c == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

            var review = _reviews.For(competitionId, false);
            var holders = c.RegionId > 0 ? _uppdrag.Holders(c.RegionId, BoardRoleDefinitions.RoleResultatgranskare) : new();
            var end = c.End ?? c.Date;
            var gateApplies = _gate.GateApplies(competitionId);
            var changed = review != null && ResultReviewRules.Changed(review, ResultReviewRules.Checksum(c.ResultData));
            return Json(new
            {
                success = true,
                offered = c.Offered,
                notOfferedReason = c.Offered ? null
                    : c.IsClubOnly ? "Tävlingen är bara för klubbens egna medlemmar och ger inga standardmedaljer, så kretsen granskar inte resultatlistan."
                    : "Tävlingen ger inga standardmedaljer, så kretsen granskar inte resultatlistan.",
                region = c.RegionId > 0 ? new { id = c.RegionId, name = c.RegionName } : null,
                resultListExists = c.ResultNode != null,
                resultPublished = c.ResultOfficial,
                deadline = end?.AddDays(14).ToString("yyyy-MM-dd"),
                late = end.HasValue && review == null && DateTime.Today > ResultReviewRules.SendDeadline(end.Value),
                gateApplies,
                medalsPending = gateApplies && _gate.MedalsPending(competitionId),
                hasReviewers = holders.Count > 0,
                komIgangUrl = c.RegionId > 0 ? $"/kretsen/kom-igang?krets={c.RegionId}" : null,
                changedSinceSubmit = changed,
                review = review == null ? null : Dto(review, includeSnapshot: false)
            });
        }

        /// <summary>
        /// Arrangören skickar in resultatlistan. Kräver en publicerad lista, och en uttrycklig
        /// bekräftelse av att vapenkontrollen gjorts ("1").
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(int competitionId, string? weaponCheck, string? note)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var me = await CurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Logga in först." });

            var c = Context(competitionId);
            if (c == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            if (!c.Offered) return Json(new { success = false, message = "Tävlingen ger inga standardmedaljer, så kretsen granskar inte resultatlistan." });
            if (c.RegionId <= 0) return Json(new { success = false, message = "Tävlingens krets går inte att avgöra. Kontrollera arrangör och krets på tävlingen." });
            if (c.ResultNode == null || string.IsNullOrWhiteSpace(c.ResultData))
                return Json(new { success = false, message = "Skapa resultatlistan först (Uppdatera på Resultat-fliken)." });
            if (!c.ResultOfficial)
                return Json(new { success = false, message = "Publicera resultatlistan först. Kretsen granskar den lista skyttarna ser." });
            if (!IsExplicitlyTrue(weaponCheck))
                return Json(new { success = false, message = "Bekräfta att vapenkontrollen är gjord innan listan skickas in." });

            var settings = SafeSettings(c.RegionId);
            var requiresTwo = settings?.TwoReviewersAtChampionships == true && c.IsSmOrLandsdel;
            var (r, err) = _reviews.Submit(competitionId, false, c.RegionId, c.ResultData!, _gate.GateApplies(competitionId),
                requiresTwo, true, Clip(note, 2000), me.Id, DisplayName(me));
            if (r == null) return Json(new { success = false, message = err });

            _gate.ReconcileMedals(competitionId);

            var dateText = c.Date?.ToString("d MMMM yyyy", Sv) ?? "";
            var (sent, links, linkMode) = await _hooks.NotifyReviewersAsync(r, $"Resultatlista att granska: {c.Name}", new[]
            {
                $"{c.Organiser} har skickat in resultatlistan för {c.Name}{(dateText.Length > 0 ? $" ({dateText})" : "")} till kretsens granskning.",
                "Arrangören har bekräftat att vapenkontrollen är gjord.",
                string.IsNullOrWhiteSpace(r.ArrangerNote) ? "" : $"Meddelande från arrangören: {r.ArrangerNote}",
                r.RequiresTwo ? "Kretsen kräver två olika granskare vid SM och landsdelsmästerskap." : "",
                r.Gated ? "Kretsen kräver sitt godkännande innan standardmedaljerna räknas, så skyttarnas medaljer väntar på granskningen." : ""
            }, BaseUrl, _replyTo.ForPersonInClub(me.Id, c.ClubId));

            var msg = sent > 0
                ? $"Resultatlistan är inskickad, och kretsen har fått besked ({sent} {(sent == 1 ? "mottagare" : "mottagare")})."
                : "Resultatlistan är inskickad, men mejlet till kretsen kunde inte skickas. Meddela kretsen på annat sätt.";
            return Json(new
            {
                success = true,
                message = msg,
                linkMode,
                caseLinks = links.Count > 0 && await _auth.IsCurrentUserAdminAsync() ? links : null
            });
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Kretsens sida (/kretsen/granskning)
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Kretsens inkorg: inskickade listor, avgjorda, och tävlingar som erbjuds granskning men
        /// inte skickats in (efter tävlingen — "väntar på arrangören").
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionReviews(int regionId, int? year)
        {
            var auth = await KretsAuthorityAsync(regionId);
            if (!auth.Allowed) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var y = year ?? DateTime.Today.Year;

            var reviews = SafeList(() => _reviews.ForRegion(regionId))
                .Select(r => new { r, c = Context(r.CompetitionId) })
                .Where(x => x.c?.Date == null || x.c.Date.Value.Year == y)
                .ToList();
            var reviewed = reviews.Select(x => x.r.CompetitionId).ToHashSet();

            var awaiting = new List<object>();
            foreach (var (id, info) in CompetitionsInRegion(regionId, y))
            {
                if (reviewed.Contains(id)) continue;
                var end = info.EndDate ?? info.Date;
                if (end == null || end.Value.Date >= DateTime.Today) continue;
                var c = Context(id);
                if (c == null || !c.Offered) continue;
                awaiting.Add(new
                {
                    competitionId = id,
                    name = c.Name,
                    date = c.Date?.ToString("yyyy-MM-dd"),
                    organiser = c.Organiser,
                    resultPublished = c.ResultOfficial,
                    deadline = end.Value.AddDays(14).ToString("yyyy-MM-dd"),
                    late = DateTime.Today > ResultReviewRules.SendDeadline(end.Value),
                    gateApplies = _gate.GateApplies(id)
                });
            }

            var settings = SafeSettings(regionId);
            return Json(new
            {
                success = true,
                role = auth.Role,
                year = y,
                settings = new
                {
                    requireApproval = settings?.RequireResultApproval == true,
                    since = settings?.RequireResultApprovalSince?.ToString("yyyy-MM-dd"),
                    twoReviewers = settings?.TwoReviewersAtChampionships == true
                },
                canEditSettings = await CanEditSettingsAsync(regionId),
                reviewers = _uppdrag.Holders(regionId, BoardRoleDefinitions.RoleResultatgranskare).Select(h => h.Name).ToList(),
                reviews = reviews.OrderBy(x => x.r.Status == ResultReviewStatus.Inskickad ? 0 : 1)
                    .ThenByDescending(x => x.r.SubmittedAt)
                    .Select(x => new
                    {
                        review = Dto(x.r, includeSnapshot: false),
                        name = x.c?.Name ?? $"Tävling {x.r.CompetitionId}",
                        date = x.c?.Date?.ToString("yyyy-MM-dd"),
                        organiser = x.c?.Organiser ?? ""
                    }),
                awaiting
            });
        }

        /// <summary>Ett ärende med granskarens automatiska checklista.</summary>
        [HttpGet]
        public async Task<IActionResult> GetReview(int id)
        {
            var r = _reviews.TablesExist() ? _reviews.Get(id) : null;
            if (r == null) return Json(new { success = false, message = "Granskningen hittades inte." });
            var auth = await KretsAuthorityAsync(r.RegionId);
            if (!auth.Allowed) return Json(new { success = false, message = "Åtkomst nekad" });
            var me = await CurrentMemberAsync();
            return Json(CaseJson(r, me?.Id ?? 0));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int id, string? comment)
        {
            var (r, who, deny) = await KretsActionAsync(id);
            if (deny != null) return deny;
            return Json(await ApproveCoreAsync(r!, who!, comment));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Return(int id, string? comment)
        {
            var (r, who, deny) = await KretsActionAsync(id);
            if (deny != null) return deny;
            return Json(await ReturnCoreAsync(r!, who!, comment));
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Länkläget (/kretsen/resultat-arende?t=)
        // ════════════════════════════════════════════════════════════════════════════════

        [HttpGet]
        public IActionResult GetByLink(string t)
        {
            var (r, _, error) = ReadCaseLink(t);
            if (r == null) return Json(new { success = false, message = error });
            return Json(CaseJson(r, 0));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActByLink(string t, string? action, string? comment, string? name, string? role)
        {
            var (r, link, error) = ReadCaseLink(t);
            if (r == null) return Json(new { success = false, message = error });
            name = Clip(name, 200);
            role = Clip(role, 200);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(role))
                return Json(new { success = false, message = "Skriv ditt namn och din roll i kretsen — de står med på beslutet." });
            var who = new KretsDecider(0, name!, role!, KretsChannel.Lank, link!.SentTo);
            return action switch
            {
                "approve" => Json(await ApproveCoreAsync(r, who, comment)),
                "return" => Json(await ReturnCoreAsync(r, who, comment)),
                _ => Json(new { success = false, message = "Okänd åtgärd." })
            };
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Kretsens inställningar
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// "Kräv kretsens godkännande" och "två granskare vid SM/LDM". Grinden kräver minst en
        /// resultatgranskare — annars håller kretsen inne medaljer som ingen kan släppa. Den gäller
        /// bara tävlingar från dagen den slås på; redan publicerade tävlingar rörs inte.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(int regionId, string? requireApproval, string? twoReviewers)
        {
            if (!await CanEditSettingsAsync(regionId)) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var me = await CurrentMemberAsync();
            var require = IsExplicitlyTrue(requireApproval);
            var two = IsExplicitlyTrue(twoReviewers);

            if (require && _uppdrag.Holders(regionId, BoardRoleDefinitions.RoleResultatgranskare).Count == 0)
                return Json(new { success = false, message = "Utse minst en resultatgranskare först (Styrelsearbete → Kretsens uppdrag). Annars håller kretsen inne medaljer som ingen kan släppa." });

            var s = SafeSettings(regionId) ?? new RegionCalendarSettings { RegionId = regionId };
            var wasOn = s.RequireResultApproval;
            s.RequireResultApproval = require;
            if (require && !wasOn) s.RequireResultApprovalSince = DateTime.Today;
            if (!require) s.RequireResultApprovalSince = null;
            s.TwoReviewersAtChampionships = two;
            _apps.SaveSettings(s, me?.Id ?? 0);

            var moved = wasOn != require ? _gate.ReconcileRegion(regionId) : 0;
            var msg = require && !wasOn
                ? $"Sparat. Från och med i dag ({DateTime.Today:yyyy-MM-dd}) väntar standardmedaljerna på kretsens godkännande. Redan genomförda tävlingar påverkas inte."
                : !require && wasOn
                    ? "Sparat. Kretsens godkännande krävs inte längre" + (moved > 0 ? $", och {moved} standardmedaljer som väntade har släppts." : ".")
                    : "Sparat.";
            return Json(new { success = true, message = msg });
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Kärnan
        // ════════════════════════════════════════════════════════════════════════════════

        private async Task<object> ApproveCoreAsync(CompetitionResultReview r, KretsDecider who, string? comment)
        {
            if (who.MemberId > 0 && who.MemberId == r.SubmittedByMemberId && who.Role != SupportRole)
                return new { success = false, message = "Den som skickade in listan kan inte granska den. En annan granskare i kretsen behöver godkänna." };
            var c = Context(r.CompetitionId);
            var (saved, outcome, err) = _reviews.Approve(r.Id, c?.ResultData ?? "", Clip(comment, 4000), who);
            if (saved == null) return new { success = false, message = err };

            if (outcome == ResultReviewRules.ApproveOutcome.FirstOfTwo)
                return new { success = true, message = "Ditt godkännande är registrerat. Kretsen kräver två granskare vid SM och landsdelsmästerskap, så en annan granskare behöver godkänna listan." };

            _gate.ReconcileMedals(r.CompetitionId);
            var regionName = _calendar.Region(r.RegionId)?.Name ?? "kretsen";
            var n = await NotifyOrganiserAsync(saved, who, $"Resultatlistan är granskad: {c?.Name}", new[]
            {
                $"{regionName} har granskat och godkänt resultatlistan för {c?.Name} ({who.Name}, {who.Role}).",
                string.IsNullOrWhiteSpace(saved.Comment) ? "" : $"Kommentar: {saved.Comment}",
                saved.Gated ? "Skyttarnas standardmedaljer är nu godkända." : "Listan bär nu markeringen \"Granskad av kretsen\"."
            });
            return new { success = true, notified = n, message = "Resultatlistan är godkänd" + (n > 0 ? ", och arrangören har fått besked." : ". Mejlet till arrangören kunde inte skickas — meddela arrangören på annat sätt.") };
        }

        private async Task<object> ReturnCoreAsync(CompetitionResultReview r, KretsDecider who, string? comment)
        {
            var (saved, err) = _reviews.Return(r.Id, comment, who);
            if (saved == null) return new { success = false, message = err };
            _gate.ReconcileMedals(r.CompetitionId);
            var c = Context(r.CompetitionId);
            var regionName = _calendar.Region(r.RegionId)?.Name ?? "Kretsen";
            var n = await NotifyOrganiserAsync(saved, who, $"Kretsen ber er rätta resultatlistan: {c?.Name}", new[]
            {
                $"{who.Name} ({who.Role}) i {regionName} har gått igenom resultatlistan för {c?.Name} och ber er rätta följande:",
                saved.Comment ?? "",
                "Rätta listan, tryck Uppdatera och skicka in den igen från Resultat-fliken."
            });
            return new { success = true, notified = n, message = "Listan är återsänd" + (n > 0 ? ", och arrangören har fått besked." : ". Mejlet till arrangören kunde inte skickas — meddela arrangören på annat sätt.") };
        }

        private object CaseJson(CompetitionResultReview r, int viewerId)
        {
            var c = Context(r.CompetitionId);
            var medals = _gate.OnSiteMedalCounts(r.CompetitionId);
            var changed = c != null && ResultReviewRules.Changed(r, ResultReviewRules.Checksum(c.ResultData));
            return new
            {
                success = true,
                review = Dto(r, includeSnapshot: false),
                competition = c == null ? null : new
                {
                    id = c.Id,
                    name = c.Name,
                    date = c.Date?.ToString("yyyy-MM-dd"),
                    endDate = c.End?.ToString("yyyy-MM-dd"),
                    place = c.Venue,
                    organiser = c.Organiser,
                    level = CompetitionLevel.Find(c.Level)?.Label ?? "",
                    scope = c.Scope,
                    discipline = c.TypeLabel,
                    resultUrl = c.ResultUrl,
                    competitionUrl = c.Url
                },
                region = _calendar.Region(r.RegionId) is { } reg ? new { reg.Id, reg.Name } : null,
                checklist = Checklist(c),
                medals = medals.Select(m => new { medal = m.MedalType, status = m.Status, count = m.Count }),
                changedSinceSubmit = changed,
                canAct = ResultReviewRules.KretsCanAct(r) && !changed,
                viewerApprovedFirst = viewerId > 0 && r.FirstApproverMemberId == viewerId,
                viewerIsSubmitter = viewerId > 0 && r.SubmittedByMemberId == viewerId
            };
        }

        /// <summary>
        /// Granskarens automatiska checklista ur den sparade resultatlistan. Läser JSON:en otypat —
        /// precision, fält och spring har olika former, och checklistan ska aldrig fälla ärendet.
        /// </summary>
        private object Checklist(CompCtx? c)
        {
            var classes = new List<object>();
            var merged = new List<string>();
            var medalists = new List<object>();
            var unresolved = new List<string>();
            int starters = 0;
            if (c?.ResultData is { Length: > 0 } json)
            {
                try
                {
                    var root = JObject.Parse(json);
                    foreach (var g in root["ClassGroups"] as JArray ?? new JArray())
                    {
                        var name = (string?)g["DisplayClassName"] ?? (string?)g["ClassName"] ?? (string?)g["Name"] ?? "";
                        var count = (g["Shooters"] as JArray)?.Count ?? (g["Results"] as JArray)?.Count ?? 0;
                        starters += count;
                        classes.Add(new { name, starters = count, underFive = count < 5 });
                        var raw = (string?)g["ClassName"] ?? "";
                        if (raw.Contains('+')) merged.Add(raw);
                    }
                    foreach (var cat in root["MedalAwards"] as JArray ?? new JArray())
                    {
                        var catName = (string?)cat["CategoryName"] ?? "";
                        foreach (var a in cat["Awards"] as JArray ?? new JArray())
                            medalists.Add(new { category = catName, medal = (string?)a["Medal"] ?? "", name = (string?)a["Name"] ?? "", club = (string?)a["Club"] ?? "", score = (int?)a["TotalScore"] });
                        foreach (var u in cat["Unresolved"] as JArray ?? new JArray())
                            unresolved.Add($"{catName}: {(string?)u}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Granskningens checklista kunde inte läsa resultatlistan för tävling {Id}.", c.Id);
                }
            }
            return new { starters, classes, merged, medalists, unresolved, isChampionship = c?.IsChampionship == true };
        }

        private object Dto(CompetitionResultReview r, bool includeSnapshot) => new
        {
            id = r.Id,
            competitionId = r.CompetitionId,
            status = r.Status,
            statusLabel = ResultReviewStatus.Label(r.Status),
            gated = r.Gated,
            requiresTwo = r.RequiresTwo,
            weaponCheckAttested = r.WeaponCheckAttested,
            arrangerNote = r.ArrangerNote,
            submittedAt = r.SubmittedAt.ToString("yyyy-MM-dd HH:mm"),
            submittedBy = r.SubmittedByName,
            firstApprovedAt = r.FirstApprovedAt?.ToString("yyyy-MM-dd HH:mm"),
            firstApprover = r.FirstApproverName,
            decidedAt = r.DecidedAt?.ToString("yyyy-MM-dd HH:mm"),
            decider = r.DeciderName,
            deciderRole = r.DeciderRole,
            comment = r.Comment,
            channel = r.Channel,
            events = SafeList(() => _reviews.Events(r.Id)).Select(e => new
            {
                at = e.At.ToString("yyyy-MM-dd HH:mm"),
                by = e.ByName,
                kind = e.Kind,
                label = EventLabel(e.Kind),
                text = e.Text
            })
        };

        private async Task<int> NotifyOrganiserAsync(CompetitionResultReview r, KretsDecider who, string subject, IEnumerable<string> paras)
        {
            try
            {
                var to = new List<(string Email, string Name)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string? e, string n) { if (!string.IsNullOrWhiteSpace(e) && seen.Add(e.Trim())) to.Add((e.Trim(), n)); }
                var submitter = r.SubmittedByMemberId > 0 ? _memberService.GetById(r.SubmittedByMemberId) : null;
                if (submitter != null) Add(submitter.Email, DisplayName(submitter));
                var comp = UmbracoContext.Content?.GetById(r.CompetitionId);
                Add(comp?.Value<string>("contactEmail"), comp?.Value<string>("contactPerson") ?? "");

                var replyTo = who.MemberId > 0 ? _replyTo.ForMember(who.MemberId) : _replyTo.ForRegion(r.RegionId);
                var url = $"{BaseUrl}/competitionmanagement?competitionId={r.CompetitionId}";
                int sent = 0;
                foreach (var (email, name) in to)
                    if (await _email.SendKretsgranskningAsync(email, name, subject, paras, "Öppna Resultat-fliken", url,
                            $"Beslutet fattades av {who.Name} ({who.Role}).", replyTo))
                        sent++;
                return sent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatgranskning {Id}: arrangören kunde inte aviseras.", r.Id);
                return 0;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Tävlingens sammanhang
        // ════════════════════════════════════════════════════════════════════════════════

        private sealed class CompCtx
        {
            public int Id; public string Name = ""; public DateTime? Date; public DateTime? End;
            public int ClubId; public int RegionId; public string RegionName = ""; public string Organiser = "";
            public string Venue = ""; public string Level = ""; public string Scope = ""; public string TypeLabel = "";
            public bool IsClubOnly; public bool Offered; public bool IsChampionship; public bool IsSmOrLandsdel;
            public IContent? ResultNode; public string? ResultData; public bool ResultOfficial;
            public string? ResultUrl; public string? Url;
        }

        private CompCtx? Context(int competitionId)
        {
            var comp = UmbracoContext.Content?.GetById(competitionId);
            if (comp == null || comp.ContentType.Alias != "competition") return null;
            var c = new CompCtx
            {
                Id = comp.Id,
                Name = comp.Value<string>("competitionName") is { Length: > 0 } n ? n : comp.Name ?? "",
                Date = RealDate(comp.Value<DateTime?>("competitionDate")),
                End = RealDate(comp.Value<DateTime?>("competitionEndDate")),
                ClubId = comp.Value<int>("clubId"),
                Venue = comp.Value<string>("venue") ?? "",
                Level = comp.Value<string>("competitionLevel") ?? "",
                Scope = ChampionshipCategory.NormalizeScope(comp.Value("competitionScope")?.ToString()),
                IsClubOnly = comp.Value<bool>("isClubOnly"),
                Url = comp.Url()
            };
            var type = comp.Value<string>("competitionType") ?? "";
            c.TypeLabel = HpskSite.Models.CompetitionTypes.GetFuzzy(type)?.Name ?? type;
            c.Offered = ResultReviewRules.Offered(comp.Value<bool>("isAwardingStandardMedals"), c.IsClubOnly);
            c.IsChampionship = CompetitionScopeHelper.IsChampionshipScope(c.Scope);
            c.IsSmOrLandsdel = c.Scope == CompetitionScopeHelper.SvensktMasterskap || c.Scope == CompetitionScopeHelper.Landsdelsmasterskap;

            var cr = _calendar.CompetitionRegion(competitionId);
            if (cr != null) { c.RegionId = cr.Value.Region.Id; c.RegionName = cr.Value.Region.Name; }
            c.Organiser = c.ClubId > 0 ? (_clubs.GetClubNameById(c.ClubId) ?? c.RegionName) : c.RegionName;

            try
            {
                var children = _contentService.GetPagedChildren(competitionId, 0, 200, out _)
                    .Where(x => x.ContentType.Alias == "competitionResult").ToList();
                c.ResultNode = children.FirstOrDefault(x => x.Name == "Resultat") ?? children.FirstOrDefault();
                if (c.ResultNode != null)
                {
                    c.ResultData = c.ResultNode.GetValue<string>("resultData");
                    c.ResultOfficial = c.ResultNode.GetValue<bool>("isOfficial");
                    c.ResultUrl = UmbracoContext.Content?.GetById(c.ResultNode.Id)?.Url();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resultatnoden kunde inte läsas för tävling {Id}.", competitionId);
            }
            return c;
        }

        private IEnumerable<(int Id, KretsCalendarService.CompetitionRegionInfo Info)> CompetitionsInRegion(int regionId, int year)
        {
            var region = _calendar.Region(regionId);
            if (region == null) yield break;
            foreach (var id in _calendar.CompetitionIdsInRegion(region.Code))
            {
                var info = _calendar.CompetitionRegion(id)?.Competition;
                if (info?.Date == null || info.Date.Value.Year != year) continue;
                yield return (id, info);
            }
        }

        private static DateTime? RealDate(DateTime? d) => d.HasValue && d.Value.Year > 1900 ? d : null;

        // ════════════════════════════════════════════════════════════════════════════════
        //  Behörighet och länk
        // ════════════════════════════════════════════════════════════════════════════════

        private const string SupportRole = "Sajtadministratör (support)";

        private async Task<(bool Allowed, string Role)> KretsAuthorityAsync(int regionId)
        {
            var region = _calendar.Region(regionId);
            if (region == null) return (false, "");
            var me = await CurrentMemberAsync();
            if (me == null) return (false, "");
            if (_uppdrag.HasUppdrag(regionId, me.Id, BoardRoleDefinitions.RoleResultatgranskare))
                return (true, BoardRoleDefinitions.GetLabel(BoardRoleDefinitions.RoleResultatgranskare));
            if (await _auth.IsCurrentUserAdminAsync()) return (true, SupportRole);
            if (await _auth.IsRegionalAdminForRegion(region.Code)) return (true, "Kretsadministratör");
            return (false, "");
        }

        private async Task<bool> CanEditSettingsAsync(int regionId)
        {
            var region = _calendar.Region(regionId);
            if (region == null) return false;
            return await _auth.IsCurrentUserAdminAsync() || await _auth.IsRegionalAdminForRegion(region.Code);
        }

        private async Task<(CompetitionResultReview? R, KretsDecider? Who, IActionResult? Deny)> KretsActionAsync(int id)
        {
            var r = _reviews.TablesExist() ? _reviews.Get(id) : null;
            if (r == null) return (null, null, Json(new { success = false, message = "Granskningen hittades inte." }));
            var auth = await KretsAuthorityAsync(r.RegionId);
            if (!auth.Allowed) return (null, null, Json(new { success = false, message = "Åtkomst nekad" }));
            var me = await CurrentMemberAsync();
            return (r, new KretsDecider(me?.Id ?? 0, me == null ? "" : DisplayName(me), auth.Role, KretsChannel.Inloggad), null);
        }

        private (CompetitionResultReview? R, CaseLinkPayload? Link, string? Error) ReadCaseLink(string? t)
        {
            var p = _tokens.ReadCaseLink(t);
            if (p == null || p.CaseKind != ResultReviewHooks.CaseKind)
                return (null, null, "Länken gäller inte längre. Den kan ha gått ut (den gäller i 60 dagar).");
            var r = _reviews.TablesExist() ? _reviews.Get(p.CaseId) : null;
            if (r == null || r.RegionId != p.RegionId) return (null, null, "Granskningen finns inte längre.");
            if (r.Checksum != p.Checksum)
                return (null, null, "Resultatlistan har ändrats sedan länken skickades, så länken gäller inte längre. Kretsen får en ny länk när listan skickas in igen.");
            return (r, p, null);
        }

        // ════════════════════════════════════════════════════════════════════════════════

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        private RegionCalendarSettings? SafeSettings(int regionId)
        {
            try { return _apps.Settings(regionId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Kretsens inställningar kunde inte läsas för {Id}.", regionId); return null; }
        }

        private List<T> SafeList<T>(Func<List<T>> f)
        {
            try { return f(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Resultatgranskning: läsning misslyckades."); return new(); }
        }

        /// <summary>
        /// "1"/"true"/"on" — och ALDRIG ett utelämnat värde. En bekräftelse vars standard är ja är
        /// ingen bekräftelse (samma fälla som vapenräkningen gick i).
        /// </summary>
        private static bool IsExplicitlyTrue(string? v) =>
            v != null && (v.Trim() == "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("on", StringComparison.OrdinalIgnoreCase));

        private static string EventLabel(string k) => k switch
        {
            ResultReviewEventKind.Submitted => "Inskickad",
            ResultReviewEventKind.Resubmitted => "Inskickad igen",
            ResultReviewEventKind.FirstApproval => "Första granskaren godkände",
            ResultReviewEventKind.Approved => "Godkänd",
            ResultReviewEventKind.Returned => "Återsänd",
            ResultReviewEventKind.ChangedAfterApproval => "Listan ändrades",
            _ => k
        };

        private static string? Clip(string? s, int max)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) return null;
            return t.Length > max ? t[..max] : t;
        }

        private async Task<IMember?> CurrentMemberAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            return current?.Email == null ? null : _memberService.GetByEmail(current.Email);
        }

        private static string DisplayName(IMember m)
        {
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m.Name ?? "");
        }
    }
}
