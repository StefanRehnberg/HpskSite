using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A club's membership-fee category for a given year, tied to a membershipType.
    /// The amount lives here (not on the member) so historical charges stay correct
    /// when the club changes its dues. See Documentation/MEMBER_DATABASE.md §4.
    /// </summary>
    [TableName("MembershipFeeCategory")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeeCategory
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public int Year { get; set; }

        /// <summary>Senior/Junior/Familj/Heder/Ständig/Stödjande — matches the member's membershipType.</summary>
        public string MembershipType { get; set; } = string.Empty;

        /// <summary>Display label, e.g. "Senior" or "Familj (hela hushållet)".</summary>
        public string Label { get; set; } = string.Empty;

        public decimal Amount { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
