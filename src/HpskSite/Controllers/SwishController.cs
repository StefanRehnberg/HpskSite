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
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    public class HandleInvoiceChoiceRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("competitionId")]
        public int CompetitionId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("createNew")]
        public bool CreateNew { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("existingInvoiceId")]
        public int? ExistingInvoiceId { get; set; }
    }
    public class SwishController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly PaymentService _paymentService;
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly InvoiceAuditService _auditService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<SwishController> _logger;

        public SwishController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            PaymentService paymentService,
            EmailService emailService,
            ClubService clubService,
            InvoiceAuditService auditService,
            ILogger<SwishController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager ?? throw new ArgumentNullException(nameof(memberManager));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
            _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
            _clubService = clubService ?? throw new ArgumentNullException(nameof(clubService));
            _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
            _databaseFactory = databaseFactory ?? throw new ArgumentNullException(nameof(databaseFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Simple test endpoint to verify POST requests work
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult TestSimplePost()
        {
            try
            {
                _logger.LogInformation("TestSimplePost called successfully");
                return Json(new { success = true, message = "Simple POST request works!" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestSimplePost");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Test endpoint to debug JSON binding issues
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult TestJsonBinding([FromBody] HandleInvoiceChoiceRequest request)
        {
            try
            {
                _logger.LogInformation("TestJsonBinding called");
                
                if (request == null)
                {
                    return Json(new { success = false, message = "Request is null" });
                }
                
                return Json(new { 
                    success = true, 
                    received = new {
                        CompetitionId = request.CompetitionId,
                        CreateNew = request.CreateNew,
                        ExistingInvoiceId = request.ExistingInvoiceId
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TestJsonBinding");
                return Json(new { success = false, message = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        /// <summary>
        /// Debug endpoint to test invoice detection without payment flow
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> DebugInvoiceDetection(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                var existingInvoice = await _paymentService.GetExistingInvoiceForMember(competitionId, currentMember.Id.ToString());
                
                if (existingInvoice != null)
                {
                    return Json(new { 
                        success = true, 
                        hasExistingInvoice = true,
                        invoiceId = existingInvoice.Id,
                        invoiceName = existingInvoice.Name,
                        debug = "Invoice found - check logs for detailed property values"
                    });
                }
                else
                {
                    return Json(new { 
                        success = true, 
                        hasExistingInvoice = false,
                        debug = "No existing invoice found - check logs for details"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DebugInvoiceDetection for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        /// <summary>
        /// Simple test endpoint to verify routing works
        /// </summary>
        [HttpGet]
        public IActionResult TestRoute()
        {
            Console.WriteLine("🧪 TEST ROUTE CALLED 🧪");
            return Json(new { success = true, message = "Route is working!" });
        }

        /// <summary>
        /// Generate Swish QR code for competition payment
        /// </summary>
        /// <param name="competitionId">Competition ID</param>
        /// <param name="targetMemberId">Optional: Target member ID (for admin registering on behalf of someone else)</param>
        [HttpGet]
        public async Task<IActionResult> GeneratePaymentQR(int competitionId, string targetMemberId = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Get competition details
                var umbracoContext = UmbracoContext;
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                var swishNumber = competition.Value<string>("swishNumber");
                var _feeStr = competition.Value<string>("registrationFee") ?? "0";
                decimal.TryParse(_feeStr, out var registrationFee);
                var _juniorFeeStr = competition.Value<string>("juniorRegistrationFee") ?? "0";
                decimal.TryParse(_juniorFeeStr, out var juniorFeeConfigured);
                var _subCompFeeStr = competition.Value<string>("subCompetitionFee") ?? "0";
                decimal.TryParse(_subCompFeeStr, out var subCompFeeConfigured);

                _logger.LogInformation("Swish payment request - CompetitionId: {CompetitionId}, SwishNumber: {SwishNumber}, RegistrationFee: {RegistrationFee}, JuniorFee: {JuniorFee}, SubCompFee: {SubCompFee}",
                    competitionId, swishNumber, registrationFee, juniorFeeConfigured, subCompFeeConfigured);

                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad för denna tävling." });
                }

                if (registrationFee <= 0 && juniorFeeConfigured <= 0 && subCompFeeConfigured <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get user's registrations for this competition
                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata kunde inte hittas." });
                }

                // Find user's registration for this competition (NEW: single registration per user)
                int? userRegistrationId = null;
                var userShootingClasses = new List<string>();
                string registeredMemberId = null; // The member who is registered (may differ from logged-in user if admin)
                string registeredMemberName = null;
                bool registeredIsSubCompetition = false;

                _logger.LogInformation("Looking for registration for member {MemberId} in competition {CompetitionId}", memberData.Id, competitionId);

                // Determine which member ID to search for
                var searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();
                _logger.LogInformation("Searching for registration for member {SearchMemberId} (targetMemberId param: '{TargetMemberId}', currentMember: {CurrentMemberId})",
                    searchMemberId, targetMemberId ?? "null", currentMember.Id);

                // First: try published content (fast, cached)
                var registrationsHub = competition.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");

                if (registrationsHub != null)
                {
                    var registrations = registrationsHub.Children()
                        .Where(x => x.ContentType.Alias == "competitionRegistration");

                    foreach (var registration in registrations)
                    {
                        var registrationMemberId = registration.Value<string>("memberId");
                        if (registrationMemberId == searchMemberId)
                        {
                            var isActive = registration.Value<bool>("isActive", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: true);
                            if (isActive)
                            {
                                userRegistrationId = registration.Id;
                                registeredMemberId = registrationMemberId;
                                registeredMemberName = registration.Value<string>("memberName");
                                registeredIsSubCompetition = registration.Value<bool>("isSubCompetition",
                                    fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: false);

                                try
                                {
                                    var shootingClassesJson = registration.Value<string>("shootingClasses");
                                    if (!string.IsNullOrEmpty(shootingClassesJson))
                                    {
                                        var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);
                                        foreach (var classEntry in shootingClasses)
                                        {
                                            if (!string.IsNullOrEmpty(classEntry.Class) && !userShootingClasses.Contains(classEntry.Class))
                                            {
                                                userShootingClasses.Add(classEntry.Class);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Error reading shootingClasses from registration {RegistrationId}", registration.Id);
                                }

                                _logger.LogInformation("Registration {RegistrationId} found in published content", registration.Id);
                                break;
                            }
                        }
                    }
                }

                // Fallback: check unpublished content via IContentService
                // (Registration may be saved but not yet published due to background publish delay)
                if (!userRegistrationId.HasValue)
                {
                    _logger.LogInformation("Registration not found in published content, checking via IContentService fallback");
                    var contentService = Services.ContentService;
                    var competitionContent = contentService.GetById(competitionId);
                    if (competitionContent != null)
                    {
                        long totalHub;
                        var hubChildren = contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalHub);
                        var hubContent = hubChildren.FirstOrDefault(x => x.ContentType.Alias == "competitionRegistrationsHub");

                        if (hubContent != null)
                        {
                            long totalRegs;
                            var regChildren = contentService.GetPagedChildren(hubContent.Id, 0, 500, out totalRegs);
                            foreach (var reg in regChildren)
                            {
                                if (reg.ContentType.Alias != "competitionRegistration") continue;
                                var regMemberId = reg.GetValue<string>("memberId");
                                if (regMemberId != searchMemberId) continue;

                                // Default isActive to true if property not explicitly set to false
                                var hasIsActive = reg.Properties.Any(p => p.Alias == "isActive");
                                var isActive = !hasIsActive || reg.GetValue<bool>("isActive");
                                if (isActive)
                                {
                                    userRegistrationId = reg.Id;
                                    registeredMemberId = regMemberId;
                                    registeredMemberName = reg.GetValue<string>("memberName");
                                    registeredIsSubCompetition = reg.GetValue<bool>("isSubCompetition");

                                    try
                                    {
                                        var shootingClassesJson = reg.GetValue<string>("shootingClasses");
                                        if (!string.IsNullOrEmpty(shootingClassesJson))
                                        {
                                            var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);
                                            foreach (var classEntry in shootingClasses)
                                            {
                                                if (!string.IsNullOrEmpty(classEntry.Class) && !userShootingClasses.Contains(classEntry.Class))
                                                    userShootingClasses.Add(classEntry.Class);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Error reading shootingClasses from unpublished registration {RegistrationId}", reg.Id);
                                    }

                                    _logger.LogInformation("Found unpublished registration {RegistrationId} via IContentService fallback", reg.Id);
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Calculate total amount (per-class base/junior fee + optional deltävling surcharge)
                var classesForCalc = userShootingClasses.Count > 0
                    ? (IReadOnlyCollection<string>)userShootingClasses
                    : new[] { string.Empty }; // single non-junior bucket so baseFee applies once when class list is empty
                var totalAmount = RegistrationFeeCalculator.Calculate(competition, classesForCalc, registeredIsSubCompetition);
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                _logger.LogInformation("Payment calculation - RegistrationId: {RegistrationId}, ClassCount: {ClassCount}, RegistrationFee: {Fee}, IsSubCompetition: {SubComp}, TotalAmount: {Total}",
                    userRegistrationId.Value, userShootingClasses.Count, registrationFee, registeredIsSubCompetition, totalAmount);
                _logger.LogInformation("User shooting classes found: {ShootingClasses}", string.Join(", ", userShootingClasses));

                if (totalAmount <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get or create invoice for this registration (SIMPLIFIED FLOW)
                var memberId = registeredMemberId ?? currentMember.Id.ToString();
                var memberName = registeredMemberName ?? currentMember.Name;

                _logger.LogInformation("Getting/creating invoice for registration {RegistrationId}, member {MemberId}", userRegistrationId.Value, memberId);

                // Get the registration document to check for existing invoice
                var registrationDoc = Services.ContentService.GetById(userRegistrationId.Value);
                if (registrationDoc == null)
                {
                    return Json(new { success = false, message = "Anmälan kunde inte hittas." });
                }

                var invoiceId = registrationDoc.GetValue<int>("invoiceId");
                IContent? invoice = null;

                if (invoiceId > 0)
                {
                    // Invoice exists, check its status
                    invoice = Services.ContentService.GetById(invoiceId);
                    if (invoice != null)
                    {
                        var paymentStatus = invoice.GetValue<string>("paymentStatus");
                        _logger.LogInformation("Found existing invoice {InvoiceId} with status {Status}", invoiceId, paymentStatus);

                        if (paymentStatus == "Paid")
                        {
                            return Json(new { success = false, message = "Denna anmälan har redan betalats." });
                        }

                        if (paymentStatus == "Cancelled")
                        {
                            // Old invoice was cancelled, create new one
                            _logger.LogInformation("Existing invoice {InvoiceId} is cancelled, creating new invoice", invoiceId);

                            invoice = await _paymentService.CreateInvoiceAsync(
                                competitionId,
                                memberId,
                                memberName ?? "Okänd medlem",
                                userRegistrationId.Value,
                                totalAmount,
                                "Swish");

                            if (invoice == null)
                            {
                                return Json(new { success = false, message = "Kunde inte skapa faktura för betalning." });
                            }

                            // Link new invoice back to registration
                            registrationDoc.SetValue("invoiceId", invoice.Id);
                            var saveResult = Services.ContentService.Save(registrationDoc);
                            if (saveResult.Success)
                            {
                                Services.ContentService.Publish(registrationDoc, new[] { "*" });
                            }
                        }

                        // If status is "Pending", reuse the invoice (fee didn't change)
                    }
                    else
                    {
                        return Json(new { success = false, message = "Faktura kunde inte hittas." });
                    }
                }
                else
                {
                    // No invoice exists yet, create it
                    _logger.LogInformation("No invoice exists for registration {RegistrationId}, creating new invoice", userRegistrationId.Value);

                    invoice = await _paymentService.CreateInvoiceAsync(
                        competitionId,
                        memberId,
                        memberName ?? "Okänd medlem",
                        userRegistrationId.Value,
                        totalAmount,
                        "Swish");

                    if (invoice == null)
                    {
                        return Json(new { success = false, message = "Kunde inte skapa faktura för betalning." });
                    }

                    // Link invoice back to registration
                    registrationDoc.SetValue("invoiceId", invoice.Id);
                    var saveResult = Services.ContentService.Save(registrationDoc);
                    if (saveResult.Success)
                    {
                        Services.ContentService.Publish(registrationDoc, new[] { "*" });
                    }
                }

                // Get the invoice number from the created invoice
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                
                // Generate QR code message
                var message = $"Betalning: {invoiceNumber}";

                // Validate Swish number format
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!normalizedSwishNumber.All(char.IsDigit) || normalizedSwishNumber.Length != 10 || !normalizedSwishNumber.StartsWith("0"))
                {
                    return Json(new { success = false, message = "Swish-numret måste vara 10 siffror som börjar med 0 (t.ex. 0701234567)." });
                }

                _logger.LogInformation("Generating QR code - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                    normalizedSwishNumber, amountString, message);

                // Generate QR code
                byte[] qrCodeBytes;
                try
                {
                    qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                }
                catch (Exception qrEx)
                {
                    _logger.LogError(qrEx, "QR code generation failed - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                        normalizedSwishNumber, amountString, message);
                    return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                }

                var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                var subCompPortion = RegistrationFeeCalculator.CalculateSubCompetitionPortion(
                    competition, classesForCalc, registeredIsSubCompetition);
                var subCompetitionName = competition.Value<string>("subCompetitionName") ?? "";

                return Json(new {
                    success = true,
                    qrCode = $"data:image/png;base64,{qrCodeBase64}",
                    amount = totalAmount,
                    registrationCount = userShootingClasses.Count,
                    shootingClasses = string.Join(", ", userShootingClasses),
                    invoiceId = invoice.Id,
                    invoiceNumber = invoiceNumber,
                    message = message,
                    includesSubCompetition = subCompPortion > 0,
                    subCompetitionName = subCompetitionName,
                    subCompetitionFeeTotal = subCompPortion
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌❌❌ EXCEPTION IN GeneratePaymentQR ❌❌❌");
                Console.WriteLine($"Exception Type: {ex.GetType().Name}");
                Console.WriteLine($"Exception Message: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                Console.WriteLine($"❌❌❌ EXCEPTION END ❌❌❌");
                
                _logger.LogError(ex, "Error generating Swish QR code for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get unpaid invoices for the current user in a competition.
        /// Returns both individual and team invoices belonging to the user.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUnpaidInvoices(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, invoices = Array.Empty<object>() });

                var competition = UmbracoContext?.Content?.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, invoices = Array.Empty<object>() });

                var memberId = currentMember.Id.ToString();
                var feeStr = competition.Value<string>("registrationFee") ?? "0";
                decimal.TryParse(feeStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var registrationFee);

                var unpaid = new List<object>();
                var hasIndividualInvoice = false;

                // Check existing invoices
                var invoicesHub = competition.Children()
                    .FirstOrDefault(c => c.ContentType?.Alias == "registrationInvoicesHub");

                if (invoicesHub != null)
                {
                    var allInvoices = invoicesHub.Children()
                        .Where(c => c.ContentType?.Alias == "registrationInvoice")
                        .ToList();

                    foreach (var inv in allInvoices)
                    {
                        try
                        {
                            var invMemberId = inv.Value<string>("memberId") ?? "";

                            // Read paymentStatus via raw source value — the Dropdown property editor's
                            // FlexibleDropdownPropertyValueConverter crashes on plain strings like "Pending"
                            var statusProp = inv.GetProperty("paymentStatus");
                            var status = statusProp?.GetSourceValue()?.ToString()?.Trim('"', '\'', ' ') ?? "Pending";

                            // Clean status (handle JSON array format)
                            if (status.StartsWith("["))
                            {
                                try { status = System.Text.Json.JsonSerializer.Deserialize<string[]>(status)?[0] ?? "Pending"; }
                                catch { status = status.Trim('[', ']', '"', ' '); }
                            }
                            status = status.Trim('"', '\'', ' ');

                            // Track if this member has any invoice (paid or unpaid)
                            if (invMemberId == memberId)
                                hasIndividualInvoice = true;

                            if (status.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                                status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Parse amount as string first (Umbraco may store as string)
                            var amountStr = inv.Value<string>("totalAmount") ?? "0";
                            decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount);

                            // Include individual invoices for this member
                            if (invMemberId == memberId)
                            {
                                unpaid.Add(new
                                {
                                    id = inv.Id,
                                    invoiceNumber = inv.Value<string>("invoiceNumber") ?? "",
                                    amount = amount,
                                    memberName = inv.Value<string>("memberName") ?? "",
                                    status = status,
                                    createdDate = inv.CreateDate.ToString("yyyy-MM-dd"),
                                    isTeam = false,
                                    noInvoice = false
                                });
                            }
                            // Include team invoices where the user created the team
                            else if (invMemberId.StartsWith("team-"))
                            {
                                if (int.TryParse(invMemberId.Substring(5), out var teamId))
                                {
                                    using var db = _databaseFactory.CreateDatabase();
                                    var team = db.SingleOrDefault<CompetitionTeamDto>(
                                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                                    if (team != null && team.CreatedBy.ToString() == memberId)
                                    {
                                        unpaid.Add(new
                                        {
                                            id = inv.Id,
                                            invoiceNumber = inv.Value<string>("invoiceNumber") ?? "",
                                            amount = amount,
                                            memberName = inv.Value<string>("memberName") ?? "",
                                            status = status,
                                            createdDate = inv.CreateDate.ToString("yyyy-MM-dd"),
                                            isTeam = true,
                                            noInvoice = false
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception invEx)
                        {
                            _logger.LogError(invEx, "GetUnpaidInvoices: Error processing invoice {InvoiceId}", inv.Id);
                        }
                    }
                }

                // If the member is registered but has no invoice at all, include a "no invoice" entry
                if (!hasIndividualInvoice && registrationFee > 0)
                {
                    var registrationsHub = competition.Children()
                        .FirstOrDefault(c => c.ContentType?.Alias == "competitionRegistrationsHub");

                    if (registrationsHub != null)
                    {
                        var userReg = registrationsHub.Children()
                            .FirstOrDefault(r => r.ContentType?.Alias == "competitionRegistration"
                                && (r.Value<string>("memberId") ?? "") == memberId
                                && r.Value<bool>("isActive", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: true));

                        if (userReg != null)
                        {
                            var memberData = _memberService.GetById(currentMember.Key);
                            var memberName = memberData != null
                                ? $"{memberData.GetValue<string>("firstName")} {memberData.GetValue<string>("lastName")}"
                                : currentMember.Email ?? "";

                            unpaid.Add(new
                            {
                                id = 0,
                                invoiceNumber = "",
                                amount = registrationFee,
                                memberName = memberName.Trim(),
                                status = "Ej fakturerad",
                                createdDate = userReg.CreateDate.ToString("yyyy-MM-dd"),
                                isTeam = false,
                                noInvoice = true
                            });
                        }
                    }
                }

                return Json(new { success = true, invoices = unpaid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unpaid invoices for competition {CompetitionId}", competitionId);
                return Json(new { success = false, invoices = Array.Empty<object>() });
            }
        }

        /// <summary>
        /// Generate Swish QR code for team registration payment
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GenerateTeamPaymentQR(int competitionId, int teamId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var competition = UmbracoContext.Content.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                var swishNumber = competition.Value<string>("swishNumber");
                if (string.IsNullOrEmpty(swishNumber))
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad." });

                // Look up the team first so we can determine relay vs regular fee
                CompetitionTeamDto team;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    team = db.SingleOrDefault<CompetitionTeamDto>(
                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                }

                if (team == null)
                    return Json(new { success = false, message = "Laget kunde inte hittas." });

                var feeProperty = team.IsRelay ? "stafettRegistrationFee" : "teamRegistrationFee";
                var feeStr = competition.Value<string>(feeProperty) ?? "0";
                if (!decimal.TryParse(feeStr, out var teamFee) || teamFee <= 0)
                    return Json(new { success = false, message = "Ingen lagavgift är konfigurerad." });

                var clubName = _clubService.GetClubNameById(team.ClubId) ?? "Okänd förening";
                var amountString = teamFee.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                // Find the team registration doc for invoice linking
                int teamRegistrationDocId = 0;
                var regHub = competition.Children()
                    .FirstOrDefault(c => c.ContentType?.Alias == "competitionRegistrationsHub");
                if (regHub != null)
                {
                    var teamRegDoc = regHub.Children()
                        .FirstOrDefault(c => c.ContentType?.Alias == "competitionTeamRegistration"
                            && c.Value<int>("teamId") == teamId);
                    if (teamRegDoc != null)
                        teamRegistrationDocId = teamRegDoc.Id;
                }

                // Check for existing team invoice
                var teamMemberId = $"team-{teamId}";
                var existingInvoice = await _paymentService.GetExistingInvoiceForMember(competitionId, teamMemberId);
                IContent? invoice = null;

                if (existingInvoice != null)
                {
                    var paymentStatusProp = existingInvoice.GetProperty("paymentStatus");
                    var paymentStatus = paymentStatusProp?.GetSourceValue()?.ToString()?.Trim('"', '\'', ' ') ?? "Pending";

                    if (paymentStatus == "Paid")
                        return Json(new { success = false, message = "Lagavgiften har redan betalats." });

                    if (paymentStatus == "Cancelled")
                    {
                        invoice = await _paymentService.CreateTeamInvoiceAsync(
                            competitionId, teamId, team.TeamName, clubName, teamFee, teamRegistrationDocId);
                    }
                    else
                    {
                        // Reuse pending invoice
                        invoice = Services.ContentService.GetById(existingInvoice.Id);
                    }
                }
                else
                {
                    invoice = await _paymentService.CreateTeamInvoiceAsync(
                        competitionId, teamId, team.TeamName, clubName, teamFee, teamRegistrationDocId);
                }

                if (invoice == null)
                    return Json(new { success = false, message = "Kunde inte skapa faktura." });

                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                var message = $"Lag: {invoiceNumber}";

                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!normalizedSwishNumber.All(char.IsDigit) || normalizedSwishNumber.Length != 10 || !normalizedSwishNumber.StartsWith("0"))
                    return Json(new { success = false, message = "Ogiltigt Swish-nummer." });

                byte[] qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                return Json(new
                {
                    success = true,
                    qrCode = $"data:image/png;base64,{qrCodeBase64}",
                    amount = teamFee,
                    teamName = team.TeamName,
                    teamClass = team.TeamClass,
                    clubName = clubName,
                    invoiceNumber = invoiceNumber,
                    message = message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating team Swish QR for competition {CompetitionId}, team {TeamId}", competitionId, teamId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Handle user's choice for existing invoice (create new or use existing)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> HandleExistingInvoiceChoice()
        {
            try
            {
                // Read the request body manually
                string requestBody;
                using (var reader = new StreamReader(Request.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
                
                _logger.LogInformation("HandleExistingInvoiceChoice called with raw body: {RequestBody}", requestBody);
                
                if (string.IsNullOrEmpty(requestBody))
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice received empty request body");
                    return Json(new { success = false, message = "Ogiltig begäran - tom request body." });
                }
                
                // Parse JSON manually
                HandleInvoiceChoiceRequest request;
                try
                {
                    request = System.Text.Json.JsonSerializer.Deserialize<HandleInvoiceChoiceRequest>(requestBody);
                }
                catch (Exception jsonEx)
                {
                    _logger.LogError(jsonEx, "Failed to deserialize JSON request body: {RequestBody}", requestBody);
                    return Json(new { success = false, message = $"JSON parsing error: {jsonEx.Message}" });
                }
                
                if (request == null)
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice deserialized to null request");
                    return Json(new { success = false, message = "Ogiltig begäran - request är null efter deserialisering." });
                }
                
                _logger.LogInformation("HandleExistingInvoiceChoice parsed request: CompetitionId={CompetitionId}, CreateNew={CreateNew}, ExistingInvoiceId={ExistingInvoiceId}", 
                    request.CompetitionId, request.CreateNew, request.ExistingInvoiceId);
                
                if (request.CompetitionId <= 0)
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice received invalid CompetitionId: {CompetitionId}", request.CompetitionId);
                    return Json(new { success = false, message = "Ogiltig tävlings-ID." });
                }

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                if (!request.CreateNew && request.ExistingInvoiceId.HasValue)
                {
                    // Use existing invoice - get it and generate QR code
                    var umbracoContext = UmbracoContext;
                    var existingInvoice = umbracoContext.Content.GetById(request.ExistingInvoiceId.Value);
                    
                    if (existingInvoice == null)
                    {
                        return Json(new { success = false, message = "Befintlig faktura kunde inte hittas." });
                    }

                    // Verify the invoice belongs to the current user
                    string invoiceMemberId = null;
                    try
                    {
                        var memberIdProperty = existingInvoice.GetProperty("memberId");
                        if (memberIdProperty != null)
                        {
                            var rawSourceValue = memberIdProperty.GetSourceValue();
                            if (rawSourceValue != null)
                            {
                                invoiceMemberId = rawSourceValue.ToString().Trim('"', '\'', ' ');
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading memberId from existing invoice {InvoiceId}, defaulting to empty", existingInvoice.Id);
                        invoiceMemberId = "";
                    }
                    
                    if (string.IsNullOrEmpty(invoiceMemberId) || invoiceMemberId != currentMember.Id.ToString())
                    {
                        return Json(new { success = false, message = "Du har inte behörighet till denna faktura." });
                    }

                    // Generate QR code for existing invoice
                    var competition = umbracoContext.Content.GetById(request.CompetitionId);
                    if (competition == null)
                    {
                        return Json(new { success = false, message = "Tävling kunde inte hittas." });
                    }

                    var swishNumber = competition.Value<string>("swishNumber");
                    var totalAmount = existingInvoice.Value<decimal>("totalAmount");
                    var invoiceNumber = existingInvoice.Value<string>("invoiceNumber") ?? existingInvoice.Id.ToString();
                    var message = $"Betalning: {invoiceNumber}";

                    // Validate Swish number format
                    var normalizedSwishNumber = swishNumber?.Trim().Replace(" ", "").Replace("-", "") ?? "";
                    if (!normalizedSwishNumber.All(char.IsDigit) || normalizedSwishNumber.Length != 10 || !normalizedSwishNumber.StartsWith("0"))
                    {
                        return Json(new { success = false, message = "Swish-numret måste vara 10 siffror som börjar med 0 (t.ex. 0701234567)." });
                    }

                    var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                    _logger.LogInformation("Generating QR code for existing invoice - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                        normalizedSwishNumber, amountString, message);

                    try
                    {
                        var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                        var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                        // Get shooting classes from the existing invoice's related registrations
                        var relatedRegistrationIds = existingInvoice.Value<string>("relatedRegistrationIds");
                        var shootingClasses = new List<string>();
                        
                        if (!string.IsNullOrEmpty(relatedRegistrationIds))
                        {
                            try
                            {
                                // Parse JSON array: [123, 124, 125]
                                var jsonArray = relatedRegistrationIds.Trim('[', ']');
                                if (!string.IsNullOrEmpty(jsonArray))
                                {
                                    var registrationIds = jsonArray.Split(',')
                                        .Select(id => id.Trim())
                                        .Where(id => int.TryParse(id, out _))
                                        .Select(int.Parse)
                                        .ToList();
                                    
                                    // Get shooting classes for these registrations
                                    var registrationsHub = competition.Children()
                                        .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");
                                    
                                    if (registrationsHub != null)
                                    {
                                        var registrations = registrationsHub.Children()
                                            .Where(x => x.ContentType.Alias == "competitionRegistration")
                                            .Where(x => registrationIds.Contains(x.Id))
                                            .ToList();
                                        
                                        foreach (var reg in registrations)
                                        {
                                            var shootingClass = reg.Value<string>("shootingClass");
                                            if (!string.IsNullOrEmpty(shootingClass) && !shootingClasses.Contains(shootingClass))
                                            {
                                                shootingClasses.Add(shootingClass);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error parsing related registration IDs from existing invoice");
                            }
                        }

                        return Json(new { 
                            success = true, 
                            qrCode = $"data:image/png;base64,{qrCodeBase64}",
                            amount = totalAmount,
                            shootingClasses = string.Join(", ", shootingClasses),
                            invoiceId = existingInvoice.Id,
                            invoiceNumber = invoiceNumber,
                            message = message,
                            usingExisting = true
                        });
                    }
                    catch (Exception qrEx)
                    {
                        _logger.LogError(qrEx, "QR code generation failed for existing invoice - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                            normalizedSwishNumber, amountString, message);
                        return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                    }
                }
                else
                {
                    // Create new invoice - this will be handled by the normal flow
                    return Json(new { success = false, message = "Skapa ny faktura", createNew = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling existing invoice choice");
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Test method to debug invoice detection
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> TestInvoiceDetection(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var memberId = currentMember.Id.ToString();
                var umbracoContext = UmbracoContext;
                var competition = umbracoContext.Content.GetById(competitionId);
                
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävling kunde inte hittas." });
                }

                // Get all children of the competition
                var allChildren = competition.Children().ToList();
                var childrenInfo = allChildren.Select(c => (object)new { 
                    name = c.Name, 
                    alias = c.ContentType?.Alias, 
                    id = c.Id 
                }).ToList();

                // Look for invoices hub
                var invoicesHub = allChildren.FirstOrDefault(x => 
                    x.ContentType?.Alias == "registrationInvoicesHub" || 
                    x.Name?.Contains("Fakturor") == true || 
                    x.Name?.Contains("Betalningar") == true);

                var invoicesInfo = new List<object>();
                if (invoicesHub != null)
                {
                    var allInvoices = invoicesHub.Children().ToList();
                    invoicesInfo = allInvoices.Select(i => (object)new {
                        name = i.Name,
                        alias = i.ContentType?.Alias,
                        id = i.Id,
                        memberId = i.Value<string>("memberId"),
                        invoiceNumber = i.Value<string>("invoiceNumber"),
                        paymentStatus = i.Value<string>("paymentStatus")
                    }).ToList();
                }

                return Json(new {
                    success = true,
                    memberId = memberId,
                    competitionId = competitionId,
                    children = childrenInfo,
                    invoicesHub = invoicesHub != null ? new { name = invoicesHub.Name, alias = invoicesHub.ContentType?.Alias, id = invoicesHub.Id } : null,
                    invoices = invoicesInfo,
                    memberInvoices = invoicesInfo.Where(i => ((dynamic)i).memberId == memberId).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in test invoice detection for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Redirect endpoint for Swish deep links (Gmail-compatible)
        /// </summary>
        [HttpGet]
        public IActionResult SwishRedirect(string payee, decimal amount, string message)
        {
            try
            {
                // Validate parameters
                if (string.IsNullOrEmpty(payee) || amount <= 0 || string.IsNullOrEmpty(message))
                {
                    return BadRequest("Invalid payment parameters");
                }

                // Create payment data object
                var paymentData = new
                {
                    version = 1,
                    payee = payee,
                    amount = amount,
                    message = message
                };

                // Serialize to JSON
                var jsonData = System.Text.Json.JsonSerializer.Serialize(paymentData);

                // Encode to base64
                var base64Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jsonData));

                // Create Swish deep link
                var swishDeepLink = $"swish://payment?data={base64Data}";

                _logger.LogInformation("Redirecting to Swish for payment - Payee: {Payee}, Amount: {Amount}", payee, amount);

                // Return redirect to Swish app
                return Redirect(swishDeepLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Swish redirect");
                return BadRequest("Failed to create Swish payment link");
            }
        }

        /// <summary>
        /// Get payment status for user's registrations in a competition
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPaymentStatus(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Get user's registrations for this competition
                var umbracoContext = UmbracoContext;
                var userRegistrations = new List<object>();

                // Get the competition first, then find its registrations
                var competitionForPayment = umbracoContext.Content.GetById(competitionId);
                if (competitionForPayment == null)
                {
                    return Json(new { success = false, message = "Tävling kunde inte hittas." });
                }

                // Find registrations hub under the competition
                var registrationsHub = competitionForPayment.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");

                if (registrationsHub != null)
                {
                    // Get all registrations under the hub
                    var registrations = registrationsHub.Children()
                        .Where(x => x.ContentType.Alias == "competitionRegistration");

                    foreach (var registration in registrations)
                    {
                        // Check if this registration belongs to the current user
                        var registrationMemberId = registration.Value<string>("memberId");
                        if (registrationMemberId == currentMember.Id.ToString())
                        {
                            var isActive = registration.Value<bool>("isActive", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: true);
                            if (isActive)
                            {
                                var invoiceId = registration.Value<int?>("invoiceId");
                                var paymentStatus = invoiceId.HasValue ? _paymentService.GetRegistrationPaymentStatus(registration.Id) : "No Invoice";

                                userRegistrations.Add(new
                                {
                                    registrationId = registration.Id,
                                    shootingClass = registration.Value<string>("shootingClass"),
                                    paymentStatus = paymentStatus,
                                    invoiceId = invoiceId
                                });
                            }
                        }
                    }
                }

                return Json(new { success = true, registrations = userRegistrations });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting payment status for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Send Swish QR code via email to the registered member
        /// </summary>
        /// <param name="competitionId">Competition ID</param>
        /// <param name="targetMemberId">Optional: Target member ID (for admin registering on behalf of someone else)</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendQRCodeEmail(int competitionId, string targetMemberId = null)
        {
            try
            {
                _logger.LogInformation("SendQRCodeEmail called for CompetitionId: {CompetitionId}", competitionId);

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata kunde inte hittas." });
                }

                // The recipient (shooter) email is resolved later, after we've decided
                // whether targetMemberId points at someone other than the logged-in user.
                // Don't bail here on missing memberData.Email — when an admin sends a QR
                // on behalf of a shooter, the admin's own email is irrelevant.

                // Get competition details
                var umbracoContext = UmbracoContext;
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                var competitionName = competition.Name;
                var swishNumber = competition.Value<string>("swishNumber");
                var _feeStr = competition.Value<string>("registrationFee") ?? "0";
                decimal.TryParse(_feeStr, out var registrationFee);
                var _juniorFeeStr = competition.Value<string>("juniorRegistrationFee") ?? "0";
                decimal.TryParse(_juniorFeeStr, out var juniorFeeConfigured);
                var _subCompFeeStr = competition.Value<string>("subCompetitionFee") ?? "0";
                decimal.TryParse(_subCompFeeStr, out var subCompFeeConfigured);

                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad för denna tävling." });
                }

                if (registrationFee <= 0 && juniorFeeConfigured <= 0 && subCompFeeConfigured <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get user's registration for this competition (single registration with multiple classes)
                int? userRegistrationId = null;
                var userShootingClasses = new List<string>();
                string targetMemberIdFromReg = null;
                string targetMemberNameFromReg = null;
                string searchMemberId = null; // Declare outside the if block so it's accessible later
                bool registeredIsSubCompetition = false;

                // First: try published content (fast, cached)
                searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();

                var registrationsHub = competition.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");

                if (registrationsHub != null)
                {
                    var registrations = registrationsHub.Children()
                        .Where(x => x.ContentType.Alias == "competitionRegistration");

                    foreach (var registration in registrations)
                    {
                        var registrationMemberId = registration.Value<string>("memberId");
                        if (registrationMemberId == searchMemberId)
                        {
                            var isActive = registration.Value<bool>("isActive", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: true);
                            if (isActive)
                            {
                                userRegistrationId = registration.Id;
                                targetMemberIdFromReg = registrationMemberId;
                                targetMemberNameFromReg = registration.Value<string>("memberName");
                                registeredIsSubCompetition = registration.Value<bool>("isSubCompetition",
                                    fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: false);

                                var shootingClassesJson = registration.Value<string>("shootingClasses");
                                var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                                foreach (var classEntry in shootingClasses)
                                {
                                    if (!string.IsNullOrEmpty(classEntry.Class) && !userShootingClasses.Contains(classEntry.Class))
                                    {
                                        userShootingClasses.Add(classEntry.Class);
                                    }
                                }

                                break;
                            }
                        }
                    }
                }

                // Fallback: check unpublished content via IContentService
                if (!userRegistrationId.HasValue)
                {
                    _logger.LogInformation("SendQRCodeEmail: Registration not found in published content, checking via IContentService fallback");
                    var contentService = Services.ContentService;
                    var competitionContent = contentService.GetById(competitionId);
                    if (competitionContent != null)
                    {
                        long totalHub;
                        var hubChildren = contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalHub);
                        var hubContent = hubChildren.FirstOrDefault(x => x.ContentType.Alias == "competitionRegistrationsHub");

                        if (hubContent != null)
                        {
                            long totalRegs;
                            var regChildren = contentService.GetPagedChildren(hubContent.Id, 0, 500, out totalRegs);
                            foreach (var reg in regChildren)
                            {
                                if (reg.ContentType.Alias != "competitionRegistration") continue;
                                var regMemberId = reg.GetValue<string>("memberId");
                                if (regMemberId != searchMemberId) continue;

                                var hasIsActive = reg.Properties.Any(p => p.Alias == "isActive");
                                var isActive = !hasIsActive || reg.GetValue<bool>("isActive");
                                if (isActive)
                                {
                                    userRegistrationId = reg.Id;
                                    targetMemberIdFromReg = regMemberId;
                                    targetMemberNameFromReg = reg.GetValue<string>("memberName");
                                    registeredIsSubCompetition = reg.GetValue<bool>("isSubCompetition");

                                    try
                                    {
                                        var shootingClassesJson = reg.GetValue<string>("shootingClasses");
                                        if (!string.IsNullOrEmpty(shootingClassesJson))
                                        {
                                            var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);
                                            foreach (var classEntry in shootingClasses)
                                            {
                                                if (!string.IsNullOrEmpty(classEntry.Class) && !userShootingClasses.Contains(classEntry.Class))
                                                    userShootingClasses.Add(classEntry.Class);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Error reading shootingClasses from unpublished registration {RegistrationId}", reg.Id);
                                    }

                                    _logger.LogInformation("SendQRCodeEmail: Found unpublished registration {RegistrationId} via IContentService fallback", reg.Id);
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Calculate total amount (per-class base/junior fee + optional deltävling surcharge)
                var classesForCalc = userShootingClasses.Count > 0
                    ? (IReadOnlyCollection<string>)userShootingClasses
                    : new[] { string.Empty };
                var totalAmount = RegistrationFeeCalculator.Calculate(competition, classesForCalc, registeredIsSubCompetition);
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                if (totalAmount <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Check for existing invoice for the TARGET MEMBER (not the logged-in user)
                var memberId = searchMemberId;
                var existingInvoicePublished = await _paymentService.GetExistingInvoiceForMember(competitionId, memberId);

                IContent? invoice;
                string invoiceNumber;
                if (existingInvoicePublished != null)
                {
                    _logger.LogInformation("Using existing invoice {InvoiceId} for member {MemberId}", existingInvoicePublished.Id, memberId);
                    // Convert IPublishedContent to IContent
                    invoice = Services.ContentService.GetById(existingInvoicePublished.Key);
                    if (invoice == null)
                    {
                        return Json(new { success = false, message = "Kunde inte hämta fakturadata." });
                    }
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }
                else
                {
                    _logger.LogInformation("Creating new invoice for member {MemberId} in competition {CompetitionId}", memberId, competitionId);

                    // Create new invoice for the registered member (not the logged-in user)
                    invoice = await _paymentService.CreateInvoiceAsync(
                        competitionId,
                        targetMemberIdFromReg ?? currentMember.Id.ToString(),
                        targetMemberNameFromReg ?? currentMember.Name ?? "Okänd medlem",
                        userRegistrationId.Value,
                        totalAmount,
                        "Swish");

                    if (invoice == null)
                    {
                        return Json(new { success = false, message = "Kunde inte skapa faktura för betalning." });
                    }
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }

                // Generate QR code message
                var message = $"Betalning: {invoiceNumber}";

                // Validate Swish number format
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!normalizedSwishNumber.All(char.IsDigit) || normalizedSwishNumber.Length != 10 || !normalizedSwishNumber.StartsWith("0"))
                {
                    return Json(new { success = false, message = "Swish-numret måste vara 10 siffror som börjar med 0 (t.ex. 0701234567)." });
                }

                // Generate QR code
                byte[] qrCodeBytes;
                try
                {
                    qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                }
                catch (Exception qrEx)
                {
                    _logger.LogError(qrEx, "QR code generation failed - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}",
                        normalizedSwishNumber, amountString, message);
                    return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                }

                // Resolve the actual recipient. When an admin clicks "Skicka QR via e-post"
                // on a shooter's row, targetMemberId points at the shooter — that's who
                // should get the email, not the admin. Fall back to the logged-in user
                // when targetMemberId is empty or matches them (the public self-pay path).
                string recipientEmail;
                string recipientName;
                if (!string.IsNullOrEmpty(targetMemberId)
                    && int.TryParse(targetMemberId, out var targetMemberIdInt)
                    && targetMemberIdInt != memberData.Id)
                {
                    var targetMember = _memberService.GetById(targetMemberIdInt);
                    if (targetMember == null)
                    {
                        return Json(new { success = false, message = "Mottagande skytt kunde inte hittas." });
                    }
                    if (string.IsNullOrEmpty(targetMember.Email))
                    {
                        var displayName = targetMemberNameFromReg ?? targetMember.Name ?? "skytten";
                        return Json(new { success = false, message = $"{displayName} saknar e-postadress." });
                    }
                    recipientEmail = targetMember.Email;
                    recipientName = targetMemberNameFromReg ?? targetMember.Name ?? "Medlem";
                }
                else
                {
                    if (string.IsNullOrEmpty(memberData.Email))
                    {
                        return Json(new { success = false, message = "Ingen e-postadress registrerad för ditt konto." });
                    }
                    recipientEmail = memberData.Email;
                    recipientName = currentMember.Name ?? "Medlem";
                }

                // Send email with QR code as inline attachment
                await _emailService.SendSwishQRCodeEmailAsync(
                    recipientEmail,
                    recipientName,
                    competitionName ?? "Tävling",
                    qrCodeBytes,
                    totalAmount,
                    string.Join(", ", userShootingClasses),
                    invoiceNumber,
                    normalizedSwishNumber,
                    message);

                _logger.LogInformation("Swish QR code email sent to {Email} for competition {CompetitionId}", recipientEmail, competitionId);

                // Audit: actor = the logged-in admin who triggered the send; recipient
                // captured in the notes so the history modal shows where the email
                // actually went.
                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.EmailSent,
                    byMemberId: memberData.Id,
                    byMemberName: memberData.Name,
                    amount: totalAmount,
                    reference: invoiceNumber,
                    notes: $"QR-faktura mejlad till {recipientEmail}");

                return Json(new {
                    success = true,
                    message = $"QR-kod skickad till {recipientEmail}",
                    email = recipientEmail
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Swish QR code email for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Send Swish QR code for team payment via email to the logged-in user
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SendTeamQRCodeEmail(int competitionId, int teamId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null || string.IsNullOrEmpty(memberData.Email))
                    return Json(new { success = false, message = "Ingen e-postadress registrerad." });

                var competition = UmbracoContext.Content.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                var swishNumber = competition.Value<string>("swishNumber");
                var feeStr = competition.Value<string>("teamRegistrationFee") ?? "0";
                if (!decimal.TryParse(feeStr, out var teamFee) || teamFee <= 0 || string.IsNullOrEmpty(swishNumber))
                    return Json(new { success = false, message = "Betalning ej konfigurerad." });

                // Look up the team
                CompetitionTeamDto team;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    team = db.SingleOrDefault<CompetitionTeamDto>(
                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                }
                if (team == null)
                    return Json(new { success = false, message = "Laget kunde inte hittas." });

                var clubName = _clubService.GetClubNameById(team.ClubId) ?? "Okänd förening";
                var amountString = teamFee.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var teamMemberId = $"team-{teamId}";

                // Find team registration doc for invoice linking
                int teamRegDocId = 0;
                var regHub2 = competition.Children()
                    .FirstOrDefault(c => c.ContentType?.Alias == "competitionRegistrationsHub");
                if (regHub2 != null)
                {
                    var teamRegDoc = regHub2.Children()
                        .FirstOrDefault(c => c.ContentType?.Alias == "competitionTeamRegistration"
                            && c.Value<int>("teamId") == teamId);
                    if (teamRegDoc != null) teamRegDocId = teamRegDoc.Id;
                }

                // Get or create invoice
                var existingInvoice = await _paymentService.GetExistingInvoiceForMember(competitionId, teamMemberId);
                IContent? invoice;
                string invoiceNumber;

                if (existingInvoice != null)
                {
                    invoice = Services.ContentService.GetById(existingInvoice.Key);
                    if (invoice == null)
                        return Json(new { success = false, message = "Kunde inte hämta fakturadata." });
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }
                else
                {
                    invoice = await _paymentService.CreateTeamInvoiceAsync(
                        competitionId, teamId, team.TeamName, clubName, teamFee, teamRegDocId);
                    if (invoice == null)
                        return Json(new { success = false, message = "Kunde inte skapa faktura." });
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }

                var message = $"Lag: {invoiceNumber}";
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!normalizedSwishNumber.All(char.IsDigit) || normalizedSwishNumber.Length != 10 || !normalizedSwishNumber.StartsWith("0"))
                    return Json(new { success = false, message = "Ogiltigt Swish-nummer." });

                var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);

                await _emailService.SendSwishQRCodeEmailAsync(
                    memberData.Email,
                    currentMember.Name ?? "Medlem",
                    $"{competition.Name} - Lag: {team.TeamName}",
                    qrCodeBytes,
                    teamFee,
                    $"Lagklass: {team.TeamClass}",
                    invoiceNumber,
                    normalizedSwishNumber,
                    message);

                // Audit: log the team-invoice email send.
                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.EmailSent,
                    byMemberId: memberData.Id,
                    byMemberName: memberData.Name,
                    amount: teamFee,
                    reference: invoiceNumber,
                    notes: $"Lag-QR mejlad till {memberData.Email} (lag: {team.TeamName})");

                return Json(new { success = true, message = $"QR-kod skickad till {memberData.Email}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending team Swish QR email for competition {CompetitionId}, team {TeamId}", competitionId, teamId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }
    }
}
