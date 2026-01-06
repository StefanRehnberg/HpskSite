using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Extensions
{
    /// <summary>
    /// Extension methods for generating competition URLs
    /// </summary>
    public static class CompetitionUrlExtensions
    {
        /// <summary>
        /// Generates an ID-based URL for a competition
        /// Format: /competitions/{id}/
        /// </summary>
        /// <param name="competition">The competition published content</param>
        /// <returns>ID-based URL string, or empty string if invalid</returns>
        public static string CompetitionUrl(this IPublishedContent competition)
        {
            if (competition == null || competition.ContentType.Alias != "competition")
                return string.Empty;

            return $"/competitions/{competition.Id}/";
        }
    }
}
