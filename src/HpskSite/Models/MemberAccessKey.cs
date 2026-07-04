using NPoco;

namespace HpskSite.Models
{
    [TableName("MemberAccessKey")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberAccessKey
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public string KeyType { get; set; } = string.Empty;   // Nyckel / Bricka / Kod
        public string Identifier { get; set; } = string.Empty; // e.g. "B32"
        public decimal? Deposit { get; set; }
        public DateTime? IssuedDate { get; set; }
        public DateTime? ReturnedDate { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedDate { get; set; }
        public int? CreatedByMemberId { get; set; }

        // Display-only property (not mapped to a DB column)
        [ResultColumn]
        public string? MemberName { get; set; }
    }
}
