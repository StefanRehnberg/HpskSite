using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class CompetitionsHub : BasePage
    {
        public CompetitionsHub(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Competitions hub page - central hub for all competitions
        // Will contain CompetitionSeason children
    }
}