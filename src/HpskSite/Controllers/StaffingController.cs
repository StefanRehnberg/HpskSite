using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services;
using HpskSite.Services.Notifications;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Globalization;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
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
    /// Backend for the Tävlingsplanering (competition planning) workspace on /competitionmanagement's
    /// Planering tab: the day-of functionary roster (Bemanning) + the preparation work-breakdown
    /// (Förberedelser). Auth is the same four-tier competition-staff gate used by the results/messaging
    /// controllers (site admin / competition manager / club admin / skjutledare, + regional admin for
    /// region-hosted comps). See Documentation/COMPETITION_STAFFING_SYSTEM.md.
    /// </summary>
    public class StaffingController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _auth;
        private readonly ClubService _clubService;
        private readonly StaffingService _staffing;
        private readonly CompetitionPeopleService _people;
        private readonly WorkBreakdownService _work;
        private readonly StaffingTemplateService _templates;
        private readonly MaterielEstimateService _materiel;
        private readonly StaffingSignupService _signup;
        private readonly StaffRequestService _request;
        private readonly StaffHelpService _help;
        private readonly StaffPassService _pass;
        private readonly RoleCatalogService _roleCatalog;
        private readonly PrepDocumentStorage _docs;
        private readonly HpskSite.Services.Schedule.CompetitionAgendaService _agenda;
        private readonly EmailService _email;
        private readonly WebPushService _webPush;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly ILogger<StaffingController> _logger;

        public StaffingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            ClubService clubService,
            StaffingService staffing,
            CompetitionPeopleService people,
            WorkBreakdownService work,
            StaffingTemplateService templates,
            MaterielEstimateService materiel,
            StaffingSignupService signup,
            StaffRequestService request,
            StaffHelpService help,
            StaffPassService pass,
            RoleCatalogService roleCatalog,
            PrepDocumentStorage docs,
            HpskSite.Services.Schedule.CompetitionAgendaService agenda,
            EmailService email,
            WebPushService webPush,
            IDataProtectionProvider dataProtection,
            ILogger<StaffingController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _auth = auth;
            _clubService = clubService;
            _staffing = staffing;
            _people = people;
            _work = work;
            _templates = templates;
            _materiel = materiel;
            _signup = signup;
            _request = request;
            _help = help;
            _pass = pass;
            _roleCatalog = roleCatalog;
            _docs = docs;
            _agenda = agenda;
            _email = email;
            _webPush = webPush;
            _dataProtection = dataProtection;
            _logger = logger;
        }

        // ======================= Bemanning (roster) =======================

        [HttpGet]
        public async Task<IActionResult> GetRoles(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = GetDiscipline(competitionId);
            var roles = _roleCatalog.ForCompetition(competitionId, discipline).Select(r => new
            {
                key = r.Key,
                name = r.DisplayName,
                plural = r.Plural,
                defaultScopeType = r.DefaultScopeType,
                supportsTargetRange = r.SupportsTargetRange,
                supportsFunctionTitle = r.SupportsFunctionTitle,
                description = r.Description,
                needs = r.Needs,
                isCustom = _roleCatalog.IsCustom(competitionId, r.Key),
            });
            return Json(new { success = true, discipline, roles });
        }

        /// <summary>
        /// Create a role from a typed name, or rename an existing one (including a built-in). Free naming
        /// is the point: a club that calls it "Vapenkontroll" or "Starter" must not be forced onto our word
        /// — being pushed onto a word they already use for a DIFFERENT job makes the data actively wrong.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRole([FromBody] SaveStaffRoleRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var key = _roleCatalog.SaveRole(request, GetDiscipline(request.CompetitionId), viewer.Id);
                return Json(new { success = true, roleKey = key });
            }
            catch (ArgumentException ax)
            {
                return Json(new { success = false, message = ax.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SaveRole failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara rollen." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRole([FromBody] DeleteStaffRoleRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var (ok, msg) = _roleCatalog.DeleteRole(request.CompetitionId, request.RoleKey, GetDiscipline(request.CompetitionId));
                return Json(new { success = ok, message = msg });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: DeleteRole failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte ta bort rollen." });
            }
        }

        /// <summary>
        /// The Bemanning grid: roles as rows, competition DAYS as columns, people in the cells.
        /// <para>Day — not pass — is the column axis on purpose. Three days × five passes is fifteen columns
        /// that never fit, and five identical cells for a whole-day person read as five people. The arrangör
        /// who inspired this had the option in Excel and chose day columns with the time written in the cell;
        /// so a person with no shift renders as a bare name, and a time chip appears only where one exists.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetGrid(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var discipline = GetDiscipline(competitionId);
                var resp = BuildGrid(competitionId, discipline);
                return Json(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetGrid failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Kunde inte läsa rutnätet." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetRoster(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = GetDiscipline(competitionId);
            var roster = _staffing.BuildRoster(competitionId, discipline, canEdit: true);
            return Json(new
            {
                success = true,
                discipline = roster.Discipline,
                canEdit = roster.CanEdit,
                totalAssigned = roster.TotalAssigned,
                groups = roster.Groups,
            });
        }

        /// <summary>
        /// THE PEOPLE VIEW: everyone connected to this competition, once each, with their roles, their
        /// sign-up, their availability and their prep ownership on the same row. This is the endpoint that
        /// makes the planning surfaces stop contradicting each other — see CompetitionPeopleService.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPeople(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var p = _people.Build(competitionId, GetDiscipline(competitionId), canEdit: true);
            return Json(new
            {
                success = true,
                canEdit = p.CanEdit,
                discipline = p.Discipline,
                totalPeople = p.TotalPeople,
                assignedCount = p.AssignedCount,
                unassignedVolunteerCount = p.UnassignedVolunteerCount,
                needsResponseCount = p.NeedsResponseCount,
                declinedCount = p.DeclinedCount,
                externalCount = p.ExternalCount,
                leadership = p.Leadership,
                people = p.People,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAssignment([FromBody] SaveStaffAssignmentRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Roll saknas" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            // The role must exist in this competition's merged catalog (built-ins + arrangör-named rows).
            // It is no longer a closed set — "Vapenkontroll" is as valid as "Startledare" once someone has
            // created it — but it must still be a NAMED role, so everything downstream can group and count.
            var discipline = GetDiscipline(request.CompetitionId);
            var role = _roleCatalog.Resolve(request.CompetitionId, discipline, request.RoleKey);
            if (role == null)
                return Json(new { success = false, message = "Rollen finns inte. Skapa den först i rutnätet." });

            // Resolve/normalise the person: a member id autofills name (+ phone if not supplied).
            if (request.MemberId is > 0)
            {
                var m = _memberService.GetById(request.MemberId.Value);
                if (m != null)
                {
                    if (string.IsNullOrWhiteSpace(request.DisplayName))
                        request.DisplayName = MemberDisplayName(request.MemberId.Value) ?? m.Name;
                    if (string.IsNullOrWhiteSpace(request.Phone) && m.HasProperty("phoneNumber"))
                        request.Phone = m.GetValue<string>("phoneNumber");
                }
            }
            if (string.IsNullOrWhiteSpace(request.DisplayName))
                return Json(new { success = false, message = "Ange en person (medlem eller namn)." });

            // Same person, same role, same scope, same pass = a duplicate the organiser almost never wants.
            // Refuse unless they explicitly confirmed it (the dialog re-posts with AllowDuplicate) — the old
            // behaviour silently created a second identical row, which is how one person ended up listed
            // twice on the same skjutlag.
            if (!request.AllowDuplicate)
            {
                var dup = _people.FindDuplicate(request.CompetitionId, discipline, request.MemberId,
                    request.DisplayName, request.RoleKey, request.FunctionTitle,
                    request.ScopeType, request.ScopeKey, request.PassId, request.Id);
                if (dup != null)
                    return Json(new
                    {
                        success = false,
                        duplicate = true,
                        message = $"{request.DisplayName} har redan uppdraget \"{dup.Label}\". Lägg till ändå?",
                    });
            }

            try
            {
                var id = _staffing.Save(request, viewer.Id);
                return Json(new { success = true, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SaveAssignment failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAssignment([FromBody] DeleteStaffAssignmentRequest request)
        {
            if (request == null || request.Id <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });

            var compId = _staffing.GetCompetitionIdFor(request.Id) ?? request.CompetitionId;
            if (!await HasCompetitionAccessAsync(compId))
                return Json(new { success = false, message = "Ingen behörighet" });

            _staffing.Delete(request.Id, compId);
            return Json(new { success = true });
        }

        // ======================= Dagen: upprop + planerad-vs-aktiv (day-of cockpit) =======================

        [HttpGet]
        public async Task<IActionResult> GetDayOfCockpit(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var c = _staffing.BuildDayOfCockpit(competitionId, GetDiscipline(competitionId), canEdit: true);
            return Json(new
            {
                success = true,
                canEdit = c.CanEdit,
                discipline = c.Discipline,
                totalPlanned = c.TotalPlanned,
                totalCheckedIn = c.TotalCheckedIn,
                groups = c.Groups,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCheckedIn([FromBody] CheckInRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var compId = _staffing.GetCompetitionIdFor(request.Id) ?? request.CompetitionId;
            if (!await HasCompetitionAccessAsync(compId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _staffing.SetCheckedIn(request.Id, compId, request.CheckedIn);
            return Json(new { success = true });
        }

        // ======================= Förberedelser (work-breakdown) =======================

        [HttpGet]
        public async Task<IActionResult> GetWorkBreakdown(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var wb = _work.Build(competitionId, canEdit: true);

            // Enrich with competition context (date anchoring + Fält station-seed availability).
            var discipline = GetDiscipline(competitionId);
            var (compDate, daysUntil) = GetCompDate(competitionId);
            wb.Discipline = discipline;
            wb.CompDate = compDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            wb.DaysUntilComp = daysUntil;
            wb.StationSeed = GetStationSeed(competitionId, discipline);

            // Tävlingsledning READ-THROUGH. The roster owns who leads the competition (those rows are what
            // mirror into competitionManagers). Förberedelser used to carry a *second*, unconnected copy of
            // the same fact — a "Tävlingsledning" område whose "Utse tävlingsledare"-task sat unassigned
            // even after a Tävlingsledare had been appointed in Bemanning. It now reads the roster instead
            // of owning anything, so there is one answer to "who leads this competition".
            var leadership = new List<CompetitionPersonAssignment>();
            try { leadership = _people.Build(competitionId, discipline, canEdit: false, includePrep: false).Leadership; }
            catch (Exception ex) { _logger.LogWarning(ex, "Prep: leadership read-through failed for {CompetitionId}", competitionId); }

            return Json(new
            {
                success = true,
                canEdit = wb.CanEdit,
                discipline = wb.Discipline,
                compDate = wb.CompDate,
                daysUntilComp = wb.DaysUntilComp,
                stationSeed = wb.StationSeed,
                totalEstimatedCost = wb.TotalEstimatedCost,
                totalActualCost = wb.TotalActualCost,
                compLinks = wb.CompLinks,
                areas = wb.Areas,
                leadership,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkArea([FromBody] SaveWorkAreaRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.Name))
                return Json(new { success = false, message = "Ange ett namn på området." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            if (request.ResponsibleMemberId is > 0 && string.IsNullOrWhiteSpace(request.ResponsibleName))
                request.ResponsibleName = MemberDisplayName(request.ResponsibleMemberId.Value);

            var id = _work.SaveArea(request, viewer.Id);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkArea([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteArea(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkItem([FromBody] SaveWorkItemRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.WorkAreaId <= 0 || string.IsNullOrWhiteSpace(request.Title))
                return Json(new { success = false, message = "Ange en uppgift." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            if (request.AssignedMemberId is > 0 && string.IsNullOrWhiteSpace(request.AssignedName))
                request.AssignedName = MemberDisplayName(request.AssignedMemberId.Value);

            var id = _work.SaveItem(request, viewer.Id, viewer.Name);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkItem([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteItem(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetWorkItemStatus([FromBody] SaveWorkItemRequest request)
        {
            if (request == null || request.Id <= 0 || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.SetItemStatus(request.Id, request.CompetitionId, request.Status ?? WorkItemStatus.Planerad, viewer.Id, viewer.Name);
            return Json(new { success = true });
        }

        // ======================= Kommentarer / logg + beroenden (uppgift-tråd) =======================

        [HttpGet]
        public async Task<IActionResult> GetWorkItemThread(int competitionId, int workItemId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var t = _work.GetThread(competitionId, workItemId, canEdit: true);
            return Json(new
            {
                success = t.Success,
                message = t.Message,
                canEdit = t.CanEdit,
                title = t.Title,
                comments = t.Comments,
                blockedBy = t.BlockedBy,
                candidates = t.Candidates,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWorkItemComment([FromBody] AddWorkItemCommentRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.WorkItemId <= 0 || string.IsNullOrWhiteSpace(request.Body))
                return Json(new { success = false, message = "Skriv en kommentar." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var body = request.Body.Trim();
            if (body.Length > 4000) body = body.Substring(0, 4000);
            var id = _work.AddComment(request.CompetitionId, request.WorkItemId, body, viewer.Id, viewer.Name);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkItemComment([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteComment(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWorkItemDependency([FromBody] WorkItemDependencyRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var (ok, msg) = _work.AddDependency(request.CompetitionId, request.WorkItemId, request.BlockedByItemId, viewer.Id);
            return Json(new { success = ok, message = msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveWorkItemDependency([FromBody] WorkItemDependencyRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.RemoveDependency(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedPrepTemplate([FromBody] SeedPrepTemplateRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var discipline = GetDiscipline(request.CompetitionId);
            var (compDate, _) = GetCompDate(request.CompetitionId);
            var added = request.TemplateId > 0
                ? _templates.SeedFromTemplate(request.CompetitionId, request.TemplateId, compDate, viewer.Id)
                : _work.SeedTemplate(request.CompetitionId, request.Size, discipline, compDate, viewer.Id);
            return Json(new { success = true, added });
        }

        /// <summary>Discipline-aware materiel-quantity estimate from participant/class/series counts.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMaterielEstimate(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var est = _materiel.Estimate(competitionId);
            return Json(new
            {
                success = est.Success,
                message = est.Message,
                discipline = est.Discipline,
                participantCount = est.ParticipantCount,
                startCount = est.StartCount,
                classCount = est.ClassCount,
                series = est.Series,
                rows = est.Rows,
            });
        }

        // ---- Big-comp staffing: passes + crew needs + coverage matrix ----

        [HttpGet]
        public async Task<IActionResult> GetPasses(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, passes = _pass.GetPasses(competitionId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePass([FromBody] SavePassRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.Date))
                return Json(new { success = false, message = "Ange ett datum för passet." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var id = _pass.SavePass(request, viewer.Id);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePass([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _pass.DeletePass(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetCrewNeeds(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, needs = _pass.GetCrewNeeds(competitionId, GetDiscipline(competitionId)) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCrewNeeds([FromBody] SaveCrewNeedsRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _pass.SaveCrewNeeds(request.CompetitionId, request.Needs);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyPassAssignments([FromBody] CopyPassRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.FromPassId <= 0 || request.ToPassId <= 0)
                return Json(new { success = false, message = "Välj pass att kopiera från och till." });
            if (request.FromPassId == request.ToPassId)
                return Json(new { success = false, message = "Välj två olika pass." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var copied = _staffing.CopyPassAssignments(request.CompetitionId, request.FromPassId, request.ToPassId, viewer.Id);
            return Json(new { success = true, copied });
        }

        [HttpGet]
        public async Task<IActionResult> GetCoverage(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var discipline = GetDiscipline(competitionId);
            var seed = GetStationSeed(competitionId, discipline);
            var cov = _pass.BuildCoverage(competitionId, discipline, seed?.StationCount ?? 0);
            return Json(new
            {
                success = true,
                discipline = cov.Discipline,
                stationCount = cov.StationCount,
                hasNeeds = cov.HasNeeds,
                totalNeeded = cov.TotalNeeded,
                totalFilled = cov.TotalFilled,
                passes = cov.Passes,
            });
        }

        // ---- Self-sign-up rework: help-slots (organiser config) + review ----

        [HttpGet]
        public async Task<IActionResult> GetHelpSlots(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, slots = _help.GetSlots(competitionId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveHelpSlot([FromBody] SaveHelpSlotRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.Headline) || string.IsNullOrWhiteSpace(request.Date))
                return Json(new { success = false, message = "Ange datum och rubrik." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var id = _help.SaveSlot(request, viewer.Id);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHelpSlot([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _help.DeleteSlot(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        /// <summary>
        /// The volunteers, each carrying what they have ALREADY been given in the roster. Without the
        /// assignment join a volunteer stayed visually "unassigned" forever, so the organiser's queue never
        /// emptied and the same person got offered up for assignment again and again.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetHelpSignups(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var people = _people.Build(competitionId, GetDiscipline(competitionId), canEdit: true, includePrep: false);
            var byKey = people.People.ToDictionary(p => p.Key, p => p, StringComparer.Ordinal);

            var signups = _help.GetReview(competitionId).Select(v =>
            {
                byKey.TryGetValue(CompetitionPeopleService.KeyFor(v.MemberId, v.MemberName), out var person);
                return new
                {
                    memberId = v.MemberId,
                    memberName = v.MemberName,
                    comment = v.Comment,
                    updated = v.Updated,
                    slots = v.Slots,
                    // --- the join that was missing ---
                    isAssigned = person != null && person.Assignments.Count > 0,
                    assignmentCount = person?.Assignments.Count ?? 0,
                    assignments = person?.Assignments ?? new List<CompetitionPersonAssignment>(),
                    roleSummary = person == null ? "" : string.Join(" · ", person.RoleLabels),
                    state = person?.State ?? PersonState.Anmald,
                };
            }).ToList();

            return Json(new
            {
                success = true,
                signups,
                unassignedCount = signups.Count(s => !s.isAssigned),
            });
        }

        // ---- Phase 3: sourcing scope (organiser opens the comp for self-sign-up) ----

        [HttpGet]
        public async Task<IActionResult> GetSourceScopes(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, scopes = _signup.GetScopes(competitionId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSourceScope([FromBody] SaveSourceScopeRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var (ok, msg, id) = _signup.AddScope(request.CompetitionId, request.ScopeType, request.ScopeKey, viewer.Id);
            return Json(new { success = ok, message = msg, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSourceScope([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _signup.RemoveScope(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> PreviewStaffRequest(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var p = _request.Preview(competitionId);
            return Json(new
            {
                success = true,
                hasScopes = p.HasScopes,
                relayCount = p.RelayCount,
                directCount = p.DirectCount,
                directAvailable = p.DirectAvailable,
                audienceLabels = p.AudienceLabels,
                lastSent = p.LastSent,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendStaffRequest([FromBody] SendStaffRequestRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var (ok, msg, sent, push, recipients) = _request.Send(request.CompetitionId, request.Mode, request.Message, viewer.Id);
            return Json(new { success = ok, message = msg, sent, push, recipients });
        }

        // ---- Phase 1.5: editable per-club/region planning templates ----

        [HttpGet]
        public async Task<IActionResult> ListTemplates(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var templates = _templates.GetForCompetition(competitionId, GetDiscipline(competitionId), canManage: true);
            return Json(new { success = true, templates });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAsTemplate([FromBody] SaveAsTemplateRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.Name))
                return Json(new { success = false, message = "Ange ett namn på mallen." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var id = _templates.SaveSnapshot(request.CompetitionId, request.Name, request.OwnerType, viewer.Id);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTemplate([FromBody] DeleteTemplateRequest request)
        {
            if (request == null || request.TemplateId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            // Only allow deleting a template that belongs to this competition's club/region.
            var owned = _templates.GetForCompetition(request.CompetitionId, GetDiscipline(request.CompetitionId), canManage: true)
                .Any(t => t.Id == request.TemplateId);
            if (!owned) return Json(new { success = false, message = "Mallen tillhör inte den här tävlingens klubb/krets." });
            _templates.Delete(request.TemplateId);
            return Json(new { success = true });
        }

        /// <summary>Auto-seed one "Bygg station N" uppgift per configured station (Fältskytte).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedStationTasks([FromBody] SeedStationTasksRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var seed = GetStationSeed(request.CompetitionId, GetDiscipline(request.CompetitionId));
            if (seed == null || !seed.Available)
                return Json(new { success = false, message = "Ingen stationskonfiguration hittades." });
            var added = _work.SeedStationTasks(request.CompetitionId, seed.StationCount, viewer.Id);
            return Json(new { success = true, added });
        }

        // ======================= Dokument & länkar (WorkLink) =======================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkLink([FromBody] SaveWorkLinkRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.Url)) return Json(new { success = false, message = "Ange en länk (URL)." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var url = request.Url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            var title = string.IsNullOrWhiteSpace(request.Title) ? url : request.Title.Trim();

            var id = _work.SaveLink(new WorkLink
            {
                CompetitionId = request.CompetitionId,
                WorkAreaId = request.WorkAreaId is > 0 ? request.WorkAreaId : null,
                WorkItemId = request.WorkItemId is > 0 ? request.WorkItemId : null,
                Title = title.Length > 200 ? title.Substring(0, 200) : title,
                Url = url,
                CreatedByMemberId = viewer.Id,
            });
            return Json(new { success = true, id });
        }

        [HttpPost]
        public async Task<IActionResult> UploadWorkDocument(int competitionId, int? workAreaId, int? workItemId, string? title, IFormFile? file)
        {
            if (competitionId <= 0 || file == null) return Json(new { success = false, message = "Ingen fil vald." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var (ok, err) = _docs.Validate(file.FileName, file.Length);
            if (!ok) return Json(new { success = false, message = err });

            string stored;
            using (var s = file.OpenReadStream()) stored = await _docs.SaveAsync(s, file.FileName);
            var display = string.IsNullOrWhiteSpace(title) ? file.FileName : title.Trim();

            var id = _work.SaveLink(new WorkLink
            {
                CompetitionId = competitionId,
                WorkAreaId = workAreaId is > 0 ? workAreaId : null,
                WorkItemId = workItemId is > 0 ? workItemId : null,
                Title = display.Length > 200 ? display.Substring(0, 200) : display,
                StoredFileName = stored,
                OriginalFileName = file.FileName,
                CreatedByMemberId = viewer.Id,
            });
            return Json(new { success = true, id });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadWorkDocument(int id)
        {
            var link = _work.GetLink(id);
            if (link == null || string.IsNullOrEmpty(link.StoredFileName)) return NotFound();
            if (!await HasCompetitionAccessAsync(link.CompetitionId)) return Forbid();
            var path = _docs.GetFilePath(link.StoredFileName);
            if (path == null) return NotFound();
            var download = string.IsNullOrEmpty(link.OriginalFileName) ? link.StoredFileName : link.OriginalFileName;
            return PhysicalFile(path, PrepDocumentStorage.ContentTypeFor(link.StoredFileName), download);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkLink([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var storedFile = _work.DeleteLink(request.Id, request.CompetitionId);
            if (!string.IsNullOrEmpty(storedFile)) _docs.Delete(storedFile);
            return Json(new { success = true });
        }

        // ======================= Notiser & påminnelser (P2) =======================

        /// <summary>Notify a roster person (member → e-post + push; extern hjälpare → e-post) about their
        /// role, and flip Planerad → Inbjuden. The Phase-2 invitation seam.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NotifyAssignment([FromBody] DeleteStaffAssignmentRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var a = _staffing.GetById(request.Id);
            if (a == null) return Json(new { success = false, message = "Funktionären hittades inte." });
            if (!await HasCompetitionAccessAsync(a.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            if (a.MemberId is not > 0 && string.IsNullOrWhiteSpace(a.Email))
                return Json(new { success = false, message = "Personen saknar konto och e-post — kan inte notifieras." });

            var meta = GetCompMeta(a.CompetitionId);
            var roleName = _roleCatalog.NameFor(a.CompetitionId, GetDiscipline(a.CompetitionId), a.RoleKey);
            var where = string.IsNullOrEmpty(a.ScopeType) || string.Equals(a.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase)
                ? "hela tävlingen" : $"{a.ScopeType} {a.ScopeKey}".Trim();
            var subject = $"Funktionärsförfrågan: {roleName} – {meta.Name}";
            var bodyText = $"Du är inplanerad som <strong>{System.Net.WebUtility.HtmlEncode(roleName)}</strong> på {System.Net.WebUtility.HtmlEncode(where)} under <strong>{System.Net.WebUtility.HtmlEncode(meta.Name)}</strong>. Tacka ja eller nej.";

            bool email = false; int push = 0;
            if (a.MemberId is > 0)
            {
                // Members act on the per-comp landing page (accept/decline + availability, in context).
                var beUrl = $"/bemanna?c={a.CompetitionId}";
                var html = NotifyEmailHtml(bodyText + " Ange gärna när du kan arbeta.", "Öppna bemanningssidan", beUrl, viewer.Name);
                (email, push) = await NotifyMemberAsync(a.MemberId.Value, subject, html, "Funktionärsförfrågan", $"{roleName} – {meta.Name}", beUrl);
            }
            else if (!string.IsNullOrWhiteSpace(a.Email))
            {
                // External (non-member) helper: a tokened accept/decline link, no login required.
                var token = StaffingInviteToken.Protect(_dataProtection, a.Id);
                var html = NotifyEmailHtml(bodyText, "Svara på förfrågan", $"/mina-uppdrag/svar?t={Uri.EscapeDataString(token)}", viewer.Name);
                try { email = await _email.SendHtmlEmailAsync(a.Email!, subject, html); } catch { }
            }

            if (!email && push == 0)
                return Json(new { success = false, message = "Kunde inte skicka — ingen e-post eller push-prenumeration." });

            // Only flip Planerad → Inbjuden once something actually reached the person.
            if (string.Equals(a.Status, StaffAssignmentStatus.Planned, StringComparison.OrdinalIgnoreCase))
                _staffing.SetStatus(a.Id, a.CompetitionId, StaffAssignmentStatus.Invited);

            return Json(new { success = true, email, push });
        }

        /// <summary>Return the tokened external accept/decline link for an assignment, so the organiser can
        /// send it manually (SMS etc.) to a non-member helper. Staff-gated.</summary>
        [HttpGet]
        public async Task<IActionResult> GetInviteLink(int competitionId, int id)
        {
            if (id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var a = _staffing.GetById(id);
            if (a == null) return Json(new { success = false, message = "Funktionären hittades inte." });
            if (!await HasCompetitionAccessAsync(a.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var token = StaffingInviteToken.Protect(_dataProtection, a.Id);
            var url = $"{Request.Scheme}://{Request.Host}/mina-uppdrag/svar?t={Uri.EscapeDataString(token)}";
            return Json(new { success = true, url, token });
        }

        /// <summary>Notify the member assigned to an uppgift about the task + deadline; log it to the thread.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NotifyWorkItem([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var it = _work.GetItem(request.Id);
            if (it == null) return Json(new { success = false, message = "Uppgiften hittades inte." });
            if (!await HasCompetitionAccessAsync(it.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            if (it.AssignedMemberId is not > 0)
                return Json(new { success = false, message = "Uppgiften har ingen tilldelad medlem att notifiera." });

            var meta = GetCompMeta(it.CompetitionId);
            var due = it.DueDate.HasValue ? it.DueDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
            var subject = $"Uppgift: {it.Title} – {meta.Name}";
            var body = $"Du är ansvarig för uppgiften <strong>{System.Net.WebUtility.HtmlEncode(it.Title)}</strong> inför <strong>{System.Net.WebUtility.HtmlEncode(meta.Name)}</strong>."
                + (due != null ? $" Senast: <strong>{due}</strong>." : "");
            var html = NotifyEmailHtml(body, "Öppna planeringen", meta.Url, viewer.Name);
            var (email, push) = await NotifyMemberAsync(it.AssignedMemberId.Value, subject, html, "Uppgift",
                it.Title + (due != null ? " · senast " + due : ""), meta.Url);

            _work.LogAudit(it.CompetitionId, it.Id, $"Påminnelse skickad till {it.AssignedName ?? "tilldelad medlem"}", viewer.Id, viewer.Name);
            if (!email && push == 0)
                return Json(new { success = false, message = "Kunde inte skicka — medlemmen saknar e-post och push-prenumeration." });
            return Json(new { success = true, email, push });
        }

        /// <summary>Nudge everyone with an overdue uppgift — one grouped reminder per member. Logs each item.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindOverdue([FromBody] SeedStationTasksRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var wb = _work.Build(request.CompetitionId, canEdit: true);
            var overdue = wb.Areas.SelectMany(a => a.Items).Where(i => i.IsOverdue && i.AssignedMemberId is > 0).ToList();
            if (overdue.Count == 0)
                return Json(new { success = true, notified = 0, items = 0, message = "Inga försenade uppgifter med tilldelad medlem." });

            var meta = GetCompMeta(request.CompetitionId);
            int notified = 0, sentItems = 0;
            foreach (var g in overdue.GroupBy(i => i.AssignedMemberId!.Value))
            {
                var list = g.ToList();
                var rows = string.Join("", list.Select(i =>
                    $"<li>{System.Net.WebUtility.HtmlEncode(i.Title)}{(i.DueDate != null ? $" — senast {System.Net.WebUtility.HtmlEncode(i.DueDate)}" : "")}</li>"));
                var html = NotifyEmailHtml(
                    $"Du har <strong>{list.Count}</strong> försenad(e) uppgift(er) inför <strong>{System.Net.WebUtility.HtmlEncode(meta.Name)}</strong>:<ul>{rows}</ul>",
                    "Öppna planeringen", meta.Url, viewer.Name);
                var (email, push) = await NotifyMemberAsync(g.Key, $"Påminnelse: försenade uppgifter – {meta.Name}", html,
                    "Försenade uppgifter", $"{list.Count} uppgift(er) på {meta.Name}", meta.Url);
                if (email || push > 0) notified++;
                foreach (var i in list) { _work.LogAudit(request.CompetitionId, i.Id, "Påminnelse skickad (försenad)", viewer.Id, viewer.Name); sentItems++; }
            }
            return Json(new { success = true, notified, items = sentItems });
        }

        // ======================= Shared: member picker =======================

        /// <summary>
        /// The competition's OWN crew, for the "föreslagna" block that now heads every member picker here.
        /// Prep owners used to be picked out of a site-wide member search with no reference to the roster,
        /// which is how a person could own half the preparation without ever appearing in the crew list.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCrewCandidates(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var candidates = _people.Candidates(competitionId, GetDiscipline(competitionId)).Select(p => new
            {
                memberId = p.MemberId,
                memberName = p.DisplayName,
                clubName = p.ClubName,
                phone = p.Phone,
                roleSummary = string.Join(" · ", p.RoleLabels),
                state = p.State,
                prepOpenCount = p.PrepOpenCount,
            });
            return Json(new { success = true, members = candidates });
        }

        [HttpGet]
        public async Task<IActionResult> SearchMembers(int competitionId, string query)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return Json(new { success = true, members = new List<object>() });

            var all = _memberService.GetAll(0, int.MaxValue, out _);
            var matches = all
                .Where(m => m.IsApproved
                    && ((m.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (m.Email ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Take(20)
                .Select(m =>
                {
                    string? clubName = null;
                    var pcid = m.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(pcid) && int.TryParse(pcid, out int clubId))
                        clubName = _clubService.GetClubNameById(clubId);
                    var first = m.GetValue<string>("firstName");
                    var last = m.GetValue<string>("lastName");
                    var displayName = string.IsNullOrWhiteSpace($"{first} {last}".Trim()) ? m.Name : $"{first} {last}".Trim();
                    var phone = m.HasProperty("phoneNumber") ? (m.GetValue<string>("phoneNumber") ?? "") : "";
                    return new { memberId = m.Id, memberName = displayName, clubName, phone };
                })
                .ToList();
            return Json(new { success = true, members = matches });
        }

        // ======================= helpers =======================

        private record Viewer(int Id, string Name);

        private async Task<Viewer?> ResolveViewerAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            var md = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (md == null) return null;
            var name = MemberDisplayName(md.Id) ?? md.Name ?? "";
            return new Viewer(md.Id, name);
        }

        private string? MemberDisplayName(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m == null) return null;
            var first = m.GetValue<string>("firstName") ?? "";
            var last = m.GetValue<string>("lastName") ?? "";
            var name = $"{first} {last}".Trim();
            return string.IsNullOrEmpty(name) ? m.Name : name;
        }

        private string GetDiscipline(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    return comp?.Value("competitionType")?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>Competition display name + the planning page URL, for notifications.</summary>
        private (string Name, string Url) GetCompMeta(int competitionId)
        {
            var name = "tävlingen";
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    var n = comp?.Value<string>("competitionName");
                    if (!string.IsNullOrWhiteSpace(n)) name = n!;
                }
            }
            catch { }
            return (name, $"/tavlingsplanering?c={competitionId}");
        }

        /// <summary>Send an in-app web-push + e-mail to a member (best-effort). Returns which channels landed.</summary>
        private async Task<(bool Email, int Push)> NotifyMemberAsync(int memberId, string subject, string emailHtml, string pushTitle, string pushBody, string url)
        {
            bool email = false; int push = 0;
            try
            {
                var m = _memberService.GetById(memberId);
                var addr = m?.Email;
                if (!string.IsNullOrWhiteSpace(addr))
                    email = await _email.SendHtmlEmailAsync(addr!, subject, emailHtml);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: notify e-mail failed for member {MemberId}", memberId); }
            try { push = await _webPush.SendToMemberAsync(memberId, pushTitle, pushBody, url, $"planering-{memberId}"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: notify push failed for member {MemberId}", memberId); }
            return (email, push);
        }

        private static string NotifyEmailHtml(string bodyHtml, string ctaLabel, string ctaUrl, string senderName)
        {
            var abs = ctaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? ctaUrl : $"https://pistol.nu{ctaUrl}";
            return $@"<div style='font-family:Arial,sans-serif;max-width:560px;margin:0 auto;color:#222'>
<p>Hej!</p>
<p>{bodyHtml}</p>
<p style='margin:24px 0'><a href='{abs}' style='background:#0d6efd;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none'>{System.Net.WebUtility.HtmlEncode(ctaLabel)}</a></p>
<p style='color:#666;font-size:13px'>Skickat av {System.Net.WebUtility.HtmlEncode(senderName)} via pistol.nu tävlingsplanering.</p>
</div>";
        }

        /// <summary>Competition date + whole days until it (negative once passed), for deadline anchoring.</summary>
        private (DateTime? Date, int? DaysUntil) GetCompDate(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    var d = comp?.Value<DateTime?>("competitionDate");
                    if (d.HasValue && d.Value != default)
                        return (d.Value, (int)(d.Value.Date - DateTime.Now.Date).TotalDays);
                }
            }
            catch { }
            return (null, null);
        }

        /// <summary>
        /// For a Fältskytte/MagnumFält comp, whether we can auto-seed station-build tasks: station count
        /// (numberOfSeriesOrStations) + the attached Fältkonfigurator id (from the stationConfig blob meta).
        /// </summary>
        private StationSeedInfo? GetStationSeed(int competitionId, string discipline)
        {
            if (discipline is not ("Faltskytte" or "MagnumFalt")) return null;
            try
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return null;

                var stationCount = comp.Value<int>("numberOfSeriesOrStations");
                int attachedConfigId = 0;
                var cfgJson = comp.HasProperty("stationConfig") ? comp.Value<string>("stationConfig") : null;
                if (!string.IsNullOrWhiteSpace(cfgJson))
                {
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(cfgJson);
                        attachedConfigId = jo.Value<int?>("_attachedConfigId") ?? 0;
                    }
                    catch { /* legacy/non-object blob — leave 0 */ }
                }
                return new StationSeedInfo
                {
                    Available = stationCount > 0,
                    StationCount = stationCount,
                    AttachedConfigId = attachedConfigId,
                };
            }
            catch { return null; }
        }

        // ======================= Dagsprogram (day programme) =======================

        [HttpGet]
        public async Task<IActionResult> GetAgenda(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var rows = _agenda.GetForCompetition(competitionId);
            return Json(new
            {
                success = true,
                items = rows.Select(r => new
                {
                    id = r.Id,
                    itemDate = r.ItemDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    startTime = r.StartTime,
                    endTime = r.EndTime,
                    title = r.Title,
                    location = r.Location,
                    note = r.Note,
                    audience = r.Audience,
                    audienceLabel = AgendaAudience.Label(r.Audience),
                    icon = r.Icon,
                }),
                competitionDate = GetCompetitionDate(competitionId)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAgendaItem([FromBody] SaveAgendaItemRequest request)
        {
            if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var (ok, msg, id) = _agenda.Save(request, (await ResolveViewerAsync())?.Id ?? 0);
            return Json(new { success = ok, message = msg, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAgendaItem([FromBody] DeleteAgendaItemRequest request)
        {
            if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var ok = _agenda.Delete(request.Id, request.CompetitionId);
            return Json(new { success = ok, message = ok ? null : "Kunde inte ta bort." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedAgenda([FromBody] SeedAgendaRequest request)
        {
            if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var (ok, msg, created) = _agenda.SeedDefaults(
                request.CompetitionId, GetCompetitionDate(request.CompetitionId), (await ResolveViewerAsync())?.Id ?? 0);
            return Json(new { success = ok, message = msg, created });
        }

        /// <summary>
        /// Schedule quality for the organiser: how many assignments a functionary would see as "Heldag"
        /// because they carry neither a shift nor a pass, plus overlapping commitments per person.
        ///
        /// This is the same overlap logic the member sees on /mitt-schema, surfaced here on purpose —
        /// a clash is cheap to fix while planning and expensive to discover on the day.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetScheduleQuality(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var assignments = _staffing.BuildRoster(competitionId, GetDiscipline(competitionId), canEdit: true).Groups
                    .SelectMany(g => g.Assignments)
                    .Where(a => !a.ReadOnly)
                    .ToList();

                var total = assignments.Count;
                var untimed = assignments.Count(a => string.IsNullOrWhiteSpace(a.ShiftLabel) && a.PassId == null);

                // Per-member overlap. Only members can be checked — a free-text helper has no identity
                // to collide with, and their rows carry no MemberId.
                var clashes = new List<object>();
                foreach (var grp in assignments.Where(a => a.MemberId is > 0).GroupBy(a => a.MemberId!.Value))
                {
                    var named = grp.ToList();
                    for (var i = 0; i < named.Count; i++)
                    {
                        for (var j = i + 1; j < named.Count; j++)
                        {
                            if (!ShiftsOverlap(named[i], named[j])) continue;
                            clashes.Add(new
                            {
                                memberId = grp.Key,
                                name = named[i].DisplayName,
                                a = $"{named[i].RoleName} {named[i].ScopeLabel} ({named[i].ShiftLabel ?? named[i].PassLabel})",
                                b = $"{named[j].RoleName} {named[j].ScopeLabel} ({named[j].ShiftLabel ?? named[j].PassLabel})",
                            });
                        }
                    }
                }

                return Json(new { success = true, total, untimed, clashes });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schedule quality check failed for comp {Comp}", competitionId);
                return Json(new { success = false, message = "Kunde inte läsa schemat." });
            }
        }

        /// <summary>
        /// True when two assignments for the same person demonstrably collide. Conservative: two rows in
        /// the same pass clash, and two parsed time ranges that intersect clash. Untimed ("heldag") rows
        /// never clash — a person can legitimately hold two whole-day roles, and guessing otherwise would
        /// bury the real clashes in noise.
        /// </summary>
        private static bool ShiftsOverlap(StaffAssignmentView a, StaffAssignmentView b)
        {
            if (a.PassId != null && b.PassId != null) return a.PassId == b.PassId;

            var ra = ParseShiftRange(a.ShiftLabel);
            var rb = ParseShiftRange(b.ShiftLabel);
            if (ra == null || rb == null) return false;
            return ra.Value.start < rb.Value.end && rb.Value.start < ra.Value.end;
        }

        /// <summary>Parses the "13:00–16:00" shape BuildShiftLabel emits. Anything else → null.</summary>
        private static (TimeSpan start, TimeSpan end)? ParseShiftRange(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var parts = label.Split('–', '-');
            if (parts.Length != 2) return null;
            if (!TimeSpan.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var s)) return null;
            if (!TimeSpan.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var e)) return null;
            return e <= s ? null : (s, e);
        }

        private DateTime? GetCompetitionDate(int competitionId)
        {
            try
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var d = ctx.Content.GetById(competitionId)?.Value<DateTime>("competitionDate");
                return d == default ? null : d;
            }
            catch { return null; }
        }

        // ======================= Bemanning grid (roll × dag) =======================

        /// <summary>
        /// Projects the roster into the grid shape. Built ON TOP of <c>BuildRoster</c> rather than reading
        /// StaffAssignment directly, so role names, scope labels, shift labels and the Fält station-chief
        /// mirror stay identical to every other surface — the grid is a second VIEW, never a second truth.
        /// </summary>
        private GridResponse BuildGrid(int competitionId, string? discipline)
        {
            var roster = _staffing.BuildRoster(competitionId, discipline, canEdit: true);
            var resp = new GridResponse { Discipline = discipline ?? "", CanEdit = true };

            var all = roster.Groups.SelectMany(g => g.Assignments).ToList();

            // ---- columns: every day the competition touches -------------------------------------
            // Union of pass dates, explicit shift dates and the competition date, so a build-day pass or a
            // Friday shift can't fall outside the grid. Sorted; an "undated" bucket is appended only when
            // something actually lands in it (a heldag row on a multi-day comp pins no day — we bucket it
            // rather than guessing, same rule the schedule uses).
            var passes = SafeGetPasses(competitionId);
            var dates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in passes) if (!string.IsNullOrEmpty(p.Date)) dates.Add(p.Date);
            foreach (var a in all) if (!string.IsNullOrEmpty(a.DateKey)) dates.Add(a.DateKey!);
            if (GetCompetitionDate(competitionId) is { } cd) dates.Add(cd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var sv = CultureInfo.GetCultureInfo("sv-SE");
            foreach (var d in dates)
            {
                var parsed = DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt : (DateTime?)null;
                var dayPasses = passes.Where(p => p.Date == d).ToList();
                resp.Columns.Add(new GridColumn
                {
                    Key = d,
                    Label = parsed?.ToString("ddd d MMM", sv) ?? d,
                    TimeLabel = DayTimeLabel(dayPasses),
                    PassIds = dayPasses.Select(p => p.Id).ToList(),
                });
            }

            var single = resp.Columns.Count == 1 ? resp.Columns[0].Key : null;
            bool NeedsUndated() => all.Any(a => string.IsNullOrEmpty(a.DateKey)) && single == null;
            if (NeedsUndated())
                resp.Columns.Add(new GridColumn { Key = "", Label = "Utan datum", TimeLabel = null });

            // ---- rows: one per role, split by scope where the role is scoped ---------------------
            var catalog = _roleCatalog.ForCompetition(competitionId, discipline);
            var clubs = ResolveClubNames(all);

            foreach (var role in catalog)
            {
                var mine = all.Where(a => string.Equals(a.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var scopeKeys = mine
                    .Select(a => (a.ScopeType, a.ScopeKey, a.ScopeLabel))
                    .Distinct()
                    .OrderBy(x => x.ScopeKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (scopeKeys.Count == 0) scopeKeys.Add((null, null, ""));

                foreach (var (scopeType, scopeKey, scopeLabel) in scopeKeys)
                {
                    var row = new GridRow
                    {
                        RoleKey = role.Key,
                        RoleName = role.DisplayName,
                        ScopeType = scopeType,
                        ScopeKey = scopeKey,
                        ScopeLabel = string.Equals(scopeLabel, "Hela tävlingen", StringComparison.OrdinalIgnoreCase) ? null : scopeLabel,
                        IsCustom = _roleCatalog.IsCustom(competitionId, role.Key),
                        SupportsTargetRange = role.SupportsTargetRange,
                        SupportsFunctionTitle = role.SupportsFunctionTitle,
                        DefaultScopeType = role.DefaultScopeType,
                    };

                    foreach (var a in mine.Where(a => a.ScopeType == scopeType && a.ScopeKey == scopeKey))
                    {
                        var colKey = a.DateKey ?? single ?? "";
                        if (!row.Cells.TryGetValue(colKey, out var list))
                            row.Cells[colKey] = list = new List<GridEntry>();
                        list.Add(new GridEntry
                        {
                            Id = a.Id,
                            MemberId = a.MemberId,
                            DisplayName = a.DisplayName,
                            ClubName = a.MemberId is int mid && clubs.TryGetValue(mid, out var cn) ? cn : null,
                            // A whole-day person renders as a bare name; a chip appears only where a real
                            // time exists. Five identical chips would read as five people.
                            TimeLabel = a.ShiftLabel ?? a.PassLabel,
                            ScopeLabel = row.ScopeLabel,
                            Status = a.Status,
                            IsResponsible = a.IsResponsible,
                            IsExternal = a.MemberId is not > 0,
                            ReadOnly = a.ReadOnly,
                            Note = a.Note,
                        });
                        row.Filled++;
                    }
                    resp.Rows.Add(row);
                }
            }

            resp.TotalAssigned = all.Count;
            resp.ExternalCount = all.Count(a => a.MemberId is not > 0);
            return resp;
        }

        /// <summary>
        /// Clone every assignment on one competition day onto another. The grid's "fyll höger" — a two-day
        /// competition usually staffs the second day almost identically, and retyping 40 rows is the single
        /// biggest reason a plan stays in Excel.
        /// <para>Idempotent by (person, role, scope, target day): running it twice adds nothing, so the
        /// organiser can copy, hand-edit the differences, and copy again without breeding duplicates.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyDay([FromBody] CopyDayRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.ToDate) || request.FromDate == request.ToDate)
                return Json(new { success = false, message = "Välj en annan måldag." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            if (!DateTime.TryParseExact(request.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
                return Json(new { success = false, message = "Ogiltigt datum." });

            try
            {
                var discipline = GetDiscipline(request.CompetitionId);
                var passes = SafeGetPasses(request.CompetitionId);
                var targetPassId = passes.FirstOrDefault(p => p.Date == request.ToDate)?.Id;
                var passDateById = passes.ToDictionary(p => p.Id, p => p.Date);

                var rows = _staffing.GetForCompetition(request.CompetitionId);

                string? DayOf(StaffAssignment a)
                {
                    if (a.StartsAt is { } s) return s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (a.PassId is int pid && passDateById.TryGetValue(pid, out var d)) return d;
                    return null;
                }

                var source = rows.Where(a => DayOf(a) == request.FromDate).ToList();
                if (source.Count == 0)
                    return Json(new { success = false, message = "Det finns inget att kopiera från den dagen." });

                // What already exists on the target day, so a re-run is a no-op.
                var existing = new HashSet<string>(
                    rows.Where(a => DayOf(a) == request.ToDate)
                        .Select(a => $"{a.MemberId?.ToString() ?? a.DisplayName?.ToLowerInvariant()}|{a.RoleKey}|{a.ScopeType}|{a.ScopeKey}"),
                    StringComparer.OrdinalIgnoreCase);

                var copied = 0;
                foreach (var a in source)
                {
                    var key = $"{a.MemberId?.ToString() ?? a.DisplayName?.ToLowerInvariant()}|{a.RoleKey}|{a.ScopeType}|{a.ScopeKey}";
                    if (!existing.Add(key)) continue;

                    // Keep the clock time, move the date. A shift that carried no time stays untimed.
                    string? Shift(DateTime? t) => t is { } v
                        ? toDate.Date.Add(v.TimeOfDay).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                        : null;

                    _staffing.Save(new SaveStaffAssignmentRequest
                    {
                        Id = 0,
                        CompetitionId = request.CompetitionId,
                        MemberId = a.MemberId,
                        DisplayName = a.DisplayName,
                        Phone = a.Phone,
                        Email = a.Email,
                        RoleKey = a.RoleKey,
                        FunctionTitle = a.FunctionTitle,
                        ScopeType = a.ScopeType,
                        ScopeKey = a.ScopeKey,
                        TargetFrom = a.TargetFrom,
                        TargetTo = a.TargetTo,
                        StartsAt = Shift(a.StartsAt),
                        EndsAt = Shift(a.EndsAt),
                        // Only attach a pass when the source row used one AND the target day has one;
                        // otherwise the copy would silently inherit the wrong day's times.
                        PassId = a.PassId != null ? targetPassId : null,
                        IsResponsible = a.IsResponsible,
                        HasAdminAccess = a.HasAdminAccess,
                        // A fresh day is a fresh ask — an "Accepted" answer for Saturday says nothing about
                        // Sunday, so the clone starts as Planerad and must be invited on its own.
                        Status = StaffAssignmentStatus.Planned,
                        Note = a.Note,
                        AllowDuplicate = true,
                    }, viewer.Id);
                    copied++;
                }

                return Json(new { success = true, copied, skipped = source.Count - copied });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: CopyDay failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte kopiera dagen." });
            }
        }

        private List<StaffPassView> SafeGetPasses(int competitionId)
        {
            try { return _pass.GetPasses(competitionId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: pass lookup failed for competition {CompetitionId}", competitionId);
                return new List<StaffPassView>();
            }
        }

        private static string? DayTimeLabel(List<StaffPassView> dayPasses)
        {
            var starts = dayPasses.Select(p => p.StartTime).Where(t => !string.IsNullOrEmpty(t)).OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault();
            var ends = dayPasses.Select(p => p.EndTime).Where(t => !string.IsNullOrEmpty(t)).OrderByDescending(t => t, StringComparer.Ordinal).FirstOrDefault();
            if (starts == null && ends == null) return null;
            return $"{starts ?? "?"}–{ends ?? "?"}";
        }

        /// <summary>
        /// Club per person, batched. Club is not decoration here: some competitions split any surplus
        /// between the clubs that staffed the event, so it is the basis for that split — which is also why
        /// it belongs to the CELL and not the row (the same function is often held by different clubs on
        /// different days). Best-effort; a missing club just renders nothing.
        /// </summary>
        private Dictionary<int, string> ResolveClubNames(List<StaffAssignmentView> rows)
        {
            var result = new Dictionary<int, string>();
            var memberIds = rows.Where(r => r.MemberId is > 0).Select(r => r.MemberId!.Value).Distinct().ToList();
            if (memberIds.Count == 0) return result;

            var clubNameById = new Dictionary<int, string>();
            foreach (var mid in memberIds)
            {
                try
                {
                    var m = _memberService.GetById(mid);
                    // primaryClubId is stored as a STRING on the member type — GetValue<int> returns 0.
                    // Same read as SearchMembers, which is the proven one.
                    var raw = m?.GetValue<string>("primaryClubId");
                    if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var clubId) || clubId <= 0) continue;
                    if (!clubNameById.TryGetValue(clubId, out var name))
                    {
                        name = _clubService.GetClubNameById(clubId) ?? "";
                        clubNameById[clubId] = name;
                    }
                    if (!string.IsNullOrEmpty(name)) result[mid] = name;
                }
                catch { /* one unreadable member must not blank the whole grid */ }
            }
            return result;
        }

        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted
        /// competitions. Mirrors EventMessageController.HasCompetitionAccessAsync.
        /// </summary>
        private async Task<bool> HasCompetitionAccessAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            try
            {
                if (await _auth.IsCurrentUserAdminAsync()) return true;
                if (await _auth.IsCompetitionManager(competitionId)) return true;

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return false;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return false;

                var clubId = comp.Value<int>("clubId");
                if (clubId > 0)
                {
                    if (await _auth.IsClubAdminForClub(clubId)) return true;   // includes regional admin for club's region
                    if (await _auth.IsSkjutledareForClub(clubId)) return true;
                }
                else
                {
                    var regionCode = comp.Value<string>("regionalFederation") ?? "";
                    if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}
