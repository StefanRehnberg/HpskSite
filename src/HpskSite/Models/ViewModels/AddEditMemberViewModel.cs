namespace HpskSite.Models.ViewModels
{
    public class AddEditMemberViewModel
    {
        public int? Id { get; set; } // null for new member
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string PrimaryClubName { get; set; } = "";
        public int? PrimaryClubId { get; set; }
        public string MemberClubIds { get; set; } = "";
        public bool IsApproved { get; set; } = true;
        public List<string> Groups { get; set; } = new List<string>();
        public List<string> AvailableGroups { get; set; } = new List<string>();
    }
}
