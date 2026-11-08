using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>En klubb i kretsen, som kretsens "Klubbar i kretsen"-block visar den: bara namn och adress.</summary>
    public sealed record SiteRegionClub(int Id, string Name, string Url);

    /// <summary>En skjutbana som kretsens klubbar använder. Bara namn och ort (aldrig koordinater: en begränsad bana tas inte med alls).</summary>
    public sealed record SiteRangeInfo(int Id, string Name, string? City, string Url);

    /// <summary>En serie som kretsen själv arrangerar.</summary>
    public sealed record SiteSeriesInfo(string Name, string Url, DateTime? Start, DateTime? End);

    /// <summary>Kretsens siffror (inga personuppgifter: bara antal).</summary>
    public sealed record SiteRegionStats(int Clubs, int Ranges, int Series, int UpcomingCompetitions);

    /// <summary>
    /// Kretsens klubbar — EN plats (klubbsidorna 2d). Samma källa som kretssidans egen "antal klubbar" (klubbsidan under kretsen,
    /// <c>clubsPage</c>), så kretsens block, kretsmedlemskapet och kretsens startsida räknar likadant.
    /// </summary>
    public static class SiteRegion
    {
        public static IReadOnlyList<SiteRegionClub> Clubs(IPublishedContent? regionNode)
        {
            if (regionNode == null) return Array.Empty<SiteRegionClub>();
            var hub = regionNode.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
            if (hub == null) return Array.Empty<SiteRegionClub>();
            return hub.Children
                .Where(c => c.ContentType.Alias == "club" && c.IsPublished())
                .Select(c => new SiteRegionClub(c.Id, c.Value<string>("clubName") ?? c.Name ?? "", c.Url()))
                .Where(c => c.Name.Length > 0)
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
