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
    }
}
