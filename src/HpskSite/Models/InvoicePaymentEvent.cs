using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Append-only audit row for a registrationInvoice state change. One row per
    /// significant action — creation, status flip, email-sent, etc. The invoice's
    /// own properties always reflect the *current* state; this table preserves
    /// history.
    /// </summary>
    public static class InvoicePaymentEventTypes
    {
        public const string Created        = "Created";
        public const string MarkedPaid     = "MarkedPaid";
        public const string Cancelled      = "Cancelled";
        public const string Refunded       = "Refunded";
        public const string EmailSent      = "EmailSent";
        public const string ReceiptSent    = "ReceiptSent";   // payment receipt emailed to the shooter after mark-as-paid
        public const string Transferred    = "Transferred";   // registration (and this invoice) re-pointed to a different member
        public const string StatusChanged  = "StatusChanged"; // catch-all for status flips that aren't one of the above

        /// <summary>Resolves a paymentStatus value into the event type that should be logged when transitioning to it.</summary>
        public static string FromStatus(string paymentStatus) => paymentStatus switch
        {
            "Paid"      => MarkedPaid,
            "Cancelled" => Cancelled,
            "Refunded"  => Refunded,
            _           => StatusChanged
        };
    }

    [TableName("InvoicePaymentEvents")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class InvoicePaymentEvent
    {
        public int Id { get; set; }
        public int InvoiceId { get; set; }
        public int CompetitionId { get; set; }
        public string EventType { get; set; } = "";
        public DateTime OccurredAt { get; set; }
        public int? ByMemberId { get; set; }
        public string? ByMemberName { get; set; }
        public string? PaymentMethod { get; set; }
        public decimal? Amount { get; set; }
        public string? Reference { get; set; }
        public string? Notes { get; set; }
    }
}
