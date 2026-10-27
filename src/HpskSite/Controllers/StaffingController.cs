using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services;
using HpskSite.Services.Notifications;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
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
    public partial class StaffingController : SurfaceController
    {
        /// <summary>Svaret när ett id utifrån pekar på en rad (eller ett område, en uppgift, ett pass) i en
        /// annan tävling än den anropet gäller. Behörigheten prövas bara för anropets tävling.</summary>
        internal const string NotInCompetitionMessage =
            "Raden hittades inte i den här tävlingen. Ladda om sidan och försök igen.";

        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _auth;
        private readonly ClubService _clubService;
        private readonly StaffingService _staffing;
        private readonly CompetitionPeopleService _people;
        private readonly MaterielEstimateService _materiel;
        private readonly StaffingSignupService _signup;
        private readonly StaffRequestService _request;
        private readonly StaffHelpService _help;
        private readonly StaffPassService _pass;
        private readonly RoleCatalogService _roleCatalog;
        private readonly StaffDayService _days;
        private readonly PersonMatchService _match;
        private readonly StaffingGridService _grid;
        private readonly HpskSite.Services.Schedule.CompetitionAgendaService _agenda;
        private readonly EmailService _email;
        private readonly WebPushService _webPush;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly ILogger<StaffingController> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.MemberDirectoryService _directory;
        private readonly StaffOwnerResolver _owners;
        private readonly HpskSite.Services.Planning.PlanningService _planning;

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
            MaterielEstimateService materiel,
            StaffingSignupService signup,
            StaffRequestService request,
            StaffHelpService help,
            StaffPassService pass,
            RoleCatalogService roleCatalog,
            StaffDayService days,
            PersonMatchService match,
            StaffingGridService grid,
            HpskSite.Services.Schedule.CompetitionAgendaService agenda,
            EmailService email,
            WebPushService webPush,
            IDataProtectionProvider dataProtection,
            ILogger<StaffingController> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.MemberDirectoryService directory,
            StaffOwnerResolver owners,
            HpskSite.Services.Planning.PlanningService planning)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _owners = owners;
            _planning = planning;
            _directory = directory;
            _competitions = competitions;
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _auth = auth;
            _clubService = clubService;
            _staffing = staffing;
            _people = people;
            _materiel = materiel;
            _signup = signup;
            _request = request;
            _help = help;
            _pass = pass;
            _roleCatalog = roleCatalog;
            _days = days;
            _match = match;
            _grid = grid;
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = _owners.Discipline(owner);
            var roles = _roleCatalog.For(owner, discipline).Select(r => new
            {
                key = r.Key,
                name = r.DisplayName,
                plural = r.Plural,
                defaultScopeType = r.DefaultScopeType,
                supportsTargetRange = r.SupportsTargetRange,
                supportsFunctionTitle = r.SupportsFunctionTitle,
                description = r.Description,
                needs = r.Needs,
                isCustom = _roleCatalog.IsCustom(owner, r.Key),
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
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var key = _roleCatalog.SaveRole(request, owner, _owners.Discipline(owner), viewer.Id);
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
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var (ok, msg, inUse) = _roleCatalog.DeleteRole(
                    owner, request.RoleKey, _owners.Discipline(owner),
                    viewer.Id, request.DeleteAssignments);
                // Not an error — the row carries crew and the arrangör hasn't said what should happen to
                // them yet. Nothing was written; the client asks and calls back with DeleteAssignments.
                if (!ok && msg == null && inUse > 0)
                    return Json(new { success = false, requiresConfirm = true, inUse });
                return Json(new { success = ok, message = msg, removedAssignments = inUse });
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var viewer = await ResolveViewerAsync();
                var discipline = _owners.Discipline(owner);
                var resp = _grid.BuildGrid(owner, discipline, viewer?.Id ?? 0);
                // Omgång 3 steg 9: krockar över hela veckan (familjen). Ett fel här får inte fälla rutnätet.
                if (owner.PlanningIdOrNull is int pid && pid > 0)
                {
                    try
                    {
                        var intents = HttpContext.RequestServices.GetRequiredService<ShootIntentService>();
                        resp.FamilyClashes = intents.Clashes(pid).Select(c => new GridClash
                        {
                            PersonKey = c.PersonKey, Name = c.Name, DateKey = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            A = c.A, B = c.B, Kind = c.Kind,
                        }).ToList();
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: family clashes failed for planning {Planning}", pid); }
                    var sm = _owners.Planning(pid)?.ShooterMode;
                    resp.ShooterMode = ShooterModeRules.IsValid(sm) ? sm : null;
                    // "Bygg startlistan runt funktionärerna" finns bara när planeringen hör till en fälttävling, och bara för den som också har tävlingens
                    // personalbehörighet (samma regel som ändpunkterna, PatrolOwnerAsync) — annars visas ett val som svarar "Ingen behörighet" först efter klicket.
                    resp.PatrolPlanAvailable = owner.HasCompetition
                        && FunctionaryRoles.FaltFamily.Contains(_competitions.GetSaved(owner.Comp)?.CompetitionType ?? "", StringComparer.OrdinalIgnoreCase)
                        && await HasCompetitionAccessAsync(owner.Comp);
                    // Stående "Ångra senaste förslaget": en tillämpning går att ångra så länge raderna är orörda, också efter en omladdning (inte bara i en toast).
                    try
                    {
                        if (HttpContext.RequestServices.GetRequiredService<StaffProposalService>().LatestUndoable(owner) is { } u) { resp.UndoBatchId = u.BatchId; resp.UndoRows = u.Rows; }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: latest undoable failed for planning {Planning}", pid); }
                    try { ApplyRoleChoice(resp, owner, discipline, pid); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: role choice failed for planning {Planning}", pid); }
                    // "Dagens plan": stegen härleds ur det som redan står i svaret — efter rollvalet, så att dolda rader inte räknas.
                    try
                    {
                        // Vilka som har en egen start i tävlingen (familjen): "ingen av de N krockar" räknas bara på dem.
                        ISet<int>? starters = null;
                        if (owner.HasCompetition)
                        {
                            try { starters = HttpContext.RequestServices.GetRequiredService<StaffBusyReader>().StartsByMember(owner).Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet(); }
                            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: starters for plan steps failed for planning {Planning}", pid); }
                        }
                        DerivePlanSteps(resp, owner, starters);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: plan steps failed for planning {Planning}", pid); }
                }
                return Json(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetGrid failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Kunde inte läsa rutnätet." });
            }
        }

        /// <summary>
        /// Omgång 3 steg 10 (progressivt avslöjande): en planering vars roller inte är valda visar bara rader med folk,
        /// behov eller arrangörens egna roller — elva tomma rader skrämmer en liten klubb — och ber om ett val.
        /// </summary>
        private void ApplyRoleChoice(GridResponse resp, StaffOwner owner, string? discipline, int planningId)
        {
            var p = _planning.Get(planningId);
            if (p == null) return;
            var withContent = new HashSet<string>(
                resp.Rows.Where(r => r.IsCustom || r.Filled > 0 || r.HasNeed).Select(r => r.RoleKey), StringComparer.OrdinalIgnoreCase);
            var crew = new HashSet<string>(resp.Rows.Where(r => r.Filled > 0).Select(r => r.RoleKey), StringComparer.OrdinalIgnoreCase);
            resp.RoleChoices = _roleCatalog.BuiltInChoices(owner, discipline).Select(c => new GridRoleChoice
            {
                Key = c.Key, Name = c.Name, Description = c.Description, HasCrew = crew.Contains(c.Key),
                Chosen = p.RolesChosen ? c.Active : withContent.Contains(c.Key),
            }).ToList();
            if (p.RolesChosen) return;
            resp.RoleChoicePending = true;
            resp.Rows = resp.Rows.Where(r => withContent.Contains(r.RoleKey)).ToList();
        }

        /// <summary>Omgång 3 steg 10: "Välj roller" — de valda inbyggda rollerna framme, övriga dolda (utom de med folk).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChooseRoles([FromBody] ChooseStaffRolesRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            if (owner.PlanningIdOrNull is not int pid) return Json(new { success = false, message = "Rollerna väljs i planeringen." });
            try
            {
                var kept = _roleCatalog.ApplyBuiltInChoice(owner, _owners.Discipline(owner),
                    new HashSet<string>(request.Keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase), viewer.Id);
                _planning.MarkRolesChosen(pid);
                return Json(new { success = true, kept });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ChooseRoles failed for planning {Planning}", pid);
                return Json(new { success = false, message = "Rollerna kunde inte sparas." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetRoster(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = _owners.Discipline(owner);
            var roster = _staffing.BuildRoster(owner, discipline, canEdit: true);
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            var p = _people.Build(owner, _owners.Discipline(owner), canEdit: true);
            // Personer-fliken visar när var och en själv skjuter (persontest 8: det gick inte att se att ingen jobbar när hen skjuter). Ett fel här får inte fälla listan.
            try
            {
                var starts = HttpContext.RequestServices.GetRequiredService<StaffBusyReader>().StartsByMember(owner);
                var sv = CultureInfo.GetCultureInfo("sv-SE");
                foreach (var row in p.People.Where(r => r.MemberId is > 0 && r.Assignments.Count > 0))
                    if (starts.TryGetValue(row.MemberId!.Value, out var w))
                        row.Starts = w.OrderBy(x => x.Start).Select(x => x.Start.ToString("ddd d MMM 'kl' HH:mm", sv)).Distinct().Take(4).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: starts for the people view failed for competition {Competition}", competitionId); }
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
                // P3 steg E: personer och uppdrag per förening de ställer upp för.
                byOrganisation = p.ByOrganisation,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAssignment([FromBody] SaveStaffAssignmentRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Roll saknas" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            // Steg 5 (K2/K8): att SÄTTA "Ge behörighet" prövas — den som redan har den kvar får fortfarande ändra raden.
            if (request.HasAdminAccess && !(request.Id > 0 && _staffing.GetById(request.Id)?.HasAdminAccess == true))
            {
                var refusal = await AdminGrantRefusalAsync(owner);
                if (refusal != null) return Json(new { success = false, message = refusal });
            }

            // The role must exist in this competition's merged catalog (built-ins + arrangör-named rows).
            // It is no longer a closed set — "Vapenkontroll" is as valid as "Startledare" once someone has
            // created it — but it must still be a NAMED role, so everything downstream can group and count.
            var discipline = _owners.Discipline(owner);
            var role = _roleCatalog.Resolve(owner, discipline, request.RoleKey);
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
                // Resolve the day the same way everything else does: StartsAt.Date → DayDate. A duplicate
                // is only a duplicate on the SAME day.
                var dupDay = request.DayDate;
                if (!string.IsNullOrWhiteSpace(request.StartsAt)
                    && DateTime.TryParse(request.StartsAt, CultureInfo.GetCultureInfo("sv-SE"),
                        DateTimeStyles.None, out var dupStart))
                {
                    dupDay = dupStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                var dup = _people.FindDuplicate(owner, discipline, request.MemberId,
                    request.DisplayName, request.RoleKey, request.FunctionTitle,
                    request.ScopeType, request.ScopeKey, request.PassId, request.Id, dupDay);
                if (dup != null)
                    return Json(new
                    {
                        success = false,
                        duplicate = true,
                        message = $"{request.DisplayName} har redan uppdraget \"{dup.Label}\""
                                  + (string.IsNullOrEmpty(dup.DayLabel) ? "" : $" den {dup.DayLabel}")
                                  + ". Lägg till ändå?",
                    });
            }

            try
            {
                var id = _staffing.Save(request, owner, viewer.Id);
                if (id == 0) return Json(new { success = false, message = NotInCompetitionMessage });
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

            var owner = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            _staffing.Delete(request.Id, owner);
            return Json(new { success = true });
        }

        // ======================= Dagen: upprop + planerad-vs-aktiv (day-of cockpit) =======================

        [HttpGet]
        public async Task<IActionResult> GetDayOfCockpit(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var c = _staffing.BuildDayOfCockpit(owner, _owners.Discipline(owner), canEdit: true);
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
            var owner = await DayOfRowOwnerAsync(_staffing.OwnerOf(request.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            _staffing.SetCheckedIn(request.Id, owner, request.CheckedIn);
            return Json(new { success = true });
        }

        // ======================= Förberedelser (work-breakdown) =======================

        // ======================= Kommentarer / logg + beroenden (uppgift-tråd) =======================

        public class ApplyKretsChecklistRequest
        {
            public int CompetitionId { get; set; }
            public List<int>? AddIds { get; set; }
            /// <summary>Punkter som inte behövs för tävlingen — registreras som bortvalda och erbjuds inte igen.</summary>
            public List<int>? DismissIds { get; set; }
        }

        /// <summary>Discipline-aware materiel-quantity estimate from participant/class/series counts.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMaterielEstimate(int competitionId)
        {
            // Tävlingens behörighet, eller Kan ändra i planeringen som är kopplad till just den tävlingen.
            if (!await CanManageAgendaAsync(competitionId))
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, passes = _pass.GetPasses(owner) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePass([FromBody] SavePassRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.Date))
                return Json(new { success = false, message = "Ange ett datum för passet." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var id = _pass.SavePass(request, owner, viewer.Id);
            if (id == 0) return Json(new { success = false, message = NotInCompetitionMessage });
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePass([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            _pass.DeletePass(request.Id, owner);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetCrewNeeds(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, needs = _pass.GetCrewNeeds(owner, _owners.Discipline(owner)) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCrewNeeds([FromBody] SaveCrewNeedsRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            _pass.SaveCrewNeeds(owner, request.Needs);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyPassAssignments([FromBody] CopyPassRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.FromPassId <= 0 || request.ToPassId <= 0)
                return Json(new { success = false, message = "Välj pass att kopiera från och till." });
            if (request.FromPassId == request.ToPassId)
                return Json(new { success = false, message = "Välj två olika pass." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var copied = _staffing.CopyPassAssignments(owner, request.FromPassId, request.ToPassId, viewer.Id);
            return Json(new { success = true, copied });
        }

        [HttpGet]
        public async Task<IActionResult> GetCoverage(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var discipline = _owners.Discipline(owner);
            var seed = owner.HasCompetition ? GetStationSeed(owner.Comp, discipline) : null;
            var cov = _pass.BuildCoverage(owner, discipline, seed?.StationCount ?? 0);
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, slots = _help.GetSlots(owner) });
        }

        /// <summary>
        /// "Hitta funktionärer": behoven per dag (det medlemmarna kan anmäla sig till), om medlemmen får välja
        /// roll, och anmälningssidans adress.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSignupOverview(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var signupPath = owner.HasPlanning ? $"/bemanna?p={owner.PlanningId}" : $"/bemanna?c={owner.Comp}";
            return Json(new
            {
                success = true,
                needDays = _help.NeedDays(owner),
                roleChoice = _owners.SignupRoleChoice(owner),
                roleChoiceSettable = owner.HasPlanning,
                hasPlanning = owner.HasPlanning,
                hasCompetition = owner.HasCompetition,
                signupPath,
            });
        }

        /// <summary>Klubbarna och kretsarna man kan öppna anmälan för (väljarens lista).</summary>
        [HttpGet]
        public async Task<IActionResult> GetSourceScopeOptions(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var (clubs, regions, suggested) = _signup.ScopeOptions(owner);
            return Json(new { success = true, clubs, regions, suggested });
        }

        /// <summary>Får medlemmen välja roll vid anmälan? Bara på en planering (en tävling utan planering: alltid ja).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSignupRoleChoice([FromBody] SetSignupRoleChoiceRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (!owner.HasPlanning)
                return Json(new { success = false, message = "Inställningen hör till planeringen. Starta tävlingens planering först." });
            var value = string.Equals(request.Value, "1", StringComparison.Ordinal) || string.Equals(request.Value, "true", StringComparison.OrdinalIgnoreCase);
            _planning.SetSignupRoleChoice(owner.PlanningId, value, viewer.Id);
            return Json(new { success = true, roleChoice = value });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveHelpSlot([FromBody] SaveHelpSlotRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.Headline) || string.IsNullOrWhiteSpace(request.Date))
                return Json(new { success = false, message = "Ange datum och rubrik." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var id = _help.SaveSlot(request, owner, viewer.Id);
            if (id == 0) return Json(new { success = false, message = NotInCompetitionMessage });
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHelpSlot([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            _help.DeleteSlot(request.Id, owner);
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            var people = _people.Build(owner, _owners.Discipline(owner), canEdit: true, includePrep: false);
            var byKey = people.People.ToDictionary(p => p.Key, p => p, StringComparer.Ordinal);

            var signups = _help.GetReview(owner).Select(v =>
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            return Json(new { success = true, scopes = _signup.GetScopes(owner) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSourceScope([FromBody] SaveSourceScopeRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var (ok, msg, id) = _signup.AddScope(owner, request.ScopeType, request.ScopeKey, viewer.Id);
            return Json(new { success = ok, message = msg, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSourceScope([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            _signup.RemoveScope(request.Id, owner);
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> PreviewStaffRequest(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var p = _request.Preview(owner);
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
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            // Omgång 3: relämejlet bär den som frågar (namn, Reply-To), vad som fattas och sajtens riktiga adress.
            var senderEmail = _memberService.GetById(viewer.Id)?.Email;
            List<NeedDayView>? needs = null;
            try { needs = _help.NeedDays(owner); } catch (Exception ex) { _logger.LogWarning(ex, "Förfrågan om funktionärer: behoven kunde inte läsas."); }
            var (ok, msg, sent, push, recipients) = _request.Send(owner, request.Mode, request.Message, viewer.Id,
                new StaffRequestService.RequestContext(Request.PublicBase(), viewer.Name, senderEmail, needs));
            return Json(new { success = ok, message = msg, sent, push, recipients });
        }

        // ---- Phase 1.5: editable per-club/region planning templates ----

        // ======================= Dokument & länkar (WorkLink) =======================

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
            var owner = await RowOwnerAsync(_staffing.OwnerOf(a.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (a.MemberId is not > 0 && string.IsNullOrWhiteSpace(a.Email))
                return Json(new { success = false, message = "Personen saknar konto och e-post — kan inte notifieras." });

            var meta = (Name: _owners.Name(owner), Url: _owners.ManageUrl(owner));
            var roleName = _roleCatalog.NameFor(owner, _owners.Discipline(owner), a.RoleKey);
            var where = string.IsNullOrEmpty(a.ScopeType) || string.Equals(a.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase)
                ? "hela tävlingen" : $"{a.ScopeType} {a.ScopeKey}".Trim();
            var subject = $"Funktionärsförfrågan: {roleName} – {meta.Name}";
            var bodyText = $"Du är inplanerad som <strong>{System.Net.WebUtility.HtmlEncode(roleName)}</strong> på {System.Net.WebUtility.HtmlEncode(where)} under <strong>{System.Net.WebUtility.HtmlEncode(meta.Name)}</strong>. Tacka ja eller nej.";

            bool email = false; int push = 0;
            if (a.MemberId is > 0)
            {
                // Members act on the per-comp landing page (accept/decline + availability, in context).
                var beUrl = owner.SignupPath;
                var html = NotifyEmailHtml(bodyText + " Ange gärna när du kan arbeta.", "Öppna bemanningssidan", beUrl, viewer.Name);
                (email, push) = await NotifyMemberAsync(a.MemberId.Value, subject, html, "Funktionärsförfrågan", $"{roleName} – {meta.Name}", beUrl);
            }
            else if (!string.IsNullOrWhiteSpace(a.Email))
            {
                // External (non-member) helper: a tokened accept/decline link, no login required.
                var token = StaffingInviteToken.Protect(_dataProtection, a);
                var html = NotifyEmailHtml(bodyText, "Svara på förfrågan", $"/mina-uppdrag/svar?t={Uri.EscapeDataString(token)}", viewer.Name);
                try { email = await _email.SendHtmlEmailAsync(a.Email!, subject, html); } catch { }
            }

            if (!email && push == 0)
                return Json(new { success = false, message = "Kunde inte skicka — ingen e-post eller push-prenumeration." });

            // Only flip Planerad → Inbjuden once something actually reached the person.
            if (string.Equals(a.Status, StaffAssignmentStatus.Planned, StringComparison.OrdinalIgnoreCase))
                _staffing.SetStatus(a.Id, owner, StaffAssignmentStatus.Invited);

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
            if (await RowOwnerAsync(_staffing.OwnerOf(a.Id)) == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var token = StaffingInviteToken.Protect(_dataProtection, a);
            var url = $"{Request.PublicBase()}/mina-uppdrag/svar?t={Uri.EscapeDataString(token)}";
            return Json(new { success = true, url, token });
        }

        // ======================= Shared: member picker =======================

        [HttpGet]
        public async Task<IActionResult> SearchMembers(int competitionId, string query)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return Json(new { success = true, members = new List<object>() });

            // Sökningen i SQL (skalplanen B) — förut byggdes hela registret för att svara med 20 rader.
            var matches = _directory.Search(query, 20)
                .Select(m => new
                {
                    memberId = m.MemberId,
                    memberName = m.DisplayName,
                    clubName = m.PrimaryClubId is int clubId ? _clubService.GetClubNameById(clubId) : null,
                    phone = m.Phone
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
                return _competitions.Get(competitionId)?.CompetitionType ?? "";
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
                var n = _competitions.Get(competitionId)?.CompetitionName;
                if (!string.IsNullOrWhiteSpace(n)) name = n!;
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
                var d = _competitions.Get(competitionId)?.CompetitionDate;
                if (d.HasValue)
                    return (d.Value, (int)(d.Value.Date - DateTime.Now.Date).TotalDays);
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
                var comp = _competitions.Get(competitionId);
                if (comp == null) return null;

                var stationCount = comp.NumberOfSeriesOrStations;
                int attachedConfigId = 0;
                var cfgJson = comp.StationConfig;
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
            if (!await CanManageAgendaAsync(competitionId))
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
            if (!await CanManageAgendaAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var (ok, msg, id) = _agenda.Save(request, (await ResolveViewerAsync())?.Id ?? 0);
            return Json(new { success = ok, message = msg, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAgendaItem([FromBody] DeleteAgendaItemRequest request)
        {
            if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await CanManageAgendaAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var ok = _agenda.Delete(request.Id, request.CompetitionId);
            return Json(new { success = ok, message = ok ? null : "Kunde inte ta bort." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedAgenda([FromBody] SeedAgendaRequest request)
        {
            if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (!await CanManageAgendaAsync(request.CompetitionId))
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
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var assignments = _staffing.BuildRoster(owner, _owners.Discipline(owner), canEdit: true).Groups
                    .SelectMany(g => g.Assignments)
                    .Where(a => !a.ReadOnly)
                    .ToList();

                var total = assignments.Count;
                var untimed = assignments.Count(a => string.IsNullOrWhiteSpace(a.ShiftLabel) && a.PassId == null);

                // Per-member overlap. Only members can be checked — a free-text helper has no identity
                // to collide with, and their rows carry no MemberId.
                var clashes = new List<object>();
                foreach (var grp in assignments.Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
                                               .GroupBy(PersonKeyOf))
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
            // DIFFERENT DAYS NEVER CLASH. Comparing clock times alone made Kristian's 08–09 on Saturday
            // collide with his 07:30–09:00 on Sunday — noise that buries the real clashes.
            if (a.DateKey != null && b.DateKey != null && a.DateKey != b.DateKey) return false;

            if (a.PassId != null && b.PassId != null) return a.PassId == b.PassId;

            var ra = ParseShiftRange(a.ShiftLabel);
            var rb = ParseShiftRange(b.ShiftLabel);
            if (ra == null || rb == null) return false;
            return ra.Value.start < rb.Value.end && rb.Value.start < ra.Value.end;
        }

        /// <summary>
        /// Identity for clash detection. Used to be MemberId only, on the reasoning that "a free-text helper
        /// has no identity to collide with" — true before the people layer, false now that free-text rows are
        /// grouped by name. On a real plan 37 of 41 people are free text, so the old rule checked almost
        /// nobody: the same person could be booked on two posts at the same hour with no warning at all.
        /// </summary>
        private static string PersonKeyOf(StaffAssignmentView a)
            => StaffPersonKey.For(a.MemberId, a.DisplayName);

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

        /// <summary>Passes for a competition, never throwing. The grid builds its own copy in
        /// StaffingGridService; this one serves CopyDay and MoveAssignmentToDay.</summary>
        private List<StaffPassView> SafeGetPasses(StaffOwner owner)
        {
            try { return _pass.GetPasses(owner); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: pass lookup failed for {Owner}", owner.MemoKey);
                return new List<StaffPassView>();
            }
        }

        private DateTime? GetCompetitionDate(int competitionId)
        {
            try
            {
                return _competitions.Get(competitionId)?.CompetitionDate;
            }
            catch { return null; }
        }

        /// <summary>
        /// The days of the plan. Bemanning AND Dagsprogram both read this, so the two surfaces cannot
        /// disagree about what days the event has — which was the original complaint.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDays(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var viewer = await ResolveViewerAsync();
                var withWork = _days.DatesWithAssignments(owner);
                var days = _days.EnsureSeeded(owner, viewer?.Id ?? 0)
                    .Select(d => _days.ToView(d, withWork)).ToList();
                return Json(new { success = true, days });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetDays failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Kunde inte läsa dagarna." });
            }
        }

        /// <summary>Add or edit a day of the plan — including a build/teardown day outside the competition.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDay([FromBody] SaveStaffDayRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var (ok, msg, id) = _days.SaveDay(request, owner, viewer.Id);
                return Json(new { success = ok, message = msg, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SaveDay failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara dagen." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDay([FromBody] DeleteStaffDayRequest request)
        {
            // Id OR DateKey — a column that exists only because assignments are dated there has no StaffDay
            // row, and demanding an id made those columns permanent.
            if (request == null || !HasOwnerKey(request.CompetitionId)
                || (request.Id <= 0 && string.IsNullOrWhiteSpace(request.DateKey)))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var (ok, msg, inUse) = _days.DeleteDay(
                    request.Id, owner, request.DateKey, request.DeleteAssignments);
                if (!ok && msg == null && inUse > 0)
                    return Json(new { success = false, requiresConfirm = true, inUse });
                return Json(new { success = ok, message = msg, removedAssignments = inUse });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: DeleteDay failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte ta bort dagen." });
            }
        }

        /// <summary>Move an assignment to another day — the escape hatch that keeps the day/role guards
        /// from becoming dead ends.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAssignmentToDay([FromBody] MoveAssignmentRequest request)
        {
            if (request == null || request.Id <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (!DateTime.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Json(new { success = false, message = "Ogiltigt datum." });
            try
            {
                var passId = SafeGetPasses(owner).FirstOrDefault(p => p.Date == request.Date)?.Id;
                var (ok, msg) = _staffing.MoveToDay(request.Id, owner, day, passId);
                return Json(new { success = ok, message = msg });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: MoveAssignmentToDay failed for {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte flytta uppdraget." });
            }
        }

        /// <summary>
        /// Which pistol.nu member did the organiser mean? One call for every free-text name on the
        /// competition, so 32 unidentified helpers become one screen to confirm instead of 32 searches.
        /// Suggestions only — nothing is ever linked without a human saying yes.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SuggestPersonMatches(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var rows = _match.Suggest(owner);
                return Json(new
                {
                    success = true,
                    people = rows,
                    total = rows.Count,
                    confident = rows.Count(r => r.Confident),
                    unmatched = rows.Count(r => r.Candidates.Count == 0 && !r.LooksLikeTwoPeople),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SuggestPersonMatches failed for {Comp}", competitionId);
                return Json(new { success = false, message = "Kunde inte föreslå matchningar." });
            }
        }

        /// <summary>
        /// Apply an identity change to EVERY row a person holds — link to a member, rename, set contact,
        /// or hand the work to someone else. Row-at-a-time editing is what let the same human end up with
        /// three spellings and an e-mail on only one of them.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyPersonIdentity([FromBody] PersonActionRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (request.MemberId <= 0 && string.IsNullOrWhiteSpace(request.NewName)
                && string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(request.Phone))
                return Json(new { success = false, message = "Inget att ändra." });

            var email = (request.Email ?? "").Trim();
            if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
                return Json(new { success = false, message = "Ogiltig e-postadress." });

            try
            {
                var r = _staffing.ApplyPersonIdentity(request, owner);
                return Json(new { success = r.Success, message = r.Message, affected = r.Affected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyPersonIdentity failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara ändringen." });
            }
        }

        /// <summary>Put back the originally typed name and unlink — the undo for a link that went to the
        /// wrong person.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UndoPersonIdentity([FromBody] PersonActionRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var r = _staffing.UndoPersonIdentity(owner, request.PersonKey);
                return Json(new { success = r.Success, message = r.Message, affected = r.Affected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: UndoPersonIdentity failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte ångra." });
            }
        }

        /// <summary>Grant or revoke the right to manage the competition — for the person, across all their
        /// uppdrag. Person-level because that is how it has always been READ.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPersonAdminAccess([FromBody] PersonAdminAccessRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            if (request.Grant)
            {
                var refusal = await AdminGrantRefusalAsync(owner);
                if (refusal != null) return Json(new { success = false, message = refusal });
            }
            try
            {
                var r = _staffing.SetPersonAdminAccess(owner, request.PersonKey, request.Grant);
                return Json(new { success = r.Success, message = r.Message, affected = r.Affected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetPersonAdminAccess failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte ändra behörigheten." });
            }
        }

        /// <summary>
        /// P3 steg E (beslut 9.6): vilken förening personen ställer upp för. Bara ett av personens egna val
        /// (medlemmens klubbar och deras kretsar; för en utan konto de föreningar andra i bemanningen har) —
        /// annars kunde vem som helst skrivas in som en annan förenings funktionär i fördelningen av ett överskott.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPersonRepresents([FromBody] PersonRepresentsRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                int? club = null, region = null;
                if (request.Id > 0)
                {
                    var person = _people.Build(owner, _owners.Discipline(owner), canEdit: true, includePrep: false).People
                        .FirstOrDefault(p => p.Key == request.PersonKey);
                    if (person == null) return Json(new { success = false, message = "Hittade inte personen." });
                    if (!person.RepresentChoices.Any(c => c.Type == request.Type && c.Id == request.Id))
                        return Json(new { success = false, message = "Välj en av personens föreningar." });
                    if (request.Type == "club") club = request.Id; else region = request.Id;
                }
                var r = _staffing.SetPersonRepresents(owner, request.PersonKey, club, region);
                return Json(new { success = r.Success, message = r.Message, affected = r.Affected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetPersonRepresents failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara föreningen." });
            }
        }

        /// <summary>Cut a shift at a clock time and hand the tail to someone else — the mid-shift dropout.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SplitAssignment([FromBody] SplitAssignmentRequest request)
        {
            if (request == null || request.Id <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            // "12" och "1230" räcker (ClockTime) — samma tolkning som fältet i webbläsaren.
            if (HpskSite.Models.ClockTime.ToTimeSpan(request.At) is not TimeSpan at)
                return Json(new { success = false, message = "Tiden går inte att läsa. Skriv till exempel 12 eller 12:30." });
            try
            {
                var (ok, msg, newId) = _staffing.SplitAssignment(request.Id, owner, at,
                    request.MemberId > 0 ? request.MemberId : null, request.NewName, viewer.Id);
                return Json(new { success = ok, message = msg, id = newId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SplitAssignment failed for {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte dela passet." });
            }
        }

        /// <summary>
        /// Behovet för en roll: antalet för alla dagar och de dagar som avviker (Stefan 2026-10-23 — markörer
        /// behövs bara på tävlingsdagarna). Dagarna måste vara giltiga datum.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRoleNeeds([FromBody] SetRoleNeedsRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (request.All < 0 || request.All > 999 || (request.Days ?? new()).Any(d => d.Count < 0 || d.Count > 999))
                return Json(new { success = false, message = "Ange ett antal mellan 0 och 999." });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            var days = new List<(DateTime, int)>();
            foreach (var d in request.Days ?? new())
            {
                if (!DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return Json(new { success = false, message = "Ett datum gick inte att läsa." });
                days.Add((day, d.Count));
            }
            // Omgång 3 (3.2): passen måste vara ägarens egna — ett främmande pass-id hade lagt ett behov ingen kan se.
            var passes = new List<(int, DateTime, int)>();
            if ((request.Passes ?? new()).Count > 0)
            {
                var own = _pass.GetPasses(owner).ToDictionary(p => p.Id, p => p.Date);
                foreach (var p in request.Passes!)
                {
                    if (p.Count < 0 || p.Count > 999) return Json(new { success = false, message = "Ange ett antal mellan 0 och 999." });
                    if (!own.TryGetValue(p.PassId, out var pd) || !DateTime.TryParseExact(pd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var passDay))
                        return Json(new { success = false, message = "Passet finns inte i bemanningen." });
                    passes.Add((p.PassId, passDay, p.Count));
                }
            }
            var scopeKey = string.IsNullOrWhiteSpace(request.ScopeKey) ? null : request.ScopeKey.Trim();
            if (scopeKey != null && (scopeKey.Length > 50 || !string.Equals(request.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase)))
                return Json(new { success = false, message = "Bara ett stationsbehov kan gälla en bestämd station." });
            try
            {
                _pass.SetRoleNeeds(owner, request.RoleKey, request.ScopeKind ?? CrewNeedScope.All, scopeKey, request.All, days, passes);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetRoleNeeds failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara behovet." });
            }
        }

        /// <summary>Set how many of one role are needed. One role at a time, because it is now edited from
        /// the grid row rather than a full-replace needs editor on a screen nobody opened.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCrewNeed([FromBody] SetCrewNeedRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var discipline = _owners.Discipline(owner);
                var needs = _pass.GetCrewNeeds(owner, discipline)
                    .Where(n => !string.Equals(n.RoleKey, request.RoleKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (request.Count > 0)
                    needs.Add(new CrewNeedRow { ScopeKind = CrewNeedScope.All, RoleKey = request.RoleKey, Count = request.Count });
                _pass.SaveCrewNeeds(owner, needs);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetCrewNeed failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara behovet." });
            }
        }

        /// <summary>Save the running order the arrangör dragged the rows into.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReorderRoles([FromBody] ReorderRolesRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var (ok, msg) = _roleCatalog.Reorder(owner, request.RoleKeys,
                    _owners.Discipline(owner), viewer.Id);
                return Json(new { success = ok, message = msg });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ReorderRoles failed for {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara ordningen." });
            }
        }

        // HideRole is gone with the "Dolda roller" idea it existed for. DeleteRole removes any role now,
        // built-in included; hiding lives on only as the storage trick inside RoleCatalogService.

        /// <summary>
        /// Give an account-less helper an e-mail (and phone) straight from the grid cell. Without this the
        /// row is a dead end: it cannot be invited, reminded or told anything — which is the entire payoff
        /// the organiser came here for.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetContact([FromBody] SetContactRequest request)
        {
            if (request == null || request.Id <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            var email = (request.Email ?? "").Trim();
            if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
                return Json(new { success = false, message = "Ogiltig e-postadress." });
            try
            {
                _staffing.SetContact(request.Id, owner, email.Length == 0 ? null : email,
                    string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone!.Trim());
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SetContact failed for assignment {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte spara kontaktuppgiften." });
            }
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
            if (request == null || !HasOwnerKey(request.CompetitionId))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.ToDate) || request.FromDate == request.ToDate)
                return Json(new { success = false, message = "Välj en annan måldag." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null)
                return Json(new { success = false, message = "Ingen behörighet" });

            if (!DateTime.TryParseExact(request.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
                return Json(new { success = false, message = "Ogiltigt datum." });

            try
            {
                var discipline = _owners.Discipline(owner);
                var passes = SafeGetPasses(owner);
                var targetPassId = passes.FirstOrDefault(p => p.Date == request.ToDate)?.Id;
                var passDateById = passes.ToDictionary(p => p.Id, p => p.Date);

                var rows = _staffing.GetForOwner(owner);

                string? DayOf(StaffAssignment a)
                {
                    if (a.StartsAt is { } s) return s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (a.DayDate is { } dd) return dd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
                        // An all-day row keeps being all-day — on the new day.
                        DayDate = a.StartsAt == null ? request.ToDate : null,
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
                    }, owner, viewer.Id);
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




        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted
        /// competitions. Mirrors EventMessageController.HasCompetitionAccessAsync.
        /// </summary>
        private Task<bool> HasCompetitionAccessAsync(int competitionId) =>
            _auth.HasCompetitionStaffAccessAsync(competitionId);

        // ======================= P2b: bemanningens ägare =======================

        /// <summary>
        /// Planeringen anropet gäller — <c>?planningId=</c> i adressen. Planeringens bemanningssida lägger
        /// till den på varje anrop hit (se Bemanning.cshtml), så de delade vyerna (_StaffingGrid,
        /// _StaffingRoster) behöver inte veta om den.
        /// </summary>
        private int PlanningIdFromRequest() =>
            int.TryParse(Request.Query["planningId"], out var p) && p > 0 ? p : 0;

        /// <summary>Anropet har en ägare att pröva: en tävling, eller en planering i adressen.</summary>
        private bool HasOwnerKey(int competitionId) => competitionId > 0 || PlanningIdFromRequest() > 0;

        /// <summary>
        /// Bemanningens ägare för anropet, efter behörighetsprövning — null = ingen behörighet (eller finns
        /// inte; de två skiljs medvetet inte åt).
        /// <para>Med <c>?planningId=</c>: planeringens bemanning. Den får hanteras av den som får ändra i
        /// planeringen (Kan ändra eller mer), eller av den som har tillgång till den kopplade tävlingen
        /// ("via kopplingen", Stefan 2026-10-23).</para>
        /// <para>Utan: tävlingens bemanning (dess planerings, om den har en) — med tävlingens behörighet,
        /// som förut.</para>
        /// </summary>
        private async Task<StaffOwner?> OwnerAsync(int competitionId)
        {
            var pid = PlanningIdFromRequest();
            if (pid > 0) return await RowOwnerAsync(_owners.ForPlanning(pid));
            if (competitionId <= 0) return null;
            if (!await HasCompetitionAccessAsync(competitionId)) return null;
            var owner = _owners.ForCompetition(competitionId);
            // Expertgruppen K1 / Stefan B2: tävlingens personal LÄSER bemanningen via tävlingen, men en planerad
            // tävlings bemanning ÄNDRAS bara av den som får ändra i planeringen (annars kunde den som fått behörighet
            // genom bemanningen — kassan, sekretariatet — ändra den och dela ut behörighet vidare). En tävling utan
            // planering (prod i dag) är oförändrad.
            if (owner.HasPlanning && !HttpMethods.IsGet(Request.Method) && !await CanEditPlanningStaffAsync(owner.PlanningId))
                return null;
            return owner;
        }

        /// <summary>
        /// Prövningen för en befintlig rad: radens EGEN ägare avgör, aldrig ett id i anropet — så ett id ur en
        /// annan planering eller tävling ger ingen behörighet. En rad i en planering prövas BARA på planeringens regel
        /// (Stefan B1/B2) — tävlingsbehörigheten ger ingen rätt att ändra planeringens bemanning.
        /// </summary>
        private async Task<StaffOwner?> RowOwnerAsync(StaffOwner? owner)
        {
            if (owner == null) return null;
            if (owner.HasPlanning) return await CanEditPlanningStaffAsync(owner.PlanningId) ? owner : null;
            if (owner.HasCompetition && await HasCompetitionAccessAsync(owner.Comp)) return owner;
            return null;
        }

        /// <summary>
        /// Tävlingsdagen (upprop/incheckning på Funktionärer-fliken): tävlingens personal bockar av funktionärerna
        /// även när bemanningen hör till en planering — det är driften, inte planeringen (B2 gäller ändringar i den).
        /// </summary>
        private async Task<StaffOwner?> DayOfRowOwnerAsync(StaffOwner? owner)
        {
            if (owner == null) return null;
            if (owner.HasCompetition && await HasCompetitionAccessAsync(owner.Comp)) return owner;
            return await RowOwnerAsync(owner);
        }

        /// <summary>
        /// Dagsprogrammet hör till tävlingen: tävlingens behörighet, eller (med ?planningId=) den som får ändra
        /// bemanningen i planeringen som är kopplad till JUST den tävlingen.
        /// </summary>
        private async Task<bool> CanManageAgendaAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            if (await HasCompetitionAccessAsync(competitionId)) return true;
            var pid = PlanningIdFromRequest();
            if (pid <= 0) return false;
            var owner = _owners.ForPlanning(pid);
            return owner is { HasCompetition: true } && owner.Comp == competitionId && await CanEditPlanningStaffAsync(pid);
        }

        /// <summary>
        /// Steg 5 (Expertgruppen steg 4–6, K2 och K8): vem får dela ut "Ge behörighet"? Null = får. Annars svaret till den som frågar.
        /// <list type="bullet">
        /// <item><b>Det gemensamma</b> (en huvudplanering med delar, utan egen tävling) gäller alla veckans tävlingar — det ger bara
        /// huvudplaneringens <b>Sammankallande</b>, inte en "Kan ändra". Annars kunde den som bara skriver in folk ge sig själv
        /// tävlingshantering i varje tävling.</item>
        /// <item><b>En del som inte själv är kopplad</b> (den får syskonets eller rotens tävling via "Bemannar för" eller arv) ger
        /// bara den som har organisatörsrätt i just den tävlingen — annars kunde en delplaneringsledare ge appbehörighet i en annan
        /// klubbs tävling.</item>
        /// <item>En tävlings egen bemanning och en planering med egen koppling: som förut.</item>
        /// </list>
        /// </summary>
        private async Task<string?> AdminGrantRefusalAsync(StaffOwner owner)
        {
            if (!owner.HasPlanning) return null;
            // Faktan hämtas bara när den behövs (varje fråga kostar en databasläsning); själva regeln är ren och enhetstestad.
            var carries = !owner.HasCompetition && _staffing.CarriesFamilyGrant(owner);
            var convener = carries && await IsRootConvenerAsync(owner.PlanningId);
            var ownLink = owner.HasCompetition && _planning.LinkFor(owner.PlanningId)?.TargetId == owner.Comp;
            var organiser = owner.HasCompetition && !ownLink && await _auth.HasCompetitionOrganiserAccessAsync(owner.Comp);
            return StaffAdminGrantRules.Refusal(true, owner.HasCompetition, carries, convener, ownLink, organiser);
        }

        private async Task<bool> IsRootConvenerAsync(int planningId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return false;
            var row = _planning.ParticipantForMember(planningId, viewer.Id);
            return row is { IsActive: true, Access: HpskSite.Models.Planning.PlanningAccessLevel.Owner } && !row.ViaCompetition;
        }

        private async Task<bool> CanEditPlanningStaffAsync(int planningId)
        {
            var viewer = await ResolveViewerAsync();
            return viewer != null && _planning.CanEditStaffingIn(planningId, viewer.Id);
        }
    }
}
