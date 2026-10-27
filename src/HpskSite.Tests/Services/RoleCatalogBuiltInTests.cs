using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Ett äldre uppdrag på en tävling utan planering (prod har sådana, 2026-10-27) saknar ägare och därmed arrangörens egna roller. Medlemmens "Mitt schema" ska ändå visa
    /// de inbyggda rollerna med sina riktiga namn ("Sekretariat"), inte nyckeln ("sekretariat").
    /// </summary>
    public class RoleCatalogBuiltInTests
    {
        [Theory]
        [InlineData("Precision", "sekretariat", "Sekretariat")]
        [InlineData("Faltskytte", "stationsmarkor", "Stationsmarkör")]
        [InlineData("Precision", "SEKRETARIAT", "Sekretariat")]
        public void Inbyggda_roller_far_sitt_namn_utan_agare(string discipline, string key, string expected)
            => RoleCatalogService.BuiltInNameFor(discipline, key).Should().Be(expected);

        [Fact]
        public void En_okand_roll_visas_med_nyckeln_och_aldrig_tomt()
        {
            RoleCatalogService.BuiltInNameFor("Precision", "zzp-kaffekokare").Should().Be("zzp-kaffekokare");
            RoleCatalogService.BuiltInNameFor("Precision", null).Should().BeEmpty();
        }
    }
}
