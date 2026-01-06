using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class CompetitionResult : BasePage
    {
        public CompetitionResult(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Competition result properties
        public string ResultType => this.Value<string>("resultType") ?? "Leaderboard"; // Leaderboard, Overall
        public string ResultData => this.Value<string>("resultData") ?? ""; // JSON data with results
        public DateTime LastUpdated => this.Value<DateTime>("lastUpdated", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Now);
        public bool IsOfficial => this.Value<bool>("isOfficial", fallback: Fallback.ToDefaultValue, defaultValue: false);
        
        // Result display properties
        public string DisplayTitle => GetDisplayTitle();
        public string DisplayDescription => GetDisplayDescription();
        public string Description => this.Value<string>("description") ?? "";

        private string GetDisplayTitle()
        {
            var resultType = ResultType;
            return resultType switch
            {
                "Leaderboard" => IsOfficial ? "Slutresultat" : "Live Resultat",
                "Overall" => "Slutresultat",
                _ => Name
            };
        }

        private string GetDisplayDescription()
        {
            var resultType = ResultType;
            return resultType switch
            {
                "Leaderboard" => IsOfficial ? "Slutlig ställning för hela tävlingen" : "Live uppdaterande ställning",
                "Overall" => "Slutlig ställning för hela tävlingen",
                _ => Description
            };
        }
    }
}
