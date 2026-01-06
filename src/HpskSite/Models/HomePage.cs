using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class HomePage : BasePage
    {
        public HomePage(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        public string WelcomeTitle => this.Value<string>("welcomeTitle") ?? "Welcome to HPSK";
        public string WelcomeText => this.Value<string>("welcomeText") ?? "";
        public IEnumerable<IPublishedContent> FeaturedItems => this.Value<IEnumerable<IPublishedContent>>("featuredItems") ?? Enumerable.Empty<IPublishedContent>();
    }
}
