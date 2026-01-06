using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class ContentPage : BasePage
    {
        public ContentPage(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        public string BodyText => this.Value<string>("bodyText") ?? "";
    }
}
