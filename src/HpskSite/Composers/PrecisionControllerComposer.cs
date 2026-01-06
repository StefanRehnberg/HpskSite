using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Composer to ensure SurfaceControllers in nested namespaces are properly registered.
    /// The [Area("Umbraco")] attribute on PrecisionStartListController should be sufficient,
    /// but this ensures proper endpoint mapping if needed.
    /// </summary>
    public class PrecisionControllerComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Umbraco automatically discovers and registers SurfaceControllers
            // The [Area("Umbraco")] attribute on PrecisionStartListController handles routing
            // This composer is kept for future customization if needed
        }
    }
}
