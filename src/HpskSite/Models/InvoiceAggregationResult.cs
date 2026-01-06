namespace HpskSite.Models
{
    /// <summary>
    /// Result of invoice aggregation query with metadata
    /// Returned by InvoiceAdminService.GetAllInvoices()
    /// </summary>
    public class InvoiceAggregationResult
    {
        /// <summary>
        /// Whether the aggregation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// List of invoices (paginated)
        /// </summary>
        public List<InvoiceInfo> Invoices { get; set; } = new List<InvoiceInfo>();

        /// <summary>
        /// Aggregation metadata (counts, totals, etc.)
        /// </summary>
        public InvoiceMetadata Metadata { get; set; } = new InvoiceMetadata();

        /// <summary>
        /// Error message if Success = false
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Metadata about the invoice aggregation
    /// Provides summary statistics for dashboard display
    /// </summary>
    public class InvoiceMetadata
    {
        /// <summary>
        /// Total number of invoices across all competitions (before filtering)
        /// </summary>
        public int TotalInvoices { get; set; }

        /// <summary>
        /// Number of invoices after applying filters
        /// </summary>
        public int FilteredInvoices { get; set; }

        /// <summary>
        /// Current page number
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// Number of invoices per page
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Total number of pages for pagination
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// Number of active competitions with invoices
        /// </summary>
        public int ActiveCompetitions { get; set; }

        /// <summary>
        /// Total amount across all invoices (before filtering)
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Total amount with status "Paid"
        /// </summary>
        public decimal PaidAmount { get; set; }

        /// <summary>
        /// Total amount with status "Pending"
        /// </summary>
        public decimal PendingAmount { get; set; }
    }
}
