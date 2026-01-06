namespace HpskSite.Models.ViewModels
{
    public class ClubViewModel
    {
        public int? Id { get; set; } // null for new club
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ContactPerson { get; set; } = "";
        public string ContactEmail { get; set; } = "";
        public string ContactPhone { get; set; } = "";
        public string WebSite { get; set; } = "";
        public string Address { get; set; } = "";
        public string City { get; set; } = "";
        public string PostalCode { get; set; } = "";
        public string UrlSegment { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int MemberCount { get; set; } = 0;
        public int AdminCount { get; set; } = 0;
        public int? ClubId { get; set; }  // SPSF federation ID
        public string RegionalFederation { get; set; } = "";  // Enum value as string
    }

    public class ClubMemberViewModel
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsPrimary { get; set; }
    }
}
