using System.Collections.Generic;

namespace HpskSite.CompetitionTypes.Springskytte.Models
{
    /// <summary>
    /// View model for the standalone Springskytte start-list page
    /// (routed at /startlista/{competitionId}[/{slug}]).
    /// </summary>
    public class SpringskytteStartListPageModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";

        /// <summary>All start lists for the competition, ordered by first start time.</summary>
        public List<SpringskytteStartListView> Lists { get; set; } = new();

        /// <summary>When set, the page shows this single list (index/hub otherwise).</summary>
        public string? SelectedSlug { get; set; }

        // For highlighting the viewer's own row / club (empty when anonymous).
        public string CurrentMemberName { get; set; } = "";
        public string CurrentMemberClub { get; set; } = "";
    }

    /// <summary>One start list (a published precisionStartList child node) in a display-ready shape.</summary>
    public class SpringskytteStartListView
    {
        public int NodeId { get; set; }
        public string ListName { get; set; } = "";
        public string Slug { get; set; } = "";
        public bool IsOfficial { get; set; }
        public System.DateTime GeneratedDate { get; set; }
        public SpringskytteStartListConfig Config { get; set; } = new();
    }
}
