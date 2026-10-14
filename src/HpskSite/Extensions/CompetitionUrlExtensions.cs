using Umbraco.Extensions;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Extensions
{
    /// <summary>
    /// Extension methods for generating competition URLs
    /// </summary>
    public static class CompetitionUrlExtensions
    {
        /// <summary>
        /// Tävlingens adress i länkar: den FINA adressen (CompetitionUrlProvider). Stefan 2026-10-14
        /// (C3-planen §9a): en adressform; <c>/competitions/{id}/</c> ger bara 301 dit. Saknar tävlingen en
        /// fin adress (ingen värd, inget datum) faller länken tillbaka på id-formen, som då visar sidan.
        /// </summary>
        public static string CompetitionUrl(this IPublishedContent competition)
        {
            if (competition == null || competition.ContentType.Alias != "competition")
                return string.Empty;

            var pretty = competition.Url();
            return string.IsNullOrEmpty(pretty) || pretty == "#" ? $"/competitions/{competition.Id}/" : pretty;
        }
    }
}
