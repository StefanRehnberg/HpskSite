using HpskSite.Models;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>One row in the home hub's "Ditt styrelsearbete" section (meeting / åtgärd / årshjul).</summary>
    public class BoardHubItem
    {
        public string Title { get; set; } = "";
        public DateTime? Date { get; set; }
        public bool Overdue { get; set; }
        public string OwnerName { get; set; } = "";   // which club/krets it belongs to (member may sit on several)
        public string Url { get; set; } = "";          // /styrelse?type=&id= for that board
    }

    public class BoardHubSummary
    {
        public int BoardCount { get; set; }
        public List<BoardHubItem> Meetings { get; set; } = new();
        public List<BoardHubItem> Actions { get; set; } = new();
        public List<BoardHubItem> WheelItems { get; set; } = new();
        public bool HasBoards => BoardCount > 0;
        public bool HasAny => Meetings.Count > 0 || Actions.Count > 0 || WheelItems.Count > 0;
    }

    /// <summary>
    /// Cheap, login-only summary of the member's board work across ALL clubs + regions they sit on
    /// (board-member rows only). Only the hub's "Ditt styrelsearbete" section uses it; all calls are
    /// indexed SQL / Umbraco-cached, so no extra caching is needed. Exception-safe (degrades to empty).
    /// </summary>
    public class BoardHubService
    {
        private readonly BoardRoleService _roles;
        private readonly BoardMeetingService _meetings;
        private readonly BoardGovernanceService _governance;
        private readonly ClubService _clubs;
        private readonly IUmbracoContextFactory _umbracoContextFactory;

        public BoardHubService(
            BoardRoleService roles,
            BoardMeetingService meetings,
            BoardGovernanceService governance,
            ClubService clubs,
            IUmbracoContextFactory umbracoContextFactory)
        {
            _roles = roles;
            _meetings = meetings;
            _governance = governance;
            _clubs = clubs;
            _umbracoContextFactory = umbracoContextFactory;
        }

        public BoardHubSummary GetSummary(int memberId)
        {
            var s = new BoardHubSummary();
            if (memberId <= 0) return s;

            List<(int OwnerType, int OwnerId)> boards;
            try { boards = _roles.GetBoardMembershipsForMember(memberId); }
            catch { return s; }

            s.BoardCount = boards.Count;
            if (boards.Count == 0) return s;

            var today = DateTime.Today;
            try
            {
                using var cref = _umbracoContextFactory.EnsureUmbracoContext();
                var content = cref.UmbracoContext.Content;
                var nameCache = new Dictionary<(int, int), string>();

                string NameOf(int ot, int oid)
                {
                    if (nameCache.TryGetValue((ot, oid), out var cached)) return cached;
                    string name;
                    if (ot == (int)DocumentOwnerType.Club)
                        name = _clubs.GetClubNameById(oid) ?? "Klubb";
                    else
                    {
                        var node = content?.GetById(oid);
                        name = node?.Value<string>("regionName") ?? node?.Name ?? "Krets";
                    }
                    nameCache[(ot, oid)] = name;
                    return name;
                }

                string UrlOf(int ot, int oid) => $"/styrelse?type={ot}&id={oid}";

                foreach (var (ot, oid) in boards)
                {
                    var owner = NameOf(ot, oid);
                    var url = UrlOf(ot, oid);

                    try
                    {
                        foreach (var m in _meetings.GetMeetings(ot, oid)
                            .Where(m => m.MeetingDate.Date >= today)
                            .OrderBy(m => m.MeetingDate)
                            .Take(3))
                        {
                            s.Meetings.Add(new BoardHubItem
                            {
                                Title = string.IsNullOrWhiteSpace(m.Title) ? m.MeetingType : m.Title,
                                Date = m.MeetingDate,
                                OwnerName = owner,
                                Url = url
                            });
                        }
                    }
                    catch { }

                    try
                    {
                        foreach (var w in _governance.GetYearWheel(ot, oid, today.Year)
                            .Where(w => !w.Done && (w.IsOverdue || (w.TargetDate.HasValue && w.TargetDate.Value.Date <= today.AddDays(45))))
                            .OrderBy(w => w.TargetDate ?? DateTime.MaxValue)
                            .Take(3))
                        {
                            s.WheelItems.Add(new BoardHubItem
                            {
                                Title = w.Title,
                                Date = w.TargetDate,
                                Overdue = w.IsOverdue,
                                OwnerName = owner,
                                Url = url
                            });
                        }
                    }
                    catch { }
                }

                // the member's own open actions (already spans all boards by AssignedToMemberId)
                try
                {
                    foreach (var a in _meetings.GetMyActions(memberId).OrderBy(a => a.DueDate ?? DateTime.MaxValue))
                    {
                        s.Actions.Add(new BoardHubItem
                        {
                            Title = a.Description,
                            Date = a.DueDate,
                            Overdue = a.IsOverdue,
                            OwnerName = NameOf(a.OwnerType, a.OwnerId),
                            Url = UrlOf(a.OwnerType, a.OwnerId)
                        });
                    }
                }
                catch { }
            }
            catch { }

            s.Meetings = s.Meetings.OrderBy(x => x.Date ?? DateTime.MaxValue).Take(4).ToList();
            s.Actions = s.Actions.Take(5).ToList();
            s.WheelItems = s.WheelItems.OrderBy(x => x.Date ?? DateTime.MaxValue).Take(4).ToList();
            return s;
        }
    }
}
