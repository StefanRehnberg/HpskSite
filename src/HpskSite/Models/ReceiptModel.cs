namespace HpskSite.Models
{
    /// <summary>
    /// Everything needed to render a legal "Kvitto" (receipt) for a paid competition
    /// registration — the printable document a shooter can produce from Min sida →
    /// Tävlingar and hand to an employer for friskvårdsbidrag.
    ///
    /// Built by <see cref="HpskSite.Services.ReceiptModelBuilder"/> from a single
    /// registrationInvoice id; <see cref="AmountPaid"/> sums every Paid invoice linked
    /// to the same registration so top-ups are included in the total.
    /// </summary>
    public class ReceiptModel
    {
        public bool Found { get; set; }

        public int InvoiceId { get; set; }
        public int RegistrationId { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }

        // Buyer (köpare)
        public string MemberName { get; set; } = "";
        public string MemberEmail { get; set; } = "";

        // What was paid for
        public string CompetitionName { get; set; } = "";
        public DateTime? CompetitionDate { get; set; }
        public string ShootingClasses { get; set; } = "";

        /// <summary>
        /// For a samlingsfaktura: the registrations this one payment covered. A club paying for N
        /// shooters needs them itemised for its own bookkeeping — a single "Anmälningsavgift" line for
        /// a lump sum is not something an accountant can reconcile. Empty for a normal receipt.
        /// </summary>
        public List<ReceiptLine> CoveredLines { get; set; } = new();

        /// <summary>True when this receipt is for a consolidated payment covering several people.</summary>
        public bool IsConsolidated => CoveredLines.Count > 0;

        // Issuer / seller (utställare / säljare) — the hosting club, or the region
        // for region-hosted competitions.
        public string IssuerName { get; set; } = "";
        public string IssuerOrgNumber { get; set; } = "";
        public string IssuerStreet { get; set; } = "";
        public string IssuerPostalCode { get; set; } = "";
        public string IssuerCity { get; set; } = "";
        public string IssuerContactEmail { get; set; } = "";
        /// <summary>
        /// The issuer's bankgiro (club/regionalPage level), "" when they have none. Printed on the
        /// receipt as part of the seller's details — normal on a Swedish kvitto/faktura, and it is what
        /// a club looks for when reconciling a BG payment against the document.
        /// </summary>
        public string IssuerBgNumber { get; set; } = "";
        /// <summary>Resolved media URL for the issuer's logo, or "" when none is set.</summary>
        public string IssuerLogoUrl { get; set; } = "";

        public bool IssuerHasOrgNumber => !string.IsNullOrWhiteSpace(IssuerOrgNumber);
        public bool IssuerHasBgNumber => !string.IsNullOrWhiteSpace(IssuerBgNumber);

        // Money
        public decimal AmountPaid { get; set; }
        public string PaymentMethod { get; set; } = "";
        /// <summary>Operator reference — Swish transaction id when present, else the invoice number.</summary>
        public string Reference { get; set; } = "";
        /// <summary>The receipt/verification number shown to the buyer (the primary invoice number).</summary>
        public string ReceiptNumber { get; set; } = "";
        public DateTime PaidAt { get; set; }
        public bool IsPaid { get; set; }

        /// <summary>
        /// The receipt exists but belongs to someone else. Rendered as a plain explanation rather than
        /// Forbid(), which redirects to /Account/AccessDenied — not an Umbraco document, so the visitor
        /// got a raw "Page Not Found" quoting an internal path.
        /// </summary>
        public bool AccessDenied { get; set; }
    }

    /// <summary>One itemised line on a consolidated receipt — a single covered registration.</summary>
    public class ReceiptLine
    {
        public string InvoiceNumber { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string ShootingClasses { get; set; } = "";
        public decimal Amount { get; set; }
    }
}
