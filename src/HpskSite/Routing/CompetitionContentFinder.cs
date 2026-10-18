using Umbraco.Extensions;
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
        private readonly HpskSite.Services.SiteSettings.SiteSettingService _settings;
        private readonly HpskSite.Services.Competitions.CompetitionLinks _links;

        public CompetitionContentFinder(IUmbracoContextAccessor umbracoContextAccessor,
            HpskSite.Services.SiteSettings.SiteSettingService settings, HpskSite.Services.Competitions.CompetitionLinks links)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _settings = settings;
            _links = links;
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
                        // Tabelläget (C3-planen §5d): ingen nod att visa — adressen ur PageAddress, annars ingen sida.
                        if (HpskSite.Services.Competitions.CompetitionStoreSettings.UsesTable(_settings))
                        {
                            var url = _links.Url(competitionId);
                            if (string.IsNullOrEmpty(url) || url.TrimEnd('/').Equals(path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                                return Task.FromResult(false);
                            request.SetRedirectPermanent(url + request.Uri.Query);
                            return Task.FromResult(true);
                        }

                        // Get Umbraco context
                        if (!_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
                        {
                            return Task.FromResult(false);
                        }

                        // Find the content by ID
                        var content = umbracoContext.Content?.GetById(competitionId);

                        if (content != null && content.ContentType.Alias == "competition")
                        {
                            // Stefan 2026-10-14 (C3-planen §9a): den fina adressen är den enda. /competitions/{id}/
                            // ger 301 dit i stället för att visa sidan — annars kopieras den här formen ur
                            // webbläsaren och delas vidare (den var prods mest besökta tävlingsadress).
                            // Frågesträngen följer med (?tab=… m.m.). Saknar tävlingen en fin adress visas
                            // sidan här som förut, hellre än en 404.
                            var pretty = content.Url();
                            if (!string.IsNullOrEmpty(pretty) && pretty != "#"
                                && !pretty.TrimEnd('/').Equals(path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                            {
                                request.SetRedirectPermanent(pretty + request.Uri.Query);
                                return Task.FromResult(true);
                            }
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
