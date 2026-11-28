using HpskSite.Services.Pqt;

namespace HpskSite.Models
{
    /// <summary>Allt som delen med kommentarer på /pqt behöver. Byggs av <c>PqtController</c> och visas av <c>_PqtComments.cshtml</c> (sidan och fragmentet efter varje åtgärd).</summary>
    public sealed class PqtCommentsModel
    {
        public bool LoggedIn { get; init; }
        public bool Moderator { get; init; }
        public int ViewerMemberId { get; init; }
        public int VisibleCount { get; init; }
        public int Total { get; init; }
        public List<PqtCommentRow> Rows { get; init; } = new();
        public DateTime Now { get; init; }
    }

    /// <summary>Sidans övriga tillstånd: gillningar och intresseanmälan.</summary>
    public sealed class PqtPageModel
    {
        public bool LoggedIn { get; init; }
        public bool Admin { get; init; }
        public int LikeCount { get; init; }
        public bool Liked { get; init; }
        public PqtInterestRow? MyInterest { get; init; }
        public List<PqtInterestRow> AllInterest { get; init; } = new();
        public PqtCommentsModel Comments { get; init; } = new();
    }
}
