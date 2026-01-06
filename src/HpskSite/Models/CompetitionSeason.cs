using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class CompetitionSeason : BasePage
    {
        public CompetitionSeason(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Competition season page - groups competitions by year/season
        // Will contain Competition children
        public string SeasonDescription => this.Value<string>("seasonDescription") ?? "";
    }
}