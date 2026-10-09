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
        private readonly MemberClubService _memberClubService;
        private readonly RegistrationClubPropagationService _clubPropagationService;
        // Used to create a fresh DI scope for deferred background work. The controller's
        // own scoped services (_contentService, _dpStartListService) get disposed when the
        // HTTP request ends — capturing them in a Task.Run lambda that fires later leaks
        // half-broken connections, which manifests as DataReader exceptions during
        // ContentService.Publish + orphaned ContentTree write locks (id -333) that
        // freeze the whole site. EnqueueBackground builds a fresh scope per task.
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;

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
            MemberClubService memberClubService,
            RegistrationClubPropagationService clubPropagationService,
            IServiceScopeFactory scopeFactory,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberClubService = memberClubService;
            _clubPropagationService = clubPropagationService;
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
            _registrations = registrations;
        }

        /// <summary>
        /// Run <paramref name="work"/> in a background task after an optional delay,
        /// using a fresh DI scope. Use this for any background work that touches
        /// scoped services (IContentService, DirektplaceringStartListService, etc.)
        /// to avoid leaking write locks via disposed-scope-captured services.
        ///
        /// ⚠️ The fresh DI scope is NOT enough on its own, and a missing
        /// <see cref="ExecutionContext.SuppressFlow"/> took production down for three hours on
        /// 2026-08-29. Umbraco's AMBIENT scope is an <c>AsyncLocal</c>, so it rides the execution
        /// context into the background thread regardless of which DI scope the work resolves its
        /// services from. <c>ContentService.Publish</c> then ran on this thread against the
        /// request's scope while the request thread was disposing it — "The Scope … being disposed
        /// is not the Ambient Scope", plus "There is already an open DataReader associated with
        /// this Connection". The request's scope never completed, so the ContentTree write lock
        /// (umbracoLock id -333) stayed held by an abandoned transaction: every subsequent
        /// ContentService read waited 60 s and failed with "Failed to acquire read lock for id:
        /// -333", until the app pool was recycled.
        ///
        /// Suppressing the flow is safe here because the delegates resolve everything from
        /// <c>sp</c> and build their own UmbracoContext where they need one (see the eager-invoice
        /// call sites). Do not add work here that reads HttpContext or an inherited UmbracoContext.
        /// </summary>
        private void EnqueueBackground(TimeSpan delay, Action<IServiceProvider> work, string description)
        {
            var scopeFactory = _scopeFactory;
            var fallbackLogger = _logger;

            // The suppression must wrap the point where the task is CREATED — that is when the
            // execution context is captured. Restored on dispose, so the request thread is unaffected.
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay);
                        using var scope = scopeFactory.CreateScope();
                        try
                        {
                            WarnIfAmbientScopeFlowed(scope.ServiceProvider, fallbackLogger, description);
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
        }

        /// <summary>
        /// Self-proving guard for the suppression above. If a sixth fire-and-forget site is ever added
        /// without <see cref="ExecutionContext.SuppressFlow"/>, this says so in the log the moment the
        /// work starts — instead of the failure surfacing hours later as a site-wide -333 lock timeout,
        /// which is how it was found the first time. Read-only and best-effort: a diagnostic must never
        /// be the reason the background work fails.
        /// </summary>
        private static void WarnIfAmbientScopeFlowed(IServiceProvider sp, ILogger fallbackLogger, string description)
        {
            try
            {
                var accessor = sp.GetService<Umbraco.Cms.Infrastructure.Scoping.IScopeAccessor>();
                if (accessor?.AmbientScope != null)
                {
                    var logger = sp.GetService<ILogger<CompetitionController>>() ?? fallbackLogger;
                    logger.LogWarning(
                        "Background work {Description} started with an AMBIENT UMBRACO SCOPE still attached. " +
                        "The request's scope has flowed into this thread and content locks can be orphaned " +
                        "(see the -333 incident 2026-08-29). Wrap the Task.Run in ExecutionContext.SuppressFlow().",
                        description);
                }
            }
            catch
            {
                // Diagnostics only.
            }
        }

        // Helper method to detect AJAX requests
        private bool IsAjaxRequest()
        {
            return Request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
                   Request.Headers["Accept"].ToString().Contains("application/json");
        }

        #region Registration System (Mock)

        /// <param name="representingClubId">
        /// The club the shooter competes FOR in this competition. Optional, and only honoured when
        /// the shooter is actually a member of it — a shooter with several club memberships may
        /// enter for any of them, and everyone else has exactly one option, so the picker is hidden
        /// and this arrives absent. Absent / not a member of it → the primary club, as before.
        /// </param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterForCompetition(int competitionId,
            string selectedClasses = "", string startPreference = "Inget", int? targetMemberId = null,
            string startPreferencesJson = "", bool isSubCompetition = false,
            string teamAssignmentsJson = "", int? representingClubId = null, bool? clubPays = null)
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

                // VALIDATION: startlistan publicerad + arrangören har valt att stänga självanmälan.
                //
                // ⚠️ Detta MÅSTE finnas på servern och inte bara som en släckt knapp. Skytten har ofta
                // tävlingssidan öppen i en flik medan startlistan publiceras; utan kontrollen här
                // landar just den anmälan som grinden finns för att förhindra — en anmälan ingen har
                // placerat, upptäckt först av täckningspanelen eller av skytten på plats.
                //
                // ⚠️ Funktionärer är UNDANTAGNA, och det är inte en artighet: arrangören anmäler
                // rutinmässigt någon annan via samma modal (targetMemberId nedan), och en spärr utan
                // undantag hade tagit bort deras enda självbetjäningsväg samtidigt som den stängde
                // skyttarnas. Går via HasCompetitionStaffAccessAsync, som bär både klubbvärdad och
                // kretsvärdad tävling — en handskriven clubId-koll låser ute kretsen på ett SM.
                // Registreringsbordets egen väg (RegistrationAdmin/AddLateRegistration) är en annan
                // endpoint och berörs inte alls.
                var registrationGate = HttpContext?.RequestServices
                    .GetService(typeof(HpskSite.Services.RegistrationGate.StartListRegistrationGate))
                    as HpskSite.Services.RegistrationGate.StartListRegistrationGate;
                if (registrationGate != null && registrationGate.IsClosed(competitionId)
                    && !await _authorizationService.HasCompetitionStaffAccessAsync(competitionId))
                {
                    var errorMsg = HpskSite.Services.RegistrationGate.StartListRegistrationGate.ShooterMessage;
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

                // Which club does this shooter compete FOR? Defaults to their primary club; a
                // shooter who belongs to several clubs may pick another of theirs (Eva is primary
                // at X but shoots some disciplines for Y). Anything they are not a member of is
                // silently ignored rather than rejected — see MemberClubService.ResolveRegistrationClubId.
                var resolvedClubId = _memberClubService.ResolveRegistrationClubId(targetMember, representingClubId);
                int? clubId = resolvedClubId > 0 ? resolvedClubId : null;

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

                // ⚠️ Regeln fanns bara i klienten — servern tog emot vad som helst. En anmälan
                //    ersätter hela klasslistan (även vid uppdatering), så listan här är hela svaret.
                var classRuleError = ClassRegistrationRule.Conflict(selectedClassesList,
                    competition.GetValue<bool>(ClassRegistrationRule.PropertyAlias));
                if (classRuleError != null)
                {
                    if (IsAjaxRequest()) return Json(new { success = false, message = classRuleError });
                    TempData["Error"] = classRuleError;
                    return RedirectToCurrentUmbracoPage();
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

                // Validate: Direktplacering requires a skjutlag for EVERY selected class — not just
                // one. Checking only "any assignment" let a two-class registration through with the
                // second class silently unplaced. Also refuse the same skjutlag for two classes
                // (e.g. two C classes on a dual-C competition): a shooter cannot stand in one
                // skjutlag twice. The client checks both first; this is the backstop.
                if (dpConfig != null)
                {
                    string ClassName(string id) => HpskSite.Models.ShootingClasses.GetById(id)?.Name ?? id;

                    var missingTeam = selectedClassesList
                        .Where(sc => !teamAssignments.TryGetValue(sc, out var t) || t <= 0)
                        .Select(ClassName)
                        .ToList();

                    string? errorMsg = null;
                    if (missingTeam.Count > 0)
                    {
                        errorMsg = $"Du måste välja skjutlag för {string.Join(" och ", missingTeam)}.";
                    }
                    else
                    {
                        var doubled = selectedClassesList
                            .GroupBy(sc => teamAssignments[sc])
                            .FirstOrDefault(g => g.Count() > 1);
                        if (doubled != null)
                            errorMsg = $"{string.Join(" och ", doubled.Select(ClassName))} kan inte skjutas i samma skjutlag. Välj olika skjutlag.";
                    }

                    if (errorMsg != null)
                    {
                        if (IsAjaxRequest())
                            return Json(new { success = false, message = errorMsg });
                        TempData["Error"] = errorMsg;
                        return RedirectToCurrentUmbracoPage();
                    }
                }

                // Build shooting classes array with per-class preferences and team assignments
                var shootingClassEntries = selectedClassesList.Select(sc => new ShootingClassEntry
                {
                    Class = sc,
                    StartPreference = startPreferencesDict.ContainsKey(sc) ? startPreferencesDict[sc] : startPreference,
                    TeamNumber = teamAssignments.TryGetValue(sc, out var tn) ? tn : null
                }).ToList();

                // Befintlig anmälan — via anmälningslagringen (sajtstrukturen fas 1a), som läser alla
                // anmälningar oavsett om de publicerats.
                var existingRegistration = _registrations.FindForMember(competitionId, targetMember.Id);

                HpskSite.Models.Registrations.RegistrationRecord registration;
                bool isUpdate = false;
                decimal oldFee = 0;
                decimal newFee = 0;
                // Set when re-registering moved an EXISTING registration to a different club, so the
                // change can be pushed out to any start list that already snapshotted the old one.
                int? registrationClubChangedTo = null;

                // Calculate new fee (per-class base/junior + optional deltävling surcharge)
                newFee = RegistrationFeeCalculator.Calculate(competition, selectedClassesList, isSubCompetition);

                if (existingRegistration != null)
                {
                    _logger.LogInformation("Found existing registration (ID: {RegId}) for member {MemberId}. Updating with classes: {Classes}",
                        existingRegistration.Id, targetMember.Id, string.Join(", ", selectedClassesList));

                    // Old fee from the existing classes + stored isSubCompetition flag
                    var oldClassNames = existingRegistration.ClassIds;
                    if (oldClassNames.Count > 0)
                        oldFee = RegistrationFeeCalculator.Calculate(competition, oldClassNames, existingRegistration.IsSubCompetition);

                    // A paid registration is NO LONGER blocked from changes. Adding/swapping/removing
                    // classes is reconciled after save via PaymentService.ReconcileRegistrationInvoiceAsync
                    // (below) under the delta/top-up model: Paid invoices are never touched, a new Pending
                    // top-up invoice is minted for any additional amount owed, and if the fee drops to or
                    // below what's already paid, any leftover Pending is cancelled (a refund, if owed, is
                    // handled manually by the organizer). The client is told the outstanding amount via
                    // `amountDue` in the response so it can offer a Swish top-up.

                    registration = existingRegistration.Clone();
                    isUpdate = true;
                }
                else
                {
                    registration = new HpskSite.Models.Registrations.RegistrationRecord
                    {
                        CompetitionId = competitionId,
                        MemberId = targetMember.Id,
                        MemberName = memberName,
                        IsActive = true,
                        ClubId = clubId ?? 0
                    };
                    _logger.LogInformation("Creating new registration for member {MemberId} with classes: {Classes}",
                        targetMember.Id, string.Join(", ", selectedClassesList));
                }

                // clubId is re-applied on the UPDATE path too, but ONLY when the caller actually
                // asked for a club. Re-registering (adding a class, say) must not silently drag a
                // shooter who was correctly filed under their second club back to their primary
                // one — which is exactly what stamping the resolved default here unconditionally
                // would do, because the resolver falls back to primary whenever nothing is asked for.
                if (isUpdate && representingClubId is > 0 && clubId.HasValue && registration.ClubId != clubId.Value)
                {
                    registration.ClubId = clubId.Value;
                    registrationClubChangedTo = clubId.Value;
                }

                // A re-registration always writes the class LIST (as it always has), even when the
                // old registration carried a single legacy scalar class.
                registration.Classes = shootingClassEntries;
                registration.ClassesFromLegacyScalar = false;
                registration.RegistrationDate = DateTime.Now; // Update to current timestamp
                registration.RegisteredBy = currentMemberData.Name; // Track who performed the registration/update
                registration.IsSubCompetition = isSubCompetition;

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
                                        var existingInSameTeam = isUpdate && existingRegistration != null
                                            ? existingRegistration.Classes.Count(c => c.TeamNumber == entry.TeamNumber.Value)
                                            : 0;

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
                        _registrations.Save(registration);

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
                    _registrations.Save(registration);
                }

                // ⚠️ Ingen publicering längre (sajtstrukturen fas 1a). Anmälan publicerades förut
                // 10 s senare i bakgrunden, och det misslyckades tyst under en opublicerad hubb — så
                // den publicerade cachen höll bara en delmängd. Alla läsare går nu via lagringen.
                var registrationIdForInvoice = registration.Id;

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
                        paymentService.EnsureRegistrationInvoiceAsync(compIdForInvoice, registrationIdForInvoice)
                            .GetAwaiter().GetResult();
                    }, $"ensure invoice for registration {registrationIdForInvoice}");
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
                        paymentService.ReconcileRegistrationInvoiceAsync(compIdForInvoice, registrationIdForInvoice)
                            .GetAwaiter().GetResult();
                    }, $"reconcile invoice for registration {registrationIdForInvoice}");
                }

                // A club correction on an existing registration must reach the start lists that
                // already snapshotted the old club — the result list reads the start list first,
                // so the registration alone is not enough once a list exists. Best-effort: the
                // registration is committed either way.
                if (registrationClubChangedTo is > 0)
                {
                    try
                    {
                        var newClubName = _clubService.GetClubNameById(registrationClubChangedTo.Value);
                        if (!string.IsNullOrWhiteSpace(newClubName))
                            await _clubPropagationService.PropagateAsync(competitionId, targetMember.Id, newClubName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Could not propagate club change for member {MemberId} in competition {CompetitionId}",
                            targetMember.Id, competitionId);
                    }
                }

                int registrationId = registration.Id;
                bool feeChanged = oldFee != newFee;
                var createdRegistrations = !isUpdate ? selectedClassesList : new List<string>();
                var updatedRegistrations = isUpdate ? selectedClassesList : new List<string>();

                // Build success message
                var successMessages = new List<string>();

                if (createdRegistrations.Any())
                {
                    var classesText = string.Join(", ", createdRegistrations.Select(HpskSite.Models.ShootingClasses.DisplayName));
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
                    var classesText = string.Join(", ", updatedRegistrations.Select(HpskSite.Models.ShootingClasses.DisplayName));
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

                    // ⚠️⚠️ DEN NYA MODELLEN (P3/P4): avgiften är begärda betalningar i liggaren, och de
                    // måste finnas NU — betalsteget visar dem i samma andetag som anmälan bekräftas.
                    // Synken är idempotent; bakgrundsjobbet ovan kör den en gång till utan skada.
                    string paymentModel = "legacy";
                    object? ledgerFees = null;
                    try
                    {
                        var models = HttpContext?.RequestServices?.GetService(typeof(HpskSite.Services.CompetitionFees.CompetitionPaymentModelService))
                            as HpskSite.Services.CompetitionFees.CompetitionPaymentModelService;
                        var fees = HttpContext?.RequestServices?.GetService(typeof(HpskSite.Services.CompetitionFees.CompetitionFeeService))
                            as HpskSite.Services.CompetitionFees.CompetitionFeeService;
                        if (models != null && fees != null && models.IsLedger(competitionId))
                        {
                            paymentModel = "ledger";
                            var settings = fees.GetSettings(competitionId);
                            // "Klubben betalar" är en hint och gäller bara typer arrangören tillåtit —
                            // en skytt kan inte binda sin klubb genom att kryssa i en ruta.
                            if (clubPays.HasValue)
                            {
                                var allowed = HpskSite.Services.CompetitionFees.CompetitionFeePlanner
                                    .AnyClubPayable(selectedClassesList, settings.ClubPayable);
                                fees.SetClubPaysChoice(competitionId, registrationId, clubPays.Value && allowed, currentMemberData.Id);
                            }
                            fees.SyncRegistration(competitionId, registrationId, currentMemberData.Id);

                            var open = fees.LoadFeeRows(competitionId)
                                .Where(r => r.SourceType == HpskSite.Models.Ledger.LedgerSourceType.CompetitionRegistration
                                            && r.SourceItemId == registrationId
                                            && r.VoidedUtc == null && r.ConfirmedUtc == null)
                                .ToList();
                            var toPay = open.Where(r => r.FeePart != HpskSite.Models.CompetitionFees.CompetitionFeePart.Club && r.ClaimedUtc == null).ToList();
                            amountDue = toPay.Sum(r => r.Amount);
                            ledgerFees = new
                            {
                                toPay = toPay.Select(r => new { id = r.Id, amount = r.Amount, reference = HpskSite.Services.CompetitionFees.CompetitionFeeService.ReferenceFor(r) }),
                                clubPart = open.Where(r => r.FeePart == HpskSite.Models.CompetitionFees.CompetitionFeePart.Club).Sum(r => r.Amount),
                                canChooseClubPays = HpskSite.Services.CompetitionFees.CompetitionFeePlanner.AnyClubPayable(selectedClassesList, settings.ClubPayable),
                                clubPays = fees.GetClubPaysChoice(registrationId)
                            };
                        }
                        else
                        {
                            var paymentSvc = HttpContext?.RequestServices?.GetService(typeof(PaymentService)) as PaymentService;
                            if (paymentSvc != null)
                                amountDue = paymentSvc.GetInvoiceTotalsForRegistration(competitionId, registrationId).Outstanding;
                        }
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
                            teamAssignments = dpConfig != null ? teamAssignments : null,
                            paymentModel,
                            ledgerFees
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

        /// <summary>
        /// The clubs a shooter may compete for — their primary club plus every club on
        /// <c>memberClubIds</c> — primary first. Feeds the "Tävlar för"-picker on every
        /// registration surface and the club dropdown in Redigera anmälan.
        ///
        /// <para>Returns <c>clubs</c> even when there is only one, so each caller can decide
        /// for itself whether to render a picker; the shared rule is to hide it below two
        /// options rather than show a dropdown that cannot be changed.</para>
        ///
        /// <para><b>Auth.</b> Anyone may read their OWN clubs. Reading someone else's requires
        /// the same standing as registering them: site admin, a manager/desk role on the
        /// competition, or club admin / skjutledare for a club that shooter belongs to. Club
        /// membership is not secret, but it is personal data, so it is not handed to any
        /// logged-in member who asks.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberClubs(int memberId, int? competitionId = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var currentMemberData = _memberService.GetById(currentMember.Key);
                if (currentMemberData == null)
                    return Json(new { success = false, message = "Kunde inte hämta din profil." });

                var target = memberId > 0 ? _memberService.GetById(memberId) : null;
                if (target == null)
                    return Json(new { success = false, message = "Medlemmen kunde inte hittas." });

                bool allowed = target.Id == currentMemberData.Id
                    || await _authorizationService.IsCurrentUserAdminAsync();

                if (!allowed && competitionId is > 0)
                    allowed = await _authorizationService.HasCompetitionStaffAccessAsync(competitionId.Value);

                if (!allowed)
                {
                    // Club admin / skjutledare for ANY of the target's clubs, not just the primary
                    // one — the whole point of this feature is that a shooter has more than one.
                    foreach (var clubId in _memberClubService.GetAllClubIds(target))
                    {
                        if (await _authorizationService.IsClubAdminForClub(clubId)
                            || await _authorizationService.IsSkjutledareForClub(clubId))
                        {
                            allowed = true;
                            break;
                        }
                    }
                }

                if (!allowed)
                    return Json(new { success = false, message = "Ingen behörighet." });

                var options = _memberClubService.GetClubOptions(target);
                return Json(new
                {
                    success = true,
                    memberId = target.Id,
                    clubs = options.Select(o => new { id = o.Id, name = o.Name, isPrimary = o.IsPrimary })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMemberClubs failed for member {MemberId}", memberId);
                return Json(new { success = false, message = "Kunde inte hämta klubbar." });
            }
        }

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
        public async Task<IActionResult> GetClubsForRegistration(int? competitionId = null)
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
                // A competition manager needs the club list for the walk-in registration modal's club
                // dropdown. They held no club role, so the desk's club picker came back "Access denied"
                // and stayed empty. This is a list of club NAMES — no personal data.
                if (!hasAccess && competitionId.HasValue && competitionId.Value > 0)
                {
                    hasAccess = await _authorizationService.IsCompetitionManager(competitionId.Value)
                             || await _authorizationService.IsRegionHostAdminAsync(competitionId.Value);
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
        public async Task<IActionResult> GetClubMembers(int clubId, int? competitionId = null)
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

                // Who may list a club's members for registration purposes?
                //
                // This feeds the "register someone else" picker in BOTH registration modals (the
                // generic one in competition-registration.js and SpringskytteRegistrationModal), so it
                // must match what RegisterForCompetition will actually ALLOW (see the targetMemberId
                // authorization above): self, or a member of a club you administer / are Skjutledare
                // for, or anyone if you manage the competition.
                //
                // It used to grant ANY club's members to anyone holding ANY club-admin or ANY
                // Skjutledare role — "if (managedClubIds.Count > 0) canAccess = true". So a Skjutledare
                // of one club could list the name AND EMAIL of every member of every club, and the
                // picker offered shooters the server would then refuse to register. Reported by Stefan
                // 2026-08-05 (Skjutledare of Varbergs PK seeing every club).
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool canAccess = isSiteAdmin;

                // Running the competition (named manager, roster app access, or the hosting krets on a
                // region-hosted competition) legitimately means registering anyone at the desk.
                if (!canAccess && competitionId.HasValue && competitionId.Value > 0)
                {
                    canAccess = await _authorizationService.IsCompetitionManager(competitionId.Value)
                             || await _authorizationService.IsRegionHostAdminAsync(competitionId.Value);
                }

                // Otherwise: only the caller's OWN clubs. GetManagedClubIds already folds in a regional
                // admin's clubs, so a krets admin keeps every club in their region.
                if (!canAccess)
                {
                    var managedClubIds = await _authorizationService.GetManagedClubIds();
                    var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
                    canAccess = managedClubIds.Contains(clubId) || skjutledareClubIds.Contains(clubId);
                }

                if (!canAccess)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Du kan bara hämta medlemmar ur en klubb du är administratör eller skjutledare för."
                    });
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
                bool isRegionHost = false;
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted (clubId unset — the SM shape): the organiser is the krets, so its
                    // regional admin runs the competition. Without this the Anmälningar tab could not
                    // even LIST the registrations of a region-hosted competition.
                    var regionCode = (competition.Value<string>("regionalFederation") ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(regionCode))
                        isRegionHost = await _authorizationService.IsRegionalAdminForRegion(regionCode);
                }

                if (!isCompetitionManager && !isClubAdmin && !isSkjutledare && !isRegionHost)
                {
                    return Json(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // PERFORMANCE FIX: Direct query to competition children instead of full tree traversal
                var competitionChildren = _contentService.GetPagedChildren(competitionId.Value, 0, 100, out _).ToList();
                // Anmälningarna via anmälningslagringen (sajtstrukturen fas 1a).
                var registrationContents = _registrations.List(competitionId.Value);

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
                // Sum of the BILLED totals of Paid invoices — deliberately not paidAmountMap, which
                // carries what was actually collected. What a registration still OWES is measured
                // against what it was billed; a cash-rounding variance is not a debt.
                var paidBilledMap = new Dictionary<int, decimal>();        // registrationId -> sum of Paid invoices' totalAmount
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
                                paidBilledMap[invoiceRegistrationId] = paidBilledMap.GetValueOrDefault(invoiceRegistrationId) + invoiceAmount;
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
                                    paidBilledMap[regId] = paidBilledMap.GetValueOrDefault(regId) + perRegAmount;
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

                // ── Den nya modellen (P3/P4): betalstatus ur LIGGAREN, inte ur fakturor ──────────
                // En tävling i den nya modellen har inga fakturor alls, så varje rad hade stått som
                // "Saknar faktura". Läget härleds i stället ur avgiftsraderna — samma källa som
                // arrangörens avprickning och Ekonomi, så de tre ytorna kan inte säga olika saker.
                string paymentModel = "legacy";
                var ledgerStatus = new Dictionary<int, HpskSite.Services.CompetitionFees.FeeItemStatus>();
                var ledgerClaimed = new Dictionary<int, DateTime>();
                try
                {
                    var models = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.CompetitionFees.CompetitionPaymentModelService))
                        as HpskSite.Services.CompetitionFees.CompetitionPaymentModelService;
                    var fees = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.CompetitionFees.CompetitionFeeService))
                        as HpskSite.Services.CompetitionFees.CompetitionFeeService;
                    if (models != null && fees != null && models.IsLedger(competitionId.Value))
                    {
                        paymentModel = "ledger";
                        foreach (var item in fees.BuildOverview(competitionId.Value).Items
                                     .Where(i => i.ItemType == HpskSite.Models.Ledger.LedgerSourceType.CompetitionRegistration))
                        {
                            ledgerStatus[item.ItemId] = item.Status;
                            var claim = item.Rows.Where(r => r.ClaimedUtc != null && r.ConfirmedUtc == null && r.VoidedUtc == null)
                                .Select(r => r.ClaimedUtc!.Value).OrderByDescending(d => d).FirstOrDefault();
                            if (claim != default) ledgerClaimed[item.ItemId] = claim;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read ledger fee status for competition {CompetitionId}", competitionId);
                }

                // PERFORMANCE FIX: Batch load club names - collect all unique club IDs first
                var clubIdsToLoad = new HashSet<int>();
                var memberIdsNeedingClubLookup = new List<(HpskSite.Models.Registrations.RegistrationRecord content, int memberId)>();

                foreach (var content in registrationContents)
                {
                    var clubId = content.ClubId;
                    if (clubId > 0)
                    {
                        clubIdsToLoad.Add(clubId);
                    }
                    else
                    {
                        var memberClubStr = content.LegacyClubText;
                        if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                        {
                            clubIdsToLoad.Add(legacyClubId);
                        }
                        else if (string.IsNullOrWhiteSpace(memberClubStr))
                        {
                            var memberId = content.MemberId;
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

                // Which of these shooters can actually be reached by a push notification. One query
                // for the whole table (a per-row lookup would be N+1 across a few hundred rows).
                // Resolved lazily like the other notification touch-points so the controller's
                // constructor stays untouched; a null service just leaves every indicator dark.
                var pushEnabledMemberIds = new HashSet<int>();
                try
                {
                    var webPush = HttpContext.RequestServices
                        .GetService(typeof(HpskSite.Services.Notifications.WebPushService))
                        as HpskSite.Services.Notifications.WebPushService;
                    if (webPush != null)
                    {
                        pushEnabledMemberIds = webPush.GetMemberIdsWithSubscriptions(
                            registrationContents.Select(c => c.MemberId));
                    }
                }
                catch (Exception ex)
                {
                    // The desk table must render even if push is misconfigured.
                    _logger.LogDebug(ex, "Push-reachability lookup failed for competition {CompetitionId}", competitionId);
                }

                // Now build the registrations list with pre-loaded data
                var registrations = registrationContents
                    .Select(content =>
                    {
                        var memberId = content.MemberId;

                        // Get club name from pre-loaded map
                        string clubName = "Unknown Club";
                        var clubId = content.ClubId;

                        if (clubId > 0)
                        {
                            clubName = clubNameMap.TryGetValue(clubId, out var name) ? name : $"Club {clubId}";
                        }
                        else
                        {
                            var memberClubStr = content.LegacyClubText;
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

                        // Klasserna, redan tolkade av lagringen (alla lagrade former).
                        var shootingClasses = content.Classes;

                        // Fallback for "Saknar betalning" rows: when no invoice exists yet
                        // the maps don't carry an amount, so the manage-payment modal shows
                        // 0 kr while the QR (which always recomputes via the calculator)
                        // shows the real fee. Surface the expected fee here so both surfaces
                        // agree — the invoice gets created lazily on QR-generate / mark-paid.
                        var classIdsForFee = shootingClasses.Select(sc => sc.Class).ToList();
                        var isSubCompForFee = content.IsSubCompetition;
                        var fullFee = RegistrationFeeCalculator.Calculate(competition, classIdsForFee, isSubCompForFee);

                        if (paymentAmount == 0m && invoiceId == 0)
                        {
                            paymentAmount = fullFee;
                        }

                        // ── What "Väntande" means, in one place ────────────────────────────────
                        // What this registration still OWES = its fee minus what has been paid
                        // against it. Deliberately NOT the sum of its Pending invoice rows, which
                        // is what every summary used to show.
                        //
                        // Why it matters (SM Springskytte, 2026-08-20): a registration with a
                        // duplicate invoice — one Paid, one Pending for the same 400 kr — had a
                        // row reading "Betald" (the newest invoice) while the heading counted the
                        // stale Pending row, so the same screen claimed 1000 kr and 600 kr at once.
                        // Both numbers were faithfully computed; they were answering different
                        // questions under the same word.
                        //
                        // Derived from the fee, this figure cannot double-count: a phantom invoice,
                        // a top-up, and a samlingsfaktura covering the same money all collapse into
                        // one debt. A registration with no invoice yet still owes its fee — it
                        // shows here, and the "Saknar faktura" row explains why it cannot be
                        // collected until the invoice exists.
                        var paidBilled = paidBilledMap.TryGetValue(content.Id, out var pb) ? pb : 0m;
                        var outstandingAmount = Math.Max(0m, fullFee - paidBilled);

                        // Now that invoices are created eagerly, a fee-bearing registration with
                        // no invoice is an error/edge ("No Invoice" → "Saknar Faktura"); a 0-fee
                        // registration legitimately has none ("No Fee" → "Ingen avgift").
                        if (paymentStatus == "No Invoice")
                            paymentStatus = paymentAmount > 0m ? "No Invoice" : "No Fee";

                        // Den nya modellen: läget ur liggaren. De gamla statussträngarna behålls där
                        // de betyder samma sak, så tabellen och summakorten fungerar oförändrade;
                        // "Invoiced" är ny (fakturerad klubben, obetald).
                        string? feeStatusKey = null, feeStatusLabel = null;
                        if (paymentModel == "ledger")
                        {
                            var fs = ledgerStatus.TryGetValue(content.Id, out var st)
                                ? st : HpskSite.Services.CompetitionFees.FeeItemStatus.For(fullFee,
                                    new List<HpskSite.Models.Ledger.LedgerPayment>(),
                                    new Dictionary<int, HpskSite.Services.CompetitionFees.ChargeCoverage>());
                            feeStatusKey = fs.Key;
                            feeStatusLabel = HpskSite.Services.CompetitionFees.FeeStatusKeys.Label(fs.Key);
                            paymentStatus = fs.Key switch
                            {
                                HpskSite.Services.CompetitionFees.FeeStatusKeys.Paid => "Paid",
                                HpskSite.Services.CompetitionFees.FeeStatusKeys.Overpaid => "Paid",
                                HpskSite.Services.CompetitionFees.FeeStatusKeys.Invoiced => "Invoiced",
                                HpskSite.Services.CompetitionFees.FeeStatusKeys.NoFee => "No Fee",
                                _ => "Pending"
                            };
                            invoiceId = 0;
                            invoiceNumber = "";
                            paidAmount = fs.Paid + fs.InvoicePaid;
                            pendingAmount = fs.Open + fs.Missing + fs.AwaitingInvoice + fs.Claimed + fs.Invoiced;
                            outstandingAmount = Math.Max(0m, fullFee - paidAmount);
                            paymentAmount = fs.Open + fs.Missing + fs.AwaitingInvoice;
                            hasVariance = fs.Overpaid > 0;
                            paymentSentDate = ledgerClaimed.TryGetValue(content.Id, out var cd) ? cd : null;
                            paymentSentBy = paymentSentDate != null ? "betalaren" : null;
                        }

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
                            memberName = string.IsNullOrEmpty(content.MemberName) ? "Unknown Member" : content.MemberName,
                            memberClub = clubName,
                            // The stored club id (0 on legacy rows whose club was only ever derived
                            // at read time). The Redigera-anmälan club dropdown needs the ID, not
                            // just the resolved name, to know which option is currently selected.
                            memberClubId = clubId,
                            shootingClasses = shootingClassesWithNames,
                            registrationDate = content.RegistrationDate,
                            isActive = content.IsActive,
                            paymentStatus = paymentStatus,
                            invoiceId = invoiceId,
                            paymentAmount = paymentAmount,
                            invoiceNumber = invoiceNumber,
                            transactionId = existingTxnId,
                            paidAmount = paidAmount,
                            pendingAmount = pendingAmount,
                            // What the registration still owes. This is the number "Väntande" means;
                            // pendingAmount above is kept for the invoice-row views, which count a
                            // different thing and must therefore be labelled differently.
                            outstandingAmount = outstandingAmount,
                            fullFee = fullFee,
                            hasVariance = hasVariance,
                            isCheckedIn = content.IsCheckedIn,
                            isSubCompetition = content.IsSubCompetition,
                            paymentSentDate = paymentSentDate,
                            paymentSentBy = paymentSentBy,
                            lastReminderSentDate = lastReminderSentDate,
                            reminderCount = reminderCount,
                            // "Can we push to this shooter right now" — true when they have at least one
                            // browser subscribed. Not a competition-specific opt-in; see WebPushService.
                            hasPushSubscription = memberId > 0 && pushEnabledMemberIds.Contains(memberId),
                            feeStatusKey,
                            feeStatusLabel
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
                    subCompetitionName = competition.Value<string>("subCompetitionName") ?? "",
                    // "ledger" = avgifterna i liggaren (P3/P4). Anmälningar-flikens JS väljer väg på den.
                    paymentModel
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

                // Via anmälningslagringen (sajtstrukturen fas 1a) — alla anmälningar, publicerade eller inte.
                var registrationContents = _registrations.List(competitionId);

                // Batch-resolve club names (same precedence as the admin endpoint:
                // explicit clubId → legacy memberClub → member's primaryClubId).
                var clubIdsToLoad = new HashSet<int>();
                var memberIdsNeedingClubLookup = new List<(HpskSite.Models.Registrations.RegistrationRecord content, int memberId)>();
                foreach (var content in registrationContents)
                {
                    var clubId = content.ClubId;
                    if (clubId > 0) { clubIdsToLoad.Add(clubId); continue; }
                    var memberClubStr = content.LegacyClubText;
                    if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                        clubIdsToLoad.Add(legacyClubId);
                    else if (string.IsNullOrWhiteSpace(memberClubStr))
                    {
                        var memberId = content.MemberId;
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
                    var memberId = content.MemberId;

                    string clubName = "Okänd klubb";
                    var clubId = content.ClubId;
                    if (clubId > 0)
                    {
                        clubName = clubNameMap.TryGetValue(clubId, out var name) ? name : $"Club {clubId}";
                    }
                    else
                    {
                        var memberClubStr = content.LegacyClubText;
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

                    var shootingClasses = content.Classes;

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
                        memberName = string.IsNullOrEmpty(content.MemberName) ? "Okänd" : content.MemberName,
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

        #region Hub Management

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

                // Via anmälningslagringen (sajtstrukturen fas 1a) — alla anmälningar, publicerade eller inte.
                var existingRegistrations = _registrations.ListForMemberInCompetition(competitionId, targetMemberId)
                    .Select(r => (object)new
                    {
                        id = r.Id,
                        shootingClasses = r.Classes,
                        registrationDate = r.RegistrationDate,
                        isSubCompetition = r.IsSubCompetition
                    })
                    .ToList();

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
            // Occupancy per team, counted from the registrations (via the registration store).
            // Count team assignments from existing registrations
            var teamCounts = new Dictionary<int, Dictionary<string, int>>(); // teamNumber -> { weaponGroup -> count }
            foreach (var team in config.Teams)
            {
                teamCounts[team.TeamNumber] = new Dictionary<string, int>();
            }

            {
                var registrations = _registrations.List(competitionId);

                foreach (var reg in registrations)
                {
                    foreach (var entry in reg.Classes)
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
