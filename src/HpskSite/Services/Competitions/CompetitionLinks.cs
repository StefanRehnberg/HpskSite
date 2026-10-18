using HpskSite.Services.Addresses;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Tävlingens adress ur id:t, utan nod</b> (C3-planen §5d). Den fina adressen ur <c>PageAddress</c>; i nodläget
    /// annars nodens adress; sist <c>/competitions/{id}/</c>, som skickar vidare till sidan.
    /// Ersätter <c>GetById(id).CompetitionUrl()</c> — i tabelläget finns ingen nod att fråga.
    /// </summary>
    public class CompetitionLinks
    {
        private readonly PageAddressUrls _urls;
        private readonly SiteSettingService _settings;
        private readonly IUmbracoContextAccessor _ctx;

        public CompetitionLinks(PageAddressUrls urls, SiteSettingService settings, IUmbracoContextAccessor ctx)
        {
            _urls = urls;
            _settings = settings;
            _ctx = ctx;
        }

        public string Url(int competitionId)
        {
            if (competitionId <= 0) return "";
            var pretty = _urls.UrlFor(PageAddressKinds.Competition, competitionId);
            if (!string.IsNullOrEmpty(pretty)) return pretty;
            if (!CompetitionStoreSettings.UsesTable(_settings)
                && _ctx.TryGetUmbracoContext(out var uc)
                && uc.Content?.GetById(competitionId) is { } node
                && node.ContentType.Alias == Models.Competitions.CompetitionFieldTable.NodeAlias)
            {
                var url = node.Url();
                if (!string.IsNullOrEmpty(url) && url != "#") return url;
            }
            return $"/competitions/{competitionId}/";
        }

        /// <summary>Seriens adress, eller tom när den saknar adress.</summary>
        public string SeriesUrl(int seriesId)
        {
            if (seriesId <= 0) return "";
            var pretty = _urls.UrlFor(PageAddressKinds.Series, seriesId);
            if (!string.IsNullOrEmpty(pretty)) return pretty;
            if (!CompetitionStoreSettings.UsesTable(_settings)
                && _ctx.TryGetUmbracoContext(out var uc)
                && uc.Content?.GetById(seriesId) is { } node
                && node.ContentType.Alias == Models.Competitions.CompetitionSeriesFieldTable.NodeAlias)
                return node.Url();
            return "";
        }
    }
}
