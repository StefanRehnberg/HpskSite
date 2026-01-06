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
            ILogger<SwishController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager ?? throw new ArgumentNullException(nameof(memberManager));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
            _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
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
                Console.WriteLine("");
                Console.WriteLine("🎯🎯🎯 SWISH CONTROLLER ENTRY POINT 🎯🎯🎯");
                Console.WriteLine($"GeneratePaymentQR called with CompetitionId: {competitionId}");
                Console.WriteLine("🎯🎯🎯 SWISH CONTROLLER ENTRY POINT END 🎯🎯🎯");
                Console.WriteLine("");
                
                Console.WriteLine("Step 1: Getting current member...");
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                Console.WriteLine($"Step 1 Complete: Current member = {currentMember?.Name ?? "NULL"}");
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
                var registrationFee = competition.Value<decimal>("registrationFee");

                _logger.LogInformation("Swish payment request - CompetitionId: {CompetitionId}, SwishNumber: {SwishNumber}, RegistrationFee: {RegistrationFee}", 
                    competitionId, swishNumber, registrationFee);

                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad för denna tävling." });
                }

                if (registrationFee <= 0)
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

                _logger.LogInformation("Looking for registration for member {MemberId} in competition {CompetitionId}", memberData.Id, competitionId);

                // Find registrations hub under the competition
                var registrationsHub = competition.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");

                if (registrationsHub != null)
                {
                    // Get all registrations under the hub
                    var registrations = registrationsHub.Children()
                        .Where(x => x.ContentType.Alias == "competitionRegistration");

                    // Determine which member ID to search for
                    var searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();
                    _logger.LogInformation("Searching for registration for member {SearchMemberId} (targetMemberId param: '{TargetMemberId}', currentMember: {CurrentMemberId})",
                        searchMemberId, targetMemberId ?? "null", currentMember.Id);

                    foreach (var registration in registrations)
                    {
                        // Check if this registration belongs to the search member (could be target member or current user)
                        var registrationMemberId = registration.Value<string>("memberId");
                        if (registrationMemberId == searchMemberId)
                        {
                            var isActive = registration.Value<bool>("isActive", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: true);
                            if (isActive)
                            {
                                userRegistrationId = registration.Id;
                                registeredMemberId = registrationMemberId; // Store the actual registered member's ID
                                registeredMemberName = registration.Value<string>("memberName");

                                // NEW: Extract shooting classes from JSON array
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

                                _logger.LogInformation("Registration {RegistrationId} has shooting classes: '{ShootingClasses}'",
                                    registration.Id, string.Join(", ", userShootingClasses));
                                break; // Only one registration per user
                            }
                        }
                    }
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Calculate total amount (fee × number of classes)
                var classCount = userShootingClasses.Count > 0 ? userShootingClasses.Count : 1;
                var totalAmount = registrationFee * classCount;
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                _logger.LogInformation("Payment calculation - RegistrationId: {RegistrationId}, ClassCount: {ClassCount}, RegistrationFee: {Fee}, TotalAmount: {Total}",
                    userRegistrationId.Value, classCount, registrationFee, totalAmount);
                _logger.LogInformation("User shooting classes found: {ShootingClasses}", string.Join(", ", userShootingClasses));

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

                return Json(new {
                    success = true,
                    qrCode = $"data:image/png;base64,{qrCodeBase64}",
                    amount = totalAmount,
                    registrationCount = userShootingClasses.Count,
                    shootingClasses = string.Join(", ", userShootingClasses),
                    invoiceId = invoice.Id,
                    invoiceNumber = invoiceNumber,
                    message = message
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

                // Get member email
                var memberEmail = memberData.Email;
                if (string.IsNullOrEmpty(memberEmail))
                {
                    return Json(new { success = false, message = "Ingen e-postadress registrerad för ditt konto." });
                }

                // Get competition details
                var umbracoContext = UmbracoContext;
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                var competitionName = competition.Name;
                var swishNumber = competition.Value<string>("swishNumber");
                var registrationFee = competition.Value<decimal>("registrationFee");

                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad för denna tävling." });
                }

                if (registrationFee <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get user's registration for this competition (single registration with multiple classes)
                int? userRegistrationId = null;
                var userShootingClasses = new List<string>();
                string targetMemberIdFromReg = null;
                string targetMemberNameFromReg = null;
                string searchMemberId = null; // Declare outside the if block so it's accessible later

                var registrationsHub = competition.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "competitionRegistrationsHub");

                if (registrationsHub != null)
                {
                    var registrations = registrationsHub.Children()
                        .Where(x => x.ContentType.Alias == "competitionRegistration");

                    // Determine which member ID to search for
                    searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();

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

                                // Extract classes from JSON array
                                var shootingClassesJson = registration.Value<string>("shootingClasses");
                                var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                                foreach (var classEntry in shootingClasses)
                                {
                                    if (!string.IsNullOrEmpty(classEntry.Class) && !userShootingClasses.Contains(classEntry.Class))
                                    {
                                        userShootingClasses.Add(classEntry.Class);
                                    }
                                }

                                break; // Only one registration per user per competition
                            }
                        }
                    }
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Calculate total amount: fee × number of classes
                var classCount = userShootingClasses.Count > 0 ? userShootingClasses.Count : 1;
                var totalAmount = registrationFee * classCount;
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

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

                // Send email with QR code as inline attachment
                await _emailService.SendSwishQRCodeEmailAsync(
                    memberEmail,
                    currentMember.Name ?? "Medlem",
                    competitionName ?? "Tävling",
                    qrCodeBytes,
                    totalAmount,
                    string.Join(", ", userShootingClasses),
                    invoiceNumber,
                    normalizedSwishNumber,
                    message);

                _logger.LogInformation("Swish QR code email sent to {Email} for competition {CompetitionId}", memberEmail, competitionId);

                return Json(new {
                    success = true,
                    message = $"QR-kod skickad till {memberEmail}",
                    email = memberEmail
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Swish QR code email for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

    }
}
