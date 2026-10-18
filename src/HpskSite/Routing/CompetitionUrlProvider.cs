using HpskSite.Services.Addresses;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace HpskSite.Routing
{
    /// <summary>
    /// C3-planen S6: tävlingarnas, seriernas och listans adresser (Stefans beslut 2026-10-14, §9a), ur
    /// <c>PageAddress</c> via <see cref="PageAddressUrls"/>:
    /// <list type="bullet">
    /// <item>tävling: <c>/{krets}/{klubb}/tavlingar/{år}/{tävling}/</c>, <c>/{krets}/tavlingar/{år}/{tävling}/</c>,
    /// <c>/sm/{år}/{tävling}/</c>, <c>/ssm|vsm|osm|nsm/{år}/{tävling}/</c></item>
    /// <item>tävlingens barn (resultat, startlista, finalstartlista): tävlingens adress + eget segment</item>
    /// <item>serie: <c>/{värd}/serier/{år}/{serie}/</c></item>
    /// <item>navet: <c>/tavlingar/</c>. Säsongen har ingen egen sida längre och pekar på navet.</item>
    /// </list>
    /// Saknar en tävling eller serie adress (ingen värd) returneras null, och Umbracos trädadress gäller —
    /// den visas då som förut (<see cref="CompetitionTreeRedirectFinder"/> 301:ar bara när adressen flyttat).
    /// </summary>
    public class CompetitionUrlProvider : IUrlProvider
    {
        // Umbraco 17 kräver ett unikt alias per adressgivare.
        public const string ProviderAlias = "Hpsk.CompetitionUrlProvider";
        public string Alias => ProviderAlias;

        private readonly PageAddressUrls _urls;
        private readonly ILogger<CompetitionUrlProvider> _logger;

        // Tävlingens barn: tävlingens adress + eget segment.
        private static readonly HashSet<string> ChildAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            "precisionStartList",
            "competitionResult",
            "finalsStartList"
        };

        public CompetitionUrlProvider(PageAddressUrls urls, ILogger<CompetitionUrlProvider> logger)
        {
            _urls = urls;
            _logger = logger;
        }

        public UrlInfo? GetUrl(IPublishedContent content, UrlMode mode, string? culture, Uri current)
        {
            try
            {
                if (content == null) return null;
                var url = UrlOf(content);
                return url == null ? null : UrlInfo.AsUrl(url, ProviderAlias, culture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CompetitionUrlProvider failed for content {Id} — falling through to default", content?.Id);
                return null;
            }
        }

        private string? UrlOf(IPublishedContent content)
        {
            switch (content.ContentType.Alias)
            {
                case "competition": // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                    return _urls.UrlFor(PageAddressKinds.Competition, content.Id);
                case "competitionSeries": // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
                    return _urls.UrlFor(PageAddressKinds.Series, content.Id);
                case "competitionsHub":
                case "competitionSeason": // säsongssidan finns inte längre (2026-10-18) — listan har årsväljaren
                    return PageAddressUrls.CompetitionsListPath;
            }
            if (ChildAliases.Contains(content.ContentType.Alias) && content.Parent?.ContentType.Alias == "competition") // competitionstore-ok: adresserna (C3 S6) routar till noden tills sidorna blir controllers (S7)
            {
                var parentUrl = _urls.UrlFor(PageAddressKinds.Competition, content.Parent.Id);
                return parentUrl == null ? null : parentUrl + content.UrlSegment + "/";
            }
            return null;
        }

        public IEnumerable<UrlInfo> GetOtherUrls(int id, Uri current) => Enumerable.Empty<UrlInfo>();

        // Förhandsvisningen anpassas inte — Umbracos egen adressgivare tar den.
        public Task<UrlInfo?> GetPreviewUrlAsync(IContent content, string? culture, string? segment)
            => Task.FromResult<UrlInfo?>(null);
    }
}
