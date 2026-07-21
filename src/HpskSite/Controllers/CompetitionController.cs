using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Microsoft.Extensions.Logging;
using HpskSite.Services;
using HpskSite.Models;
using System.Collections.Concurrent;

namespace HpskSite.Controllers
{
    public class CompetitionController : SurfaceController
    {
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _registrationLocks = new();

        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly ILogger<CompetitionController> _logger;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly DirektplaceringStartListService _dpStartListService;
        private readonly InvoiceAuditService _auditService;
        // Used to create a fresh DI scope for deferred background work. The controller's
        // own scoped services (_contentService, _dpStartListService) get disposed when the
        // HTTP request ends — capturing them in a Task.Run lambda that fires later leaks
        // half-broken connections, which manifests as DataReader exceptions during
        // ContentService.Publish + orphaned ContentTree write locks (id -333) that
        // freeze the whole site. EnqueueBackground builds a fresh scope per task.
        private readonly IServiceScopeFactory _scopeFactory;

        public CompetitionController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            IContentTypeService contentTypeService,
            ILogger<CompetitionController> logger,
            ClubService clubService,
            AdminAuthorizationService authorizationService,
            DirektplaceringStartListService dpStartListService,
            InvoiceAuditService auditService,
            IServiceScopeFactory scopeFactory)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _logger = logger;
            _clubService = clubService;
            _authorizationService = authorizationService;
            _dpStartListService = dpStartListService;
            _auditService = auditService;
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// Run <paramref name="work"/> in a background task after an optional delay,
        /// using a fresh DI scope. Use this for any background work that touches
        /// scoped services (IContentService, DirektplaceringStartListService, etc.)
        /// to avoid leaking write locks via disposed-scope-captured services.
        /// </summary>
        private void EnqueueBackground(TimeSpan delay, Action<IServiceProvider> work, string description)
        {
            var scopeFactory = _scopeFactory;
            var fallbackLogger = _logger;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay);
                    using var scope = scopeFactory.CreateScope();
                    try
                    {
                        work(scope.ServiceProvider);
                    }
                    catch (Exception ex)
                    {
                        var logger = scope.ServiceProvider.GetService<ILogger<CompetitionController>>() ?? fallbackLogger;
                        logger.LogWarning(ex, "Background work failed: {Description}", description);
                    }
                }
                catch (Exception ex)
                {
                    fallbackLogger.LogWarning(ex, "Background scope creation failed: {Description}", description);
                }
            });
        }

        // Helper method to detect AJAX requests
        private bool IsAjaxRequest()
        {
            return Request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
                   Request.Headers["Accept"].ToString().Contains("application/json");
        }

        #region Registration System (Mock)

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterForCompetition(int competitionId,
            string selectedClasses = "", string startPreference = "Inget", int? targetMemberId = null,
            string startPreferencesJson = "", bool isSubCompetition = false,
            string teamAssignmentsJson = "")
        {
            try
            {
                // Get current member (always required for authentication)
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    var errorMsg = "Du måste vara inloggad för att anmäla dig till tävlingar.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                // Get competition details
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    var errorMsg = "Tävlingen kunde inte hittas.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                // VALIDATION: Check if competition is external
                var isExternal = competition.GetValue<bool>("isExternal");
                if (isExternal)
                {
                    var errorMsg = "Detta är en extern tävling. Anmälan sker via extern länk eller e-post.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                // Determine target member (who to register)
                IMember targetMember;
                var currentMemberData = _memberService.GetById(currentMember.Key);
                if (currentMemberData == null)
                {
                    var errorMsg = "Kunde inte hämta användardata. Vänligen logga in igen.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                if (targetMemberId.HasValue && targetMemberId.Value > 0)
                {
                    // Enhanced registration: registering someone else
                    targetMember = _memberService.GetById(targetMemberId.Value);
                    if (targetMember == null)
                    {
                        var errorMsg = "Den valda medlemmen kunde inte hittas.";
                        if (IsAjaxRequest())
                        {
                            return Json(new { success = false, message = errorMsg });
                        }
                        TempData["Error"] = errorMsg;
                        return RedirectToCurrentUmbracoPage();
                    }

                    // Authorization check: can current user register this target member?
                    bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    bool canRegisterTargetMember = false;

                    if (isSiteAdmin)
                    {
                        // Site admin can register anyone
                        canRegisterTargetMember = true;
                    }
                    else
                    {
                        // Competition manager can register anyone for their managed competition
                        bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);
                        if (isCompetitionManager)
                        {
                            canRegisterTargetMember = true;
                        }
                        else
                        {
                            // Club admin or skjutledare can register members from their clubs
                            var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int targetClubId))
                            {
                                canRegisterTargetMember = await _authorizationService.IsClubAdminForClub(targetClubId)
                                                       || await _authorizationService.IsSkjutledareForClub(targetClubId);
                            }
                        }

                        // Users can register themselves
                        if (targetMember.Id == currentMemberData.Id)
                        {
                            canRegisterTargetMember = true;
                        }
                    }

                    if (!canRegisterTargetMember)
                    {
                        var errorMsg = "Du har inte behörighet att anmäla den valda medlemmen.";
                        if (IsAjaxRequest())
                        {
                            return Json(new { success = false, message = errorMsg });
                        }
                        TempData["Error"] = errorMsg;
                        return RedirectToCurrentUmbracoPage();
                    }
                }
                else
                {
                    // Standard registration: registering self
                    targetMember = currentMemberData;
                }

                if (targetMember == null)
                {
                    var errorMsg = "Medlemsdata kunde inte hittas.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                var memberName = targetMember.Name;
                var primaryClubIdStr = targetMember.GetValue<string>("primaryClubId");
                int? clubId = null;
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var parsedClubId))
                {
                    clubId = parsedClubId;
                }

                // Validate selected class
                if (string.IsNullOrEmpty(selectedClasses))
                {
                    TempData["Error"] = "Du måste välja en skytteklass.";
                    return RedirectToCurrentUmbracoPage();
                }

                // Split selected classes (comma-separated string) into individual class IDs
                var selectedClassesList = selectedClasses
                    .Split(',')
                    .Select(c => c.Trim())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();

                IContent registrationsHub;
                var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
                var existingHub = competitionChildren.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                if (existingHub != null)
                {
                    registrationsHub = existingHub;
                }
                else
                {
                    try
                    {
                        var hubContentType = _contentTypeService.Get("competitionRegistrationsHub")
                                          ?? _contentTypeService.Get("contentPage");

                        var newHub = _contentService.Create("Anmälningar", competition, hubContentType.Alias);

                        if (hubContentType.Alias == "contentPage")
                        {
                            newHub.SetValue("pageTitle", "Anmälningar");
                            newHub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
                        }

                        try { _contentService.Save(newHub); }
                        catch (Exception ex) when (IsDocumentUrlTimeout(ex))
                        {
                            _logger.LogWarning("Hub saved but URL segment rebuild timed out (non-critical)");
                        }
                        registrationsHub = newHub;
                        // Publish hub in background. Runs in a fresh DI scope so the
                        // IContentService isn't the disposed request-scoped one (that
                        // path leaks ContentTree write locks — see EnqueueBackground).
                        var hubId = newHub.Id;
                        EnqueueBackground(TimeSpan.FromSeconds(10), sp =>
                        {
                            var contentService = sp.GetRequiredService<IContentService>();
                            var hub = contentService.GetById(hubId);
                            if (hub != null) contentService.Publish(hub, new[] { "*" }, -1);
                        }, $"publish registrationsHub {hubId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "REGISTRATION: Exception during hub creation, falling back to competition");
                        registrationsHub = competition;
                    }
                }

                // Parse start preferences (support both single preference and per-class preferences)
                var startPreferencesDict = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(startPreferencesJson))
                {
                    try
                    {
                        startPreferencesDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(startPreferencesJson)
                            ?? new Dictionary<string, string>();
                    }
                    catch
                    {
                        _logger.LogWarning("Failed to parse startPreferencesJson, using default preference");
                    }
                }

                // Parse Direktplacering config and team assignments
                var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                var teamAssignments = new Dictionary<string, int>();
                if (dpConfig != null && !string.IsNullOrEmpty(teamAssignmentsJson))
                {
                    try
                    {
                        teamAssignments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(teamAssignmentsJson)
                            ?? new Dictionary<string, int>();
                    }
                    catch
                    {
                        _logger.LogWarning("Failed to parse teamAssignmentsJson for competition {CompetitionId}", competitionId);
                    }
                }

                // Validate: Direktplacering requires team assignments for all classes
                if (dpConfig != null && teamAssignments.Count == 0)
                {
                    var errorMsg = "Du måste välja skjutlag för varje vapengrupp.";
                    if (IsAjaxRequest())
                        return Json(new { success = false, message = errorMsg });
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                // Build shooting classes array with per-class preferences and team assignments
                var shootingClassEntries = selectedClassesList.Select(sc => new ShootingClassEntry
                {
                    Class = sc,
                    StartPreference = startPreferencesDict.ContainsKey(sc) ? startPreferencesDict[sc] : startPreference,
                    TeamNumber = teamAssignments.TryGetValue(sc, out var tn) ? tn : null
                }).ToList();

                var shootingClassesJson = CompetitionRegistrationDocument.SerializeShootingClasses(shootingClassEntries);

                // Check if registration already exists — reuse the hub we already found
                IContent existingRegistration = null;
                if (registrationsHub.Id != competition.Id)
                {
                    var hubRegistrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 500, out _);
                    existingRegistration = hubRegistrations.FirstOrDefault(r =>
                        r.ContentType.Alias == "competitionRegistration" &&
                        r.GetValue<int>("memberId") == targetMember.Id &&
                        r.GetValue<int>("competitionId") == competitionId);
                }

                IContent registration;
                bool isUpdate = false;
                decimal oldFee = 0;
                decimal newFee = 0;

                // Calculate new fee (per-class base/junior + optional deltävling surcharge)
                newFee = RegistrationFeeCalculator.Calculate(competition, selectedClassesList, isSubCompetition);

                if (existingRegistration != null)
                {
                    // Update existing registration
                    _logger.LogInformation("Found existing registration (ID: {RegId}) for member {MemberId}. Updating with classes: {Classes}",
                        existingRegistration.Id, targetMember.Id, string.Join(", ", selectedClassesList));

                    // Calculate old fee from existing classes + stored isSubCompetition flag
                    var oldClassesJson = existingRegistration.GetValue<string>("shootingClasses");
                    if (!string.IsNullOrEmpty(oldClassesJson))
                    {
                        try
                        {
                            var oldClassEntries = CompetitionRegistrationDocument.DeserializeShootingClasses(oldClassesJson);
                            var oldClassNames = oldClassEntries
                                .Select(e => e.Class)
                                .Where(c => !string.IsNullOrEmpty(c))
                                .ToList();
                            var oldIsSubCompetition = existingRegistration.GetValue<bool>("isSubCompetition");
                            oldFee = RegistrationFeeCalculator.Calculate(competition, oldClassNames, oldIsSubCompetition);
                        }
                        catch
                        {
                            _logger.LogWarning("Failed to parse old shooting classes JSON for registration {RegId}", existingRegistration.Id);
                            oldFee = 0;
                        }
                    }

                    // A paid registration is NO LONGER blocked from changes. Adding/swapping/removing
                    // classes is reconciled after save via PaymentService.ReconcileRegistrationInvoiceAsync
                    // (below) under the delta/top-up model: Paid invoices are never touched, a new Pending
                    // top-up invoice is minted for any additional amount owed, and if the fee drops to or
                    // below what's already paid, any leftover Pending is cancelled (a refund, if owed, is
                    // handled manually by the organizer). The client is told the outstanding amount via
                    // `amountDue` in the response so it can offer a Swish top-up.

                    registration = existingRegistration;
                    isUpdate = true;
                }
                else
                {
                    // Create new registration
                    var registrationName = $"{memberName} - {DateTime.Now:yyyy-MM-dd}";
                    registration = _contentService.Create(registrationName, registrationsHub, "competitionRegistration");
                    _logger.LogInformation("Creating new registration for member {MemberId} with classes: {Classes}",
                        targetMember.Id, string.Join(", ", selectedClassesList));

                    // Set properties that don't change (only set on creation)
                    registration.SetValue("competitionId", competitionId);
                    registration.SetValue("memberId", targetMember.Id);
                    registration.SetValue("memberName", memberName);
                    registration.SetValue("isActive", true); // Set registration as active by default
                    if (clubId.HasValue)
                    {
                        registration.SetValue("clubId", clubId.Value);
                    }
                }

                // Set/update properties that can change
                registration.SetValue("shootingClasses", shootingClassesJson);
                registration.SetValue("registrationDate", DateTime.Now); // Update to current timestamp
                registration.SetValue("registeredBy", currentMemberData.Name); // Track who performed the registration/update
                if (registration.HasProperty("isSubCompetition"))
                    registration.SetValue("isSubCompetition", isSubCompetition);

                // Direktplacering: validate team availability under lock before saving
                if (dpConfig != null)
                {
                    var semaphore = _registrationLocks.GetOrAdd(competitionId, _ => new SemaphoreSlim(1, 1));
                    if (!await semaphore.WaitAsync(TimeSpan.FromSeconds(10)))
                    {
                        var errorMsg = "Anmälningen kunde inte genomföras just nu, försök igen.";
                        if (IsAjaxRequest())
                            return Json(new { success = false, message = errorMsg });
                        TempData["Error"] = errorMsg;
                        return RedirectToCurrentUmbracoPage();
                    }
                    try
                    {
                        // Re-check availability inside lock
                        var availability = BuildTeamAvailability(competitionId, competition, dpConfig);
                        var availabilityJson = System.Text.Json.JsonSerializer.Serialize(availability);
                        var availabilityData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(availabilityJson);

                        if (availabilityData.TryGetProperty("teams", out var teamsEl))
                        {
                            foreach (var entry in shootingClassEntries)
                            {
                                if (!entry.TeamNumber.HasValue) continue;
                                foreach (var teamEl in teamsEl.EnumerateArray())
                                {
                                    if (teamEl.GetProperty("teamNumber").GetInt32() == entry.TeamNumber.Value)
                                    {
                                        var remaining = teamEl.GetProperty("positionsRemaining").GetInt32();
                                        // If updating, the existing registration's spots are already counted,
                                        // so we get an extra spot per class that was in the same team
                                        var existingInSameTeam = 0;
                                        if (isUpdate && existingRegistration != null)
                                        {
                                            var oldClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(
                                                existingRegistration.GetValue<string>("shootingClasses") ?? "");
                                            existingInSameTeam = oldClasses.Count(c => c.TeamNumber == entry.TeamNumber.Value);
                                        }

                                        if (remaining + existingInSameTeam <= 0)
                                        {
                                            var errorMsg = $"Skjutlag {entry.TeamNumber.Value} är fullt. Välj ett annat skjutlag.";
                                            if (IsAjaxRequest())
                                                return Json(new { success = false, message = errorMsg, teamFull = true });
                                            TempData["Error"] = errorMsg;
                                            return RedirectToCurrentUmbracoPage();
                                        }
                                        break;
                                    }
                                }
                            }
                        }

                        // Save inside lock to prevent race conditions
                        try { _contentService.Save(registration); }
                        catch (Exception ex) when (IsDocumentUrlTimeout(ex))
                        {
                            _logger.LogWarning("Registration saved but URL segment rebuild timed out (non-critical) for regId={RegId}", registration.Id);
                        }

                        InvalidateDirektplaceringCache(competitionId);

                        // Auto-update the start list in background, in a fresh DI scope.
                        // The Regenerate(int) overload re-fetches the competition from the
                        // background scope's own IContentService — passing the IContent from
                        // the request scope (which is being disposed) would carry the same
                        // stale-connection bug as the publish path above.
                        var compId = competitionId;
                        EnqueueBackground(TimeSpan.FromSeconds(5), sp =>
                        {
                            var dpService = sp.GetRequiredService<DirektplaceringStartListService>();
                            dpService.Regenerate(compId);
                        }, $"DP start list regenerate {compId}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                else
                {
                // Save content — the DB transaction commits before notifications fire,
                // so even if DocumentUrlService times out, the data IS persisted.
                try
                {
                    _contentService.Save(registration);
                }
                catch (Exception ex) when (IsDocumentUrlTimeout(ex))
                {
                    // SqlBulkCopy timeout in DocumentUrlService — data was saved, URL rebuild is non-critical
                    _logger.LogWarning("Registration saved but URL segment rebuild timed out (non-critical) for regId={RegId}", registration.Id);
                }
                }
                // Delayed fire-and-forget publish. Runs in a fresh DI scope (the previous
                // capture-_contentService pattern is what was leaking ContentTree write
                // locks (-333) on Simply.com — confirmed by the production trace log).
                var registrationId_forPublish = registration.Id;
                EnqueueBackground(TimeSpan.FromSeconds(10), sp =>
                {
                    var contentService = sp.GetRequiredService<IContentService>();
                    var logger = sp.GetRequiredService<ILogger<CompetitionController>>();
                    var content = contentService.GetById(registrationId_forPublish);
                    if (content != null)
                    {
                        contentService.Publish(content, new[] { "*" }, -1);
                        logger.LogInformation("Background publish completed for registration {RegId}", registrationId_forPublish);
                    }
                }, $"publish registration {registrationId_forPublish}");

                // Eager invoice: every new fee-bearing registration gets its Pending invoice
                // up front (instead of one being lazily minted when a payment option is chosen).
                // Runs in a fresh DI scope after the publish; idempotent + best-effort, so a
                // racing "Betala med Swish" click that creates the invoice first is harmless.
                //
                // CRITICAL: PaymentService.CreateInvoiceAsync reads the published cache via
                // IUmbracoContextAccessor.GetRequiredUmbracoContext(), which THROWS on a
                // background thread (the Umbraco context is per-HTTP-request). Without the
                // EnsureUmbracoContext() wrapper below the eager invoice silently failed —
                // the in-request paths (late walk-in, "Hantera betalning" → EnsureInvoice)
                // worked because they already had a request context. Establish one here.
                if (!isUpdate)
                {
                    var compIdForInvoice = competitionId;
                    EnqueueBackground(TimeSpan.FromSeconds(12), sp =>
                    {
                        var contextFactory = sp.GetRequiredService<IUmbracoContextFactory>();
                        using var contextRef = contextFactory.EnsureUmbracoContext();
                        var paymentService = sp.GetRequiredService<PaymentService>();
                        paymentService.EnsureRegistrationInvoiceAsync(compIdForInvoice, registrationId_forPublish)
                            .GetAwaiter().GetResult();
                    }, $"ensure invoice for registration {registrationId_forPublish}");
                }
                else if (oldFee != newFee)
                {
                    // Reconcile the invoice to the new fee whenever the registration changes
                    // (class added/removed, or deltävling toggled). Uses the shared delta/top-up
                    // model so an unpaid invoice is patched/created/cancelled to match — replacing
                    // the old cancel-only logic that left the registration without a correct invoice.
                    var compIdForInvoice = competitionId;
                    EnqueueBackground(TimeSpan.FromSeconds(12), sp =>
                    {
                        var contextFactory = sp.GetRequiredService<IUmbracoContextFactory>();
                        using var contextRef = contextFactory.EnsureUmbracoContext();
                        var paymentService = sp.GetRequiredService<PaymentService>();
                        paymentService.ReconcileRegistrationInvoiceAsync(compIdForInvoice, registrationId_forPublish)
                            .GetAwaiter().GetResult();
                    }, $"reconcile invoice for registration {registrationId_forPublish}");
                }

                int registrationId = registration.Id;
                bool feeChanged = oldFee != newFee;
                var createdRegistrations = !isUpdate ? selectedClassesList : new List<string>();
                var updatedRegistrations = isUpdate ? selectedClassesList : new List<string>();

                // Build success message
                var successMessages = new List<string>();

                if (createdRegistrations.Any())
                {
                    var classesText = string.Join(", ", createdRegistrations);
                    if (targetMemberId.HasValue && targetMemberId.Value != currentMemberData.Id)
                    {
                        successMessages.Add($"{memberName} har anmälts till tävlingen i klasserna: {classesText}");
                    }
                    else
                    {
                        successMessages.Add($"Du har anmält dig till tävlingen i klasserna: {classesText}");
                    }
                }

                if (updatedRegistrations.Any())
                {
                    var classesText = string.Join(", ", updatedRegistrations);
                    if (targetMemberId.HasValue && targetMemberId.Value != currentMemberData.Id)
                    {
                        successMessages.Add($"Uppdaterade anmälan för {memberName} i klasserna: {classesText}");
                    }
                    else
                    {
                        successMessages.Add($"Din anmälan har uppdaterats i klasserna: {classesText}");
                    }
                }

                if (successMessages.Any())
                {
                    var successMessage = string.Join(" ", successMessages);

                    // Append team info for Direktplacering registrations
                    if (dpConfig != null && teamAssignments.Count > 0)
                    {
                        var teamInfoParts = teamAssignments.Select(ta =>
                        {
                            var team = dpConfig.Teams.FirstOrDefault(t => t.TeamNumber == ta.Value);
                            return team != null ? $"{ta.Key} → Skjutlag {ta.Value} ({team.StartTime}-{team.EndTime})" : $"{ta.Key} → Skjutlag {ta.Value}";
                        });
                        successMessage += " Placering: " + string.Join(", ", teamInfoParts);
                    }

                    // Outstanding amount the shooter still owes AFTER this change, under the delta/
                    // top-up model (full fee minus everything already Paid). The client uses this to
                    // decide whether to offer a Swish top-up (>0) or just confirm (0 — e.g. a swap, a
                    // removal, or an already-fully-paid registration). Read-only; best-effort.
                    decimal amountDue = newFee;
                    try
                    {
                        var paymentSvc = HttpContext?.RequestServices?.GetService(typeof(PaymentService)) as PaymentService;
                        if (paymentSvc != null)
                            amountDue = paymentSvc.GetInvoiceTotalsForRegistration(competitionId, registrationId).Outstanding;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not compute amountDue for registration {RegId}", registrationId);
                    }

                    if (IsAjaxRequest())
                    {
                        return Json(new
                        {
                            success = true,
                            message = successMessage,
                            registrationId = registrationId,
                            isUpdate = isUpdate,
                            feeChanged = feeChanged,
                            oldFee = oldFee,
                            newFee = newFee,
                            amountDue = amountDue,
                            teamAssignments = dpConfig != null ? teamAssignments : null
                        });
                    }
                    TempData["Success"] = successMessage;
                }
                else
                {
                    var errorMsg = "Ett fel uppstod vid skapandet/uppdateringen av anmälningarna.";
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                }

                return RedirectToCurrentUmbracoPage();
            }
            catch (Exception ex)
            {
                var errorMsg = $"Ett fel uppstod vid anmälan: {ex.Message}";
                _logger.LogError(ex, "Registration error for competition {CompetitionId}", competitionId);
                if (IsAjaxRequest())
                {
                    return Json(new { success = false, message = errorMsg });
                }
                TempData["Error"] = errorMsg;
                return RedirectToCurrentUmbracoPage();
            }
        }

        private string GetShootingClassName(string classId)
        {
            // Convert class ID to class name - this would typically query the shooting classes
            // For now, return the ID as the class name
            return classId;
        }

        private IContent GetOrCreateRegistrationsFolder(IContent competition)
        {
            // Look for existing registrations folder
            var childContents = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out var totalRecords);
            var existingFolder = childContents.FirstOrDefault(x => x.Name == "Registrations" || x.Name == "Anmälningar");
            if (existingFolder != null)
            {
                // Ensure folder is published (fixes issue where folder exists but is unpublished)
                if (!existingFolder.Published)
                {
                    _logger.LogInformation("Publishing existing unpublished registrations folder {FolderId} for competition {CompetitionId}",
                        existingFolder.Id, competition.Id);
                    var publishResult = _contentService.Publish(existingFolder, new[] { "*" }, -1);
                    if (!publishResult.Success)
                    {
                        _logger.LogWarning("Failed to publish existing registrations folder {FolderId} for competition {CompetitionId}",
                            existingFolder.Id, competition.Id);
                    }
                }
                return existingFolder;
            }

            // Create new registrations folder using a basic content type
            var folder = _contentService.Create("Anmälningar", competition.Id, "contentPage");
            var saveResult = _contentService.Save(folder);
            if (saveResult.Success)
            {
                var publishResult = _contentService.Publish(folder, new[] { "*" }, -1);
                if (!publishResult.Success)
                {
                    _logger.LogWarning("Failed to publish new registrations folder for competition {CompetitionId}", competition.Id);
                }
            }
            return folder;
        }

        [HttpGet]
        public async Task<IActionResult> GetRegistrationStatus(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { isRegistered = false, canRegister = false, message = "Du måste vara inloggad." });
                }

                // Mock registration status
                return Json(new { isRegistered = false, canRegister = true, message = "Anmälan öppen" });
            }
            catch (Exception)
            {
                return Json(new { isRegistered = false, canRegister = false, message = "Ett fel uppstod." });
            }
        }

        #endregion

        #region Results Entry System (Mock)

        [HttpGet]
        public async Task<IActionResult> GetResultsEntry(int competitionId, int? registrationId = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Create mock shot entry view model
                var entry = new PrecisionShotEntryViewModel
                {
                    RegistrationId = 1001,
                    SeriesId = 1,
                    SeriesNumber = 1,
                    SeriesType = "Precision",
                    CompetitionName = "Test Competition 2025",
                    MemberName = currentMember.Name ?? "Test Skytt",
                    MemberClass = "Träningsklass",
                    MaxPossible = 50,
                    IsReadOnly = false,
                    IsCompleted = false
                };

                // Initialize with empty shots
                entry.InitializeShots();

                return Json(new { success = true, data = entry });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveShotResults(PrecisionShotEntryViewModel model)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Mock save success
                return Json(new { success = true, message = "Resultaten har sparats!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CalculateSeriesTotal([FromBody] List<ShotEntryRow> shots)
        {
            try
            {
                if (shots == null || !shots.Any())
                {
                    return Json(new { success = false, message = "Inga skott att beräkna." });
                }

                var total = shots.Sum(s => s.ShotPoints);
                var innerTens = shots.Count(s => s.ShotValue == "X");
                var tens = shots.Count(s => s.ShotValue == "10" || s.ShotValue == "X");
                var percentage = (total / 109.0m) * 100;

                return Json(new
                {
                    success = true,
                    total = total,
                    innerTens = innerTens,
                    tens = tens,
                    percentage = percentage
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        #endregion

        #region Leaderboard System (Mock)

        [HttpGet]
        public async Task<IActionResult> GetCompetitionLeaderboard(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();

                // Create mock leaderboard data
                var participants = new[]
                {
                    new {
                        position = 1,
                        memberName = "Anna Andersson",
                        memberClass = "Damklass",
                        club = "Halmstads PSK",
                        total = 48,
                        innerTens = 3,
                        tens = 5,
                        percentage = 96.0m
                    },
                    new {
                        position = 2,
                        memberName = "Björn Karlsson",
                        memberClass = "Öppenklass",
                        club = "Varbergs SK",
                        total = 45,
                        innerTens = 2,
                        tens = 4,
                        percentage = 90.0m
                    },
                    new {
                        position = 3,
                        memberName = currentMember?.Name ?? "Test Skytt",
                        memberClass = "Träningsklass",
                        club = "HPSK",
                        total = 42,
                        innerTens = 1,
                        tens = 3,
                        percentage = 84.0m
                    }
                };

                return Json(new { success = true, data = participants });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        #endregion

        #region Management Dashboard (Mock)

        [HttpGet]
        public async Task<IActionResult> GetCompetitionDashboard(int competitionId)
        {
            try
            {
                var dashboardData = new
                {
                    competitionName = "Test Competition 2025",
                    totalRegistrations = 15,
                    completedResults = 8,
                    pendingResults = 7,
                    averageScore = 42.8m,
                    topScore = 48,
                    lastUpdated = DateTime.Now
                };

                return Json(new { success = true, data = dashboardData });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        #endregion

        #region Enhanced Registration APIs

        [HttpGet]
        public async Task<IActionResult> GetCurrentUserRegistrationInfo()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                // Get member details
                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Member data not found" });
                }

                // Get user roles once
                var roles = _memberService.GetAllRoles(memberData.Id);
                var rolesList = roles?.ToList() ?? new List<string>();

                // Log roles for debugging
                Console.WriteLine($"User {memberData.Name} roles: {string.Join(", ", rolesList)}");

                // Check if user is site admin
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                // Check if user is club admin and get their club ID
                var clubAdminRole = rolesList.FirstOrDefault(r => r.StartsWith("ClubAdmin_"));
                bool isClubAdmin = clubAdminRole != null;

                // Check if user is skjutledare
                var skjutledareRole = rolesList.FirstOrDefault(r => r.StartsWith("Skjutledare_"));
                bool isSkjutledare = skjutledareRole != null;

                int? clubId = null;
                string clubName = "";

                // Site Admins take precedence - they can manage all clubs but we still return their personal info for pre-selection
                if (isSiteAdmin)
                {
                    // Site admin - get their personal club info for pre-selection but they can manage all clubs
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int adminClubId))
                    {
                        clubId = adminClubId;
                        clubName = _clubService.GetClubNameById(clubId.Value) ?? $"Club {clubId}";
                    }
                    else if (isClubAdmin && clubAdminRole != null)
                    {
                        // Fallback: If admin doesn't have primaryClubId, use their club admin club
                        var clubIdStr = clubAdminRole.Replace("ClubAdmin_", "");
                        if (int.TryParse(clubIdStr, out int fallbackClubId))
                        {
                            clubId = fallbackClubId;
                            clubName = _clubService.GetClubNameById(clubId.Value) ?? $"Club {clubId}";
                        }
                    }
                }
                else if (isClubAdmin && clubAdminRole != null)
                {
                    // Club admin only - get their specific club ID
                    var clubIdStr = clubAdminRole.Replace("ClubAdmin_", "");
                    if (int.TryParse(clubIdStr, out int extractedClubId))
                    {
                        clubId = extractedClubId;
                        clubName = _clubService.GetClubNameById(clubId.Value) ?? $"Club {clubId}";
                    }
                }
                else if (isSkjutledare && skjutledareRole != null)
                {
                    // Skjutledare - get their club ID from the role
                    var clubIdStr = skjutledareRole.Replace("Skjutledare_", "");
                    if (int.TryParse(clubIdStr, out int extractedClubId))
                    {
                        clubId = extractedClubId;
                        clubName = _clubService.GetClubNameById(clubId.Value) ?? $"Club {clubId}";
                    }
                }
                else
                {
                    // Regular member - get their primary club
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                    {
                        clubId = primaryClubId;
                        clubName = _clubService.GetClubNameById(clubId.Value) ?? $"Club {clubId}";
                    }
                }

                // Site admin role takes precedence, then club admin, then skjutledare
                string userRole = isSiteAdmin ? "admin" : (isClubAdmin ? "clubAdmin" : (isSkjutledare ? "skjutledare" : "member"));

                return Json(new
                {
                    success = true,
                    role = userRole,
                    memberId = memberData.Id,
                    memberName = memberData.Name,
                    email = memberData.Email,
                    clubId = clubId,
                    clubName = clubName
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error getting user info: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClubsForRegistration()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Member data not found" });
                }

                // Allow site admins, club admins, and skjutledare to load all clubs
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool hasAccess = isSiteAdmin;

                if (!hasAccess)
                {
                    var managedClubIds = await _authorizationService.GetManagedClubIds();
                    if (managedClubIds.Count > 0) hasAccess = true;
                }
                if (!hasAccess)
                {
                    var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
                    if (skjutledareClubIds.Count > 0) hasAccess = true;
                }

                if (!hasAccess)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // PERFORMANCE FIX: Use ClubService which has caching, instead of full tree traversal
                var allClubs = _clubService.GetAllClubs();
                var clubs = allClubs
                    .Where(c => c.IsActive)
                    .Select(club => new
                    {
                        id = club.Id,
                        name = club.Name
                    })
                    .OrderBy(c => c.name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), ignoreCase: true))
                    .ToList();

                return Json(new { success = true, clubs = clubs });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClubMembers(int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Member data not found" });
                }

                // Check if user can access club members for registration
                // Club admins and skjutledare can load members from any club (for cross-club registration)
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool canAccess = isSiteAdmin;

                if (!canAccess)
                {
                    var managedClubIds = await _authorizationService.GetManagedClubIds();
                    if (managedClubIds.Count > 0) canAccess = true;
                }
                if (!canAccess)
                {
                    var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
                    if (skjutledareClubIds.Count > 0) canAccess = true;
                }

                if (!canAccess)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // PERFORMANCE FIX: Cache club members for 2 minutes to avoid repeated full member scans
                var cacheKey = $"club_members_{clubId}";
                var clubMembers = AppCaches.RuntimeCache.GetCacheItem(cacheKey, () =>
                {
                    // Get all regular members (not clubs) that belong to this club
                    var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                    return allMembers
                        .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved)
                        .Where(m =>
                        {
                            var primaryClubId = m.GetValue<string>("primaryClubId");
                            var memberClubIds = m.GetValue<string>("memberClubIds");

                            // Check primary club
                            if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out int primary))
                            {
                                if (primary == clubId) return true;
                            }

                            // Check additional clubs
                            if (!string.IsNullOrEmpty(memberClubIds))
                            {
                                var additionalClubIds = memberClubIds.Split(',').Select(id => id.Trim());
                                if (additionalClubIds.Contains(clubId.ToString())) return true;
                            }

                            return false;
                        })
                        .Select(member => new
                        {
                            id = member.Id,
                            name = member.Name,
                            email = member.Email
                        })
                        .OrderBy(m => m.name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), ignoreCase: true))
                        .ToList();
                }, TimeSpan.FromMinutes(2));

                return Json(new { success = true, members = clubMembers });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading club members: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetMemberDetails(int memberId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                var currentMemberData = _memberService.GetById(currentMember.Key);
                if (currentMemberData == null)
                {
                    return Json(new { success = false, message = "Current member data not found" });
                }

                // Get the target member
                var targetMember = _memberService.GetById(memberId);
                if (targetMember == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Check authorization - Site admin or club admin for target member's club
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool canAccess = false;

                if (isSiteAdmin)
                {
                    canAccess = true;
                }
                else
                {
                    // Club admin or skjutledare can access members from their clubs
                    var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int clubId))
                    {
                        canAccess = await _authorizationService.IsClubAdminForClub(clubId);
                        if (!canAccess)
                            canAccess = await _authorizationService.IsSkjutledareForClub(clubId);
                    }
                }

                if (!canAccess)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Get club name - use ClubService (clubs are Document Types, not Members!)
                var primaryClubIdStr = targetMember.GetValue<string>("primaryClubId");
                string clubName = "Unknown Club";
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                {
                    clubName = _clubService.GetClubNameById(primaryClubId) ?? $"Club {primaryClubId}";
                }

                return Json(new
                {
                    success = true,
                    member = new
                    {
                        id = targetMember.Id,
                        name = targetMember.Name,
                        email = targetMember.Email,
                        clubName = clubName
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading member details: " + ex.Message });
            }
        }

        #endregion

        #region Competition Management API Endpoints

        [HttpGet]
        public async Task<IActionResult> GetCompetitionRegistrations(int? competitionId = null)
        {
            try
            {
                if (!competitionId.HasValue)
                {
                    return Json(new { success = false, message = "Competition ID is required" });
                }

                // Check if user can manage this competition
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Access denied - not logged in" });
                }

                // Get the competition content
                var competition = UmbracoContext.Content.GetById(competitionId.Value);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
                }

                // Check authorization - Site Admin, Competition Manager, Club Admin, or Skjutledare
                bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId.Value);
                bool isClubAdmin = false;
                bool isSkjutledare = false;

                // Check if user is club admin or skjutledare for this competition's club
                var competitionClubId = competition.Value<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }

                if (!isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Json(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // PERFORMANCE FIX: Direct query to competition children instead of full tree traversal
                var competitionChildren = _contentService.GetPagedChildren(competitionId.Value, 0, 100, out _).ToList();
                var registrationsHub = competitionChildren
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

                // Get registrations directly from the hub (single query)
                var registrationContents = registrationsHub != null
                    ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                        .Where(c => c.ContentType.Alias == "competitionRegistration")
                        .ToList()
                    : new List<Umbraco.Cms.Core.Models.IContent>();

                // PERFORMANCE FIX: Batch load payment status - pre-load all invoices once
                var invoicesHub = competitionChildren
                    .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");

                var paymentStatusMap = new Dictionary<int, string>();
                var paymentInvoiceMap = new Dictionary<int, int>();        // registrationId -> invoiceId (newest actionable)
                var paymentAmountMap = new Dictionary<int, decimal>();     // registrationId -> actionable invoice amount
                var invoiceNumberMap = new Dictionary<int, string>();      // registrationId -> invoiceNumber (Swish reference)
                var transactionIdMap = new Dictionary<int, string>();      // registrationId -> existing transactionId (if any)
                var paidAmountMap = new Dictionary<int, decimal>();        // registrationId -> sum of actual paid amounts (fallback to billed)
                var pendingAmountMap = new Dictionary<int, decimal>();     // registrationId -> sum of all Pending invoices
                var hasVarianceMap = new Dictionary<int, bool>();          // registrationId -> any paid invoice where actual != billed
                var paymentSentMap = new Dictionary<int, (DateTime date, string by)>(); // registrationId -> payer "betalning anmäld" claim
                var invoiceToRegIds = new Dictionary<int, List<int>>();    // invoiceId -> registrationId(s) it covers (for mapping audit events back)
                if (invoicesHub != null)
                {
                    var allInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                        .Where(x => x.ContentType.Alias == "registrationInvoice")
                        .Where(x => x.GetValue<string>("paymentStatus") != "Cancelled")
                        .OrderByDescending(x => x.Id)
                        .ToList();

                    foreach (var invoice in allInvoices)
                    {
                        var invoiceStatus = CleanPaymentStatus(invoice.GetValue<string>("paymentStatus") ?? "Unknown");
                        var invoiceAmount = invoice.GetValue<decimal>("totalAmount");
                        var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? "";
                        var transactionId = invoice.GetValue<string>("transactionId") ?? "";
                        // actualPaidAmount is set when the cashier records a sum that differs from
                        // the billed total (cash rounding, partial settlement). Fall back to the
                        // billed amount when not set so legacy paid invoices still total correctly.
                        var actualPaid = invoice.GetValue<decimal?>("actualPaidAmount");
                        var paidContribution = actualPaid ?? invoiceAmount;
                        var paidVariance = actualPaid.HasValue && actualPaid.Value != invoiceAmount;

                        // Check new property first (registrationId - single integer)
                        var invoiceRegistrationId = invoice.GetValue<int>("registrationId");
                        if (invoiceRegistrationId > 0)
                        {
                            invoiceToRegIds[invoice.Id] = new List<int> { invoiceRegistrationId };

                            // Aggregate totals across ALL invoices for this registration
                            if (invoiceStatus == "Paid")
                            {
                                paidAmountMap[invoiceRegistrationId] = paidAmountMap.GetValueOrDefault(invoiceRegistrationId) + paidContribution;
                                if (paidVariance) hasVarianceMap[invoiceRegistrationId] = true;
                            }
                            else if (invoiceStatus == "Pending")
                                pendingAmountMap[invoiceRegistrationId] = pendingAmountMap.GetValueOrDefault(invoiceRegistrationId) + invoiceAmount;

                            // Actionable invoice = newest (first encountered due to OrderByDescending)
                            if (!paymentStatusMap.ContainsKey(invoiceRegistrationId))
                            {
                                paymentStatusMap[invoiceRegistrationId] = invoiceStatus;
                                paymentInvoiceMap[invoiceRegistrationId] = invoice.Id;
                                paymentAmountMap[invoiceRegistrationId] = invoiceAmount;
                                invoiceNumberMap[invoiceRegistrationId] = invoiceNumber;
                                transactionIdMap[invoiceRegistrationId] = transactionId;

                                var sentDate = invoice.GetValue<DateTime?>("paymentSentDate");
                                if (sentDate.HasValue)
                                    paymentSentMap[invoiceRegistrationId] = (sentDate.Value, invoice.GetValue<string>("paymentSentBy") ?? "");
                            }
                        }

                        // Also check relatedRegistrationIds for backward compatibility
                        var relatedIdsJson = invoice.GetValue<string>("relatedRegistrationIds") ?? "";
                        if (!string.IsNullOrEmpty(relatedIdsJson))
                        {
                            var registrationIds = ParseRegistrationIds(relatedIdsJson);
                            if (registrationIds.Count > 0 && !invoiceToRegIds.ContainsKey(invoice.Id))
                                invoiceToRegIds[invoice.Id] = registrationIds.ToList();
                            // For multi-registration invoices, split the amount evenly across the
                            // related registrations so the per-row totals don't double-count.
                            var perRegAmount = registrationIds.Count > 0
                                ? invoiceAmount / registrationIds.Count
                                : invoiceAmount;
                            // Split actual amount the same way for variance reporting.
                            var perRegActual = registrationIds.Count > 0
                                ? paidContribution / registrationIds.Count
                                : paidContribution;
                            foreach (var regId in registrationIds)
                            {
                                if (invoiceStatus == "Paid")
                                {
                                    paidAmountMap[regId] = paidAmountMap.GetValueOrDefault(regId) + perRegActual;
                                    if (paidVariance) hasVarianceMap[regId] = true;
                                }
                                else if (invoiceStatus == "Pending")
                                    pendingAmountMap[regId] = pendingAmountMap.GetValueOrDefault(regId) + perRegAmount;

                                if (!paymentStatusMap.ContainsKey(regId))
                                {
                                    paymentStatusMap[regId] = invoiceStatus;
                                    paymentInvoiceMap[regId] = invoice.Id;
                                    paymentAmountMap[regId] = perRegAmount;
                                    invoiceNumberMap[regId] = invoiceNumber;
                                    transactionIdMap[regId] = transactionId;
                                }
                            }
                        }
                    }
                }

                // Reminder activity: an EmailSent audit event means a payment reminder /
                // Swish-QR mail went out for that invoice. Surface the latest one + a count
                // per registration so the row can flag "påminnelse skickad" and tint its
                // history button. One read for the whole competition, then map back via
                // invoiceToRegIds (built above).
                var reminderMap = new Dictionary<int, (DateTime last, int count)>(); // registrationId -> (latest reminder, total reminders)
                try
                {
                    var auditEvents = await _auditService.GetForCompetitionAsync(competitionId.Value);
                    foreach (var ev in auditEvents.Where(e => e.EventType == InvoicePaymentEventTypes.EmailSent))
                    {
                        if (!invoiceToRegIds.TryGetValue(ev.InvoiceId, out var regIds)) continue;
                        foreach (var regId in regIds)
                        {
                            if (reminderMap.TryGetValue(regId, out var existing))
                                reminderMap[regId] = (existing.last >= ev.OccurredAt ? existing.last : ev.OccurredAt, existing.count + 1);
                            else
                                reminderMap[regId] = (ev.OccurredAt, 1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Reminder info is purely informational — never fail the list because of it.
                    _logger.LogWarning(ex, "Failed to load reminder audit events for competition {CompetitionId}", competitionId.Value);
                }

                // PERFORMANCE FIX: Batch load club names - collect all unique club IDs first
                var clubIdsToLoad = new HashSet<int>();
                var memberIdsNeedingClubLookup = new List<(Umbraco.Cms.Core.Models.IContent content, int memberId)>();

                foreach (var content in registrationContents)
                {
                    var clubId = content.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        clubIdsToLoad.Add(clubId);
                    }
                    else
                    {
                        var memberClubStr = content.GetValue<string>("memberClub");
                        if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                        {
                            clubIdsToLoad.Add(legacyClubId);
                        }
                        else if (string.IsNullOrWhiteSpace(memberClubStr))
                        {
                            var memberId = content.GetValue<int>("memberId");
                            if (memberId > 0)
                            {
                                memberIdsNeedingClubLookup.Add((content, memberId));
                            }
                        }
                    }
                }

                // Load member primary clubs for registrations that need it
                foreach (var (content, memberId) in memberIdsNeedingClubLookup)
                {
                    var member = _memberService.GetById(memberId);
                    var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                    {
                        clubIdsToLoad.Add(primaryClubId);
                    }
                }

                // Batch load all club names (ClubService already has caching, but this groups the calls)
                var clubNameMap = clubIdsToLoad.ToDictionary(
                    id => id,
                    id => _clubService.GetClubNameById(id) ?? $"Club {id}"
                );

                // Now build the registrations list with pre-loaded data
                var registrations = registrationContents
                    .Select(content =>
                    {
                        var memberId = content.GetValue<int>("memberId");

                        // Get club name from pre-loaded map
                        string clubName = "Unknown Club";
                        var clubId = content.GetValue<int>("clubId");

                        if (clubId > 0)
                        {
                            clubName = clubNameMap.TryGetValue(clubId, out var name) ? name : $"Club {clubId}";
                        }
                        else
                        {
                            var memberClubStr = content.GetValue<string>("memberClub");
                            if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                            {
                                clubName = clubNameMap.TryGetValue(legacyClubId, out var name) ? name : $"Club {legacyClubId}";
                            }
                            else if (!string.IsNullOrWhiteSpace(memberClubStr))
                            {
                                clubName = memberClubStr;
                            }
                            else if (memberId > 0)
                            {
                                var member = _memberService.GetById(memberId);
                                var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
                                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                                {
                                    clubName = clubNameMap.TryGetValue(primaryClubId, out var name) ? name : $"Club {primaryClubId}";
                                }
                            }
                        }

                        // Get payment status / invoice / amount from pre-loaded maps
                        var paymentStatus = paymentStatusMap.TryGetValue(content.Id, out var status) ? status : "No Invoice";
                        var invoiceId = paymentInvoiceMap.TryGetValue(content.Id, out var invId) ? invId : 0;
                        var paymentAmount = paymentAmountMap.TryGetValue(content.Id, out var amt) ? amt : 0m;
                        var invoiceNumber = invoiceNumberMap.TryGetValue(content.Id, out var invNum) ? invNum : "";
                        var existingTxnId = transactionIdMap.TryGetValue(content.Id, out var txnId) ? txnId : "";
                        var paidAmount = paidAmountMap.TryGetValue(content.Id, out var pa) ? pa : 0m;
                        var pendingAmount = pendingAmountMap.TryGetValue(content.Id, out var pe) ? pe : 0m;
                        var hasVariance = hasVarianceMap.TryGetValue(content.Id, out var hv) && hv;
                        DateTime? paymentSentDate = null;
                        string? paymentSentBy = null;
                        if (paymentSentMap.TryGetValue(content.Id, out var psInfo))
                        {
                            paymentSentDate = psInfo.date;
                            paymentSentBy = psInfo.by;
                        }

                        DateTime? lastReminderSentDate = null;
                        int reminderCount = 0;
                        if (reminderMap.TryGetValue(content.Id, out var remInfo))
                        {
                            lastReminderSentDate = remInfo.last;
                            reminderCount = remInfo.count;
                        }

                        // Get shooting classes (new JSON array format)
                        var shootingClassesJson = content.GetValue<string>("shootingClasses");
                        var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                        // Fallback for "Saknar betalning" rows: when no invoice exists yet
                        // the maps don't carry an amount, so the manage-payment modal shows
                        // 0 kr while the QR (which always recomputes via the calculator)
                        // shows the real fee. Surface the expected fee here so both surfaces
                        // agree — the invoice gets created lazily on QR-generate / mark-paid.
                        if (paymentAmount == 0m && invoiceId == 0)
                        {
                            var classIds = shootingClasses.Select(sc => sc.Class).ToList();
                            var isSubComp = content.GetValue<bool>("isSubCompetition");
                            paymentAmount = RegistrationFeeCalculator.Calculate(competition, classIds, isSubComp);
                        }

                        // Now that invoices are created eagerly, a fee-bearing registration with
                        // no invoice is an error/edge ("No Invoice" → "Saknar Faktura"); a 0-fee
                        // registration legitimately has none ("No Fee" → "Ingen avgift").
                        if (paymentStatus == "No Invoice")
                            paymentStatus = paymentAmount > 0m ? "No Invoice" : "No Fee";

                        // Convert class IDs to display names
                        var shootingClassesWithNames = shootingClasses.Select(sc => new
                        {
                            @class = sc.Class,
                            className = ShootingClasses.GetById(sc.Class)?.Name ?? sc.Class,
                            startPreference = sc.StartPreference,
                            teamNumber = sc.TeamNumber
                        }).ToList();

                        return new
                        {
                            id = content.Id,
                            memberId = memberId,
                            memberName = content.GetValue<string>("memberName") ?? "Unknown Member",
                            memberClub = clubName,
                            shootingClasses = shootingClassesWithNames,
                            registrationDate = content.GetValue<DateTime>("registrationDate"),
                            isActive = content.GetValue<bool>("isActive"),
                            paymentStatus = paymentStatus,
                            invoiceId = invoiceId,
                            paymentAmount = paymentAmount,
                            invoiceNumber = invoiceNumber,
                            transactionId = existingTxnId,
                            paidAmount = paidAmount,
                            pendingAmount = pendingAmount,
                            hasVariance = hasVariance,
                            isCheckedIn = content.GetValue<bool>("isCheckedIn"),
                            isSubCompetition = content.HasProperty("isSubCompetition") && content.GetValue<bool>("isSubCompetition"),
                            paymentSentDate = paymentSentDate,
                            paymentSentBy = paymentSentBy,
                            lastReminderSentDate = lastReminderSentDate,
                            reminderCount = reminderCount
                        };
                    })
                    .OrderBy(r => r.memberName)
                    .ToList();

                // Calculate statistics (count class entries, not registrations)
                var totalClassEntries = registrations.Sum(r => r.shootingClasses.Count);
                var uniqueMembers = registrations.Select(r => r.memberId).Distinct().Count();

                // Flatten all classes for breakdown
                var allClassEntries = registrations
                    .SelectMany(r => r.shootingClasses)
                    .ToList();

                var classBreakdown = allClassEntries
                    .GroupBy(c => c.className)
                    .Select(g => new { shootingClass = g.Key, count = g.Count() })
                    .OrderBy(x => x.shootingClass)
                    .ToList();

                var statistics = new
                {
                    totalRegistrations = totalClassEntries, // Total class entries
                    uniqueMembers = uniqueMembers,
                    activeRegistrations = registrations.Count(r => r.isActive),
                    classBreakdown = classBreakdown
                };

                return Json(new {
                    success = true,
                    registrations = registrations,
                    statistics = statistics,
                    // Name of the optional sub-competition (Deltävling), empty when the
                    // competition has none. Lets the Anmälningar tab show a Deltävling column
                    // (with a check on rows where the shooter opted in) only when relevant.
                    subCompetitionName = competition.Value<string>("subCompetitionName") ?? ""
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading registrations: " + ex.Message });
            }
        }

        /// <summary>
        /// "Visa anmälda skyttar" list — any LOGGED-IN member can see who is registered
        /// for a competition (name, club, classes only); anonymous visitors cannot.
        /// Intentionally NOT gated on competition-manager/club-admin like
        /// GetCompetitionRegistrations, which also exposes payment/invoice data and is
        /// admin-only. Using that admin endpoint for this modal made the list show
        /// "Inga anmälda skyttar" for ordinary shooters while only admins saw the list.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegisteredShooters(int competitionId)
        {
            try
            {
                // Logged-in only (no anonymous access), but open to every member —
                // shooters use it to verify their own registration.
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                if (UmbracoContext.Content.GetById(competitionId) == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
                }

                // Direct child query (no full-tree traversal). No r.Published filter —
                // registrations are Save()d synchronously and Publish() is deferred, so
                // requiring Published would hide freshly-registered shooters here too.
                var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
                var registrationsHub = competitionChildren
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

                var registrationContents = registrationsHub != null
                    ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                        .Where(c => c.ContentType.Alias == "competitionRegistration")
                        .ToList()
                    : new List<IContent>();

                // Batch-resolve club names (same precedence as the admin endpoint:
                // explicit clubId → legacy memberClub → member's primaryClubId).
                var clubIdsToLoad = new HashSet<int>();
                var memberIdsNeedingClubLookup = new List<(IContent content, int memberId)>();
                foreach (var content in registrationContents)
                {
                    var clubId = content.GetValue<int>("clubId");
                    if (clubId > 0) { clubIdsToLoad.Add(clubId); continue; }
                    var memberClubStr = content.GetValue<string>("memberClub");
                    if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                        clubIdsToLoad.Add(legacyClubId);
                    else if (string.IsNullOrWhiteSpace(memberClubStr))
                    {
                        var memberId = content.GetValue<int>("memberId");
                        if (memberId > 0) memberIdsNeedingClubLookup.Add((content, memberId));
                    }
                }
                foreach (var (_, memberId) in memberIdsNeedingClubLookup)
                {
                    var member = _memberService.GetById(memberId);
                    var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                        clubIdsToLoad.Add(primaryClubId);
                }
                var clubNameMap = clubIdsToLoad.ToDictionary(
                    id => id,
                    id => _clubService.GetClubNameById(id) ?? $"Club {id}");

                var registrations = registrationContents.Select(content =>
                {
                    var memberId = content.GetValue<int>("memberId");

                    string clubName = "Okänd klubb";
                    var clubId = content.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        clubName = clubNameMap.TryGetValue(clubId, out var name) ? name : $"Club {clubId}";
                    }
                    else
                    {
                        var memberClubStr = content.GetValue<string>("memberClub");
                        if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                            clubName = clubNameMap.TryGetValue(legacyClubId, out var name) ? name : $"Club {legacyClubId}";
                        else if (!string.IsNullOrWhiteSpace(memberClubStr))
                            clubName = memberClubStr;
                        else if (memberId > 0)
                        {
                            var member = _memberService.GetById(memberId);
                            var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                                clubName = clubNameMap.TryGetValue(primaryClubId, out var name) ? name : $"Club {primaryClubId}";
                        }
                    }

                    var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(
                        content.GetValue<string>("shootingClasses"));

                    var shootingClassesWithNames = shootingClasses.Select(sc => new
                    {
                        @class = sc.Class,
                        // ShootingClasses only knows the standard disciplines; Springskytte
                        // composite ids ("A-H 21") aren't there, so fall back to the raw id.
                        className = ShootingClasses.GetById(sc.Class)?.Name ?? sc.Class
                    }).ToList();

                    return new
                    {
                        memberId = memberId,
                        memberName = content.GetValue<string>("memberName") ?? "Okänd",
                        memberClub = clubName,
                        shootingClasses = shootingClassesWithNames
                    };
                })
                .OrderBy(r => r.memberName)
                .ToList();

                return Json(new { success = true, registrations = registrations });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading registrations: " + ex.Message });
            }
        }

        private IEnumerable<IContent> GetAllDescendants(IContent content)
        {
            yield return content;
            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
            foreach (var child in children)
            {
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Gets the payment status for a registration by checking invoices
        /// </summary>
        private string GetRegistrationPaymentStatus(int registrationId, int competitionId)
        {
            try
            {
                // Get the competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return "No Invoice";

                // Find the "Betalningar" (invoices) hub under the competition
                var children = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _);
                var invoicesHub = children.FirstOrDefault(x => x.ContentType.Alias == "registrationInvoicesHub");

                if (invoicesHub == null) return "No Invoice";

                // Get all invoices under the hub - filter out cancelled and sort by most recent
                var allInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, int.MaxValue, out _)
                    .Where(x => x.ContentType.Alias == "registrationInvoice")
                    .Where(x => x.GetValue<string>("paymentStatus") != "Cancelled")
                    .OrderByDescending(x => x.Id)
                    .ToList();

                // Search through invoices to find one for this registration
                foreach (var invoice in allInvoices)
                {
                    // Check new property first (registrationId - single integer)
                    var invoiceRegistrationId = invoice.GetValue<int>("registrationId");

                    if (invoiceRegistrationId > 0 && invoiceRegistrationId == registrationId)
                    {
                        // Found the invoice - return its payment status
                        var status = invoice.GetValue<string>("paymentStatus") ?? "Unknown";
                        // Clean up if it's in JSON array format like ["Paid"]
                        return CleanPaymentStatus(status);
                    }

                    // Fall back to old property (relatedRegistrationIds - JSON array) for backward compatibility
                    var relatedIdsJson = invoice.GetValue<string>("relatedRegistrationIds") ?? "";
                    if (!string.IsNullOrEmpty(relatedIdsJson))
                    {
                        var registrationIds = ParseRegistrationIds(relatedIdsJson);
                        if (registrationIds.Contains(registrationId))
                        {
                            var status = invoice.GetValue<string>("paymentStatus") ?? "Unknown";
                            return CleanPaymentStatus(status);
                        }
                    }
                }

                return "No Invoice";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Parses a JSON array of registration IDs from string format: "[123, 124, 125]"
        /// </summary>
        private List<int> ParseRegistrationIds(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int>();

            try
            {
                // Remove brackets and whitespace
                var trimmed = json.Trim().Trim('[', ']');
                if (string.IsNullOrWhiteSpace(trimmed)) return new List<int>();

                // Split by comma and parse each ID
                return trimmed.Split(',')
                    .Select(id => id.Trim())
                    .Where(id => int.TryParse(id, out _))
                    .Select(int.Parse)
                    .ToList();
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// Cleans payment status string, removing JSON array formatting if present
        /// Converts: ["Paid"] -> Paid, or ["Pending"] -> Pending
        /// </summary>
        private string CleanPaymentStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Unknown";

            // Remove JSON array brackets and quotes
            var cleaned = status.Trim().Trim('[', ']').Trim('"', '\'').Trim();

            return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
        }

        #endregion

        #region Hub Testing

        [HttpGet]
        [HttpPost]
        public IActionResult TestHubCreation(int competitionId)
        {
            try
            {
                _logger.LogInformation("TEST: TestHubCreation called with competitionId: {CompetitionId}", competitionId);

                if (competitionId <= 0)
                {
                    _logger.LogWarning("TEST: Invalid competition ID provided: {CompetitionId}", competitionId);
                    return Json(new { success = false, message = "Invalid competition ID" });
                }

                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("TEST: Competition not found: {CompetitionId}", competitionId);
                    return Json(new { success = false, message = "Competition not found" });
                }

                _logger.LogInformation("TEST: Starting hub creation test for competition {CompetitionId}", competitionId);

                // First check if document type exists
                var hubDocType = _contentTypeService.Get("competitionRegistrationsHub");
                var contentPageDocType = _contentTypeService.Get("contentPage");

                // Check for existing hub
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                var existingHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                var debugInfo = new {
                    competitionId = competition.Id,
                    competitionName = competition.Name,
                    competitionAlias = competition.ContentType.Alias,
                    childrenCount = children.Count(),
                    hubDocTypeFound = hubDocType != null,
                    contentPageDocTypeFound = contentPageDocType != null,
                    existingHubFound = existingHub != null,
                    existingHubId = existingHub?.Id,
                    existingHubName = existingHub?.Name,
                    existingHubAlias = existingHub?.ContentType.Alias
                };

                // Clean up any existing test hub
                var testHub = children.FirstOrDefault(c => c.Name == "TEST-Anmälningar");
                if (testHub != null)
                {
                    _contentService.Delete(testHub);
                }

                var hub = GetOrCreateRegistrationsHub(competition);

                if (hub == null)
                {
                    return Json(new { success = false, message = "Failed to create hub", debug = debugInfo });
                }

                var isActualHub = hub.Id != competition.Id;

                return Json(new {
                    success = true,
                    message = $"Hub created/found: {hub.Name} (ID: {hub.Id})",
                    hubId = hub.Id,
                    hubName = hub.Name,
                    hubAlias = hub.ContentType.Alias,
                    isActualHub = isActualHub,
                    isCompetitionFallback = !isActualHub,
                    debug = debugInfo
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestHubCreation");
                return Json(new { success = false, message = ex.Message });
            }
        }

        #endregion

        #region Hub Management

        private IContent? GetOrCreateRegistrationsHub(IContent competition)
        {
            try
            {
                _logger.LogInformation("Looking for registrations hub under competition {CompetitionId}", competition.Id);

                // First, check if a registrations hub already exists
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                _logger.LogInformation("Found {ChildCount} children under competition", children.Count());

                var existingHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                if (existingHub != null)
                {
                    _logger.LogInformation("Found existing registrations hub: {HubName} (ID: {HubId}, Alias: {Alias})", existingHub.Name, existingHub.Id, existingHub.ContentType.Alias);
                    return existingHub;
                }

                // Check if hub document type exists
                _logger.LogInformation("Checking for document type 'competitionRegistrationsHub'");
                var hubContentType = _contentTypeService.Get("competitionRegistrationsHub");
                if (hubContentType == null)
                {
                    // Create hub as a simple content page if specific type doesn't exist
                    _logger.LogWarning("Document type 'competitionRegistrationsHub' not found, using 'contentPage'");
                    hubContentType = _contentTypeService.Get("contentPage");

                    if (hubContentType == null)
                    {
                        _logger.LogError("No suitable document type found for registrations hub - RETURNING COMPETITION AS FALLBACK");
                        return competition; // Fall back to creating directly under competition
                    }
                }
                else
                {
                    _logger.LogInformation("Found document type 'competitionRegistrationsHub'");
                }

                // Create the hub
                var hubName = "Anmälningar";
                _logger.LogInformation("Creating hub '{HubName}' with document type '{Alias}' under competition {CompetitionId}", hubName, hubContentType.Alias, competition.Id);
                var hub = _contentService.Create(hubName, competition, hubContentType.Alias);

                // Set properties if it's a content page
                if (hubContentType.Alias == "contentPage")
                {
                    _logger.LogInformation("Setting properties for contentPage hub");
                    hub.SetValue("pageTitle", "Anmälningar");
                    hub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
                }
                else
                {
                    _logger.LogInformation("Setting properties for competitionRegistrationsHub");
                    // Set hub-specific properties if using proper hub document type
                    hub.SetValue("description", "Alla anmälningar för denna tävling.");
                    hub.SetValue("registrationDeadline", DateTime.Now.AddDays(30)); // Example deadline
                    hub.SetValue("maxParticipants", 100); // Example limit
                }

                _logger.LogInformation("Saving hub...");
                var saveResult = _contentService.Save(hub);
                if (saveResult.Success)
                {
                    _logger.LogInformation("Hub saved successfully, publishing...");
                    var publishResult = _contentService.Publish(hub, new[] { "*" }, -1);
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Created registrations hub '{HubName}' (ID: {HubId}) for competition {CompetitionId}", hubName, hub.Id, competition.Id);
                        return hub;
                    }
                    else
                    {
                        _logger.LogError("Failed to publish registrations hub - but returning saved hub anyway");
                        // Return the saved hub even if publishing failed
                        return hub;
                    }
                }
                else
                {
                    _logger.LogError("Failed to save registrations hub with {MessageCount} errors - returning competition as fallback", saveResult.EventMessages?.Count ?? 0);
                }

                _logger.LogError("Failed to create or publish registrations hub for competition {CompetitionId} - RETURNING COMPETITION AS FALLBACK", competition.Id);
                return competition; // Fall back to creating directly under competition
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating registrations hub for competition {CompetitionId} - RETURNING COMPETITION AS FALLBACK", competition.Id);
                return competition; // Fall back to creating directly under competition
            }
        }

        /// <summary>
        /// Checks if an exception is a SqlBulkCopy timeout from DocumentUrlService.
        /// This occurs during Save() but the content data IS committed (transaction commits before notifications).
        /// </summary>
        private static bool IsDocumentUrlTimeout(Exception ex)
        {
            // AggregateException wrapping SqlException with error -2 (timeout)
            var inner = ex is AggregateException agg ? agg.InnerException : ex;
            if (inner is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number == -2)
            {
                // Verify it's from DocumentUrlService by checking stack trace
                return ex.ToString().Contains("DocumentUrlRepository") || ex.ToString().Contains("DocumentUrlService");
            }
            return false;
        }

        /// <summary>
        /// Finds an existing registration for a member in a specific shooting class
        /// Returns null if no registration exists
        /// </summary>
        private IContent FindExistingRegistration(int competitionId, int memberId, string shootingClass = null)
        {
            try
            {
                // Get competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return null;

                // Find registrations hub
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                var registrationsHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                if (registrationsHub == null) return null;

                // Get registration nodes under the hub with pagination to avoid timeout
                // Use reasonable page size and check multiple pages if needed
                const int pageSize = 500;
                int pageIndex = 0;
                long totalRecords;

                do
                {
                    var pageRegistrations = _contentService.GetPagedChildren(registrationsHub.Id, pageIndex, pageSize, out totalRecords);

                    // Find matching registration in this page
                    // NEW: Match by competitionId + memberId only (one registration per user per competition)
                    var existingRegistration = pageRegistrations
                        .FirstOrDefault(r =>
                            r.ContentType.Alias == "competitionRegistration" &&
                            r.GetValue<int>("memberId") == memberId &&
                            r.GetValue<int>("competitionId") == competitionId);

                    if (existingRegistration != null)
                    {
                        return existingRegistration;
                    }

                    pageIndex++;
                } while ((pageIndex * pageSize) < totalRecords);

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding existing registration for member {MemberId}, competition {CompetitionId}, class {ShootingClass}",
                    memberId, competitionId, shootingClass);
                return null;
            }
        }

        /// <summary>
        /// Extracts the weapon class code (e.g., "A", "A_Opt", "B", "C", "R") from a shooting class ID
        /// via the authoritative ShootingClasses registry. Returns null if the id is unknown
        /// (we deliberately do NOT fall back to the first character — that would misclassify A_opt_X).
        /// </summary>
        private string GetWeaponClassFromShootingClass(string shootingClassId)
        {
            if (string.IsNullOrEmpty(shootingClassId)) return null;
            var code = ShootingClasses.GetWeaponClassCode(shootingClassId);
            return string.IsNullOrEmpty(code) ? null : code;
        }

        /// <summary>
        /// Determines the C-class subcategory (Regular, Veteran, Ladies, Junior)
        /// </summary>
        private string GetCClassSubcategory(string shootingClassId)
        {
            if (string.IsNullOrEmpty(shootingClassId)) return null;

            // Regular C-classes: C1, C2, C3
            if (shootingClassId == "C1" || shootingClassId == "C2" || shootingClassId == "C3")
                return "Regular";

            // Veteran C-classes: C_Vet_Y, C_Vet_A
            if (shootingClassId.Contains("Vet"))
                return "Veteran";

            // Ladies C-classes: C1_Dam, C2_Dam, C3_Dam
            if (shootingClassId.Contains("Dam"))
                return "Ladies";

            // Junior C-class: C_Jun
            if (shootingClassId.Contains("Jun"))
                return "Junior";

            return "Regular"; // Default to Regular if can't determine
        }

        /// <summary>
        /// Finds weapon class conflicts within a list of shooting classes
        /// Returns list of conflicting class pairs (for display purposes)
        /// NEW: Updated for multi-class registration system
        /// </summary>
        private List<string> FindWeaponClassConflicts(List<string> shootingClasses, bool allowDualCClassRegistration)
        {
            var conflicts = new List<string>();

            try
            {
                if (shootingClasses == null || shootingClasses.Count <= 1)
                    return conflicts; // No conflicts possible with 0 or 1 class

                // Check each class against all others
                for (int i = 0; i < shootingClasses.Count; i++)
                {
                    for (int j = i + 1; j < shootingClasses.Count; j++)
                    {
                        string class1 = shootingClasses[i];
                        string class2 = shootingClasses[j];

                        string weapon1 = GetWeaponClassFromShootingClass(class1);
                        string weapon2 = GetWeaponClassFromShootingClass(class2);

                        if (weapon1 != weapon2) continue; // Different weapons, no conflict

                        // Same weapon class detected - apply special rules

                        // C-Class special rule: Allow dual registration from different subcategories
                        if (weapon1 == "C" && allowDualCClassRegistration)
                        {
                            string subcat1 = GetCClassSubcategory(class1);
                            string subcat2 = GetCClassSubcategory(class2);

                            // If same subcategory, it's a conflict
                            if (subcat1 == subcat2)
                            {
                                conflicts.Add($"{class1} and {class2} are both {weapon1}-class {subcat1}");
                                continue;
                            }

                            // Different subcategories - check if there are more than 2 C-classes total
                            var cClassCount = shootingClasses
                                .Count(c => GetWeaponClassFromShootingClass(c) == "C");

                            if (cClassCount > 2)
                            {
                                conflicts.Add($"More than 2 C-classes registered (limit is 2 from different subcategories)");
                            }
                            // else: Different subcategory and <= 2 C-classes = allowed, no conflict
                        }
                        else
                        {
                            // All other cases: same weapon class = conflict
                            conflicts.Add($"{class1} and {class2} are both {weapon1}-class");
                        }
                    }
                }

                return conflicts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding weapon class conflicts for classes: {Classes}",
                    string.Join(", ", shootingClasses ?? new List<string>()));
                return conflicts;
            }
        }

        /// <summary>
        /// Gets existing registrations for a specific member in a competition
        /// Used to prevent duplicate registrations and show visual indicators
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberRegistrationsForCompetition(int competitionId, int? memberId = null)
        {
            try
            {
                // Get current member
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var currentMemberData = _memberService.GetById(currentMember.Key);

                // Determine target member (who to check registrations for)
                int targetMemberId;
                if (memberId.HasValue && memberId.Value > 0)
                {
                    // Check if current user has permission to view this member's registrations.
                    // Site admins, and anyone who manages THIS competition (competition manager,
                    // or admin/skjutledare of the hosting club/region), may view any member's
                    // registration in it — same tier as GetCompetitionRegistrations. Otherwise a
                    // club admin may view members of their own club, and members may view their own.
                    bool canViewMember = await _authorizationService.IsCurrentUserAdminAsync();

                    if (!canViewMember)
                        canViewMember = await _authorizationService.IsCompetitionManager(competitionId);

                    if (!canViewMember)
                    {
                        var hostingClubId = _contentService.GetById(competitionId)?.GetValue<int>("clubId") ?? 0;
                        if (hostingClubId > 0)
                        {
                            canViewMember = await _authorizationService.IsClubAdminForClub(hostingClubId)
                                || await _authorizationService.IsSkjutledareForClub(hostingClubId);
                        }
                    }

                    if (!canViewMember)
                    {
                        // Club admin of the target member's own club, or the member themselves.
                        var targetMember = _memberService.GetById(memberId.Value);
                        if (targetMember != null)
                        {
                            if (targetMember.Id == currentMemberData.Id)
                            {
                                canViewMember = true;
                            }
                            else
                            {
                                var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                                if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int targetClubId))
                                {
                                    canViewMember = await _authorizationService.IsClubAdminForClub(targetClubId);
                                }
                            }
                        }
                    }

                    if (!canViewMember)
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att se denna medlems anmälningar." });
                    }

                    targetMemberId = memberId.Value;
                }
                else
                {
                    // Default to current user
                    targetMemberId = currentMemberData.Id;
                }

                // Get competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                // Find registrations hub
                var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                var registrationsHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub" ||
                    c.Name.Contains("Anmälningar") ||
                    c.Name.Contains("Registration"));

                var existingRegistrations = new List<object>();

                if (registrationsHub != null)
                {
                    // Get all registration nodes under the hub (including unpublished)
                    // GetPagedChildren only returns published nodes, so we query all descendants
                    var allPublishedRegistrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, int.MaxValue, out _);

                    // Get unpublished registrations by querying all descendants
                    var allDescendants = _contentService.GetPagedDescendants(registrationsHub.Id, 0, int.MaxValue, out _);

                    // Combine published and unpublished, then deduplicate by Id
                    var allRegistrations = allPublishedRegistrations
                        .Union(allDescendants)
                        .Where(r => r.ContentType.Alias == "competitionRegistration")
                        .GroupBy(r => r.Id)
                        .Select(g => g.First())
                        .ToList();

                    // Filter by memberId and competitionId
                    existingRegistrations = allRegistrations
                        .Where(r => r.GetValue<int>("memberId") == targetMemberId &&
                                   r.GetValue<int>("competitionId") == competitionId)
                        .Select(r =>
                        {
                            // Get the NEW shootingClasses JSON array
                            var shootingClassesJson = r.GetValue<string>("shootingClasses");
                            var shootingClasses = string.IsNullOrEmpty(shootingClassesJson)
                                ? new List<ShootingClassEntry>()
                                : CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                            return new
                            {
                                id = r.Id,
                                shootingClasses = shootingClasses,  // ✅ Returns array of class objects
                                registrationDate = r.GetValue<DateTime>("registrationDate"),
                                isPublished = r.Published,
                                isSubCompetition = r.HasProperty("isSubCompetition") && r.GetValue<bool>("isSubCompetition")
                            };
                        })
                        .ToList<object>();
                }

                return Json(new
                {
                    success = true,
                    registrations = existingRegistrations,
                    count = existingRegistrations.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting member registrations for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Replaces an existing registration with a new shooting class
        /// Used when user wants to change from one class to another within same weapon type
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReplaceRegistration(int registrationIdToDelete, int competitionId, int? memberId, string newShootingClass, string startPreference)
        {
            try
            {
                // Get current member
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var currentMemberData = _memberService.GetById(currentMember.Key);

                // Determine target member
                IMember targetMember = currentMemberData; // Default to current member
                if (memberId.HasValue && memberId.Value > 0)
                {
                    // Check authorization for registering on behalf of another member
                    bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    bool canRegisterForMember = isSiteAdmin;

                    // Get the target member
                    var requestedMember = _memberService.GetById(memberId.Value);
                    if (requestedMember == null)
                    {
                        return Json(new { success = false, message = "Målmedlemmen kunde inte hittas." });
                    }

                    if (!isSiteAdmin)
                    {
                        // Club admins can register for members in their clubs
                        var targetMemberClubId = requestedMember.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int targetClubId))
                        {
                            canRegisterForMember = await _authorizationService.IsClubAdminForClub(targetClubId);
                        }
                    }

                    if (!canRegisterForMember)
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att ersätta denna anmälan." });
                    }

                    targetMember = requestedMember;
                }

                // Get the registration to delete
                var oldRegistration = _contentService.GetById(registrationIdToDelete);
                if (oldRegistration == null)
                {
                    return Json(new { success = false, message = "Den befintliga anmälan kunde inte hittas." });
                }

                // Verify ownership
                var regMemberId = oldRegistration.GetValue<int>("memberId");
                if (regMemberId != targetMember.Id)
                {
                    return Json(new { success = false, message = "Du kan inte ersätta en annan medlems anmälan." });
                }

                var oldShootingClass = oldRegistration.GetValue<string>("shootingClass");

                // Delete old registration
                _contentService.Unpublish(oldRegistration);
                _contentService.Delete(oldRegistration);

                // Create new registration (reuse logic from RegisterForCompetition)
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                // Get or create registrations hub
                var registrationsHub = GetOrCreateRegistrationsHub(competition);
                if (registrationsHub == null)
                {
                    return Json(new { success = false, message = "Kunde inte skapa anmälningshub." });
                }

                // Create new registration
                var memberName = $"{targetMember.GetValue<string>("firstName")} {targetMember.GetValue<string>("lastName")}";
                var registrationName = $"{memberName} - {newShootingClass} - {DateTime.Now:yyyy-MM-dd}";
                var newRegistration = _contentService.Create(registrationName, registrationsHub, "competitionRegistration");

                // Set properties
                newRegistration.SetValue("competitionId", competitionId);
                newRegistration.SetValue("memberId", targetMember.Id);
                newRegistration.SetValue("memberName", memberName);
                newRegistration.SetValue("shootingClass", newShootingClass);
                newRegistration.SetValue("startPreference", startPreference ?? "Inget");
                newRegistration.SetValue("registrationDate", DateTime.Now);
                newRegistration.SetValue("registeredBy", currentMemberData.Name);

                // Save and publish
                _contentService.Save(newRegistration);
                _contentService.Publish(newRegistration, new[] { "*" }, -1);

                return Json(new
                {
                    success = true,
                    message = $"Anmälan ersatt: {oldShootingClass} → {newShootingClass}",
                    oldClass = oldShootingClass,
                    newClass = newShootingClass,
                    registrationId = newRegistration.Id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error replacing registration {RegistrationId} with new class {NewClass}",
                    registrationIdToDelete, newShootingClass);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Admin endpoint to cleanup duplicate registrations
        /// Keeps the most recent registration for each (competitionId, memberId, shootingClass) combination
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CleanupDuplicateRegistrations(int? competitionId = null)
        {
            try
            {
                // Check if user is site admin
                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                if (!isSiteAdmin)
                {
                    return Json(new { success = false, message = "Du måste vara administratör för att köra denna funktion." });
                }

                var duplicatesFound = 0;
                var duplicatesRemoved = 0;
                var errors = new List<string>();

                // Get all competitions or specific competition
                IEnumerable<IContent> competitions;
                if (competitionId.HasValue)
                {
                    var comp = _contentService.GetById(competitionId.Value);
                    if (comp == null)
                    {
                        return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                    }
                    competitions = new[] { comp };
                }
                else
                {
                    // Get all competitions (this could be optimized with proper query)
                    var competitionType = _contentTypeService.Get("competition");
                    if (competitionType == null)
                    {
                        return Json(new { success = false, message = "Competition document type not found." });
                    }
                    var allContent = _contentService.GetPagedOfType(competitionType.Id, 0, int.MaxValue, out var total, null);
                    competitions = allContent;
                }

                foreach (var competition in competitions)
                {
                    // Find registrations hub
                    var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                    var registrationsHub = children.FirstOrDefault(c =>
                        c.ContentType.Alias == "competitionRegistrationsHub" ||
                        c.Name.Contains("Anmälningar") ||
                        c.Name.Contains("Registration"));

                    if (registrationsHub == null) continue;

                    // Get all registration nodes under the hub
                    var allRegistrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, int.MaxValue, out _)
                        .Where(r => r.ContentType.Alias == "competitionRegistration")
                        .ToList();

                    // Group by (competitionId, memberId) only - NEW: One registration per user per competition
                    var registrationGroups = allRegistrations
                        .GroupBy(r => new
                        {
                            CompetitionId = r.GetValue<int>("competitionId"),
                            MemberId = r.GetValue<int>("memberId")
                        })
                        .Where(g => g.Count() > 1); // Only groups with duplicates

                    foreach (var group in registrationGroups)
                    {
                        duplicatesFound += group.Count() - 1;

                        // Sort by registrationDate descending (most recent first)
                        var orderedRegistrations = group
                            .OrderByDescending(r => r.GetValue<DateTime>("registrationDate"))
                            .ToList();

                        // Keep the first (most recent), delete the rest
                        var toKeep = orderedRegistrations.First();
                        var toDelete = orderedRegistrations.Skip(1).ToList();

                        foreach (var duplicate in toDelete)
                        {
                            try
                            {
                                _logger.LogInformation("Deleting duplicate registration ID {RegId} for member {MemberId} (keeping ID {KeepId})",
                                    duplicate.Id, group.Key.MemberId, toKeep.Id);

                                // Unpublish first, then delete
                                _contentService.Unpublish(duplicate);
                                _contentService.Delete(duplicate);
                                duplicatesRemoved++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error deleting duplicate registration ID {RegId}", duplicate.Id);
                                errors.Add($"Failed to delete registration ID {duplicate.Id}: {ex.Message}");
                            }
                        }
                    }
                }

                return Json(new
                {
                    success = true,
                    duplicatesFound = duplicatesFound,
                    duplicatesRemoved = duplicatesRemoved,
                    errors = errors,
                    message = $"Cleanup complete. Found {duplicatesFound} duplicates, removed {duplicatesRemoved}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup of duplicate registrations");
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        #endregion

        #region Direktplacering

        [HttpGet]
        public IActionResult GetTeamAvailability(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                var config = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                if (config == null)
                    return Json(new { success = false, message = "Direktplacering är inte aktiverat för denna tävling." });

                // Use cache to avoid repeated traversal during peak registration
                var cacheKey = $"dp_availability_{competitionId}";
                var result = AppCaches.RuntimeCache.GetCacheItem(cacheKey, () =>
                {
                    return BuildTeamAvailability(competitionId, competition, config);
                }, TimeSpan.FromSeconds(30));

                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting team availability for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        private object BuildTeamAvailability(int competitionId, IContent competition, DirektplaceringConfig config)
        {
            // Find registrations hub and count occupancy per team
            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub" ||
                c.Name.Contains("Anmälningar") ||
                c.Name.Contains("Registration"));

            // Count team assignments from existing registrations
            var teamCounts = new Dictionary<int, Dictionary<string, int>>(); // teamNumber -> { weaponGroup -> count }
            foreach (var team in config.Teams)
            {
                teamCounts[team.TeamNumber] = new Dictionary<string, int>();
            }

            if (registrationsHub != null)
            {
                var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList();

                foreach (var reg in registrations)
                {
                    var classesJson = reg.GetValue<string>("shootingClasses");
                    if (string.IsNullOrWhiteSpace(classesJson)) continue;

                    var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
                    foreach (var entry in classes)
                    {
                        if (!entry.TeamNumber.HasValue) continue;
                        var teamNum = entry.TeamNumber.Value;
                        if (!teamCounts.ContainsKey(teamNum))
                            teamCounts[teamNum] = new Dictionary<string, int>();

                        // Get weapon group letter (first character of class ID)
                        var weaponGroup = entry.Class.Length > 0 ? entry.Class[0].ToString().ToUpper() : "?";
                        if (!teamCounts[teamNum].ContainsKey(weaponGroup))
                            teamCounts[teamNum][weaponGroup] = 0;
                        teamCounts[teamNum][weaponGroup]++;
                    }
                }
            }

            var teams = config.Teams.Select(team =>
            {
                var counts = teamCounts.TryGetValue(team.TeamNumber, out var c) ? c : new Dictionary<string, int>();
                var totalUsed = counts.Values.Sum();
                return new
                {
                    teamNumber = team.TeamNumber,
                    startTime = team.StartTime,
                    endTime = team.EndTime,
                    positionsTotal = team.Positions,
                    positionsUsed = totalUsed,
                    positionsRemaining = team.Positions - totalUsed,
                    isFull = totalUsed >= team.Positions,
                    label = team.Label ?? "",
                    allowedWeaponClasses = team.AllowedWeaponClasses,
                    weaponClassCounts = counts
                };
            }).ToList();

            return new
            {
                success = true,
                allowMixedClasses = config.AllowMixedClasses,
                teams
            };
        }

        private void InvalidateDirektplaceringCache(int competitionId)
        {
            AppCaches.RuntimeCache.ClearByKey($"dp_availability_{competitionId}");
        }

        #endregion
    }
}
