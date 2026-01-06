using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using Microsoft.Extensions.Logging;
using HpskSite.Models;
using System.Globalization;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for aggregating and managing invoices across multiple competitions
    /// Provides efficient invoice retrieval with server-side filtering
    /// </summary>
    public class InvoiceAdminService
    {
        private readonly ILogger<InvoiceAdminService> _logger;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public InvoiceAdminService(
            ILogger<InvoiceAdminService> logger,
            IContentService contentService,
            IUmbracoContextAccessor umbracoContextAccessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
            _umbracoContextAccessor = umbracoContextAccessor ?? throw new ArgumentNullException(nameof(umbracoContextAccessor));
        }

        /// <summary>
        /// Get all invoices with optional filtering
        /// Uses efficient flat traversal to aggregate invoices from multiple competitions
        /// </summary>
        public InvoiceAggregationResult GetAllInvoices(InvoiceFilterOptions? filters = null)
        {
            filters ??= new InvoiceFilterOptions();

            try
            {
                _logger.LogInformation("Starting invoice aggregation with filters: CompetitionId={CompetitionId}, Status={Status}, ActiveOnly={ActiveOnly}",
                    filters.CompetitionId, filters.PaymentStatus, filters.ActiveCompetitionsOnly);

                // Step 1: Get all competitions and invoice hubs efficiently (single-pass BFS)
                var allCompetitions = new List<IContent>();
                var allInvoicesHubs = new List<IContent>();

                var rootContent = _contentService.GetRootContent();
                foreach (var root in rootContent)
                {
                    var descendants = GetFlatDescendants(root);
                    allCompetitions.AddRange(descendants.Where(c => c.ContentType.Alias == "competition"));
                    allInvoicesHubs.AddRange(descendants.Where(c => c.ContentType.Alias == "registrationInvoicesHub"));
                }

                _logger.LogInformation("Found {CompetitionCount} competitions and {HubCount} invoice hubs",
                    allCompetitions.Count, allInvoicesHubs.Count);

                // Step 2: Filter competitions (active only by default)
                var filteredCompetitions = allCompetitions;
                if (filters.ActiveCompetitionsOnly)
                {
                    filteredCompetitions = allCompetitions
                        .Where(comp => IsCompetitionActive(comp))
                        .ToList();

                    _logger.LogInformation("Filtered to {ActiveCount} active competitions", filteredCompetitions.Count);
                }

                // If filtering by specific competition, narrow down further
                if (filters.CompetitionId.HasValue)
                {
                    filteredCompetitions = filteredCompetitions
                        .Where(comp => comp.Id == filters.CompetitionId.Value)
                        .ToList();
                }

                // If filtering by specific club, narrow down to competitions belonging to that club
                if (filters.ClubId.HasValue && filters.ClubId.Value > 0)
                {
                    filteredCompetitions = filteredCompetitions
                        .Where(comp => comp.GetValue<int>("clubId") == filters.ClubId.Value)
                        .ToList();

                    _logger.LogInformation("Filtered to {ClubCount} competitions for club {ClubId}",
                        filteredCompetitions.Count, filters.ClubId.Value);
                }

                // Step 3: Group invoice hubs by competition ID for O(1) lookup
                var hubsByCompetition = allInvoicesHubs.ToDictionary(hub => hub.ParentId);

                // Step 4: Aggregate all invoices from filtered competitions
                var allInvoices = new List<InvoiceInfo>();

                foreach (var competition in filteredCompetitions)
                {
                    if (hubsByCompetition.TryGetValue(competition.Id, out var hub))
                    {
                        var invoices = GetInvoicesFromHub(hub, competition);
                        allInvoices.AddRange(invoices);
                    }
                }

                _logger.LogInformation("Aggregated {InvoiceCount} total invoices", allInvoices.Count);

                // Step 5: Apply server-side filtering
                var filteredInvoices = ApplyFilters(allInvoices, filters);

                _logger.LogInformation("Filtered to {FilteredCount} invoices", filteredInvoices.Count);

                // Step 6: Calculate metadata
                var metadata = CalculateMetadata(allInvoices, filteredInvoices, filters, filteredCompetitions.Count);

                // Step 7: Apply pagination
                var paginatedInvoices = filteredInvoices
                    .Skip((filters.Page - 1) * filters.PageSize)
                    .Take(filters.PageSize)
                    .ToList();

                return new InvoiceAggregationResult
                {
                    Success = true,
                    Invoices = paginatedInvoices,
                    Metadata = metadata
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error aggregating invoices");
                return new InvoiceAggregationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Efficient flat BFS traversal (non-recursive)
        /// Pattern from CompetitionAdminController.GetFlatDescendants()
        /// </summary>
        private List<IContent> GetFlatDescendants(IContent root)
        {
            var result = new List<IContent>();
            var queue = new Queue<IContent>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                var children = _contentService.GetPagedChildren(current.Id, 0, int.MaxValue, out _);
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        /// <summary>
        /// Check if competition is active (for default filtering)
        /// Active = isActive=true AND registrationCloseDate within last 30 days
        /// </summary>
        private bool IsCompetitionActive(IContent competition)
        {
            var isActive = competition.GetValue<bool>("isActive");
            if (!isActive) return false;

            var regCloseDate = competition.GetValue<DateTime?>("registrationCloseDate");
            if (!regCloseDate.HasValue) return true; // If no close date, consider active

            // Consider active if closed within last 30 days
            return regCloseDate.Value >= DateTime.Now.AddDays(-30);
        }

        /// <summary>
        /// Get all invoices from a registrationInvoicesHub
        /// </summary>
        private List<InvoiceInfo> GetInvoicesFromHub(IContent hub, IContent competition)
        {
            var invoices = new List<InvoiceInfo>();

            try
            {
                var invoiceNodes = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "registrationInvoice")
                    .ToList();

                foreach (var invoiceNode in invoiceNodes)
                {
                    try
                    {
                        var invoice = MapInvoiceToInfo(invoiceNode, competition);
                        invoices.Add(invoice);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to map invoice {InvoiceId}", invoiceNode.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoices from hub {HubId}", hub.Id);
            }

            return invoices;
        }

        /// <summary>
        /// Map IContent invoice node to InvoiceInfo DTO
        /// Handles safe reading of paymentStatus (known tricky property)
        /// </summary>
        private InvoiceInfo MapInvoiceToInfo(IContent invoiceNode, IContent competition)
        {
            // Safe read of paymentStatus - handle JSON array format ["Paid"] or plain string "Paid"
            var paymentStatus = invoiceNode.GetValue<string>("paymentStatus");
            if (string.IsNullOrWhiteSpace(paymentStatus))
            {
                paymentStatus = "Pending";
            }
            else
            {
                // Clean any quotes or whitespace
                paymentStatus = paymentStatus.Trim('"', '\'', ' ');

                // Handle JSON array format: ["Paid"] -> Paid
                if (paymentStatus.StartsWith("[") && paymentStatus.EndsWith("]"))
                {
                    try
                    {
                        var array = System.Text.Json.JsonSerializer.Deserialize<string[]>(paymentStatus);
                        if (array != null && array.Length > 0)
                        {
                            paymentStatus = array[0];
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, try to extract manually
                        paymentStatus = paymentStatus.Trim('[', ']', '"', '\'', ' ');
                    }
                }
            }

            return new InvoiceInfo
            {
                Id = invoiceNode.Id,
                InvoiceNumber = invoiceNode.GetValue<string>("invoiceNumber") ?? "",
                CompetitionId = competition.Id,
                CompetitionName = competition.Name ?? "",
                MemberId = invoiceNode.GetValue<string>("memberId") ?? "",
                MemberName = invoiceNode.GetValue<string>("memberName") ?? "",
                TotalAmount = invoiceNode.GetValue<decimal>("totalAmount"),
                PaymentStatus = paymentStatus,
                PaymentMethod = invoiceNode.GetValue<string>("paymentMethod") ?? "Swish",
                CreatedDate = invoiceNode.GetValue<DateTime?>("createdDate") ?? invoiceNode.CreateDate,
                PaymentDate = invoiceNode.GetValue<DateTime?>("paymentDate"),
                RegistrationId = invoiceNode.GetValue<int>("registrationId"),
                IsActive = invoiceNode.GetValue<bool?>("isActive") ?? true
            };
        }

        /// <summary>
        /// Apply filters to invoice list (server-side filtering)
        /// </summary>
        private List<InvoiceInfo> ApplyFilters(List<InvoiceInfo> invoices, InvoiceFilterOptions filters)
        {
            var filtered = invoices.AsEnumerable();

            // Exclude paid and cancelled invoices (default: true)
            if (filters.ExcludePaid)
            {
                filtered = filtered.Where(inv =>
                    !inv.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) &&
                    !inv.PaymentStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase));
            }

            // Filter by payment status
            if (!string.IsNullOrEmpty(filters.PaymentStatus))
            {
                filtered = filtered.Where(inv => inv.PaymentStatus.Equals(filters.PaymentStatus, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by member name (contains, case-insensitive)
            if (!string.IsNullOrEmpty(filters.MemberSearch))
            {
                filtered = filtered.Where(inv => inv.MemberName.Contains(filters.MemberSearch, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by invoice number (contains or exact match)
            if (!string.IsNullOrEmpty(filters.InvoiceNumberSearch))
            {
                filtered = filtered.Where(inv => inv.InvoiceNumber.Contains(filters.InvoiceNumberSearch, StringComparison.OrdinalIgnoreCase));
            }

            // Sort by created date (newest first)
            filtered = filtered.OrderByDescending(inv => inv.CreatedDate);

            return filtered.ToList();
        }

        /// <summary>
        /// Calculate metadata for aggregation result
        /// </summary>
        private InvoiceMetadata CalculateMetadata(
            List<InvoiceInfo> allInvoices,
            List<InvoiceInfo> filteredInvoices,
            InvoiceFilterOptions filters,
            int activeCompetitionsCount)
        {
            var totalPages = (int)Math.Ceiling((double)filteredInvoices.Count / filters.PageSize);

            return new InvoiceMetadata
            {
                TotalInvoices = allInvoices.Count,
                FilteredInvoices = filteredInvoices.Count,
                Page = filters.Page,
                PageSize = filters.PageSize,
                TotalPages = totalPages,
                ActiveCompetitions = activeCompetitionsCount,
                TotalAmount = allInvoices.Sum(inv => inv.TotalAmount),
                PaidAmount = allInvoices.Where(inv => inv.PaymentStatus == "Paid").Sum(inv => inv.TotalAmount),
                PendingAmount = allInvoices.Where(inv => inv.PaymentStatus == "Pending").Sum(inv => inv.TotalAmount)
            };
        }
    }
}
