namespace HpskSite.Models
{
    /// <summary>
    /// Filter options for invoice aggregation query
    /// Used by InvoiceAdminService to filter invoices server-side
    /// </summary>
    public class InvoiceFilterOptions
    {
        /// <summary>
        /// Filter by specific competition (optional)
        /// </summary>
        public int? CompetitionId { get; set; }

        /// <summary>
        /// Filter by specific club (shows all invoices from competitions belonging to this club)
        /// </summary>
        public int? ClubId { get; set; }

        /// <summary>
        /// Filter by payment status: "Pending", "Paid", "Cancelled", "Failed", "Refunded"
        /// </summary>
        public string? PaymentStatus { get; set; }

        /// <summary>
        /// Search by member name (contains, case-insensitive)
        /// </summary>
        public string? MemberSearch { get; set; }

        /// <summary>
        /// Search by invoice number (contains or exact match)
        /// </summary>
        public string? InvoiceNumberSearch { get; set; }

        /// <summary>
        /// Show only invoices from active competitions (default: true)
        /// Reduces query load by 90% for most use cases
        /// </summary>
        public bool ActiveCompetitionsOnly { get; set; } = true;

        /// <summary>
        /// Exclude paid invoices from results (default: true)
        /// Focuses on actionable items (pending, failed, etc.)
        /// </summary>
        public bool ExcludePaid { get; set; } = true;

        /// <summary>
        /// Page number for pagination (1-indexed)
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// Number of invoices per page (default: 50)
        /// </summary>
        public int PageSize { get; set; } = 50;
    }
}
