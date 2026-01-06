using Umbraco.Cms.Core;
using System.Globalization;
using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services
{
    public class PaymentService
    {
        private readonly ILogger<PaymentService> _logger;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IContentTypeService _contentTypeService;
        private readonly IMemberService _memberService;

        public PaymentService(ILogger<PaymentService> logger, 
            IContentService contentService, 
            IUmbracoContextAccessor umbracoContextAccessor,
            IContentTypeService contentTypeService,
            IMemberService memberService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
            _umbracoContextAccessor = umbracoContextAccessor ?? throw new ArgumentNullException(nameof(umbracoContextAccessor));
            _contentTypeService = contentTypeService ?? throw new ArgumentNullException(nameof(contentTypeService));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        }

        // Helper method to safely set invoice properties
        private void SetInvoicePropertySafely(IContent invoice, string propertyAlias, object value, IEnumerable<IPropertyType> propertyTypes, ILogger logger)
        {
            try
            {
                var propertyType = propertyTypes.FirstOrDefault(p => p.Alias.Equals(propertyAlias, StringComparison.InvariantCultureIgnoreCase));
                if (propertyType == null)
                {
                    logger.LogWarning("Property '{PropertyAlias}' not found on content type {ContentTypeAlias}", propertyAlias, invoice.ContentType.Alias);
                    return;
                }
                
                // Special handling for paymentStatus property
                if (propertyAlias == "paymentStatus" && value is string stringValue)
                {
                    var validStatuses = new[] { "Pending", "Paid", "Failed", "Refunded", "Cancelled" };
                    if (!validStatuses.Contains(stringValue))
                    {
                        logger.LogWarning("Invalid paymentStatus value '{Value}', defaulting to 'Pending'", value);
                        value = "Pending";
                    }
                }

                logger.LogInformation("About to set '{PropertyAlias}' = '{Value}' (Type: {Type})", propertyAlias, value, value?.GetType().Name);
                invoice.SetValue(propertyAlias, value);
                logger.LogInformation("Successfully set '{PropertyAlias}' = '{Value}'", propertyAlias, value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to set property '{PropertyAlias}' with value '{Value}'", propertyAlias, value);
                
                // For relatedRegistrationIds, log the error but don't throw - allow invoice creation to continue
                if (propertyAlias == "relatedRegistrationIds")
                {
                    logger.LogWarning("Continuing invoice creation despite relatedRegistrationIds error");
                    return;
                }
                
                throw;
            }
        }

        /// <summary>
        /// Generate a unique invoice number based on competition and member
        /// </summary>
        private string GenerateInvoiceNumber(int competitionId, string memberId, int invoiceUmbracoId)
        {
            // Use the actual IDs as requested: [competition Id]-[member Id]-[#]
            int nextSequentialNum = GetNextInvoiceNumberForMember(competitionId, memberId, invoiceUmbracoId);

            return $"{competitionId}-{memberId}-{nextSequentialNum}";
        }

        /// <summary>
        /// Get the next invoice number for a member in a competition
        /// </summary>
        private int GetNextInvoiceNumberForMember(int competitionId, string memberId, int currentInvoiceUmbracoId)
        {
            var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
            var competition = umbracoContext.Content.GetById(competitionId);
            if (competition == null) return 1;

            var invoicesHub = competition.Children()
                .FirstOrDefault(x => x.ContentType?.Alias == "registrationInvoicesHub");

            if (invoicesHub == null) return 1;

            var allInvoices = invoicesHub.Children()
                .Where(x => x.ContentType?.Alias == "registrationInvoice")
                .Where(x => x.Value<string>("memberId") == memberId)
                .Where(x => x.Id != currentInvoiceUmbracoId) // Exclude the current invoice being created/updated
                .ToList();

            int maxInvoiceNum = 0;
            foreach (var invoice in allInvoices)
            {
                string invoiceNumString = invoice.Value<string>("invoiceNumber");
                if (!string.IsNullOrEmpty(invoiceNumString))
                {
                    var parts = invoiceNumString.Split('-');
                    if (parts.Length > 2 && int.TryParse(parts[2], out int num))
                    {
                        if (num > maxInvoiceNum)
                        {
                            maxInvoiceNum = num;
                        }
                    }
                }
            }
            return maxInvoiceNum + 1;
        }

        /// <summary>
        /// Check if there are existing invoices for a member in a competition
        /// </summary>
        public Task<IPublishedContent?> GetExistingInvoiceForMember(int competitionId, string memberId)
        {
            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("Competition {CompetitionId} not found", competitionId);
                    return Task.FromResult<IPublishedContent?>(null);
                }

                var allChildren = competition.Children().ToList();
                _logger.LogInformation("Competition {CompetitionId} children: {Children}", 
                    competitionId, string.Join(", ", allChildren.Select(c => $"{c.Name} ({c.ContentType?.Alias})")));

                var invoicesHub = allChildren
                    .FirstOrDefault(x => x.ContentType?.Alias == "registrationInvoicesHub" || 
                                        x.Name?.Contains("Fakturor") == true || 
                                        x.Name?.Contains("Betalningar") == true);

                if (invoicesHub == null)
                {
                    _logger.LogInformation("No invoices hub found for competition {CompetitionId}. Available children: {Children}", 
                        competitionId, string.Join(", ", allChildren.Select(c => $"{c.Name} ({c.ContentType?.Alias})")));
                    return Task.FromResult<IPublishedContent?>(null);
                }

                _logger.LogInformation("Found invoices hub: {HubName} (Alias: {Alias})", invoicesHub.Name, invoicesHub.ContentType?.Alias);

                var allInvoices = invoicesHub.Children().ToList();
                _logger.LogInformation("Invoices hub has {Count} children: {Invoices}", 
                    allInvoices.Count, string.Join(", ", allInvoices.Select(i => $"{i.Name} ({i.ContentType?.Alias})")));

                var memberInvoices = allInvoices
                    .Where(x => x.ContentType?.Alias == "registrationInvoice")
                    .Where(x => x.Value<string>("memberId") == memberId)
                    .ToList();

                _logger.LogInformation("Found {Count} invoices for member {MemberId}: {Invoices}", 
                    memberInvoices.Count, memberId, string.Join(", ", memberInvoices.Select(i => $"{i.Name} (memberId: {i.Value<string>("memberId")})")));

                var existingInvoice = memberInvoices
                    .OrderByDescending(x => {
                        try
                        {
                            return x.Value<DateTime>("createdDate", fallback: Umbraco.Cms.Core.Models.PublishedContent.Fallback.ToDefaultValue, defaultValue: DateTime.MinValue);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error reading createdDate for invoice {InvoiceId}, using default date", x.Id);
                            return DateTime.MinValue;
                        }
                    })
                    .FirstOrDefault();

                if (existingInvoice != null)
                {
                    var invoiceNumber = "Unknown";
                    var paymentStatus = "Unknown";
                    
                    try
                    {
                        invoiceNumber = existingInvoice.Value<string>("invoiceNumber") ?? "Unknown";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading invoiceNumber for invoice {InvoiceId}", existingInvoice.Id);
                    }
                    
                    try
                    {
                        // Try to read paymentStatus using the raw property approach
                        var paymentStatusProperty = existingInvoice.GetProperty("paymentStatus");
                        if (paymentStatusProperty != null)
                        {
                            // Get the raw source value to avoid JSON parsing issues
                            var rawSourceValue = paymentStatusProperty.GetSourceValue();
                            _logger.LogInformation("Raw paymentStatus source value for invoice {InvoiceId}: {RawValue} (Type: {Type})", 
                                existingInvoice.Id, rawSourceValue, rawSourceValue?.GetType().Name);
                            
                            if (rawSourceValue != null)
                            {
                                paymentStatus = rawSourceValue.ToString().Trim('"', '\'', ' ');
                                _logger.LogInformation("Extracted paymentStatus from source value: '{PaymentStatus}'", paymentStatus);
                            }
                            else
                            {
                                _logger.LogInformation("No source value for paymentStatus property on invoice {InvoiceId}", existingInvoice.Id);
                                paymentStatus = "Pending";
                            }
                        }
                        else
                        {
                            _logger.LogInformation("No paymentStatus property found on invoice {InvoiceId}", existingInvoice.Id);
                            paymentStatus = "Pending";
                        }
                        
                        // Validate it's one of the expected values
                        var validStatuses = new[] { "Pending", "Paid", "Failed", "Refunded", "Cancelled" };
                        if (!validStatuses.Contains(paymentStatus))
                        {
                            _logger.LogWarning("Invalid paymentStatus value '{PaymentStatus}' for invoice {InvoiceId}, defaulting to Pending", paymentStatus, existingInvoice.Id);
                            paymentStatus = "Pending";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading paymentStatus for invoice {InvoiceId}, defaulting to Pending", existingInvoice.Id);
                        paymentStatus = "Pending";
                    }
                    
                    _logger.LogInformation("Found existing invoice {InvoiceNumber} for member {MemberId} in competition {CompetitionId} with status {PaymentStatus}", 
                        invoiceNumber, memberId, competitionId, paymentStatus);
                }
                else
                {
                    _logger.LogInformation("No existing invoice found for member {MemberId} in competition {CompetitionId}", memberId, competitionId);
                }

                return Task.FromResult(existingInvoice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for existing invoice for competition {CompetitionId}, member {MemberId}", competitionId, memberId);
                return Task.FromResult<IPublishedContent?>(null);
            }
        }

        /// <summary>
        /// Create an invoice for a registration
        /// NEW: Updated to accept single registrationId instead of list
        /// </summary>
        public Task<IContent?> CreateInvoiceAsync(
            int competitionId,
            string memberId,
            string memberName,
            int registrationId,
            decimal totalAmount,
            string paymentMethod = "Swish")
        {
            try
            {
                _logger.LogInformation("Starting invoice creation for CompetitionId: {CompetitionId}, MemberId: {MemberId}, RegistrationId: {RegistrationId}, Amount: {Amount}",
                    competitionId, memberId, registrationId, totalAmount);

                if (registrationId <= 0)
                {
                    _logger.LogWarning("CreateInvoiceAsync called with invalid registrationId: {RegistrationId}", registrationId);
                    return Task.FromResult<IContent?>(null);
                }

                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                if (umbracoContext?.Content == null)
                {
                    _logger.LogError("Umbraco context or content is null");
                    return Task.FromResult<IContent?>(null);
                }

                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    _logger.LogWarning("Competition {CompetitionId} not found", competitionId);
                    return Task.FromResult<IContent?>(null);
                }

                // VALIDATION: Check if competition is external
                var isExternal = competition.Value<bool>("isExternal");
                if (isExternal)
                {
                    _logger.LogWarning("Attempt to create invoice for external competition {CompetitionId}. Invoices are not created for external competitions.", competitionId);
                    return Task.FromResult<IContent?>(null);
                }

                var allChildren = competition.Children().ToList();
                var invoicesHub = allChildren
                    .FirstOrDefault(x => x.ContentType?.Alias == "registrationInvoicesHub");

                if (invoicesHub == null)
                {
                    _logger.LogInformation("No invoices hub found for competition {CompetitionId}. Creating it automatically.", competitionId);

                    try
                    {
                        // Get the writable competition node
                        var competitionContent = _contentService.GetById(competitionId);
                        if (competitionContent == null)
                        {
                            _logger.LogError("Could not get writable competition content node {CompetitionId}", competitionId);
                            return Task.FromResult<IContent?>(null);
                        }

                        // Create the invoices hub
                        var hubName = "Fakturor";
                        var hub = _contentService.Create(hubName, competitionContent.Id, "registrationInvoicesHub");

                        if (hub == null)
                        {
                            _logger.LogError("Failed to create registrationInvoicesHub for competition {CompetitionId}", competitionId);
                            return Task.FromResult<IContent?>(null);
                        }

                        // Save and publish the hub
                        var hubSaveResult = _contentService.Save(hub);
                        if (!hubSaveResult.Success)
                        {
                            _logger.LogError("Failed to save registrationInvoicesHub for competition {CompetitionId}", competitionId);
                            return Task.FromResult<IContent?>(null);
                        }

                        var hubPublishResult = _contentService.Publish(hub, Array.Empty<string>());
                        if (!hubPublishResult.Success)
                        {
                            _logger.LogError("Failed to publish registrationInvoicesHub for competition {CompetitionId}", competitionId);
                            _contentService.Delete(hub);
                            return Task.FromResult<IContent?>(null);
                        }

                        _logger.LogInformation("Successfully created and published registrationInvoicesHub {HubId} for competition {CompetitionId}", hub.Id, competitionId);

                        // Re-fetch to get the published version
                        invoicesHub = umbracoContext.Content.GetById(hub.Id);
                        if (invoicesHub == null)
                        {
                            _logger.LogError("Could not retrieve newly created invoices hub {HubId}", hub.Id);
                            return Task.FromResult<IContent?>(null);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating registrationInvoicesHub for competition {CompetitionId}", competitionId);
                        return Task.FromResult<IContent?>(null);
                    }
                }

                // Create the invoice content item
                var invoiceName = $"{memberName} - {DateTime.Now:yyyy-MM-dd}";
                var invoice = _contentService.Create(invoiceName, invoicesHub.Id, "registrationInvoice");

                if (invoice == null)
                {
                    _logger.LogError("Failed to create invoice content item for competition {CompetitionId}, member {MemberId}", competitionId, memberId);
                    return Task.FromResult<IContent?>(null);
                }
                
                // Get property types for validation
                var contentType = _contentTypeService.Get(invoice.ContentType.Id);
                if (contentType == null)
                {
                    _logger.LogError("Could not get content type for invoice {InvoiceId}", invoice.Id);
                    _contentService.Delete(invoice);
                    return Task.FromResult<IContent?>(null);
                }
                var propertyTypes = contentType.PropertyTypes;
                
                SetInvoicePropertySafely(invoice, "competitionId", competitionId, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "memberId", memberId, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "memberName", memberName, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "totalAmount", totalAmount, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "paymentMethod", paymentMethod, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "paymentStatus", "Pending", propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "createdDate", DateTime.Now, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "isActive", true, propertyTypes, _logger);

                // Store single registration ID (NEW)
                _logger.LogInformation("Setting registrationId to: {RegistrationId}", registrationId);
                SetInvoicePropertySafely(invoice, "registrationId", registrationId, propertyTypes, _logger);
                
                // Generate and set invoice number
                var invoiceNumber = GenerateInvoiceNumber(competitionId, memberId, invoice.Id);
                SetInvoicePropertySafely(invoice, "invoiceNumber", invoiceNumber, propertyTypes, _logger);
                
                var saveResult = _contentService.Save(invoice);
                if (saveResult.Success)
                {
                    var publishResult = _contentService.Publish(invoice, Array.Empty<string>());
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} saved and published successfully.", invoice.Id);
                        return Task.FromResult<IContent?>(invoice);
                    }
                    else
                    {
                        _logger.LogError("Failed to publish invoice {InvoiceId}. Success: {Success}", invoice.Id, publishResult.Success);
                        _contentService.Delete(invoice);
                        return Task.FromResult<IContent?>(null);
                    }
                }
                else
                {
                    _logger.LogError("Failed to save invoice {InvoiceId}. Success: {Success}", invoice.Id, saveResult.Success);
                    _contentService.Delete(invoice);
                    return Task.FromResult<IContent?>(null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CreateInvoiceAsync for CompetitionId: {CompetitionId}, MemberId: {MemberId}", competitionId, memberId);
                return Task.FromResult<IContent?>(null);
            }
        }

        /// <summary>
        /// Update payment status for an invoice
        /// </summary>
        public Task<bool> UpdatePaymentStatusAsync(
            int invoiceId,
            string paymentStatus,
            DateTime? paymentDate = null,
            string? transactionId = null,
            string? notes = null)
        {
            try
            {
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null) return Task.FromResult(false);

                invoice.SetValue("paymentStatus", paymentStatus);
                
                if (paymentDate.HasValue)
                    invoice.SetValue("paymentDate", paymentDate.Value);
                
                if (!string.IsNullOrEmpty(transactionId))
                    invoice.SetValue("transactionId", transactionId);
                
                if (!string.IsNullOrEmpty(notes))
                    invoice.SetValue("notes", notes);

                var saveResult = _contentService.Save(invoice);
                if (saveResult.Success)
                {
                    _contentService.Publish(invoice, Array.Empty<string>());
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
            catch (Exception)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Get payment status for a specific registration
        /// </summary>
        public string GetRegistrationPaymentStatus(int registrationId)
        {
            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var registration = umbracoContext.Content.GetById(registrationId);
                
                if (registration == null) return "Unknown";

                var invoiceId = registration.Value<int?>("invoiceId");
                if (!invoiceId.HasValue) return "No Invoice";

                var invoice = umbracoContext.Content.GetById(invoiceId.Value);
                return invoice?.Value<string>("paymentStatus") ?? "Unknown";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting payment status for registration {RegistrationId}", registrationId);
                return "Unknown";
            }
        }

        /// <summary>
        /// Get all invoices for a competition
        /// </summary>
        public List<RegistrationInvoice> GetCompetitionInvoices(int competitionId)
        {
            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competition = umbracoContext.Content.GetById(competitionId);
                
                if (competition == null) return new List<RegistrationInvoice>();

                var invoicesHub = competition.Children()
                    .FirstOrDefault(x => x.ContentType.Alias == "registrationInvoicesHub");

                if (invoicesHub == null) return new List<RegistrationInvoice>();

                // For now, return empty list until we resolve the PublishedSnapshot issue
                // This will be implemented properly in the next iteration
                return new List<RegistrationInvoice>();
            }
            catch (Exception)
            {
                return new List<RegistrationInvoice>();
            }
        }

        /// <summary>
        /// Get invoices for a specific member
        /// </summary>
        public List<RegistrationInvoice> GetMemberInvoices(string memberId)
        {
            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                
                // For now, return empty list until we resolve the PublishedSnapshot issue
                // This will be implemented properly in the next iteration
                return new List<RegistrationInvoice>();
            }
            catch (Exception)
            {
                return new List<RegistrationInvoice>();
            }
        }

        /// <summary>
        /// Calculate total amount for a registration based on number of classes
        /// </summary>
        public decimal CalculateRegistrationTotal(int competitionId, int registrationId)
        {
            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competition = umbracoContext.Content.GetById(competitionId);

                if (competition == null) return 0;

                var registrationFee = competition.Value<decimal>("registrationFee");

                // Get registration and count shooting classes
                var registration = _contentService.GetById(registrationId);
                if (registration == null) return 0;

                var shootingClassesJson = registration.GetValue<string>("shootingClasses");
                var shootingClasses = CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);

                // Calculate: fee × number of classes
                return registrationFee * shootingClasses.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating registration total for registration {RegistrationId}", registrationId);
                return 0;
            }
        }


        /// <summary>
        /// Link a registration to an invoice (NEW: updated for single registration)
        /// </summary>
        private void LinkRegistrationToInvoice(int registrationId, int invoiceId)
        {
            try
            {
                _logger.LogInformation("Linking registration {RegistrationId} to invoice {InvoiceId}", registrationId, invoiceId);

                var registration = _contentService.GetById(registrationId);
                if (registration != null)
                {
                    registration.SetValue("invoiceId", invoiceId);
                    var saveResult = _contentService.Save(registration);
                    if (saveResult.Success)
                    {
                        _contentService.Publish(registration, Array.Empty<string>());
                        _logger.LogInformation("Successfully linked registration {RegistrationId} to invoice {InvoiceId}", registrationId, invoiceId);
                    }
                    else
                    {
                        _logger.LogError("Failed to save registration {RegistrationId} when linking to invoice {InvoiceId}", registrationId, invoiceId);
                    }
                }
                else
                {
                    _logger.LogWarning("Registration {RegistrationId} not found when linking to invoice {InvoiceId}", registrationId, invoiceId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error linking registration {RegistrationId} to invoice {InvoiceId}", registrationId, invoiceId);
            }
        }
    }
}