using HpskSite.Services.Addresses;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests;

/// <summary>
/// Klubbsidorna F1: en klubb får inte heta som en av kretsens sidor i den nya sidstrukturen (/halland/rekord/ delar
/// namnrymd med /halland/{klubb}/). Spärren bygger på mallregistret, så en ny mallslugg skyddas av sig själv.
/// </summary>
public class RegionTemplateSlugGuardTests
{
    [Fact]
    public void Every_region_template_slug_is_reserved_for_club_names()
    {
        var slugs = SiteTemplates.Slugs(SiteTemplates.Region);
        Assert.NotEmpty(slugs);
        foreach (var s in slugs)
            Assert.True(PageAddressRules.IsRegionTemplateSlug(s), $"kretsens sida '{s}' är inte reserverad");
    }

    [Theory]
    [InlineData("rekord")]
    [InlineData("KALENDER")]
    [InlineData("vara-tavlingar")]
    [InlineData("styrelsen")]
    [InlineData("hemsidan")]
    public void Known_region_pages_are_reserved(string segment) => Assert.True(PageAddressRules.IsRegionTemplateSlug(segment));

    [Theory]
    [InlineData("ankeborg-pistolklubb")]
    [InlineData("varbergs-pk")]
    [InlineData("banor")] // en KLUBBS sida (/klubb/banor/), inte kretsens: ett klubbnamn "Banor" skuggar ingen kretssida
    public void Ordinary_club_names_are_not_reserved(string segment) => Assert.False(PageAddressRules.IsRegionTemplateSlug(segment));
}
