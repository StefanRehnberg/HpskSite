namespace HpskSite.Shared.Models
{
    public class TrainingGroup
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int ClubId { get; set; }
        public string? ClubName { get; set; }
        public string? Description { get; set; }
        public DateTime StartDate { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; }
        public int CreatedByMemberId { get; set; }

        public List<TrainingGroupMember> Members { get; set; } = new();
        public int MemberCount { get; set; }
        public int TrainerCount { get; set; }
    }
}
