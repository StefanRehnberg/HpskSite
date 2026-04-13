using NPoco;

namespace HpskSite.Models
{
    [TableName("BoardRoles")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardRole
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int MemberId { get; set; }
        public string RoleKey { get; set; } = string.Empty;
        public string? CustomTitle { get; set; }
        public bool IsBoardMember { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime AssignedDate { get; set; }
        public int? AssignedByMemberId { get; set; }
        public bool IsActive { get; set; } = true;

        // Display-only properties (not mapped to DB columns)
        [ResultColumn]
        public string? MemberName { get; set; }

        [Ignore]
        public string DisplayTitle => RoleKey == "Custom" && !string.IsNullOrEmpty(CustomTitle)
            ? CustomTitle
            : BoardRoleDefinitions.GetLabel(RoleKey);
    }
}
