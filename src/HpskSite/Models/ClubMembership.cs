using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// The per-club membership record for a person (hpskMember). One row per
    /// (MemberId, ClubId). Holds club-scoped facts that cannot live on the shared
    /// member/login type. See Documentation/MEMBER_DATABASE.md.
    /// </summary>
    [TableName("ClubMembership")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMembership
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public string? MembershipType { get; set; }
        public string MembershipStatus { get; set; } = "Aktiv";
        public DateTime? MemberSince { get; set; }
        public DateTime? MemberUntil { get; set; }
        public string? EndReason { get; set; }
        public bool BackgroundCheckApproved { get; set; }
        public DateTime? BackgroundCheckDate { get; set; }
        public bool RegisteredInMap { get; set; }
        public string? Federations { get; set; }
        public string? MemberNotes { get; set; }
        public string? HouseholdId { get; set; }
        public bool HouseholdPrimary { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
