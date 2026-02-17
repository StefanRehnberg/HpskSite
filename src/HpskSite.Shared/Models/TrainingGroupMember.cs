namespace HpskSite.Shared.Models
{
    public class TrainingGroupMember
    {
        public int Id { get; set; }
        public int TrainingGroupId { get; set; }
        public int MemberId { get; set; }
        public string Role { get; set; } = "Member";
        public DateTime JoinedDate { get; set; }
        public int? AddedByMemberId { get; set; }
        public bool IsActive { get; set; } = true;

        public string? MemberName { get; set; }
        public string? ClubName { get; set; }

        public bool IsTrainer => Role == "Trainer";
    }
}
