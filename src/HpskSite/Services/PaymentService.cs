using Umbraco.Cms.Core;
using System.Collections.Concurrent;
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
        private readonly InvoiceAuditService _auditService;
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly Umbraco.Cms.Core.Cache.AppCaches _appCaches;

        public PaymentService(ILogger<PaymentService> logger,
            IContentService contentService,
            IUmbracoContextAccessor umbracoContextAccessor,
            IContentTypeService contentTypeService,
            IMemberService memberService,
            InvoiceAuditService auditService,
            EmailService emailService,
            ClubService clubService,
            Umbraco.Cms.Core.Cache.AppCaches appCaches)
        {
            _appCaches = appCaches;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
            _umbracoContextAccessor = umbracoContextAccessor ?? throw new ArgumentNullException(nameof(umbracoContextAccessor));
            _contentTypeService = contentTypeService ?? throw new ArgumentNullException(nameof(contentTypeService));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
            _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
            _clubService = clubService ?? throw new ArgumentNullException(nameof(clubService));
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
                    // Invoice numbers are "{competitionId}-{memberId}-{sequence}", so the sequence is
                    // the LAST segment — not parts[2]. A memberId can itself contain a hyphen
                    // ("team-13", "club-2604"), and reading parts[2] then picked up the team/club id
                    // as the previous sequence: club-2604's second invoice came out as
                    // "2576-club-2604-2605" instead of "-2". Plain member ids are unaffected.
                    var parts = invoiceNumString.Split('-');
                    if (parts.Length > 2 && int.TryParse(parts[^1], out int num))
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
        // A competition must have exactly ONE registrationInvoicesHub - every other lookup in the
        // codebase resolves it with FirstOrDefault, so invoices under a second hub are invisible to
        // payment, receipts, consolidation and the repair sweep alike. Two things conspired against
        // that invariant here: the existence check read the PUBLISHED cache, which lags behind a hub
        // the background eager-invoice job created moments earlier, and two registrations could run
        // check-then-create concurrently with nothing in between. A registration burst - exactly what
        // a competition opening is - could therefore mint a duplicate. Serialize per competition and
        // re-check against the DB, which is authoritative and has no cache lag.
        private static readonly ConcurrentDictionary<int, object> _invoiceHubGates = new();

        /// <summary>
        /// The id of this competition's invoice hub, creating it once if it is genuinely missing.
        /// Returns null only when the hub could not be created.
        /// </summary>
        private int? EnsureInvoicesHubId(int competitionId)
        {
            var gate = _invoiceHubGates.GetOrAdd(competitionId, _ => new object());
            lock (gate)
            {
                var existing = FindInvoicesHubIdFromDb(competitionId);
                if (existing.HasValue) return existing;

                _logger.LogInformation("No invoices hub found for competition {CompetitionId}. Creating it automatically.", competitionId);
                try
                {
                    var competitionContent = _contentService.GetById(competitionId);
                    if (competitionContent == null)
                    {
                        _logger.LogError("Could not get writable competition content node {CompetitionId}", competitionId);
                        return null;
                    }

                    var hub = _contentService.Create("Fakturor", competitionContent.Id, "registrationInvoicesHub");
                    if (hub == null)
                    {
                        _logger.LogError("Failed to create registrationInvoicesHub for competition {CompetitionId}", competitionId);
                        return null;
                    }

                    if (!_contentService.Save(hub).Success)
                    {
                        _logger.LogError("Failed to save registrationInvoicesHub for competition {CompetitionId}", competitionId);
                        return null;
                    }

                    if (!_contentService.Publish(hub, new[] { "*" }, -1).Success)
                    {
                        _logger.LogError("Failed to publish registrationInvoicesHub for competition {CompetitionId}", competitionId);
                        _contentService.Delete(hub);
                        return null;
                    }

                    _logger.LogInformation("Successfully created and published registrationInvoicesHub {HubId} for competition {CompetitionId}", hub.Id, competitionId);
                    return hub.Id;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating registrationInvoicesHub for competition {CompetitionId}", competitionId);
                    return null;
                }
            }
        }

        /// <summary>
        /// Reads the hub straight from the DB rather than the published cache. A competition has a
        /// handful of children, so one page covers it.
        /// </summary>
        private int? FindInvoicesHubIdFromDb(int competitionId)
        {
            var children = _contentService.GetPagedChildren(competitionId, 0, 500, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            return hub?.Id;
        }

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

                var invoicesHubId = EnsureInvoicesHubId(competitionId);
                if (invoicesHubId == null)
                {
                    return Task.FromResult<IContent?>(null);
                }

                // Create the invoice content item
                var invoiceName = $"{memberName} - {DateTime.Now:yyyy-MM-dd}";
                var invoice = _contentService.Create(invoiceName, invoicesHubId.Value, "registrationInvoice");

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
                    var publishResult = _contentService.Publish(invoice, new[] { "*" }, -1);
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Invoice {InvoiceId} saved and published successfully.", invoice.Id);
                        InvalidateInvoiceListCaches();

                        // Log a Created event in the audit table. Fire-and-forget; the audit
                        // service swallows its own exceptions and the invoice is already saved.
                        // No paymentMethod on the creation event — nothing has been paid yet.
                        // Stamping the intended method (Swish) here made unpaid invoices look paid.
                        _ = _auditService.LogAsync(
                            invoiceId: invoice.Id,
                            competitionId: competitionId,
                            eventType: InvoicePaymentEventTypes.Created,
                            byMemberId: null,
                            byMemberName: null,
                            paymentMethod: null,
                            amount: totalAmount,
                            reference: invoiceNumber,
                            notes: "Faktura skapad – väntar på betalning");

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
        /// Create an invoice that is NOT tied to a single registration — a consolidated
        /// ("samlingsfaktura") invoice covering many registrations, or a credit note against one.
        /// Same hub / numbering / audit path as <see cref="CreateInvoiceAsync"/>; the caller owns the
        /// extra properties (invoiceKind, coveredInvoiceIds, …) via <paramref name="extraProperties"/>
        /// so this method stays agnostic about what kind of document it is minting.
        ///
        /// Returns null if the invoice could not be created. Callers MUST check
        /// <see cref="MissingInvoiceProperties"/> first — a property that doesn't exist on the
        /// doctype makes SetValue a silent no-op, which would produce a parent invoice with no link
        /// to its children.
        /// </summary>
        public Task<IContent?> CreateStandaloneInvoiceAsync(
            int competitionId,
            string memberId,
            string memberName,
            decimal totalAmount,
            string paymentMethod,
            IDictionary<string, object?> extraProperties,
            string auditNote)
        {
            try
            {
                _umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext);
                var competition = umbracoContext?.Content?.GetById(competitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                {
                    _logger.LogError("CreateStandaloneInvoiceAsync: {CompetitionId} is not a competition", competitionId);
                    return Task.FromResult<IContent?>(null);
                }
                if (competition.Value<bool>("isExternal"))
                {
                    _logger.LogWarning("CreateStandaloneInvoiceAsync: competition {CompetitionId} is external — no invoices", competitionId);
                    return Task.FromResult<IContent?>(null);
                }

                var hubId = competition.Children()
                    .FirstOrDefault(x => x.ContentType?.Alias == "registrationInvoicesHub")?.Id;
                if (hubId == null)
                {
                    // The consolidated flow always runs against a competition that already has
                    // invoices (that's what is being consolidated), so a missing hub means something
                    // is wrong — don't silently create one here.
                    _logger.LogError("CreateStandaloneInvoiceAsync: competition {CompetitionId} has no registrationInvoicesHub", competitionId);
                    return Task.FromResult<IContent?>(null);
                }

                var invoiceName = $"{memberName} - {DateTime.Now:yyyy-MM-dd}";
                var invoice = _contentService.Create(invoiceName, hubId.Value, "registrationInvoice");
                if (invoice == null)
                {
                    _logger.LogError("CreateStandaloneInvoiceAsync: could not create node for competition {CompetitionId}", competitionId);
                    return Task.FromResult<IContent?>(null);
                }

                var contentType = _contentTypeService.Get(invoice.ContentType.Id);
                if (contentType == null)
                {
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

                foreach (var (alias, value) in extraProperties)
                    SetInvoicePropertySafely(invoice, alias, value!, propertyTypes, _logger);

                var invoiceNumber = GenerateInvoiceNumber(competitionId, memberId, invoice.Id);
                SetInvoicePropertySafely(invoice, "invoiceNumber", invoiceNumber, propertyTypes, _logger);

                if (!_contentService.Save(invoice).Success)
                {
                    _logger.LogError("CreateStandaloneInvoiceAsync: save failed for {InvoiceId}", invoice.Id);
                    _contentService.Delete(invoice);
                    return Task.FromResult<IContent?>(null);
                }
                if (!_contentService.Publish(invoice, new[] { "*" }, -1).Success)
                {
                    _logger.LogError("CreateStandaloneInvoiceAsync: publish failed for {InvoiceId}", invoice.Id);
                    _contentService.Delete(invoice);
                    return Task.FromResult<IContent?>(null);
                }
                InvalidateInvoiceListCaches();

                _ = _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.Created,
                    byMemberId: null,
                    byMemberName: null,
                    paymentMethod: null,
                    amount: totalAmount,
                    reference: invoiceNumber,
                    notes: auditNote);

                return Task.FromResult<IContent?>(invoice);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CreateStandaloneInvoiceAsync for competition {CompetitionId}", competitionId);
                return Task.FromResult<IContent?>(null);
            }
        }

        /// <summary>
        /// Is this invoice currently covered by a samlingsfaktura that is still open (not makulerad)?
        ///
        /// Such an invoice must NOT be quietly cancelled or re-priced: the parent was issued for a
        /// total that includes it, the club is about to pay (or has paid) that total, and nothing
        /// recalculates a parent — by design, since an issued invoice is never altered. Cancelling the
        /// child silently would leave the club paying for a registration that no longer exists.
        /// The correction is a kreditfaktura.
        ///
        /// Deliberately reads raw properties instead of taking a dependency on
        /// ConsolidatedInvoiceService, which already depends on this class.
        /// </summary>
        public bool IsCoveredByOpenConsolidation(int invoiceId, out string parentInvoiceNumber, out bool parentIsPaid)
        {
            parentInvoiceNumber = "";
            parentIsPaid = false;
            try
            {
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null || !invoice.HasProperty("settledByInvoiceId")) return false;

                var raw = (invoice.GetValue<string>("settledByInvoiceId") ?? "").Trim();
                if (!int.TryParse(raw, out var parentId) || parentId <= 0) return false;

                var parent = _contentService.GetById(parentId);
                if (parent == null) return false;

                // Older data stores paymentStatus JSON-wrapped as ["Paid"], so compare the normalised
                // value — a raw comparison would read a cancelled parent as still open (blocking a
                // legitimate cancel) or a paid one as unpaid (offering the wrong correction).
                var status = ConsolidatedInvoiceService.NormalizeStatus(parent.GetValue<string>("paymentStatus"));
                if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)) return false;

                parentInvoiceNumber = parent.GetValue<string>("invoiceNumber") ?? parentId.ToString();
                parentIsPaid = string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not check consolidation cover for invoice {InvoiceId}", invoiceId);
                return false;   // never block a normal operation because this check failed
            }
        }

        /// <summary>
        /// Drop the admin invoice-list cache. GetInvoices caches for 5 minutes whenever no status or
        /// search filter is set, and nothing invalidated it when an invoice was CREATED — so a cashier
        /// who had just registered someone could not see their invoice for minutes, and neither could
        /// the club admin about to pay it. Same key prefix InvoiceAdminController clears.
        /// </summary>
        private void InvalidateInvoiceListCaches()
        {
            try { _appCaches.RuntimeCache.ClearByRegex("^admin_invoices_"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not clear the admin invoice cache"); }
        }

        /// <summary>Swedish explanation for a refused cancel/repricing of a covered invoice.</summary>
        public static string CoveredByConsolidationMessage(string parentInvoiceNumber, bool parentIsPaid) =>
            parentIsPaid
                ? $"Fakturan ingår i samlingsfaktura {parentInvoiceNumber} som redan är betald. "
                + "Skapa en kreditfaktura istället för att makulera."
                : $"Fakturan ingår i samlingsfaktura {parentInvoiceNumber}. Makulera samlingsfakturan först "
                + "(då frigörs fakturorna), eller skapa en kreditfaktura.";

        /// <summary>
        /// Properties the consolidated-invoice / credit-note flow needs on the `registrationInvoice`
        /// doctype. Missing ones must be reported to the operator, never written through: SetValue on
        /// a non-existent property is a silent no-op, so a parent invoice would save "successfully"
        /// with no link to the invoices it covers.
        /// </summary>
        public static readonly string[] ConsolidatedInvoiceProperties =
        {
            "invoiceKind", "coveredInvoiceIds", "settledByInvoiceId", "creditsInvoiceId", "payerClubId"
        };

        /// <summary>Which of <see cref="ConsolidatedInvoiceProperties"/> are missing from the doctype.</summary>
        public List<string> MissingInvoiceProperties()
        {
            var missing = new List<string>();
            try
            {
                var contentType = _contentTypeService.Get("registrationInvoice");
                if (contentType == null) return ConsolidatedInvoiceProperties.ToList();

                var aliases = contentType.CompositionPropertyTypes.Select(pt => pt.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
                missing.AddRange(ConsolidatedInvoiceProperties.Where(a => !aliases.Contains(a)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not inspect registrationInvoice property types");
            }
            return missing;
        }

        /// <summary>
        /// Ensure a registration has a (Pending) invoice — the eager-creation entry point so
        /// every fee-bearing registration carries an invoice from the moment it's created,
        /// instead of one being lazily minted when a payment option is first chosen.
        ///
        /// Idempotent: if a non-cancelled invoice already exists for the registration (matched
        /// by the registration's own id or by the member), it is linked + returned rather than
        /// duplicated. Returns null (no invoice) when the competition is external or the
        /// computed fee is 0 — a free registration legitimately has no invoice.
        ///
        /// Fee is computed via <see cref="RegistrationFeeCalculator"/> (the single source of
        /// truth) from the registration's own classes + deltävling flag. Best-effort: callers
        /// should treat a null return as "no invoice yet" and not fail the registration.
        /// </summary>
        public async Task<IContent?> EnsureRegistrationInvoiceAsync(int competitionId, int registrationId)
        {
            try
            {
                var registration = _contentService.GetById(registrationId);
                if (registration == null || registration.ContentType.Alias != "competitionRegistration")
                    return null;

                var competition = _contentService.GetById(competitionId);
                if (competition == null || competition.GetValue<bool>("isExternal"))
                    return null;

                var memberId = registration.GetValue<int>("memberId");
                if (memberId <= 0) return null;

                // Idempotent: reuse an existing non-cancelled invoice for this registration.
                var existing = FindActiveInvoiceForRegistration(competition, registrationId, memberId);
                if (existing != null)
                {
                    if (registration.GetValue<int>("invoiceId") != existing.Id)
                    {
                        registration.SetValue("invoiceId", existing.Id);
                        if (_contentService.Save(registration).Success)
                            _contentService.Publish(registration, new[] { "*" }, -1);
                    }
                    return existing;
                }

                // Compute the fee the registration owes.
                var classEntries = CompetitionRegistrationDocument.DeserializeShootingClasses(
                    registration.GetValue<string>("shootingClasses") ?? "");
                var classCodes = classEntries.Select(c => c.Class).Where(c => !string.IsNullOrEmpty(c)).ToList();
                var isSub = registration.HasProperty("isSubCompetition") && registration.GetValue<bool>("isSubCompetition");
                var classesForCalc = classCodes.Count > 0
                    ? (IReadOnlyCollection<string>)classCodes
                    : new[] { string.Empty };
                var fee = RegistrationFeeCalculator.Calculate(competition, classesForCalc, isSub);
                if (fee <= 0) return null; // free registration → no invoice (status shows "Ingen avgift")

                var memberName = registration.GetValue<string>("memberName") ?? "";
                var invoice = await CreateInvoiceAsync(competitionId, memberId.ToString(), memberName, registrationId, fee, "Swish");
                if (invoice == null) return null;

                registration.SetValue("invoiceId", invoice.Id);
                if (_contentService.Save(registration).Success)
                    _contentService.Publish(registration, new[] { "*" }, -1);
                return invoice;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnsureRegistrationInvoiceAsync failed for registration {RegistrationId}", registrationId);
                return null;
            }
        }

        /// <summary>
        /// Reconcile a registration's invoice(s) to its CURRENT fee (derived from its stored
        /// shooting classes + deltävling opt-in), so ANY change — class added/removed, or the
        /// deltävling toggled — is reflected on the invoice. Mirrors the cashier edit path's
        /// delta/top-up model:
        ///   sumPaid         = total of every Paid invoice
        ///   existingPending = the (single) Pending invoice, if any
        ///   delta           = newFee - sumPaid   (what the shooter still owes)
        /// delta &gt; 0 → patch the Pending invoice to <c>delta</c> (or create one if missing);
        /// delta == 0 → cancel a leftover Pending invoice (fully covered / now free);
        /// delta &lt; 0 → already paid more than the new fee: a refund the organizer handles
        ///               manually — Paid invoices are never modified, we just cancel any Pending.
        /// Idempotent and best-effort.
        /// </summary>
        public async Task<bool> ReconcileRegistrationInvoiceAsync(int competitionId, int registrationId)
        {
            try
            {
                var registration = _contentService.GetById(registrationId);
                if (registration == null || registration.ContentType.Alias != "competitionRegistration")
                    return false;

                var competition = _contentService.GetById(competitionId);
                if (competition == null || competition.GetValue<bool>("isExternal"))
                    return false;

                var memberId = registration.GetValue<int>("memberId");
                if (memberId <= 0) return false;

                // Fee owed by the registration as it now stands.
                var classEntries = CompetitionRegistrationDocument.DeserializeShootingClasses(
                    registration.GetValue<string>("shootingClasses") ?? "");
                var classCodes = classEntries.Select(c => c.Class).Where(c => !string.IsNullOrEmpty(c)).ToList();
                var isSub = registration.HasProperty("isSubCompetition") && registration.GetValue<bool>("isSubCompetition");
                var classesForCalc = classCodes.Count > 0
                    ? (IReadOnlyCollection<string>)classCodes
                    : new[] { string.Empty };
                var newFee = RegistrationFeeCalculator.Calculate(competition, classesForCalc, isSub);

                var allInvoices = GetAllNonCancelledInvoicesForRegistration(competition, registrationId);

                decimal sumPaid = 0m;
                IContent? existingPending = null;
                foreach (var inv in allInvoices)
                {
                    var s = (inv.GetValue<string>("paymentStatus") ?? "Pending").Trim().Trim('[', ']').Trim('"');
                    var amt = inv.GetValue<decimal>("totalAmount");
                    if (s == "Paid") sumPaid += amt;
                    else if (s == "Pending" && existingPending == null) existingPending = inv;
                }

                var delta = newFee - sumPaid;

                if (delta > 0)
                {
                    if (existingPending != null)
                    {
                        // Adjust the outstanding Pending invoice up or down to match.
                        if (existingPending.GetValue<decimal>("totalAmount") != delta)
                        {
                            existingPending.SetValue("totalAmount", delta);
                            if (_contentService.Save(existingPending).Success)
                                _contentService.Publish(existingPending, new[] { "*" }, -1);
                        }
                    }
                    else
                    {
                        // No outstanding invoice — create one for what's owed. Paid invoices (if
                        // any) stay as the historical record; the new one is a top-up for the rest.
                        var memberName = registration.GetValue<string>("memberName") ?? "";
                        var created = await CreateInvoiceAsync(competitionId, memberId.ToString(), memberName, registrationId, delta, "Swish");
                        if (created != null && sumPaid == 0m)
                        {
                            registration.SetValue("invoiceId", created.Id);
                            if (_contentService.Save(registration).Success)
                                _contentService.Publish(registration, new[] { "*" }, -1);
                        }
                    }
                }
                else
                {
                    // delta <= 0: nothing (more) to collect. Cancel a leftover Pending invoice so
                    // the registration stops showing an amount due (e.g. a class was removed, or the
                    // deltävling was unticked, bringing the fee to what's already paid / to zero).
                    if (existingPending != null)
                    {
                        existingPending.SetValue("paymentStatus", "Cancelled");
                        var notes = existingPending.GetValue<string>("notes") ?? "";
                        notes += $"\n[{DateTime.Now:yyyy-MM-dd HH:mm}] Makulerad – avgiften är nu {newFee:0} kr (redan betalt: {sumPaid:0} kr).";
                        existingPending.SetValue("notes", notes);
                        if (_contentService.Save(existingPending).Success)
                            _contentService.Publish(existingPending, new[] { "*" }, -1);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReconcileRegistrationInvoiceAsync failed for registration {RegistrationId}", registrationId);
                return false;
            }
        }

        /// <summary>
        /// Read-only snapshot of a registration's billing state under the delta/top-up model:
        /// <c>FullFee</c> = the fee the registration owes as it now stands; <c>SumPaid</c> = total of
        /// every Paid invoice; <c>Outstanding</c> = <c>max(0, FullFee - SumPaid)</c> (what still has to
        /// be collected); <c>PendingInvoice</c> = the single outstanding Pending invoice, if one exists.
        /// Does NOT create or modify anything — use <see cref="EnsureOutstandingInvoiceAsync"/> when you
        /// need the Pending invoice to actually exist for payment.
        /// </summary>
        public RegistrationInvoiceTotals GetInvoiceTotalsForRegistration(int competitionId, int registrationId)
        {
            var result = new RegistrationInvoiceTotals();
            var competition = _contentService.GetById(competitionId);
            var registration = _contentService.GetById(registrationId);
            if (competition == null || registration == null ||
                registration.ContentType.Alias != "competitionRegistration")
                return result;

            var classEntries = CompetitionRegistrationDocument.DeserializeShootingClasses(
                registration.GetValue<string>("shootingClasses") ?? "");
            var classCodes = classEntries.Select(c => c.Class).Where(c => !string.IsNullOrEmpty(c)).ToList();
            var isSub = registration.HasProperty("isSubCompetition") && registration.GetValue<bool>("isSubCompetition");
            var classesForCalc = classCodes.Count > 0
                ? (IReadOnlyCollection<string>)classCodes
                : new[] { string.Empty };
            result.FullFee = RegistrationFeeCalculator.Calculate(competition, classesForCalc, isSub);

            foreach (var inv in GetAllNonCancelledInvoicesForRegistration(competition, registrationId))
            {
                var s = (inv.GetValue<string>("paymentStatus") ?? "Pending").Trim().Trim('[', ']').Trim('"');
                var amt = inv.GetValue<decimal>("totalAmount");
                if (s == "Paid") result.SumPaid += amt;
                else if (s == "Pending" && result.PendingInvoice == null) result.PendingInvoice = inv;
            }

            result.Outstanding = Math.Max(0m, result.FullFee - result.SumPaid);
            return result;
        }

        /// <summary>
        /// Reconcile the registration's invoices to the current fee (delta/top-up model, so an
        /// add/swap/remove of classes on an already-paid registration is billed only for the delta —
        /// Paid invoices are never modified) and return the resulting billing snapshot. On return, if
        /// <c>Outstanding &gt; 0</c> the <c>PendingInvoice</c> exists and carries exactly that amount;
        /// if <c>Outstanding == 0</c> there is nothing to collect (any leftover Pending was cancelled).
        /// This is the payment entry point — call it before generating a Swish QR so the shooter pays
        /// the top-up, not the full fee again.
        /// </summary>
        public async Task<RegistrationInvoiceTotals> EnsureOutstandingInvoiceAsync(int competitionId, int registrationId)
        {
            await ReconcileRegistrationInvoiceAsync(competitionId, registrationId);
            return GetInvoiceTotalsForRegistration(competitionId, registrationId);
        }

        /// <summary>
        /// All non-cancelled invoices for a registration (matched by <c>registrationId</c> or the
        /// legacy <c>relatedRegistrationIds</c> JSON array), newest first.
        /// </summary>
        private List<IContent> GetAllNonCancelledInvoicesForRegistration(IContent competition, int registrationId)
        {
            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub == null) return new List<IContent>();

            return _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoice")
                .Where(c => (c.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']').Trim('"') != "Cancelled")
                .Where(c => c.GetValue<int>("registrationId") == registrationId
                            || (c.GetValue<string>("relatedRegistrationIds") ?? "").Contains(registrationId.ToString()))
                .OrderByDescending(c => c.Id)
                .ToList();
        }

        /// <summary>
        /// Find a non-cancelled registrationInvoice for a registration via the writable
        /// content service (current data, not the published cache). Matches the invoice's
        /// single <c>registrationId</c>, the legacy <c>relatedRegistrationIds</c> JSON array,
        /// or — as a fallback for invoices not yet linked — the member id. Newest first.
        /// </summary>
        private IContent? FindActiveInvoiceForRegistration(IContent competition, int registrationId, int memberId)
        {
            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub == null) return null;

            var invoices = _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoice")
                .Where(c => (c.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']').Trim('"') != "Cancelled")
                .OrderByDescending(c => c.Id)
                .ToList();

            return invoices.FirstOrDefault(c =>
                       c.GetValue<int>("registrationId") == registrationId ||
                       (c.GetValue<string>("relatedRegistrationIds") ?? "").Contains(registrationId.ToString()))
                   ?? invoices.FirstOrDefault(c => c.GetValue<string>("memberId") == memberId.ToString());
        }

        /// <summary>
        /// Record (or withdraw) a "payment sent" CLAIM on an invoice — set by the payer (the
        /// shooter, or a club admin paying on members' behalf). This deliberately does NOT touch
        /// <c>paymentStatus</c>: the authoritative "received" state stays organizer-controlled.
        /// Writes <c>paymentSentDate</c> + <c>paymentSentBy</c> and logs a PaymentSent /
        /// PaymentSentCleared audit row. Best-effort; missing properties degrade to a no-op.
        /// </summary>
        public async Task<bool> SetPaymentSentAsync(
            int invoiceId,
            bool sent,
            int? actorMemberId,
            string? actorMemberName)
        {
            try
            {
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null || invoice.ContentType.Alias != "registrationInvoice") return false;

                // Never override the organizer's authoritative received state.
                var currentStatus = (invoice.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']', '"', '\'');
                if (sent && (currentStatus == "Paid" || currentStatus == "Cancelled" || currentStatus == "Refunded"))
                    return true; // already settled — a "sent" claim is moot, treat as no-op success

                if (sent)
                {
                    invoice.SetValue("paymentSentDate", DateTime.Now);
                    invoice.SetValue("paymentSentBy", actorMemberName ?? "");
                }
                else
                {
                    invoice.SetValue("paymentSentDate", null);
                    invoice.SetValue("paymentSentBy", "");
                }

                if (!_contentService.Save(invoice).Success) return false;
                _contentService.Publish(invoice, new[] { "*" }, -1);

                var competitionId = invoice.GetValue<int>("competitionId");
                await _auditService.LogAsync(
                    invoiceId: invoiceId,
                    competitionId: competitionId,
                    eventType: sent ? InvoicePaymentEventTypes.PaymentSent : InvoicePaymentEventTypes.PaymentSentCleared,
                    byMemberId: actorMemberId,
                    byMemberName: actorMemberName,
                    paymentMethod: null,
                    amount: invoice.GetValue<decimal>("totalAmount"),
                    reference: invoice.GetValue<string>("invoiceNumber"),
                    notes: sent ? "Betalning anmäld av betalaren (ej bekräftad av arrangören)" : "Betalningsanmälan återkallad");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting payment-sent flag for invoice {InvoiceId}", invoiceId);
                return false;
            }
        }

        /// <summary>
        /// Update payment status for an invoice. Optional fields are only written when supplied,
        /// so callers can use this to set just the status, or to record a full bookkeeping entry
        /// (paymentMethod / paymentDate / transactionId / notes) at the same time.
        ///
        /// Logs an InvoicePaymentEvents row for the transition (MarkedPaid / Cancelled / Refunded /
        /// StatusChanged) so the audit history modal and the Bokföringsunderlag have a reliable
        /// trail. Pass actor info if known — null acts as a system event.
        /// </summary>
        public async Task<bool> UpdatePaymentStatusAsync(
            int invoiceId,
            string paymentStatus,
            DateTime? paymentDate = null,
            string? transactionId = null,
            string? notes = null,
            string? paymentMethod = null,
            int? actorMemberId = null,
            string? actorMemberName = null,
            decimal? actualAmount = null,
            // Opt-out: every Paid transition tries to email the shooter their receipt unless
            // the caller explicitly suppresses it. Failures here never fail the underlying
            // status update — receipt is a best-effort side effect, just like the audit log.
            bool sendReceiptOnPaid = true,
            // Cancelling an invoice that a samlingsfaktura still charges for is refused by default.
            // The kreditfaktura flow is the ONE legitimate exception: it cancels the covered invoice
            // precisely because it has just issued the credit that compensates for it.
            bool allowCancelWhenConsolidated = false)
        {
            try
            {
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null) return false;

                // Refuse to cancel an invoice a samlingsfaktura is still charging for. The parent is
                // never recalculated (an issued invoice is not altered), so cancelling the child here
                // would leave the club paying for a registration that no longer exists. The caller is
                // expected to surface CoveredByConsolidationMessage.
                if (!allowCancelWhenConsolidated
                    && string.Equals(paymentStatus, "Cancelled", StringComparison.OrdinalIgnoreCase)
                    && IsCoveredByOpenConsolidation(invoiceId, out var blockingParent, out var blockingPaid))
                {
                    _logger.LogWarning(
                        "Refused to cancel invoice {InvoiceId}: covered by open samlingsfaktura {Parent} (paid={Paid})",
                        invoiceId, blockingParent, blockingPaid);
                    return false;
                }

                invoice.SetValue("paymentStatus", paymentStatus);

                if (paymentDate.HasValue)
                    invoice.SetValue("paymentDate", paymentDate.Value);

                if (!string.IsNullOrEmpty(transactionId))
                    invoice.SetValue("transactionId", transactionId);

                if (!string.IsNullOrEmpty(notes))
                    invoice.SetValue("notes", notes);

                if (!string.IsNullOrEmpty(paymentMethod))
                    invoice.SetValue("paymentMethod", paymentMethod);

                // Cashier may record an actual amount different from the invoice total
                // (cash rounding, partial settlement, etc). The invoice's totalAmount is
                // never overwritten — it remains the billed amount; the actualPaidAmount
                // property is the recorded receipt for bookkeeping.
                if (actualAmount.HasValue)
                    invoice.SetValue("actualPaidAmount", actualAmount.Value);

                var saveResult = _contentService.Save(invoice);
                if (!saveResult.Success) return false;

                _contentService.Publish(invoice, new[] { "*" }, -1);
                // Status just changed, so any cached list is wrong. Controllers that call this already
                // clear the cache, but the Swish self-pay and cascade paths do not.
                InvalidateInvoiceListCaches();

                // Log audit event for this state transition. Best effort — the underlying
                // status update already succeeded and we don't want to fail it on audit issues.
                var competitionId = invoice.GetValue<int>("competitionId");
                var billedAmount = invoice.GetValue<decimal>("totalAmount");
                var loggedAmount = actualAmount ?? billedAmount;
                var refForLog = !string.IsNullOrEmpty(transactionId)
                    ? transactionId
                    : invoice.GetValue<string>("invoiceNumber");
                await _auditService.LogAsync(
                    invoiceId: invoiceId,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.FromStatus(paymentStatus),
                    byMemberId: actorMemberId,
                    byMemberName: actorMemberName,
                    paymentMethod: paymentMethod,
                    amount: loggedAmount,
                    reference: refForLog,
                    notes: notes);

                // Self-paid via Swish or admin-marked at the desk — same outcome from the
                // shooter's perspective: a receipt in their inbox. Best-effort, no throw.
                if (sendReceiptOnPaid && paymentStatus == "Paid")
                {
                    await TrySendReceiptAsync(invoice, actorMemberId, actorMemberName);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating payment status for invoice {InvoiceId}", invoiceId);
                return false;
            }
        }

        /// <summary>
        /// Build the receipt context for a just-Paid invoice and email it to the shooter.
        /// All lookups degrade gracefully — a missing field renders blank in the email
        /// rather than blocking the send. Logs an InvoicePaymentEvents row of type
        /// ReceiptSent on success so the audit trail reflects what actually went out.
        /// </summary>
        private async Task TrySendReceiptAsync(IContent invoice, int? actorId, string? actorName)
        {
            try
            {
                var memberIdStr = invoice.GetValue<string>("memberId") ?? "";
                if (!int.TryParse(memberIdStr, out var memberId) || memberId <= 0) return;

                var member = _memberService.GetById(memberId);
                var memberEmail = member?.Email;
                if (string.IsNullOrWhiteSpace(memberEmail))
                {
                    // "No address" used to be indistinguishable from "sent" (both left no row).
                    // Record it so the desk can see the shooter never got a confirmation.
                    await LogReceiptFailureAsync(invoice, actorId, actorName,
                        "Skytten saknar e-postadress");
                    return;
                }

                var memberName = invoice.GetValue<string>("memberName") ?? member?.Name ?? "";
                var billed = invoice.GetValue<decimal>("totalAmount");
                var actual = invoice.GetValue<decimal?>("actualPaidAmount") ?? billed;
                var paymentMethod = invoice.GetValue<string>("paymentMethod") ?? "";
                var transactionId = invoice.GetValue<string>("transactionId") ?? "";
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? "";
                var paidAt = invoice.GetValue<DateTime?>("paymentDate") ?? DateTime.Now;

                var competitionId = invoice.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                var competitionName = competition?.GetValue<string>("competitionName") ?? competition?.Name ?? "";

                // Resolve the organizer name (hosting club, or the region for region-hosted
                // comps) for the confirmation. The full issuer details (org.nr + address)
                // live on the printable Kvitto, not in this confirmation email.
                var organizerClubId = competition?.GetValue<int>("clubId") ?? 0;
                string organizerName;
                if (organizerClubId > 0)
                {
                    organizerName = _clubService.GetClubNameById(organizerClubId) ?? "";
                }
                else
                {
                    var regionCode = competition?.GetValue<string>("regionalFederation") ?? "";
                    var regionNode = !string.IsNullOrWhiteSpace(regionCode) ? FindRegionByCode(regionCode) : null;
                    organizerName = regionNode?.GetValue<string>("regionName") ?? regionNode?.Name ?? "";
                }

                // Resolve the linked registration to list class names. Single-reg invoice
                // (new format) preferred; legacy multi-reg invoices fall back silently to
                // an empty class list.
                var classes = "";
                var registrationId = invoice.GetValue<int>("registrationId");
                if (registrationId > 0)
                {
                    var reg = _contentService.GetById(registrationId);
                    if (reg != null)
                    {
                        var json = reg.GetValue<string>("shootingClasses") ?? "";
                        var entries = CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                        classes = string.Join(", ", entries.Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)));
                    }
                }

                // Operator-entered transactionId wins when present (it's the Swish-app
                // reference the cashier saw); else fall back to the system-generated
                // invoice number, which is the same string the shooter saw as the Swish
                // payment message. The two are functionally the same reference for the
                // shooter — show one Referens row, not "Referens" + "Fakturanummer".
                var displayReference = !string.IsNullOrWhiteSpace(transactionId)
                    ? transactionId
                    : invoiceNumber;

                var sent = await _emailService.SendPaymentConfirmationAsync(
                    memberEmail: memberEmail,
                    memberName: memberName,
                    competitionName: competitionName,
                    organizerName: organizerName,
                    paidAt: paidAt,
                    shootingClasses: classes,
                    billedAmount: billed,
                    actualAmount: actual,
                    paymentMethod: paymentMethod,
                    reference: displayReference);

                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: sent
                        ? InvoicePaymentEventTypes.ReceiptSent
                        : InvoicePaymentEventTypes.ReceiptFailed,
                    byMemberId: actorId,
                    byMemberName: actorName,
                    paymentMethod: paymentMethod,
                    amount: actual,
                    reference: $"Email: {memberEmail}",
                    notes: sent ? null : "Utskicket misslyckades (se serverloggen)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send payment receipt for invoice {InvoiceId}", invoice.Id);
                // The send path swallows SMTP errors itself, so reaching here means something
                // else broke (a lookup, the audit write). Still record the miss — never let a
                // failed confirmation look like a successful one.
                await LogReceiptFailureAsync(invoice, actorId, actorName, "Tekniskt fel vid utskick");
            }
        }

        /// <summary>
        /// Best-effort ReceiptFailed audit row. Swallows its own errors — this runs on the failure
        /// path already and must never mask the original problem.
        /// </summary>
        private async Task LogReceiptFailureAsync(IContent invoice, int? actorId, string? actorName, string reason)
        {
            try
            {
                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: invoice.GetValue<int>("competitionId"),
                    eventType: InvoicePaymentEventTypes.ReceiptFailed,
                    byMemberId: actorId,
                    byMemberName: actorName,
                    paymentMethod: invoice.GetValue<string>("paymentMethod") ?? "",
                    amount: invoice.GetValue<decimal?>("actualPaidAmount") ?? invoice.GetValue<decimal>("totalAmount"),
                    reference: null,
                    notes: reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not log ReceiptFailed for invoice {InvoiceId}", invoice.Id);
            }
        }

        /// <summary>
        /// Find a regionalPage content node by its regionCode (a club's regionalFederation
        /// value). Returns null if no match — callers treat that as "no organizer details".
        /// </summary>
        private IContent? FindRegionByCode(string regionCode)
        {
            var rootContent = _contentService.GetRootContent().FirstOrDefault();
            if (rootContent == null) return null;

            var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
            return rootChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));
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
                        _contentService.Publish(registration, new[] { "*" }, -1);
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

        /// <summary>
        /// Create an invoice for a team registration (no individual registration link).
        /// Uses teamId as the memberId field for identification.
        /// </summary>
        public Task<IContent?> CreateTeamInvoiceAsync(
            int competitionId,
            int teamId,
            string teamName,
            string clubName,
            decimal totalAmount,
            int registrationId = 0,
            string paymentMethod = "Swish")
        {
            try
            {
                _logger.LogInformation("Creating team invoice - CompetitionId: {CompetitionId}, TeamId: {TeamId}, TeamName: {TeamName}, Amount: {Amount}",
                    competitionId, teamId, teamName, totalAmount);

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

                var invoicesHubId = EnsureInvoicesHubId(competitionId);
                if (invoicesHubId == null)
                    return Task.FromResult<IContent?>(null);

                // Use "team-{teamId}" as memberId to distinguish from individual invoices
                var teamMemberId = $"team-{teamId}";
                var invoiceName = $"Lag: {teamName} ({clubName}) - {DateTime.Now:yyyy-MM-dd}";
                var invoice = _contentService.Create(invoiceName, invoicesHubId.Value, "registrationInvoice");
                if (invoice == null)
                    return Task.FromResult<IContent?>(null);

                var contentType = _contentTypeService.Get(invoice.ContentType.Id);
                if (contentType == null)
                {
                    _contentService.Delete(invoice);
                    return Task.FromResult<IContent?>(null);
                }
                var propertyTypes = contentType.PropertyTypes;

                SetInvoicePropertySafely(invoice, "competitionId", competitionId, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "memberId", teamMemberId, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "memberName", $"Lag: {teamName} ({clubName})", propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "totalAmount", totalAmount, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "paymentMethod", paymentMethod, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "paymentStatus", "Pending", propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "createdDate", DateTime.Now, propertyTypes, _logger);
                SetInvoicePropertySafely(invoice, "isActive", true, propertyTypes, _logger);

                // Link to the competitionTeamRegistration doc if available
                if (registrationId > 0)
                    SetInvoicePropertySafely(invoice, "registrationId", registrationId, propertyTypes, _logger);

                var invoiceNumber = GenerateInvoiceNumber(competitionId, teamMemberId, invoice.Id);
                SetInvoicePropertySafely(invoice, "invoiceNumber", invoiceNumber, propertyTypes, _logger);

                var saveResult = _contentService.Save(invoice);
                if (saveResult.Success)
                {
                    var publishResult = _contentService.Publish(invoice, new[] { "*" }, -1);
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Team invoice {InvoiceId} created successfully for team {TeamId}", invoice.Id, teamId);

                        // No paymentMethod on the creation event — nothing has been paid yet.
                        _ = _auditService.LogAsync(
                            invoiceId: invoice.Id,
                            competitionId: competitionId,
                            eventType: InvoicePaymentEventTypes.Created,
                            paymentMethod: null,
                            amount: totalAmount,
                            reference: invoiceNumber,
                            notes: $"Faktura skapad – väntar på betalning: {teamName} ({clubName})");

                        return Task.FromResult<IContent?>(invoice);
                    }
                    _contentService.Delete(invoice);
                }
                else
                {
                    _contentService.Delete(invoice);
                }

                return Task.FromResult<IContent?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating team invoice for CompetitionId: {CompetitionId}, TeamId: {TeamId}", competitionId, teamId);
                return Task.FromResult<IContent?>(null);
            }
        }
    }

    /// <summary>
    /// Billing snapshot for a single registration under the delta/top-up invoice model.
    /// </summary>
    public class RegistrationInvoiceTotals
    {
        /// <summary>Fee the registration owes as it currently stands (all classes + deltävling).</summary>
        public decimal FullFee { get; set; }
        /// <summary>Total of every Paid invoice for the registration.</summary>
        public decimal SumPaid { get; set; }
        /// <summary>What still has to be collected: <c>max(0, FullFee - SumPaid)</c>.</summary>
        public decimal Outstanding { get; set; }
        /// <summary>The single outstanding Pending invoice, if one exists.</summary>
        public IContent? PendingInvoice { get; set; }
    }
}