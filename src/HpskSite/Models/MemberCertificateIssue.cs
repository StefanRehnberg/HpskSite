using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A single föreningsintyg issued by a club to a member — the record of a licence-support
    /// certificate handed out for a weapon-licence application. Issuing is a club/board act.
    /// </summary>
    [TableName("MemberCertificateIssue")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberCertificateIssue
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public DateTime IssuedDate { get; set; }
        public string Purpose { get; set; } = string.Empty;   // e.g. "Vapenlicens", "Förnyelse"
        public string? Description { get; set; }               // weapon/ändamål detail
        public int? IssuedByMemberId { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedDate { get; set; }

        // Display-only properties (not mapped to DB columns)
        [ResultColumn]
        public string? MemberName { get; set; }

        [ResultColumn]
        public string? IssuedByName { get; set; }
    }
}
