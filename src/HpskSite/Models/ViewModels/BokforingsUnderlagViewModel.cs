namespace HpskSite.Models.ViewModels
{
    /// <summary>
    /// Print-ready accounting summary for a single competition. Shape favours direct
    /// rendering by the Razor view rather than reuse — every field is something the
    /// view will display verbatim.
    /// </summary>
    public class BokforingsUnderlagViewModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public DateTime? CompetitionDate { get; set; }
        public DateTime? CompetitionEndDate { get; set; }
        public string? Venue { get; set; }
        public string? Organizer { get; set; }     // Arrangerande klubb
        public string? Scope { get; set; }         // Klubbmästerskap / Kretsmästerskap / etc.
        public DateTime GeneratedAt { get; set; }
        public string? GeneratedBy { get; set; }   // Operator name; shown in the footer

        public BokforingsSummary Summary { get; set; } = new();

        /// <summary>Paid transactions, oldest first (chronological for the bookkeeper).</summary>
        public List<BokforingsTransactionRow> PaidTransactions { get; set; } = new();

        /// <summary>Outstanding (Pending + No Invoice) — for transparency, not for the books.</summary>
        public List<BokforingsTransactionRow> OutstandingTransactions { get; set; } = new();

        /// <summary>Cancelled invoices — for completeness.</summary>
        public List<BokforingsTransactionRow> CancelledTransactions { get; set; } = new();

        /// <summary>Refunded invoices.</summary>
        public List<BokforingsTransactionRow> RefundedTransactions { get; set; } = new();
    }

    public class BokforingsSummary
    {
        public int TotalRegistrations { get; set; }
        public int PaidCount { get; set; }
        public int PendingCount { get; set; }
        public int NoInvoiceCount { get; set; }
        public int CancelledCount { get; set; }
        public int RefundedCount { get; set; }

        public decimal PaidTotal { get; set; }
        public decimal PendingTotal { get; set; }
        public decimal RefundedTotal { get; set; }

        /// <summary>Sum of paid amounts grouped by paymentMethod (Swish/Kontant/Bankgiro/Annat/empty).</summary>
        public Dictionary<string, decimal> PaidByMethod { get; set; } = new();

        /// <summary>Count of paid invoices grouped by paymentMethod.</summary>
        public Dictionary<string, int> PaidCountByMethod { get; set; } = new();
    }

    public class BokforingsTransactionRow
    {
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string? ClubName { get; set; }
        public decimal Amount { get; set; }
        /// <summary>Actual amount recorded at mark-as-paid time. Null when no variance was
        /// recorded (treat as equal to <see cref="Amount"/> for totalling purposes).</summary>
        public decimal? ActualAmount { get; set; }
        public string PaymentStatus { get; set; } = "";
        public string? PaymentMethod { get; set; }
        public DateTime? PaymentDate { get; set; }
        public DateTime? CreatedDate { get; set; }
        public string? TransactionId { get; set; }
        public string? Notes { get; set; }

        /// <summary>The amount that should appear in bookkeeping totals — actual when set,
        /// otherwise the billed amount. Lets every consumer use one rule.</summary>
        public decimal RecordedAmount => ActualAmount ?? Amount;
        public bool HasVariance => ActualAmount.HasValue && ActualAmount.Value != Amount;
    }
}
