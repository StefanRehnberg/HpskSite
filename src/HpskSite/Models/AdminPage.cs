using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Models
{
    public class AdminPage : BasePage
    {
        public AdminPage(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Admin page - inherits all properties from BasePage
        // Base for admin-only functionality pages
    }
}