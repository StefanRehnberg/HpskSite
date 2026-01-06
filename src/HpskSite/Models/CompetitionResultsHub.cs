using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class CompetitionResultsHub : BasePage
    {
        public CompetitionResultsHub(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Competition results hub page - central hub for all competition results
        // Will contain CompetitionResult children (team results, leaderboards, etc.)
    }
}








