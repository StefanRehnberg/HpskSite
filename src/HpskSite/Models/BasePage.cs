using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class BasePage : PublishedContentModel
    {
        public BasePage(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        public string PageTitle => this.Value<string>("pageTitle") ?? this.Name;
        public string MetaDescription => this.Value<string>("metaDescription") ?? "";
        public bool HideFromNavigation => this.Value<bool>("hideFromNavigation");
    }
}
