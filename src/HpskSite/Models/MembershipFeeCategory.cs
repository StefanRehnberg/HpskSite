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

        /// <summary>
        /// 0 = klubbens kategori (per medlemstyp), 1 = kretsens taxa. För kretsen är
        /// <see cref="MembershipType"/> en av <see cref="RegionFeeCalculator.BaseCategory"/> och
        /// <see cref="RegionFeeCalculator.PerMemberCategory"/>, och <see cref="ClubId"/> är 0
        /// (CK_MembershipFeeCategory_Shape).
        /// </summary>
        public int IssuerType { get; set; }

        /// <summary>Kretsens nod-id för kretsens taxa.</summary>
        public int? RegionId { get; set; }

        /// <summary>
        /// Får medlemmen själv välja den här medlemstypen på betalsidan? (Stefan 2026-09-23)
        ///
        /// <para>⚠️ Förvalt AV för familj, hedersmedlem och ständig medlem (<see cref="DefaultMemberSelectable"/>):
        /// en familj kräver att klubben kopplat ihop hushållet, och en avgiftsfri hederstyp ska inte
        /// gå att välja sig till.</para>
        /// </summary>
        public bool MemberSelectable { get; set; } = true;

        /// <summary>Förvalet för en ny medlemstyp — se <see cref="MemberSelectable"/>.</summary>
        public static bool DefaultMemberSelectable(string? membershipType)
        {
            var t = (membershipType ?? "").Trim();
            return !(t.StartsWith("Famil", StringComparison.OrdinalIgnoreCase)
                  || t.StartsWith("Heder", StringComparison.OrdinalIgnoreCase)
                  || t.StartsWith("Ständig", StringComparison.OrdinalIgnoreCase));
        }
    }
}
