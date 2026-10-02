using System.Globalization;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services;
using HpskSite.Services.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
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
    /// Kretsgranskning fas 3 — kretsens granskning av banan i fältskytte (SHB C.3.5.2.2).
    ///
    /// <para><b>En stämpel, som resultatgranskningen.</b> Frivillig för fälttävlingar på kretsnivå och
    /// högre; krävs vid nationell fältskjutning och när kretsen själv kräver den för sina
    /// kretstävlingar. SM och landsdelsmästerskap går till Förbundet — där registrerar arrangören
    /// bara att underlaget skickats och vad Förbundet svarade. Granskningen gäller tävlingens EGEN
    /// stationConfig; att den ändrats efteråt avgörs vid läsning (kontrollsumman).</para>
    /// </summary>
    public class CourseReviewController : SurfaceController
    {
        private readonly CourseReviewService _reviews;
        private readonly CourseReviewChecklist _checklist;
        private readonly CourseReviewAccess _access;
        private readonly CompetitionApplicationService _apps;
        private readonly KretsCalendarService _calendar;
        private readonly KretsUppdragService _uppdrag;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyTo;
        private readonly ClubService _clubs;
        private readonly ILogger<CourseReviewController> _logger;

        public CourseReviewController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CourseReviewService reviews, CourseReviewChecklist checklist, CourseReviewAccess access,
            CompetitionApplicationService apps, KretsCalendarService calendar, KretsUppdragService uppdrag,
            AdminAuthorizationService auth, IMemberManager memberManager, IMemberService memberService,
            IContentService contentService, EmailService email, ReplyContactResolver replyTo, ClubService clubs,
            ILogger<CourseReviewController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _reviews = reviews; _checklist = checklist; _access = access; _apps = apps; _calendar = calendar;
            _uppdrag = uppdrag; _auth = auth; _memberManager = memberManager; _memberService = memberService;
            _contentService = contentService; _email = email; _replyTo = replyTo; _clubs = clubs; _logger = logger;
        }

        private const string NoTables = "Bangranskningens tabeller saknas. Kör Migrations/create-competition-course-review-tables.sql.";

        // ════════════════════════════════════════════════════════════════════════════════
        //  Arrangörens sida (Stationer-fliken)
        // ════════════════════════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetForCompetition(int competitionId)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var c = Context(competitionId);
            if (c == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            var r = _reviews.For(competitionId);
            var kind = c.RegionId > 0 ? _access.ReviewerKind(c.RegionId) : CourseReviewerKind.Bangranskare;
            var reviewers = c.RegionId > 0 ? _uppdrag.CourseReviewers(c.RegionId, kind) : new();
            return Json(new
            {
                success = true,
                offered = c.Offered,
                missingLevel = c.OfferBlockedByMissingLevel,
                route = c.Route,
                required = c.Required,
                requiredReason = c.Route == CourseReviewRoute.Forbundet
                    ? "Vid SM och landsdelsmästerskap granskar Förbundet målförutsättningarna och stationsbeskrivningarna, minst 12 veckor före start (SHB C.3.5.2.2)."
                    : c.IsNational ? "Vid nationell fältskjutning granskar kretsen målförutsättningarna och stationsbeskrivningarna (SHB C.3.5.2.2)."
                    : c.Required ? $"{c.RegionName} kräver bangranskning för sina kretstävlingar." : null,
                region = c.RegionId > 0 ? new { id = c.RegionId, name = c.RegionName } : null,
                reviewerLabel = CourseReviewerKind.Label(kind),
                hasReviewers = reviewers.Count > 0,
                komIgangUrl = c.RegionId > 0 ? $"/kretsen/kom-igang?krets={c.RegionId}" : null,
                hasStations = c.HasStations,
                deadline = c.Deadline?.ToString("yyyy-MM-dd"),
                late = c.Deadline != null && (r == null || r.Status == CourseReviewStatus.Atersand) && DateTime.Today > c.Deadline.Value,
                changedSinceSubmit = r != null && CourseReviewRules.Changed(r, CourseReviewRules.Checksum(c.StationConfig)),
                review = r == null ? null : Dto(r),
                checklist = _checklist.Build(c.StationConfig, MemberName),
                printUrl = $"/kretsen/bana-stationer?c={competitionId}&print=1"
            });
        }

        /// <summary>Arrangören skickar in banan till kretsen — eller registrerar att underlaget skickats till Förbundet.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(int competitionId, string? note)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var me = await CurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Logga in först." });
            var c = Context(competitionId);
            if (c == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            if (!c.Offered) return Json(new { success = false, message = "Bangranskning erbjuds för fälttävlingar på kretsnivå och högre." });
            if (c.RegionId <= 0) return Json(new { success = false, message = "Tävlingens krets går inte att avgöra. Kontrollera arrangör och krets på tävlingen." });
            if (!c.HasStations) return Json(new { success = false, message = "Tävlingen har ingen stationsbeskrivning än. Anslut en konfiguration under Fältskytte-inställningar först." });

            var kind = _access.ReviewerKind(c.RegionId);
            var (r, err) = _reviews.Submit(competitionId, c.RegionId, c.Route, c.StationConfig!, c.Required, kind, Clip(note, 2000), me.Id, DisplayName(me));
            if (r == null) return Json(new { success = false, message = err });

            if (r.IsForbundet)
                return Json(new { success = true, message = "Registrerat att underlaget skickats till Förbundet. Skriv in Förbundets svar när det kommer." });

            var dateText = c.Date?.ToString("d MMMM yyyy", Sv) ?? "";
            var (sent, links, linkMode) = await _access.NotifyReviewersAsync(r, $"Bana att granska: {c.Name}", new[]
            {
                $"{c.Organiser} har skickat in banan för {c.Name}{(dateText.Length > 0 ? $" ({dateText})" : "")} till kretsens granskning: målförutsättningar och stationsbeskrivningar.",
                c.Required ? "Granskningen krävs för tävlingen." : "Arrangören har skickat in banan frivilligt. En godkänd bana markeras som granskad av kretsen.",
                string.IsNullOrWhiteSpace(r.ArrangerNote) ? "" : $"Meddelande från arrangören: {r.ArrangerNote}",
                "Stationerna är hemliga för skyttar. Är du själv anmäld i tävlingen bör en annan granskare gå igenom banan."
            }, BaseUrl, _replyTo.ForPersonInClub(me.Id, c.ClubId));
            return Json(new
            {
                success = true,
                message = sent > 0 ? "Banan är inskickad, och kretsen har fått besked." : "Banan är inskickad, men mejlet till kretsen kunde inte skickas. Meddela kretsen på annat sätt.",
                linkMode,
                caseLinks = links.Count > 0 && await _auth.IsCurrentUserAdminAsync() ? links : null
            });
        }

        /// <summary>SM/LDM: arrangören registrerar Förbundets svar ("1" = godkänt).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterForbundetAnswer(int competitionId, string? approved, string? text)
        {
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId)) return Json(new { success = false, message = "Åtkomst nekad" });
            var me = await CurrentMemberAsync();
            var c = Context(competitionId);
            if (c == null || me == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            var (r, err) = _reviews.RegisterForbundetAnswer(competitionId, IsExplicitlyTrue(approved), text, c.StationConfig ?? "", me.Id, DisplayName(me));
            return Json(r == null ? new { success = false, message = err } : new { success = true, message = r.IsApproved ? "Förbundets godkännande är registrerat." : "Förbundets begäran om ändringar är registrerad." });
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  Kretsens sida
        // ════════════════════════════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetRegionReviews(int regionId)
        {
            var me = await CurrentMemberAsync();
            var auth = await _access.AuthorityAsync(regionId, me?.Id ?? 0);
            if (!auth.Allowed) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });

            var reviews = SafeList(() => _reviews.ForRegion(regionId)).Where(r => !r.IsForbundet)
                .Select(r => new { r, c = Context(r.CompetitionId) }).ToList();
            var reviewed = reviews.Select(x => x.r.CompetitionId).ToHashSet();

            // Kommande fälttävlingar som erbjuds granskning men inte skickats in. Banan granskas FÖRE
            // tävlingen, så genomförda tävlingar är inte med.
            var upcoming = new List<object>();
            var region = _calendar.Region(regionId);
            if (region != null)
                foreach (var id in _calendar.CompetitionIdsInRegion(region.Code))
                {
                    if (reviewed.Contains(id)) continue;
                    var info = _calendar.CompetitionRegion(id)?.Competition;
                    if (info?.Date == null || info.Date.Value.Date < DateTime.Today) continue;
                    var c = Context(id);
                    if (c == null || (!c.Offered && !c.OfferBlockedByMissingLevel) || c.Route == CourseReviewRoute.Forbundet) continue;
                    upcoming.Add(new
                    {
                        competitionId = id, name = c.Name, date = c.Date?.ToString("yyyy-MM-dd"), organiser = c.Organiser,
                        required = c.Required,
                        deadline = c.Required ? c.Deadline?.ToString("yyyy-MM-dd") : null,
                        late = c.Required && c.Deadline != null && DateTime.Today > c.Deadline.Value,
                        hasStations = c.HasStations,
                        missingLevel = c.OfferBlockedByMissingLevel
                    });
                }

            var s = _access.Settings(regionId);
            var kind = CourseReviewerKind.Normalize(s?.CourseReviewer);
            return Json(new
            {
                success = true,
                role = auth.Role,
                settings = new
                {
                    requireCourseReview = s?.RequireCourseReview == true,
                    since = s?.RequireCourseReviewSince?.ToString("yyyy-MM-dd"),
                    reviewerKind = kind,
                    weeks = s?.FieldPrereqWeeks
                },
                canEditSettings = await CanEditSettingsAsync(regionId),
                reviewerLabel = CourseReviewerKind.Label(kind),
                reviewers = _uppdrag.CourseReviewers(regionId, kind).Select(h => h.Name).ToList(),
                bangranskare = _uppdrag.Holders(regionId, BoardRoleDefinitions.RoleBangranskare).Select(h => h.Name).ToList(),
                kretsinstruktorer = _uppdrag.Kretsinstruktorer(regionId).Select(h => h.Name).ToList(),
                reviews = reviews.OrderBy(x => x.r.Status == CourseReviewStatus.Inskickad ? 0 : 1).ThenBy(x => x.c?.Date)
                    .Select(x => new
                    {
                        review = Dto(x.r),
                        name = x.c?.Name ?? $"Tävling {x.r.CompetitionId}",
                        date = x.c?.Date?.ToString("yyyy-MM-dd"),
                        organiser = x.c?.Organiser ?? "",
                        changed = x.c != null && CourseReviewRules.Changed(x.r, CourseReviewRules.Checksum(x.c.StationConfig))
                    }),
                upcoming
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetReview(int id)
        {
            var r = _reviews.TablesExist() ? _reviews.Get(id) : null;
            if (r == null) return Json(new { success = false, message = "Granskningen hittades inte." });
            var me = await CurrentMemberAsync();
            if (!(await _access.AuthorityAsync(r.RegionId, me?.Id ?? 0)).Allowed) return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(CaseJson(r, me?.Id ?? 0, null));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int id, string? comment, string? customTargets)
        {
            var (r, who, deny) = await KretsActionAsync(id);
            if (deny != null) return deny;
            return Json(await ApproveCoreAsync(r!, who!, comment, customTargets));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Return(int id, string? comment)
        {
            var (r, who, deny) = await KretsActionAsync(id);
            if (deny != null) return deny;
            return Json(await ReturnCoreAsync(r!, who!, comment));
        }

        // ── Länkläget ───────────────────────────────────────────────────────────────────

        [HttpGet]
        public IActionResult GetByLink(string t)
        {
            var (r, link, error) = ReadCaseLink(t);
            if (r == null) return Json(new { success = false, message = error });
            return Json(CaseJson(r, 0, t));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActByLink(string t, string? action, string? comment, string? name, string? role, string? customTargets)
        {
            var (r, link, error) = ReadCaseLink(t);
            if (r == null) return Json(new { success = false, message = error });
            name = Clip(name, 200); role = Clip(role, 200);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(role))
                return Json(new { success = false, message = "Skriv ditt namn och din roll i kretsen — de står med på beslutet." });
            var who = new KretsDecider(0, name!, role!, KretsChannel.Lank, link!.SentTo);
            return action switch
            {
                "approve" => Json(await ApproveCoreAsync(r, who, comment, customTargets)),
                "return" => Json(await ReturnCoreAsync(r, who, comment)),
                _ => Json(new { success = false, message = "Okänd åtgärd." })
            };
        }

        // ── Inställningar ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Kräv bangranskning för kretsens kretstävlingar, vem som granskar, och framförhållningen.
        /// Kravet kräver minst en granskare av det valda slaget och gäller tävlingar från i dag.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(int regionId, string? requireCourseReview, string? reviewerKind, string? weeks)
        {
            if (!await CanEditSettingsAsync(regionId)) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!_reviews.TablesExist()) return Json(new { success = false, message = NoTables });
            var me = await CurrentMemberAsync();
            var require = IsExplicitlyTrue(requireCourseReview);
            var kind = CourseReviewerKind.Normalize(reviewerKind);
            int? w = int.TryParse((weeks ?? "").Trim(), out var wv) && wv > 0 ? Math.Min(wv, 52) : null;
            if (require && _uppdrag.CourseReviewers(regionId, kind).Count == 0)
                return Json(new { success = false, message = kind == CourseReviewerKind.Kretsinstruktor
                    ? "Kretsen har inga kretsinstruktörer på pistol.nu. Välj bangranskaren, eller utse kretsinstruktörer först."
                    : "Utse en bangranskare först (Styrelsearbete → Kretsens uppdrag)." });

            var s = _access.Settings(regionId) ?? new RegionCalendarSettings { RegionId = regionId };
            var wasOn = s.RequireCourseReview;
            s.RequireCourseReview = require;
            if (require && !wasOn) s.RequireCourseReviewSince = DateTime.Today;
            if (!require) s.RequireCourseReviewSince = null;
            s.CourseReviewer = kind;
            s.FieldPrereqWeeks = w;
            _apps.SaveSettings(s, me?.Id ?? 0);
            return Json(new
            {
                success = true,
                message = require && !wasOn
                    ? $"Sparat. Från och med i dag ({DateTime.Today:yyyy-MM-dd}) kräver kretsen bangranskning för sina kretstävlingar i fält."
                    : "Sparat."
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetPendingCount(int regionId)
        {
            var me = await CurrentMemberAsync();
            if (!(await _access.AuthorityAsync(regionId, me?.Id ?? 0)).Allowed) return Json(new { success = false });
            var n = _reviews.TablesExist() ? SafeList(() => _reviews.ForRegion(regionId)).Count(r => CourseReviewRules.KretsCanAct(r)) : 0;
            return Json(new { success = true, count = n });
        }

        // ════════════════════════════════════════════════════════════════════════════════

        private async Task<object> ApproveCoreAsync(CompetitionCourseReview r, KretsDecider who, string? comment, string? customTargets)
        {
            if (who.MemberId > 0 && who.MemberId == r.SubmittedByMemberId && who.Role != SupportRole)
                return new { success = false, message = "Den som skickade in banan kan inte granska den. En annan granskare i kretsen behöver godkänna." };
            var c = Context(r.CompetitionId);
            var check = _checklist.Build(c?.StationConfig, MemberName);
            // Radbrytning som skiljetecken: ett målnamn kan innehålla ett kommatecken.
            var ok = (customTargets ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var competitor = who.MemberId > 0 && _access.IsRegistered(r.CompetitionId, who.MemberId);
            var (saved, err) = _reviews.Approve(r.Id, c?.StationConfig ?? "", check.CustomTargets, ok, Clip(comment, 4000), who, competitor);
            if (saved == null) return new { success = false, message = err };
            var n = await NotifyOrganiserAsync(saved, who, $"Banan är granskad: {c?.Name}", new[]
            {
                $"{_calendar.Region(r.RegionId)?.Name ?? "Kretsen"} har granskat och godkänt banan för {c?.Name} ({who.Name}, {who.Role}).",
                check.CustomTargets.Count > 0 ? "Mål utanför SHB:s förteckning som godkänts: " + string.Join(", ", check.CustomTargets) + ". Ange dem i inbjudan." : "",
                string.IsNullOrWhiteSpace(saved.Comment) ? "" : $"Kommentar: {saved.Comment}",
                "Ändras banan efter godkännandet behöver den skickas in igen."
            });
            return new { success = true, notified = n, message = "Banan är godkänd" + (n > 0 ? ", och arrangören har fått besked." : ". Mejlet till arrangören kunde inte skickas — meddela arrangören på annat sätt.") };
        }

        private async Task<object> ReturnCoreAsync(CompetitionCourseReview r, KretsDecider who, string? comment)
        {
            var (saved, err) = _reviews.Return(r.Id, comment, who);
            if (saved == null) return new { success = false, message = err };
            var c = Context(r.CompetitionId);
            var n = await NotifyOrganiserAsync(saved, who, $"Kretsen ber er ändra banan: {c?.Name}", new[]
            {
                $"{who.Name} ({who.Role}) har gått igenom banan för {c?.Name} och ber er ändra följande:",
                saved.Comment ?? "",
                "Ändra stationsbeskrivningen och skicka in banan igen från Stationer-fliken."
            });
            return new { success = true, notified = n, message = "Banan är återsänd" + (n > 0 ? ", och arrangören har fått besked." : ". Mejlet till arrangören kunde inte skickas — meddela arrangören på annat sätt.") };
        }

        private object CaseJson(CompetitionCourseReview r, int viewerId, string? linkToken)
        {
            var c = Context(r.CompetitionId);
            var changed = c != null && CourseReviewRules.Changed(r, CourseReviewRules.Checksum(c.StationConfig));
            return new
            {
                success = true,
                review = Dto(r),
                competition = c == null ? null : new
                {
                    id = c.Id, name = c.Name, date = c.Date?.ToString("yyyy-MM-dd"), place = c.Venue, organiser = c.Organiser,
                    level = CompetitionLevel.Find(c.Level)?.Label ?? "", scope = c.Scope, type = c.TypeLabel, required = c.Required,
                    stationsUrl = $"/kretsen/bana-stationer?c={c.Id}" + (linkToken != null ? $"&t={Uri.EscapeDataString(linkToken)}" : "")
                },
                region = _calendar.Region(r.RegionId) is { } reg ? new { reg.Id, reg.Name } : null,
                checklist = _checklist.Build(c?.StationConfig, MemberName),
                changedSinceSubmit = changed,
                canAct = CourseReviewRules.KretsCanAct(r) && !changed,
                viewerIsSubmitter = viewerId > 0 && r.SubmittedByMemberId == viewerId,
                viewerIsCompetitor = viewerId > 0 && _access.IsRegistered(r.CompetitionId, viewerId)
            };
        }

        private object Dto(CompetitionCourseReview r) => new
        {
            id = r.Id, competitionId = r.CompetitionId, route = r.Route, status = r.Status,
            statusLabel = CourseReviewStatus.Label(r.Status, r.IsForbundet), required = r.Required,
            reviewerKind = r.ReviewerKind, arrangerNote = r.ArrangerNote,
            submittedAt = r.SubmittedAt.ToString("yyyy-MM-dd HH:mm"), submittedBy = r.SubmittedByName,
            decidedAt = r.DecidedAt?.ToString("yyyy-MM-dd HH:mm"), decider = r.DeciderName, deciderRole = r.DeciderRole,
            deciderIsCompetitor = r.DeciderIsCompetitor, comment = r.Comment, channel = r.Channel,
            approvedCustomTargets = r.ApprovedCustomTargets,
            sentToForbundetAt = r.SentToForbundetAt?.ToString("yyyy-MM-dd"),
            events = SafeList(() => _reviews.Events(r.Id)).Select(e => new { at = e.At.ToString("yyyy-MM-dd HH:mm"), by = e.ByName, kind = e.Kind, label = EventLabel(e.Kind), text = e.Text })
        };

        private async Task<int> NotifyOrganiserAsync(CompetitionCourseReview r, KretsDecider who, string subject, IEnumerable<string> paras)
        {
            try
            {
                var to = new List<(string Email, string Name)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string? e, string n) { if (!string.IsNullOrWhiteSpace(e) && seen.Add(e.Trim())) to.Add((e.Trim(), n)); }
                var submitter = r.SubmittedByMemberId > 0 ? _memberService.GetById(r.SubmittedByMemberId) : null;
                if (submitter != null) Add(submitter.Email, DisplayName(submitter));
                var comp = _contentService.GetById(r.CompetitionId);
                Add(comp?.GetValue<string>("contactEmail"), comp?.GetValue<string>("contactPerson") ?? "");
                var replyTo = who.MemberId > 0 ? _replyTo.ForMember(who.MemberId) : _replyTo.ForRegion(r.RegionId);
                var url = $"{BaseUrl}/competitionmanagement?competitionId={r.CompetitionId}";
                int sent = 0;
                foreach (var (email, name) in to)
                    if (await _email.SendKretsgranskningAsync(email, name, subject, paras, "Öppna Stationer-fliken", url, $"Beslutet fattades av {who.Name} ({who.Role}).", replyTo))
                        sent++;
                return sent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bangranskning {Id}: arrangören kunde inte aviseras.", r.Id);
                return 0;
            }
        }

        // ── Tävlingens sammanhang ─────────────────────────────────────────────────────────

        /// <summary>
        /// Regeln bor i <see cref="CourseReviewAccess.Resolve"/> (påminnelserna räknar likadant).
        /// Här läses bara omfattningen ur den publicerade cachen, som tål FlexibleDropdown-formen.
        /// </summary>
        private CourseReviewCompetition? Context(int competitionId)
        {
            string? scope = null;
            try
            {
                var published = UmbracoContext.Content?.GetById(competitionId);
                if (published != null) scope = CompetitionScopeHelper.ReadScope(published);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Bangranskning: omfattningen kunde inte läsas för {Id}.", competitionId); }
            return _access.Resolve(competitionId, scope);
        }

        // ── Behörighet och länk ────────────────────────────────────────────────────────

        private const string SupportRole = "Sajtadministratör (support)";

        private async Task<bool> CanEditSettingsAsync(int regionId)
        {
            var region = _calendar.Region(regionId);
            return region != null && (await _auth.IsCurrentUserAdminAsync() || await _auth.IsRegionalAdminForRegion(region.Code));
        }

        private async Task<(CompetitionCourseReview? R, KretsDecider? Who, IActionResult? Deny)> KretsActionAsync(int id)
        {
            var r = _reviews.TablesExist() ? _reviews.Get(id) : null;
            if (r == null) return (null, null, Json(new { success = false, message = "Granskningen hittades inte." }));
            var me = await CurrentMemberAsync();
            var auth = await _access.AuthorityAsync(r.RegionId, me?.Id ?? 0);
            if (!auth.Allowed) return (null, null, Json(new { success = false, message = "Åtkomst nekad" }));
            return (r, new KretsDecider(me!.Id, DisplayName(me), auth.Role, KretsChannel.Inloggad), null);
        }

        private (CompetitionCourseReview? R, CaseLinkPayload? Link, string? Error) ReadCaseLink(string? t)
        {
            var p = _access.ReadLink(t);
            if (p == null) return (null, null, "Länken gäller inte längre. Den kan ha gått ut (den gäller i 60 dagar).");
            var r = _reviews.TablesExist() ? _reviews.Get(p.CaseId) : null;
            if (r == null || r.RegionId != p.RegionId) return (null, null, "Granskningen finns inte längre.");
            if (r.Checksum != p.Checksum)
                return (null, null, "Banan har ändrats sedan länken skickades, så länken gäller inte längre. Kretsen får en ny länk när banan skickas in igen.");
            var c = Context(r.CompetitionId);
            if (c != null && CourseReviewRules.Changed(r, CourseReviewRules.Checksum(c.StationConfig)))
                return (null, null, "Banan har ändrats sedan den skickades in, så länken gäller inte längre. Arrangören behöver skicka in den igen.");
            return (r, p, null);
        }

        // ════════════════════════════════════════════════════════════════════════════════

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");
        private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

        private string? MemberName(int id)
        {
            try { var m = _memberService.GetById(id); return m == null ? null : DisplayName(m); } catch { return null; }
        }

        private List<T> SafeList<T>(Func<List<T>> f)
        {
            try { return f(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Bangranskning: läsning misslyckades."); return new(); }
        }

        private static bool IsExplicitlyTrue(string? v) =>
            v != null && (v.Trim() == "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("on", StringComparison.OrdinalIgnoreCase));

        private static string EventLabel(string k) => k switch
        {
            CourseReviewEventKind.Submitted => "Inskickad",
            CourseReviewEventKind.Resubmitted => "Inskickad igen",
            CourseReviewEventKind.Approved => "Godkänd",
            CourseReviewEventKind.Returned => "Återsänd",
            CourseReviewEventKind.SentToForbundet => "Skickad till Förbundet",
            CourseReviewEventKind.ForbundetAnswer => "Förbundets svar",
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
