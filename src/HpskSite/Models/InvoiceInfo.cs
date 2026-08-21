namespace HpskSite.Models
{
    /// <summary>
    /// Invoice information DTO for admin display
    /// Flattened structure with competition name embedded
    /// </summary>
    public class InvoiceInfo
    {
        /// <summary>
        /// Invoice Umbraco content ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Unique invoice number (format: competitionId-memberId-sequence)
        /// Example: "1067-2043-1"
        /// </summary>
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>
        /// Competition ID this invoice belongs to
        /// </summary>
        public int CompetitionId { get; set; }

        /// <summary>
        /// Competition name for display
        /// </summary>
        public string CompetitionName { get; set; } = string.Empty;

        /// <summary>
        /// Member ID (from IMemberService)
        /// </summary>
        public string MemberId { get; set; } = string.Empty;

        /// <summary>
        /// Member display name
        /// </summary>
        public string MemberName { get; set; } = string.Empty;

        /// <summary>
        /// Total amount to pay (registration fee × number of classes)
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// "" for an ordinary registration invoice, "consolidated" for a samlingsfaktura, "creditNote"
        /// for a credit note. Exposed because a samlingsfaktura carries the SAME money as the invoices
        /// it covers — so anything summing money must exclude it or it double-counts, and any list
        /// showing it should label it rather than let it read as another fee.
        /// </summary>
        public string InvoiceKind { get; set; } = string.Empty;

        /// <summary>The samlingsfaktura that settles this invoice, if any.</summary>
        public int SettledByInvoiceId { get; set; }

        /// <summary>
        /// Invoice number of that samlingsfaktura. Carried so the "Ingår i samlingsfaktura" tag can
        /// NAME and link to the parent: the parent is billed to the paying club and therefore sits in a
        /// different view from its children, which made the tag a dead end (Stefan, 2026-08-04).
        /// </summary>
        public string SettledByInvoiceNumber { get; set; } = string.Empty;

        /// <summary>How many invoices a samlingsfaktura covers; 0 for anything else.</summary>
        public int CoveredCount { get; set; }

        /// <summary>
        /// A team invoice (<c>memberId</c> = <c>team-{id}</c>) whose team no longer exists.
        ///
        /// Deleting a team used to leave its unpaid invoice behind (fixed 2026-08-20 in
        /// <c>CompetitionTeamService.DeleteTeamAsync</c>), and the rows that were already orphaned
        /// stay — an unpaid invoice is räkenskapsinformation, so it is makulerad by hand, never
        /// deleted. Until someone does that it sits in the list looking like a real debt: at SM
        /// 2026 three of them inflated the krets's Fakturor page by 450 kr.
        ///
        /// The list is the one place the junk is actually VISIBLE, so it is labelled here rather
        /// than filtered out. <c>GetCompetitionPayerClubs</c> already skips these — correct, but
        /// silently, which is its own problem.
        /// </summary>
        public bool IsOrphanedTeamInvoice { get; set; }

        /// <summary>
        /// Payment status: "Pending", "Paid", "Cancelled", "Failed", "Refunded"
        /// </summary>
        public string PaymentStatus { get; set; } = "Pending";

        /// <summary>
        /// Payment method: "Swish", "Bank Transfer", "Cash", etc.
        /// </summary>
        public string PaymentMethod { get; set; } = "Swish";

        /// <summary>
        /// When invoice was created
        /// </summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// When payment was completed (null if not paid)
        /// </summary>
        public DateTime? PaymentDate { get; set; }

        /// <summary>
        /// Related registration ID (single registration)
        /// </summary>
        public int RegistrationId { get; set; }

        /// <summary>
        /// Whether invoice is active
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// "Payment sent" CLAIM date — set when the payer (the shooter, or a club admin paying on
        /// the members' behalf) states they have paid. This is NOT organizer-confirmed receipt;
        /// the authoritative "received" state is <see cref="PaymentStatus"/> = "Paid". Null = no claim.
        /// </summary>
        public DateTime? PaymentSentDate { get; set; }

        /// <summary>Who lodged the "payment sent" claim (shooter or club admin name).</summary>
        public string? PaymentSentBy { get; set; }
    }
}
