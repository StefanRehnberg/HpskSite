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
using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Microsoft.Extensions.Logging;
using HpskSite.Services;
using HpskSite.Models;

namespace HpskSite.Controllers
{
    public class CompetitionController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly ILogger<CompetitionController> _logger;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _authorizationService;

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
            AdminAuthorizationService authorizationService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _logger = logger;
            _clubService = clubService;
            _authorizationService = authorizationService;
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
            string startPreferencesJson = "")
        {
            try
            {
                _logger.LogInformation("REGISTRATION: Starting registration for competitionId {CompetitionId}", competitionId);
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
                        // Club admin can register members from their clubs
                        var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int targetClubId))
                        {
                            canRegisterTargetMember = await _authorizationService.IsClubAdminForClub(targetClubId);
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

                // Force create a registrations hub (don't use fallback to competition)
                _logger.LogInformation("REGISTRATION: About to FORCE create registrations hub for competition {CompetitionId}", competition.Id);

                IContent registrationsHub;
                try
                {
                    // Try to get existing hub first
                    var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _);
                    var existingHub = children.FirstOrDefault(c =>
                        c.ContentType.Alias == "competitionRegistrationsHub" ||
                        c.Name.Contains("Anmälningar") ||
                        c.Name.Contains("Registration"));

                    if (existingHub != null)
                    {
                        registrationsHub = existingHub;
                        _logger.LogInformation("REGISTRATION: Found existing hub: {HubName} (ID: {HubId})", existingHub.Name, existingHub.Id);
                    }
                    else
                    {
                        // Force create new hub
                        var hubContentType = _contentTypeService.Get("competitionRegistrationsHub");
                        if (hubContentType == null)
                        {
                            hubContentType = _contentTypeService.Get("contentPage");
                        }

                        var hubName = "Anmälningar";
                        var newHub = _contentService.Create(hubName, competition, hubContentType.Alias);

                        if (hubContentType.Alias == "contentPage")
                        {
                            newHub.SetValue("pageTitle", "Anmälningar");
                            newHub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
                        }

                        var hubSaveResult = _contentService.Save(newHub);
                        if (hubSaveResult.Success)
                        {
                            _contentService.Publish(newHub, Array.Empty<string>());
                            registrationsHub = newHub;
                            _logger.LogInformation("REGISTRATION: FORCE created new hub: {HubName} (ID: {HubId})", newHub.Name, newHub.Id);
                        }
                        else
                        {
                            _logger.LogError("REGISTRATION: Failed to create hub, falling back to competition");
                            registrationsHub = competition;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "REGISTRATION: Exception during hub creation, falling back to competition");
                    registrationsHub = competition;
                }

                var isActualHub = registrationsHub.Id != competition.Id;
                TempData["Success"] = $"Registration will be created under: {registrationsHub.Name} (IsHub: {isActualHub})";

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

                // Build shooting classes array with per-class preferences
                var shootingClassesArray = selectedClassesList.Select(sc => new
                {
                    @class = sc,
                    startPreference = startPreferencesDict.ContainsKey(sc) ? startPreferencesDict[sc] : startPreference
                }).ToList();

                var shootingClassesJson = System.Text.Json.JsonSerializer.Serialize(shootingClassesArray);

                // Check if registration already exists for this member+competition
                var existingRegistration = FindExistingRegistration(competitionId, targetMember.Id, null);

                IContent registration;
                bool isUpdate = false;
                int? oldInvoiceId = null;
                decimal oldFee = 0;
                decimal newFee = 0;

                // Get registration fee from competition
                var registrationFee = competition.GetValue<decimal>("registrationFee");
                newFee = registrationFee * selectedClassesList.Count;

                if (existingRegistration != null)
                {
                    // Update existing registration
                    _logger.LogInformation("Found existing registration (ID: {RegId}) for member {MemberId}. Updating with classes: {Classes}",
                        existingRegistration.Id, targetMember.Id, string.Join(", ", selectedClassesList));

                    // Calculate old fee from existing classes
                    var oldClassesJson = existingRegistration.GetValue<string>("shootingClasses");
                    if (!string.IsNullOrEmpty(oldClassesJson))
                    {
                        try
                        {
                            var oldClasses = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(oldClassesJson);
                            oldFee = registrationFee * (oldClasses?.Count ?? 0);
                        }
                        catch
                        {
                            _logger.LogWarning("Failed to parse old shooting classes JSON for registration {RegId}", existingRegistration.Id);
                            oldFee = 0;
                        }
                    }

                    // Check if existing registration has an invoice
                    var existingInvoiceId = existingRegistration.GetValue<int>("invoiceId");
                    if (existingInvoiceId > 0)
                    {
                        var invoice = _contentService.GetById(existingInvoiceId);
                        if (invoice != null)
                        {
                            var paymentStatus = invoice.GetValue<string>("paymentStatus");

                            // BLOCK: Cannot update paid registrations
                            if (paymentStatus == "Paid")
                            {
                                var errorMsg = "Kan inte ändra anmälan med betald faktura. Vänligen kontakta administratören.";
                                _logger.LogWarning("Blocked update of paid registration {RegId} for member {MemberId}", existingRegistration.Id, targetMember.Id);
                                if (IsAjaxRequest())
                                {
                                    return Json(new { success = false, message = errorMsg });
                                }
                                TempData["Error"] = errorMsg;
                                return RedirectToCurrentUmbracoPage();
                            }

                            // Mark invoice for cancellation if fee changed
                            if (oldFee != newFee && paymentStatus == "Pending")
                            {
                                oldInvoiceId = existingInvoiceId;
                                _logger.LogInformation("Will cancel old invoice {InvoiceId} due to fee change from {OldFee} to {NewFee}",
                                    existingInvoiceId, oldFee, newFee);
                            }
                        }
                    }

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

                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                {
                    var errorMsg = "Failed to save registration.";
                    _logger.LogError("REGISTRATION: Save failed for member {MemberId}", targetMember.Id);
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                var publishResult = _contentService.Publish(registration, new[] { "*" });
                if (!publishResult.Success)
                {
                    var errorMsg = "Failed to publish registration.";
                    _logger.LogError("REGISTRATION: Publish failed for member {MemberId}", targetMember.Id);
                    if (IsAjaxRequest())
                    {
                        return Json(new { success = false, message = errorMsg });
                    }
                    TempData["Error"] = errorMsg;
                    return RedirectToCurrentUmbracoPage();
                }

                // AFTER successful registration update: Cancel old invoice if fee changed
                if (isUpdate && oldInvoiceId.HasValue && oldFee != newFee)
                {
                    var oldInvoice = _contentService.GetById(oldInvoiceId.Value);
                    if (oldInvoice != null)
                    {
                        oldInvoice.SetValue("paymentStatus", "Cancelled");
                        // Note: isActive property removed - paymentStatus "Cancelled" is sufficient
                        var notes = oldInvoice.GetValue<string>("notes") ?? "";
                        notes += $"\n[{DateTime.Now:yyyy-MM-dd HH:mm}] Cancelled - Registration updated with fee change from {oldFee:F2} to {newFee:F2} SEK";
                        oldInvoice.SetValue("notes", notes);
                        var invoiceSaveResult = _contentService.Save(oldInvoice);
                        if (invoiceSaveResult.Success)
                        {
                            _contentService.Publish(oldInvoice, new[] { "*" });
                        }
                        _logger.LogInformation("Cancelled old invoice {InvoiceId} due to fee change", oldInvoiceId.Value);

                        // Clear the invoice link from the registration since it was cancelled
                        registration.SetValue("invoiceId", 0);
                        var regSaveResult = _contentService.Save(registration);
                        if (regSaveResult.Success)
                        {
                            _contentService.Publish(registration, new[] { "*" });
                        }
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
                            newFee = newFee
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
                    var publishResult = _contentService.Publish(existingFolder, Array.Empty<string>());
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
                var publishResult = _contentService.Publish(folder, Array.Empty<string>());
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

                Console.WriteLine($"User {memberData.Name} - isSiteAdmin: {isSiteAdmin}, isClubAdmin: {isClubAdmin}");
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

                // Site admin role takes precedence over club admin
                string userRole = isSiteAdmin ? "admin" : (isClubAdmin ? "clubAdmin" : "member");

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

                // Check if user is site admin
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                Console.WriteLine($"GetClubsForRegistration - User {memberData.Name} - isSiteAdmin: {isSiteAdmin}");

                if (!isSiteAdmin)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Get all clubs from content tree (Document Types under clubsPage)
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
                var clubs = allContent
                    .Where(c => c.ContentType.Alias == "club" && c.Published)
                    .Select(club => new
                    {
                        id = club.Id,
                        name = club.Name
                    })
                    .OrderBy(c => c.name)
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

                // Check if user can access this club's members
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool canAccess = false;

                Console.WriteLine($"GetClubMembers - User {memberData.Name} - clubId: {clubId} - isSiteAdmin: {isSiteAdmin}");

                if (isSiteAdmin)
                {
                    canAccess = true;
                }
                else
                {
                    // Check if user is club admin for this specific club
                    canAccess = await _authorizationService.IsClubAdminForClub(clubId);
                }

                if (!canAccess)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Get all regular members (not clubs) that belong to this club
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var clubMembers = allMembers
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
                    .OrderBy(m => m.name)
                    .ToList();

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
                    // Club admin can only access members from their clubs
                    var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int clubId))
                    {
                        canAccess = await _authorizationService.IsClubAdminForClub(clubId);
                    }
                }

                if (!canAccess)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Get club name
                var primaryClubIdStr = targetMember.GetValue<string>("primaryClubId");
                string clubName = "Unknown Club";
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                {
                    var club = _memberService.GetById(primaryClubId);
                    clubName = club?.Name ?? $"Club {primaryClubId}";
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

                // Check authorization - Site Admin, Competition Manager, or Club Admin
                bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId.Value);
                bool isClubAdmin = false;

                // Check if user is club admin for this competition's club
                var competitionClubId = competition.Value<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                }

                if (!isCompetitionManager && !isClubAdmin)
                {
                    return Json(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Get all registrations for this competition
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
                var registrations = allContent
                    .Where(content => content.ContentType.Alias == "competitionRegistration")
                    .Where(content =>
                    {
                        var parentId = content.ParentId;
                        while (parentId > 0)
                        {
                            if (parentId == competitionId.Value)
                                return true;
                            var parent = _contentService.GetById(parentId);
                            parentId = parent?.ParentId ?? -1;
                        }
                        return false;
                    })
                    .Select(content =>
                    {
                        var memberId = content.GetValue<int>("memberId");

                        // Get club ID and resolve to name
                        string clubName = "Unknown Club";
                        var clubId = content.GetValue<int>("clubId");

                        if (clubId > 0)
                        {
                            // New data with numeric clubId property
                            clubName = _clubService.GetClubNameById(clubId) ?? $"Club {clubId}";
                        }
                        else
                        {
                            // Fallback for old data or missing clubId - try memberClub (migration path)
                            var memberClubStr = content.GetValue<string>("memberClub");
                            if (!string.IsNullOrWhiteSpace(memberClubStr) && int.TryParse(memberClubStr, out var legacyClubId))
                            {
                                clubName = _clubService.GetClubNameById(legacyClubId) ?? $"Club {legacyClubId}";
                            }
                            else if (!string.IsNullOrWhiteSpace(memberClubStr))
                            {
                                clubName = memberClubStr;
                            }
                            else if (memberId > 0)
                            {
                                // Last fallback: get from member's primary club
                                var member = _memberService.GetById(memberId);
                                var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
                                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
                                {
                                    clubName = _clubService.GetClubNameById(primaryClubId) ?? $"Club {primaryClubId}";
                                }
                            }
                        }

                        // Get payment status for this registration
                        var paymentStatus = GetRegistrationPaymentStatus(content.Id, competitionId.Value);

                        // Get shooting classes (new JSON array format)
                        var shootingClassesJson = content.GetValue<string>("shootingClasses");
                        var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                        // Convert class IDs to display names
                        var shootingClassesWithNames = shootingClasses.Select(sc => new
                        {
                            @class = sc.Class,
                            className = ShootingClasses.GetById(sc.Class)?.Name ?? sc.Class,
                            startPreference = sc.StartPreference
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
                            paymentStatus = paymentStatus
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
                    statistics = statistics
                });
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
                    var publishResult = _contentService.Publish(hub, Array.Empty<string>());
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
        /// Extracts the weapon class (A, B, C, R) from a shooting class ID
        /// </summary>
        private string GetWeaponClassFromShootingClass(string shootingClassId)
        {
            if (string.IsNullOrEmpty(shootingClassId)) return null;

            // Use ShootingClasses model to get weapon type
            var shootingClass = ShootingClasses.GetById(shootingClassId);
            if (shootingClass != null)
            {
                return shootingClass.Weapon.ToString(); // Returns "A", "B", "C", or "R"
            }

            // Fallback: simple string parsing (first character)
            return shootingClassId.Substring(0, 1);
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
                    // Check if current user has permission to view this member's registrations
                    bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    bool canViewMember = false;

                    if (isSiteAdmin)
                    {
                        canViewMember = true;
                    }
                    else
                    {
                        // Club admin can view members from their clubs
                        var targetMember = _memberService.GetById(memberId.Value);
                        if (targetMember != null)
                        {
                            var targetMemberClubId = targetMember.GetValue<string>("primaryClubId");
                            if (!string.IsNullOrEmpty(targetMemberClubId) && int.TryParse(targetMemberClubId, out int targetClubId))
                            {
                                canViewMember = await _authorizationService.IsClubAdminForClub(targetClubId);
                            }

                            // Users can view their own registrations
                            if (targetMember.Id == currentMemberData.Id)
                            {
                                canViewMember = true;
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
                                isPublished = r.Published
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
                _contentService.Publish(newRegistration, Array.Empty<string>());

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
    }
}
