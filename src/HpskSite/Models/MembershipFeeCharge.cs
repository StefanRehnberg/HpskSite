using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One membership-fee charge per member per year. Mirrors the competition-invoice
    /// two-state model: the payer lodges a claim (PaymentSentDate/By via "Jag har
    /// betalat"), the club admin confirms received (PaymentStatus = "Paid").
    /// See Documentation/MEMBER_DATABASE.md §4.
    /// </summary>
    [TableName("MembershipFeeCharge")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeeCharge
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public int Year { get; set; }

        /// <summary>Soft reference to MembershipFeeCategory.Id (null when charged ad hoc).</summary>
        public int? CategoryId { get; set; }

        public decimal Amount { get; set; }

        /// <summary>Pending / Paid / Cancelled.</summary>
        public string PaymentStatus { get; set; } = "Pending";

        // Payer's claim ("Jag har betalat") — never sets Paid.
        public DateTime? PaymentSentDate { get; set; }
        public string? PaymentSentBy { get; set; }

        // Organizer's authoritative "received" state.
        public DateTime? PaidDate { get; set; }
        public int? PaidConfirmedByMemberId { get; set; }

        /// <summary>
        /// Familjeavgift: a non-primary household member's charge points at the primary
        /// member's charge (which carries the actual family amount). Null = billed individually.
        /// </summary>
        public int? HouseholdCoveredByChargeId { get; set; }

        public DateTime CreatedDate { get; set; }

        // Display-only (resolved in the service, not mapped to DB columns).
        [ResultColumn]
        public string? MemberName { get; set; }

        [ResultColumn]
        public string? MemberEmail { get; set; }
    }
}
