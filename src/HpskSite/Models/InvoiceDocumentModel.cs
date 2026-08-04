namespace HpskSite.Models
{
    /// <summary>
    /// A printable "Faktura" (invoice) for a competition registration, team or samlingsfaktura —
    /// the document a club or krets opens from its Fakturor list to see and print what it owes or
    /// is owed. The counterpart to the Kvitto: the Kvitto proves a payment was MADE and is gated on
    /// Paid, while this exists from the moment the invoice does and states what is TO BE paid.
    ///
    /// Extends <see cref="ReceiptModel"/> so the issuer block, buyer block and the itemised
    /// samlingsfaktura lines are built and rendered exactly once, in one place.
    /// </summary>
    public class InvoiceDocumentModel : ReceiptModel
    {
        /// <summary>When the invoice was issued (<c>createdDate</c>), falling back to the node's date.</summary>
        public DateTime IssuedAt { get; set; }

        // ── Money. Filled from ConsolidatedInvoiceService.GetBalance so the amount due is derived
        // the same way the payment QR derives it (issued total − credit notes) — an issued invoice
        // is never edited, so a correction is a credit note and the document keeps its own total.
        public decimal IssuedTotal { get; set; }
        public decimal Credited { get; set; }
        public decimal AmountDue { get; set; }

        /// <summary>Raw paymentStatus, normalised (legacy rows can store <c>["Paid"]</c>).</summary>
        public string PaymentStatus { get; set; } = "";
        /// <summary>Swedish label for the status badge: Obetald / Betald / Krediterad / Makulerad.</summary>
        public string StatusLabel { get; set; } = "";
        public bool IsCancelled { get; set; }
        public bool IsCreditNote { get; set; }

        /// <summary>
        /// True when this invoice is already covered by a samlingsfaktura — it must then NOT be paid
        /// separately, and the document says so instead of showing payment details.
        /// </summary>
        public bool IsSettledByParent { get; set; }
        public string SettledByInvoiceNumber { get; set; } = "";

        // ── How to pay. Swish is per competition, bankgiro per organisation (see the payee resolver).
        public string SwishNumber { get; set; } = "";
        public string PaymentReference { get; set; } = "";

        public bool HasSwishNumber => !string.IsNullOrWhiteSpace(SwishNumber);
        public bool HasAnyPaymentDetails => HasSwishNumber || IssuerHasBgNumber;

        /// <summary>
        /// Base64 PNG of the bankgiro invoice QR (Swedish invoice-QR format, scanned in the payer's own
        /// bank app). "" when the issuer has no usable bankgiro — the details are always printed as text
        /// as well, so a missing QR only costs the shortcut.
        /// </summary>
        public string BgQrCodeBase64 { get; set; } = "";
        public bool HasBgQrCode => !string.IsNullOrWhiteSpace(BgQrCodeBase64);

        /// <summary>The club being billed, when the invoice is issued to a club rather than a member.</summary>
        public string BilledToName { get; set; } = "";
    }
}
