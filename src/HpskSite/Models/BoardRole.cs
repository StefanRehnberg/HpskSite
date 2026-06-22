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

        // Term tracking (Phase 1). Nullable: existing rows have no term and render as "—", never expired.
        public DateTime? ElectedDate { get; set; }     // date elected at årsmöte
        public DateTime? TermEndsDate { get; set; }    // mandate runs to (source of truth)
        public int? TermYears { get; set; }            // 1 or 2 typically (context only)

        // Display-only properties (not mapped to DB columns)
        [ResultColumn]
        public string? MemberName { get; set; }

        [Ignore]
        public string DisplayTitle => RoleKey == "Custom" && !string.IsNullOrEmpty(CustomTitle)
            ? CustomTitle
            : BoardRoleDefinitions.GetLabel(RoleKey);

        [Ignore]
        public bool HasTerm => TermEndsDate.HasValue;

        [Ignore]
        public bool IsTermExpired => TermEndsDate.HasValue && TermEndsDate.Value.Date < DateTime.Today;

        [Ignore]
        public int? DaysLeftInTerm => TermEndsDate.HasValue
            ? (int?)(TermEndsDate.Value.Date - DateTime.Today).TotalDays
            : null;
    }
}
