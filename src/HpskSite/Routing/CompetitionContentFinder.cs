using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Routing
{
    /// <summary>
    /// Custom content finder for ID-based competition URLs
    /// Handles URLs in format: /competitions/{id}/
    /// </summary>
    public class CompetitionContentFinder : IContentFinder
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public CompetitionContentFinder(IUmbracoContextAccessor umbracoContextAccessor)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            var path = request.Uri.GetAbsolutePathDecoded();

            // Check if URL matches pattern /competitions/{id}/
            if (path.StartsWith("/competitions/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                // Should have exactly 2 segments: "competitions" and "{id}"
                if (segments.Length == 2 && segments[0].Equals("competitions", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to parse the ID
                    if (int.TryParse(segments[1], out int competitionId))
                    {
                        // Get Umbraco context
                        if (!_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
                        {
                            return Task.FromResult(false);
                        }

                        // Find the content by ID
                        var content = umbracoContext.Content?.GetById(competitionId);

                        if (content != null && content.ContentType.Alias == "competition")
                        {
                            // Set the published content for this request
                            request.SetPublishedContent(content);
                            return Task.FromResult(true);
                        }
                    }
                }
            }

            return Task.FromResult(false);
        }
    }
}
