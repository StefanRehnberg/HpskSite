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
        /// Filter by region — keeps competitions hosted in this region, whether hosted by the region
        /// itself or by one of its clubs. See <see cref="RegionOwnCompetitionsOnly"/> to narrow it.
        /// </summary>
        public string? Region { get; set; }

        /// <summary>
        /// With <see cref="Region"/> set, keep ONLY the region's own competitions (clubId unset) and
        /// drop its clubs'. Default for a krets's own Fakturor tab: a club's invoices are between the
        /// club and its payers — the club is the controller of that member data, and a regional admin
        /// who genuinely needs them can open that club's own Fakturor tab. Opt in to include them.
        /// </summary>
        public bool RegionOwnCompetitionsOnly { get; set; }

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

        /// <summary>
        /// View type for club invoice tab:
        /// "incoming" = invoices for registrations to the club's own competitions (receivables)
        /// "outgoing" = team invoices the club needs to pay (payables)
        /// "members" = individual invoices for club members across all competitions
        /// Default: null (no view type filtering, original behavior)
        /// </summary>
        public string? ViewType { get; set; }
    }
}
